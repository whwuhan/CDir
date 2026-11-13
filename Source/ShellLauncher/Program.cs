using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CDir.Services;

internal static class Program
{
    private static int Main(string[] Arguments)
    {
        try
        {
            if(Arguments is ["--integration-bootstrap",var Action,var PipeName,var RootPath]) return CDir.IntegrationHost.Bootstrap(Action,PipeName,RootPath);
            if(Arguments is ["--integration",var Request,var ReplyPipe,var OwnedRoot]) return CDir.IntegrationHost.Run(Request,ReplyPipe,OwnedRoot);
        }
        catch(Exception Error) when(Error is IOException or OperationCanceledException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException) { return 2; }
        if (Arguments is ["--owned-test", var Pipe, var Root])
        {
            // The test runner never discovers or activates user windows.
            var Name = Path.GetFileName(Root);
            if (Environment.GetEnvironmentVariable("CDIR_QUIET_RUNNER") != "1" || !Name.StartsWith("CDirSmoke", StringComparison.Ordinal)
                || !Guid.TryParseExact(Name[9..], "N", out _) || File.ReadAllText(Path.Combine(Root, ".CDirSmokeOwner")) != Name[9..]
                || Pipe != "CDir.ShellLaunch.Test." + Name[9..]) return 3;
            return ShellLaunchClient.SendAsync(Pipe, new(null)).GetAwaiter().GetResult() is { Accepted: true } ? 0 : 2;
        }
        var Executable = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "CDir.exe"));
        var Identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Executable.ToUpperInvariant())))[..24];
        using var Gate = new Mutex(false, @"Local\CDir.ShellLauncher." + Identity);
        var Acquired = false;
        try
        {
            try { Acquired = Gate.WaitOne(TimeSpan.FromSeconds(3)); } catch (AbandonedMutexException) { Acquired = true; }
            if (!Acquired) return 2;
            var Location = Arguments.FirstOrDefault(Value => Value != "--shell-open");
            var Instances = ShellLaunchClient.UserInstances(Executable);
            // An existing user process owns the request. A busy window must not
            // cause a second CDir process to be created.
            if (Instances.Length > 0) return ShellLaunchClient.ForwardAsync(Instances, Location).GetAwaiter().GetResult() ? 0 : 2;
            var Start = new ProcessStartInfo(Executable) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
            if (Location is not null) { Start.ArgumentList.Add("--shell-open"); Start.ArgumentList.Add(Location); }
            using var Started = Process.Start(Start);
            if (Started is null) return 1;
            var Deadline = Environment.TickCount64 + 3000;
            while (!Started.HasExited && Environment.TickCount64 < Deadline && ShellLaunchClient.UserInstances(Executable).Length == 0) Thread.Sleep(25);
            return 0;
        }
        catch (Exception Error) when (Error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException) { return 1; }
        finally { if (Acquired) Gate.ReleaseMutex(); }
    }
}
