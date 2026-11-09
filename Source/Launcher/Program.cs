using System.Diagnostics;
using System.Runtime.InteropServices;
using CDir.Runtime;

internal static class Program
{
    private static int Main(string[] Args)
    {
        string? Probe = null;
        var ProbeOwned = false;
        try
        {
            var IsCheck = Args.Contains("--smoke-test", StringComparer.Ordinal);
            if (Args.FirstOrDefault() == "--runtime-probe")
            {
                Probe = Args.Length == 3 ? Path.GetFullPath(Args[1]) : throw new ArgumentException("The runtime probe arguments are incomplete.");
                if (Environment.GetEnvironmentVariable("CDIR_QUIET_RUNNER") != "1"
                    || !Probe.StartsWith(Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
                    || File.ReadAllText(Path.Combine(Probe, ".CDirRuntimeProbeOwner")) != Args[2])
                    throw new InvalidOperationException("Runtime probes require an owned test scope.");
                ProbeOwned = true;
            }
            var Directory = IsCheck ? AppContext.BaseDirectory : RuntimeSnapshot.Prepare(AppContext.BaseDirectory,
                Probe is null ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CDir", "RuntimeSnapshots")
                    : Path.Combine(Probe, "C"));
            var Info = new ProcessStartInfo(Path.Combine(Directory, "CDir.App.exe"))
            {
                UseShellExecute = false, WorkingDirectory = Directory
            };
            for (var Index = 0; Index < Args.Length; Index++)
            {
                var Argument = Args[Index];
                Info.ArgumentList.Add(!IsCheck && Probe is null && !Argument.StartsWith("--", StringComparison.Ordinal)
                    ? Path.GetFullPath(Argument) : Argument);
                if ((Argument is "--clone-from" or "--screenshot" or "--smoke-group") && Index + 1 < Args.Length)
                    Info.ArgumentList.Add(Args[++Index]);
            }
            using var Child = Process.Start(Info) ?? throw new IOException("The application process could not be started.");
            if (IsCheck) { Child.WaitForExit(); return Child.ExitCode; }
            return 0;
        }
        catch (Exception Error)
        {
            var Message = "CDir could not start. " + Error.Message;
            if (ProbeOwned) File.WriteAllText(Path.Combine(Probe!, "error.txt"), Error.ToString());
            else if (Args.FirstOrDefault() == "--runtime-probe") Console.Error.WriteLine(Message);
            else if (Args.Contains("--smoke-test", StringComparer.Ordinal)) Console.Error.WriteLine(Message);
            else MessageBox(IntPtr.Zero, Message, "CDir", 0x10);
            return 1;
        }
    }

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr Owner, string Text, string Caption, uint Type);
}
