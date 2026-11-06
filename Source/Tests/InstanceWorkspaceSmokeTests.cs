using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using CDir.Models;
using CDir.Services;

namespace CDir.UI;

public partial class ExplorerTab
{
    private sealed class WorkspaceReadContext : SynchronizationContext
    {
        internal int Posts;
        public override void Post(SendOrPostCallback Callback, object? State)
        {
            Interlocked.Increment(ref this.Posts);
            ThreadPool.QueueUserWorkItem(_ => Callback(State));
        }
    }

    private async Task RunInstanceWorkspaceSmokeChecksAsync(List<string> Passed)
    {
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Instance workspace check failed: " + Name);
            Passed.Add(Name);
        }
        var Root = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "Instances-" + Guid.NewGuid().ToString("N"))).FullName;
        var Folder = Directory.CreateDirectory(Path.Combine(Root, "Tree", "Expanded", "Nested")).FullName;
        var SettingsFile = Path.Combine(Root, "Settings.json");
        DurableJsonStore.Write(SettingsFile, JsonSerializer.Serialize(new AppSettings { LastPath = Folder }));
        MainWindow? A = null, B = null;
        try
        {
            Check(InstanceWorkspaceService.Peers().Length == 0 && !InstanceWorkspaceService.IsUserInstance(Environment.ProcessId),
                "UI checks do not enumerate user windows or advertise themselves as a user instance, even from the published executable");
            using (var Forbidden = new InstanceWorkspaceService(() => Task.FromResult<WorkspaceState?>(null)))
            {
                var Rejected = false;
                try { Forbidden.Start(RegisterUserInstance: true); }
                catch (InvalidOperationException) { Rejected = true; }
                Check(Rejected && !InstanceWorkspaceService.IsUserInstance(Environment.ProcessId),
                    "A UI-check process cannot accidentally register a user workspace or pause user autosaving");
            }
            var Outside = Path.GetFullPath(Path.Combine(AppMain.SmokeRoot!, "..", "UnownedSettings-" + Guid.NewGuid().ToString("N") + ".json"));
            var WriteRejected = false;
            try { DurableJsonStore.Write(Outside, "{}"); }
            catch (IOException) { WriteRejected = true; }
            Check(WriteRejected && !File.Exists(Outside) && !File.Exists(Outside + ".bak"),
                "Test configuration writes outside the owned app scope fail before creating files or backups");
            var ReadRejected = false;
            try { DurableJsonStore.ReadText(Outside); }
            catch (IOException Error) { ReadRejected = Error.Message.Contains("owned UI-check scope"); }
            Check(ReadRejected, "Test settings cannot read a user's configuration through the durable settings store");
            A = new MainWindow(SettingsFile, RestoreWorkspace: true) { Owner = this.HostWindow, ShowActivated = false, ShowInTaskbar = false };
            A.Show(); await A.ActiveTab!.WaitUntilReadyAsync(); await A.WorkspaceRestored;
            var Page = A.ActiveTab!;
            Page.TryAddSidebarCategory("Clone Category");
            Page.AddFoldersToCategory(Page.CustomCategories[0], [new FileEntry { FullPath = Path.Combine(Root, "Tree"), IsDirectory = true }]);
            var CategoryRoot = Page.CustomCategories[0].Folders[0].Node;
            await Page.EnsureChildrenAsync(CategoryRoot);
            CategoryRoot.IsExpanded = true;
            var Expanded = CategoryRoot.Children.Single(Node => Node.Name == "Expanded");
            await Page.EnsureChildrenAsync(Expanded);
            Expanded.IsExpanded = true;
            CategoryRoot.IsExpanded = false; // Retain a hidden expanded descendant.
            Page.SetTabAlias("Live unsaved tab");
            Page.FileGrid.Columns[0].Width = new DataGridLength(287);
            var Extra = A.AddTab(); await Extra.WaitUntilReadyAsync();
            Extra.SetTabAlias("Second live tab"); A.SelectTab(Page);
            A.MultiInstanceDetected = true;
            var Baseline = File.ReadAllText(SettingsFile);
            var BaselineWorkspace = File.ReadAllText(SettingsFile + ".workspace.json");
            Page.ChangeGridLines(false);
            Check(!A.PersistWorkspaceNow() && File.ReadAllText(SettingsFile) == Baseline
                && File.ReadAllText(SettingsFile + ".workspace.json") == BaselineWorkspace,
                "Overlapping instances keep settings and recovery snapshots private until exit approval");
            using (var Server = new InstanceWorkspaceService(() => Task.FromResult(A.CaptureWorkspace())))
            {
                Server.Start();
                Check(!InstanceWorkspaceService.IsUserInstance(Environment.ProcessId),
                    "An owned test workspace pipe remains excluded from user-instance discovery");
                var Live = await InstanceWorkspaceService.ReadAsync(Environment.ProcessId);
                Check(Live is { Tabs.Count: 2, ActiveTab: 0 } && Live.Tabs[0].Alias == "Live unsaved tab" && !Live.Tabs[0].Settings.ShowGridLines,
                    "The instance pipe transfers the full current unsaved workspace independently of shared files");
                var Context = new WorkspaceReadContext();
                var PreviousContext = SynchronizationContext.Current;
                Task<WorkspaceState?> StartupRead;
                SynchronizationContext.SetSynchronizationContext(Context);
                try { StartupRead = InstanceWorkspaceService.ReadAsync(Environment.ProcessId); }
                finally { SynchronizationContext.SetSynchronizationContext(PreviousContext); }
                Check(await StartupRead is { Tabs.Count: 2 } && Context.Posts == 0,
                    "Synchronous startup clients can read live snapshots without waiting for their own UI synchronization context");
                B = new MainWindow(SettingsFile, RestoreWorkspace: true, InitialWorkspace: Live)
                    { Owner = this.HostWindow, ShowActivated = false, ShowInTaskbar = false };
                B.MultiInstanceDetected = true;
                B.Show(); await B.ActiveTab!.WaitUntilReadyAsync(); await B.WorkspaceRestored;
                var Cloned = B.ActiveTab!;
                var ClonedRoot = Cloned.CustomCategories[0].Folders[0].Node;
                Check(B.Tabs.Count == 2 && B.Tabs[1].TabAlias == "Second live tab" && Cloned.TabAlias == Page.TabAlias
                    && SamePath(Cloned.CurrentPath, Folder) && !Cloned.Settings.ShowGridLines
                    && Cloned.FileGrid.Columns[0].Width.Value == 287 && !ClonedRoot.IsExpanded
                    && ClonedRoot.Children.Single(Node => Node.Name == "Expanded").IsExpanded,
                    "A new instance restores tabs, configuration, column widths and expanded descendants inside collapsed tree branches");
            }
            // A peer has now saved a genuinely different configuration while both windows were open.
            var External = ReadSettings(SettingsFile);
            External.CopyOnDrop = !B.ActiveTab!.Settings.CopyOnDrop;
            DurableJsonStore.Write(SettingsFile, JsonSerializer.Serialize(External));
            Baseline = File.ReadAllText(SettingsFile);
            var Questions = 0;
            B.ConfirmWorkspaceSave = () => { Questions++; return MessageBoxResult.Cancel; };
            B.RequestExit();
            Check(B.IsVisible && Questions == 1 && File.ReadAllText(SettingsFile) == Baseline,
                "Cancel in the multiple-instance exit prompt preserves the open window and shared configuration");
            B.ConfirmWorkspaceSave = () => { Questions++; return MessageBoxResult.No; };
            B.RequestExit(); B = null;
            Check(Questions == 2 && File.ReadAllText(SettingsFile) == Baseline
                && File.ReadAllText(SettingsFile + ".workspace.json") == BaselineWorkspace,
                "Exit without saving leaves shared settings and workspace unchanged, including the Closed handler");
            A.ConfirmWorkspaceSave = () => MessageBoxResult.Yes;
            A.RequestExit(); A = null;
            Check(!ReadSettings(SettingsFile).ShowGridLines
                && DurableJsonStore.Read<WorkspaceState>(SettingsFile + ".workspace.json") is { Tabs.Count: 2, ActiveTab: 0 } Saved
                && Saved.Tabs[0].Alias == "Live unsaved tab",
                "Explicit save on exit publishes both the chosen instance configuration and complete workspace");
        }
        finally
        {
            foreach (var Window in new[] { B, A }.OfType<MainWindow>())
            { Window.ConfirmWorkspaceSave = () => MessageBoxResult.No; Window.RequestExit(); }
        }
    }
}
