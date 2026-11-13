using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using CDir.Models;
using CDir.Services;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunShellLaunchSmokeChecksAsync(List<string> Passed)
    {
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Shell launch check failed: " + Name);
            Passed.Add(Name);
        }
        var Root = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "ShellLaunch")).FullName;
        var Child = Directory.CreateDirectory(Path.Combine(Root, "子文件夹 with spaces")).FullName;
        var Config = Path.Combine(Root, "Settings.json");
        File.WriteAllText(Config, JsonSerializer.Serialize(new AppSettings { LastPath = Root, ExitOnClose = true }));
        var Probe = new MainWindow(Config, RestoreWorkspace: true) { Owner = this.HostWindow, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            Probe.Show(); await Probe.WorkspaceRestored; await Probe.ActiveTab!.WaitUntilReadyAsync();
            var OriginalTab = Probe.ActiveTab;
            var Handle = new System.Windows.Interop.WindowInteropHelper(Probe).Handle;
            var Pipe = "CDir.ShellLaunch.Test." + Path.GetFileName(AppMain.SmokeRoot!)[9..];
            var Requests = 0;
            using var Server = new ShellLaunchServer(Pipe, async Request => await (await Probe.Dispatcher.InvokeAsync(async () =>
            {
                Requests++;
                return await Probe.HandleShellLaunchAsync(Request);
            })));
            Server.Start();
            Probe.Hide();
            var Start = new ProcessStartInfo(DefaultFileManagerService.Launcher) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            Start.ArgumentList.Add("--owned-test"); Start.ArgumentList.Add(Pipe); Start.ArgumentList.Add(AppMain.SmokeRoot!);
            using var Launcher = Process.Start(Start)!;
            await Launcher.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            Check(Launcher.ExitCode == 0 && Requests == 1 && Probe.IsVisible && ReferenceEquals(Probe.ActiveTab, OriginalTab)
                && Probe.Tabs.Count == 1 && new System.Windows.Interop.WindowInteropHelper(Probe).Handle == Handle,
                "The actual shell launcher restores the existing owned window and active tab through IPC without launching another CDir");
            Check(!InstanceWorkspaceService.IsUserInstance(Environment.ProcessId) && InstanceWorkspaceService.Peers().Length == 0,
                "Shell launcher checks never advertise or discover user instances");
            var Reply = await ShellLaunchClient.SendAsync(Pipe, new(Child));
            Check(Reply is { Accepted: true } && Probe.Tabs.Count == 2 && SamePath(Probe.ActiveTab!.TabPath, Child),
                "Opening a folder creates a tab in the same process and existing window");
            var ChildTab = Probe.ActiveTab;
            Reply = await ShellLaunchClient.SendAsync(Pipe, new(Child));
            Check(Reply is { Accepted: true } && Probe.Tabs.Count == 2 && ReferenceEquals(Probe.ActiveTab, ChildTab),
                "Opening an already open folder reuses its tab");
            Probe.WindowState = WindowState.Minimized;
            Reply = await ShellLaunchClient.SendAsync(Pipe, new(null));
            Check(Reply is { Accepted: true } && Probe.WindowState != WindowState.Minimized && ReferenceEquals(Probe.ActiveTab, ChildTab),
                "Win+E restores a minimized existing window without changing its active path");
            Check(ShellLaunchLocation.TryResolve(null, out var Empty) && Empty is null
                && !ShellLaunchLocation.TryResolve("shell:ControlPanelFolder", out _),
                "Win+E has no forced navigation and unsupported virtual folders retain their Windows route");
        }
        finally { Probe.RequestExit(); }
    }
}
