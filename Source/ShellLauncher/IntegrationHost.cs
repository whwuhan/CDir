using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using System.Runtime.InteropServices;
using CDir.Services;
using Microsoft.Win32;

namespace CDir;

// The linked registration implementation uses the normal per-user hive here.
internal static class AppMain { internal static bool SmokeTest => false; internal static string? SmokeRoot => null; }

internal static class IntegrationHost
{
    internal static int Bootstrap(string Action, string Pipe, string Root)
    {
        var Start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute=false, CreateNoWindow=true };
        foreach(var Argument in new[]{"--integration", Action, Pipe, Root}) Start.ArgumentList.Add(Argument);
        using var Child = Process.Start(Start);
        if (Child is null || !Child.WaitForExit(15000)) return 2;
        return Child.ExitCode;
    }
    internal static int Run(string Action, string Pipe, string Root) => RunAsync(Action, Pipe, Root).GetAwaiter().GetResult();
    private static async Task<int> RunAsync(string Action, string Name, string Root)
    {
        if (!Name.StartsWith("CDir.RegistryIntegration.", StringComparison.Ordinal) || !Guid.TryParseExact(Name[25..],"N",out _)) return 3;
        using var Timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var Pipe = new NamedPipeClientStream(".",Name,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        await Pipe.ConnectAsync(Timeout.Token).ConfigureAwait(false);
        using var Writer = new StreamWriter(Pipe,leaveOpen:true){AutoFlush=true};
        using var Reader = new StreamReader(Pipe,leaveOpen:true);
        string? ProbePath=null;
        var CreatedParent=false;
        var Success=false;
        var Enabled=false;
        string? Failure=null;
        try
        {
            if(Action=="owned-probe")
            {
                var Leaf=Path.GetFileName(Root);
                if(Environment.GetEnvironmentVariable("CDIR_QUIET_RUNNER")!="1" || !Leaf.StartsWith("CDirSmoke",StringComparison.Ordinal)
                    || !Guid.TryParseExact(Leaf[9..],"N",out _) || File.ReadAllText(Path.Combine(Root,".CDirSmokeOwner"))!=Leaf[9..]) throw new InvalidOperationException("The native registry probe is not owned.");
                using(var Current=Process.GetCurrentProcess())
                {
                    if(Current.PriorityClass!=ProcessPriorityClass.Idle || !IsProcessInJob(Current.Handle,IntPtr.Zero,out var InJob) || !InJob
                        || NtQueryInformationProcess(Current.Handle,33,out var Io,sizeof(uint),out _)!=0 || Io!=0)
                        throw new InvalidOperationException("The native registry probe lost the runner's job or background I/O protection.");
                }
                ProbePath=@"Software\CDir\StartupTests\"+Leaf;
                using(var Existing=Registry.CurrentUser.OpenSubKey(ProbePath))
                {
                    CreatedParent=Existing is null;
                    if(Existing is not null && Existing.GetValue("TestOwner") as string!=Root) throw new InvalidOperationException("The native registry probe key is not owned.");
                }
                using var Parent=Registry.CurrentUser.CreateSubKey(ProbePath); Parent.SetValue("TestOwner",Root);
                using var Probe=Parent.CreateSubKey("NativeProbe"); Probe.SetValue("Owner",Root); Probe.Flush();
            }
            else
            {
                if(Environment.GetEnvironmentVariable("CDIR_QUIET_RUNNER")=="1") throw new InvalidOperationException("A UI check cannot change real Shell registrations.");
                switch(Action)
                {
                    case "status": break;
                    case "enable": DefaultFileManagerService.SetEnabled(true); break;
                    case "disable": DefaultFileManagerService.SetEnabled(false); break;
                    case "refresh": DefaultFileManagerService.RefreshOwnedRegistration(); break;
                    default: throw new ArgumentException("Unknown integration request.");
                }
                Enabled=DefaultFileManagerService.IsEnabled();
            }
            Success=true;
        }
        catch(Exception Error) when(Error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Security.SecurityException) { Failure=Error.Message; }
        try
        {
            await Writer.WriteLineAsync(JsonSerializer.Serialize(new {Success,Enabled,Error=Failure}).AsMemory(),Timeout.Token).ConfigureAwait(false);
            await Reader.ReadLineAsync(Timeout.Token).ConfigureAwait(false);
        }
        finally
        {
            if(ProbePath is not null)
            {
                using(var Probe=Registry.CurrentUser.OpenSubKey(ProbePath+@"\NativeProbe"))
                    if(Probe?.GetValue("Owner") as string==Root) Registry.CurrentUser.DeleteSubKeyTree(ProbePath+@"\NativeProbe",false);
                if(CreatedParent)
                {
                    using var Parent=Registry.CurrentUser.OpenSubKey(ProbePath);
                    if(Parent is {SubKeyCount:0,ValueCount:1} && Parent.GetValue("TestOwner") as string==Root) Registry.CurrentUser.DeleteSubKeyTree(ProbePath,false);
                }
            }
        }
        return Success?0:2;
    }
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool IsProcessInJob(IntPtr Process,IntPtr Job,[MarshalAs(UnmanagedType.Bool)] out bool Result);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr Process,int Class,out uint Value,int Size,out int Returned);
}
