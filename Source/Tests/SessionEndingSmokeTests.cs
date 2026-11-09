using System.IO;
using System.Text.Json;
using System.Windows;
using CDir.Models;
using CDir.Services;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunSessionEndingSmokeChecksAsync(List<string> Passed)
    {
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Session ending check failed: " + Name);
            Passed.Add(Name);
        }
        var Root = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "SessionEnding-" + Guid.NewGuid().ToString("N"))).FullName;
        var SettingsFile = Path.Combine(Root, "Settings.json");
        var WorkspaceFile = SettingsFile + ".workspace.json";
        DurableJsonStore.Write(SettingsFile, JsonSerializer.Serialize(new AppSettings { LastPath = Root }));
        MainWindow? Older = null, Recent = null, Restored = null;
        async Task<MainWindow> Open(WorkspaceState? Initial = null)
        {
            var Window = new MainWindow(SettingsFile, RestoreWorkspace: true, InitialWorkspace: Initial)
                { Owner = this.HostWindow, ShowActivated = false, ShowInTaskbar = false };
            Window.Show(); await Window.ActiveTab!.WaitUntilReadyAsync(); await Window.WorkspaceRestored;
            return Window;
        }
        try
        {
            Older = await Open();
            Older.MultiInstanceDetected = true;
            Recent = await Open(Older.CaptureWorkspace());
            Recent.MultiInstanceDetected = true;
            Older.ActiveTab!.SetTabAlias("Older window, last shutdown callback");
            Recent.ActiveTab!.SetTabAlias("Most recent window before restart");
            Recent.ActiveTab.ChangeGridLines(!Recent.ActiveTab.Settings.ShowGridLines);
            var ExpectedGrid = Recent.ActiveTab.Settings.ShowGridLines;
            var Extra = Recent.AddTab(); await Extra.WaitUntilReadyAsync();
            Extra.SetTabAlias("Unsaved second tab");
            Recent.SelectTab(Recent.Tabs[0]);
            var Disk = File.ReadAllText(WorkspaceFile);
            var SettingsDisk = File.ReadAllText(SettingsFile);
            Check(!Recent.PersistWorkspaceNow() && new SessionWorkspaceStore(WorkspaceFile).ReadLatest() is { Tabs.Count: 2 },
                "Overlapping windows save private recovery checkpoints while shared autosaving remains suspended");
            var Questions = 0;
            foreach (var Window in new[] { Older, Recent })
            {
                Window.ConfirmWorkspaceSave = () => { Questions++; return MessageBoxResult.Cancel; };
                Window.ActiveTab!.OperationBusy = true;
                Window.ActiveTab.ChangeExitOnClose(false);
            }
            Check(Recent.PersistSessionEndingWorkspace() && Older.PersistSessionEndingWorkspace(),
                "Shutdown snapshots save both overlapping instances without shared-save approval or a peer connection");
            var Latest = new SessionWorkspaceStore(WorkspaceFile).ReadLatest();
            Check(Latest is { Tabs.Count: 2, ActiveTab: 0 } && Latest.Tabs[0].Alias == "Most recent window before restart"
                && Latest.Tabs[0].Settings.ShowGridLines == ExpectedGrid && Latest.Tabs[1].Alias == "Unsaved second tab",
                "Recovery chooses the most recently active workspace rather than the last shutdown writer, including live preferences and all tabs");
            Check(File.ReadAllText(SettingsFile) == SettingsDisk && File.ReadAllText(WorkspaceFile) == Disk,
                "Simultaneous session snapshots do not overwrite shared settings or recovery files");
            // Exercise the same Closing and Closed handlers used by WPF session shutdown.
            Recent.Close(); Older.Close();
            Check(!Recent.IsVisible && !Older.IsVisible && Recent.ActiveTab!.WindowClosed && Older.ActiveTab!.WindowClosed && Questions == 0,
                "Session shutdown bypasses hide-to-tray, busy-operation and conflict prompts without canceling window closure");
            Recent = Older = null;
            Restored = await Open();
            Check(Restored.Tabs.Count == 2 && Restored.ActiveTab!.TabAlias == "Most recent window before restart"
                && Restored.ActiveTab.Settings.ShowGridLines == ExpectedGrid && Restored.Tabs[1].TabAlias == "Unsaved second tab",
                "Starting after session shutdown restores the current configuration and complete most recent workspace");
            Restored.ActiveTab.SetTabAlias("A newer normal save");
            Restored.RequestExit(); Restored = null;
            Restored = await Open();
            Check(Restored.ActiveTab!.TabAlias == "A newer normal save",
                "Retained shutdown snapshots never replace a newer successful normal save");

            Restored.ActiveTab.Settings.ShowGridLines = !ExpectedGrid;
            Restored.ActiveTab.SetTabAlias("Single instance immediately before restart");
            Check(Restored.PersistSessionEndingWorkspace(), "A single instance captures preferences changed immediately before Windows restart");
            Restored.Close(); Restored = null;
            Restored = await Open();
            Check(Restored.ActiveTab!.TabAlias == "Single instance immediately before restart" && Restored.ActiveTab.Settings.ShowGridLines == !ExpectedGrid,
                "A single instance restores its final configuration without relying on a periodic shared save");

            var Broken = Path.Combine(Root, "Blocked.workspace.json");
            File.WriteAllText(Broken + ".sessions", "This owned file prevents creating the snapshot directory.");
            Check(!new SessionWorkspaceStore(Broken).Save(Restored.CaptureWorkspace()!),
                "A snapshot I/O failure returns immediately without a retry, dialog or shutdown veto");
            var Outside = Path.GetFullPath(Path.Combine(AppMain.SmokeRoot!, "..", "UnownedSession-" + Guid.NewGuid().ToString("N")));
            Check(!new SessionWorkspaceStore(Outside).Save(Restored.CaptureWorkspace()!)
                && new SessionWorkspaceStore(Outside).ReadLatest() is null && !Directory.Exists(Outside + ".sessions"),
                "Session recovery never reads or writes outside the owned UI-check scope");
        }
        finally
        {
            foreach (var Window in new[] { Older, Recent, Restored }.OfType<MainWindow>())
            {
                foreach (var Page in Window.Tabs) Page.OperationBusy = false;
                Window.ConfirmWorkspaceSave = () => MessageBoxResult.No;
                Window.RequestExit();
            }
        }
    }
}
