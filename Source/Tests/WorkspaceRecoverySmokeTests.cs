using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using CDir.Models;
using CDir.Services;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunWorkspaceRecoverySmokeChecksAsync(List<string> Passed)
    {
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Workspace recovery UI check failed: " + Name);
            Passed.Add(Name);
        }
        var Root = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "Recovery-" + Guid.NewGuid().ToString("N"))).FullName;
        var First = Directory.CreateDirectory(Path.Combine(Root, "First 中文")).FullName;
        var Second = Directory.CreateDirectory(Path.Combine(Root, "Second Folder")).FullName;
        File.WriteAllText(Path.Combine(Second, "Selected.txt"), "Owned recovery fixture");
        var SettingsFile = Path.Combine(Root, "Settings.json");
        var WorkspaceFile = SettingsFile + ".workspace.json";
        DurableJsonStore.Write(SettingsFile, JsonSerializer.Serialize(new AppSettings { LastPath = First }));
        DurableJsonStore.Write(WorkspaceFile, JsonSerializer.Serialize(new
        {
            Tabs = new[] { new { Settings = new { LastPath = First, Shortcuts = new Dictionary<string, string> { ["QuickEditText"] = "F9" }, SeparatePinnedEditorTabs = true } } },
            EditorWindows = new[] { new { Tabs = new[] { new { Path = Path.Combine(Second, "Selected.txt"), Text = "Legacy draft" } } } }
        }));
        MainWindow? Probe = null;
        async Task Open()
        {
            Probe = new MainWindow(SettingsFile, RestoreWorkspace: true) { Owner = this.HostWindow, ShowActivated = false, ShowInTaskbar = false };
            Probe.Show();
            await Probe.ActiveTab!.WaitUntilReadyAsync();
            await Probe.WorkspaceRestored;
        }
        try
        {
            await Open();
            var A = Probe!.ActiveTab!;
            Check(SamePath(A.CurrentPath, First) && A.ShortcutBindings["OpenWithDefault"] == "F9"
                && !Probe.OwnedWindows.Cast<Window>().Any(Window => Window.GetType().Name.Contains("TextEditor")),
                "Legacy workspaces restore file tabs and migrate their custom open key without restoring the removed editor");
            A.SetTabAlias("Kept first");
            var B = Probe.AddTab();
            await B.WaitUntilReadyAsync();
            await B.NavigateAsync(Second);
            B.SetTabAlias("Recovered 中文");
            B.ChangeGridLines(false);
            B.FileGrid.Columns.Single(Column => Column.SortMemberPath == nameof(FileEntry.Name)).Width = new DataGridLength(333);
            B.SelectPath(Path.Combine(Second, "Selected.txt"));
            Probe.Width = 1100;
            Probe.Height = 720;
            // Do not close the window: the file must already exist while it is running.
            await Task.Delay(1250);
            var Saved = DurableJsonStore.Read<WorkspaceState>(WorkspaceFile)!;
            Check(Probe.IsVisible && Saved is { Tabs.Count: 2, ActiveTab: 1 } && Saved.Tabs[1].Alias == "Recovered 中文"
                && SamePath(Saved.Tabs[1].Settings.LastPath!, Second) && !Saved.Tabs[1].Settings.ShowGridLines,
                "The open window saves ordered tabs, active directory, aliases and current configuration without a close event");
            Check(Math.Abs(Saved.Width - 1100) < 1 && Math.Abs(Saved.Height - 720) < 1
                && Saved.Tabs[1].ColumnWidths[nameof(FileEntry.Name)] == 333
                && Saved.Tabs[1].SelectedPaths.Contains(Path.Combine(Second, "Selected.txt")),
                "Periodic recovery snapshots capture window bounds, column widths and selection");
            Check(Probe.PersistWorkspaceNow(), "The current workspace can be flushed before shutdown");
            Probe.RequestExit(); Probe = null;
            // Model an interrupted write and an orphan temporary file. The valid
            // backup must restore the last complete state without running a close handler.
            var Complete = File.ReadAllText(WorkspaceFile);
            DurableJsonStore.Write(WorkspaceFile, Complete);
            File.WriteAllText(WorkspaceFile, "{interrupted");
            File.WriteAllText(WorkspaceFile + ".interrupted.tmp", "{unfinished");
            await Open();
            var Restored = Probe!.ActiveTab!;
            Check(Probe.Tabs.Count == 2 && Probe.Tabs[0].TabAlias == "Kept first" && Restored.TabAlias == "Recovered 中文"
                && SamePath(Restored.CurrentPath, Second) && !Restored.Settings.ShowGridLines,
                "Reopening restores the last complete backup when the main workspace file is interrupted");
            Check(Restored.FileGrid.Columns.Single(Column => Column.SortMemberPath == nameof(FileEntry.Name)).Width.Value == 333
                && Restored.SelectedEntries().Any(Entry => Entry.Name == "Selected.txt")
                && Restored.History.Count >= 2 && Restored.HistoryIndex == Restored.History.Count - 1,
                "Workspace restoration retains columns, selection and navigation history");
            Restored.Settings.ShowHidden = true;
            Check(Restored.SaveSettings(SettingsFile), "Recovery settings use a durable atomic write");
            DurableJsonStore.Write(SettingsFile, File.ReadAllText(SettingsFile));
            File.WriteAllText(SettingsFile, "{partial");
            Check(ReadSettings(SettingsFile).ShowHidden, "A damaged settings file falls back to its complete backup");
            var Backup = File.ReadAllText(SettingsFile + ".bak");
            DurableJsonStore.Write(SettingsFile, JsonSerializer.Serialize(new AppSettings()));
            Check(File.ReadAllText(SettingsFile + ".bak") == Backup,
                "Replacing a corrupt primary preserves its known-good backup");
        }
        finally { Probe?.RequestExit(); }
    }
}
