using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CDir.Models;
using CDir.Services;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunExitGeometrySmokeChecksAsync(List<string> Passed)
    {
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Exit geometry check failed: " + Name);
            Passed.Add(Name);
        }
        var Root = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "ExitGeometry-" + Guid.NewGuid().ToString("N"))).FullName;
        foreach (var Scenario in new[] { "Maximized", "Minimized", "Minimized from maximized", "Hidden maximized", "Normal" })
        {
            AppMain.RecordSmokeProgress("Exit geometry: " + Scenario);
            var SettingsFile = Path.Combine(Root, Scenario + ".json");
            DurableJsonStore.Write(SettingsFile, JsonSerializer.Serialize(new AppSettings { LastPath = Root, Width = 1100, Height = 720 }));
            MainWindow? Probe = null;
            try
            {
                Probe = new MainWindow(SettingsFile, RestoreWorkspace: true) { Owner = this.HostWindow, ShowActivated = false, ShowInTaskbar = false };
                Probe.Show(); await Probe.ActiveTab!.WaitUntilReadyAsync(); await Probe.WorkspaceRestored;
                var First = Probe.ActiveTab!;
                First.SetTabAlias("Exit geometry first");
                First.ChangeFileView(FileViewMode.LargeIcons);
                var Second = Probe.AddTab(); await Second.WaitUntilReadyAsync();
                Second.SetTabAlias("Exit geometry second");
                Probe.SelectTab(First);
                if (Scenario.Contains("maximized", StringComparison.OrdinalIgnoreCase)) Probe.WindowState = WindowState.Maximized;
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                if (Scenario.StartsWith("Minimized", StringComparison.Ordinal)) Probe.WindowState = WindowState.Minimized;
                if (Scenario.StartsWith("Hidden", StringComparison.Ordinal)) Probe.CloseWindowButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                var Before = Probe.CaptureWorkspace()!;
                Check(Before is not null && Probe.PersistWorkspaceNow(), Scenario + ": the live workspace saves before exit");
                var CapturesAtClosing = -1;
                Probe.Closing += (_, _) => CapturesAtClosing = Probe.WorkspaceCaptureCount;
                var Closed = false;
                Probe.Closed += (_, _) => Closed = true;
                // Run the real closing/closed event chain while the parent test
                // window keeps the application dispatcher alive to detect late errors.
                Probe.RequestExit();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check(Closed && First.WindowClosed && Second.WindowClosed && Probe.TrayIcon?.IsVisible != true,
                    Scenario + ": exit completes cleanup without a serialization exception or retained tray icon");
                Check(Probe.WorkspaceCaptureCount == CapturesAtClosing && Probe.CaptureWorkspace() is null && !Probe.PersistWorkspaceNow(),
                    Scenario + ": closed windows never recapture destroyed WPF geometry or restart workspace saving");
                var Saved = DurableJsonStore.Read<WorkspaceState>(SettingsFile + ".workspace.json")!;
                var Settings = DurableJsonStore.Read<AppSettings>(SettingsFile)!;
                Check(Saved is { Tabs.Count: 2, ActiveTab: 0 } && Saved.Tabs[0].Alias == "Exit geometry first"
                    && Saved.Tabs[1].Alias == "Exit geometry second" && Saved.Tabs[0].Settings.FileView == FileViewMode.LargeIcons,
                    Scenario + ": exit preserves tabs, active selection and the large-icon view");
                Check(double.IsFinite(Saved.Left) && double.IsFinite(Saved.Top) && double.IsFinite(Saved.Width) && double.IsFinite(Saved.Height)
                    && Saved.Width > 0 && Saved.Height > 0 && Settings.Width > 0 && Settings.Height > 0
                    && Math.Abs(Saved.Width - Before!.Width) < 1 && Math.Abs(Saved.Height - Before.Height) < 1,
                    Scenario + ": settings and workspace contain valid finite restore bounds from before teardown");
                if (Scenario.Contains("maximized", StringComparison.OrdinalIgnoreCase))
                    Check(Saved.Maximized && Settings.Maximized, Scenario + ": exit retains the maximized restore preference");
                Probe = null;
                var Reopened = new MainWindow(SettingsFile, RestoreWorkspace: true) { Owner = this.HostWindow, ShowActivated = false, ShowInTaskbar = false };
                Probe = Reopened;
                // WPF prohibits initially showing an inactive maximized window.
                // Use the same inactive-show sequence as the tray restoration path.
                var StartupState = Reopened.WindowState;
                if (StartupState == WindowState.Maximized) Reopened.WindowState = WindowState.Normal;
                Reopened.Show(); Reopened.WindowState = StartupState;
                await Reopened.ActiveTab!.WaitUntilReadyAsync(); await Reopened.WorkspaceRestored;
                Check(Reopened.Tabs.Count == 2 && Reopened.ActiveTab!.TabAlias == "Exit geometry first"
                    && Reopened.ActiveTab.PageSettings.FileView == FileViewMode.LargeIcons,
                    Scenario + ": a real restart restores the last complete pre-exit workspace");
                Reopened.RequestExit(); Probe = null;
            }
            catch (Exception Error) { AppMain.RecordSmokeProgress("Exit geometry failure: " + Scenario + " " + Error); throw; }
            finally { if (Probe is { IsLoaded: true }) Probe.RequestExit(); }
        }
    }
}
