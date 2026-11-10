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
    private async Task RunWorkspaceStartupSmokeChecksAsync(List<string> Passed)
    {
        const bool RequireActiveFirst = true;
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Workspace startup check failed: " + Name);
            Passed.Add(Name);
        }
        var Root = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "Startup workspace")).FullName;
        var SettingsFile = Path.Combine(Root, "Settings.json");
        // Hold a sidebar read open indefinitely, rather than relying on a slow disk.
        // The content must remain selectable and navigation must supersede recovery.
        var SlowFolder = Directory.CreateDirectory(Path.Combine(Root, "Slow sidebar")).FullName;
        var NextFolder = Directory.CreateDirectory(Path.Combine(Root, "User navigation")).FullName;
        File.WriteAllText(Path.Combine(SlowFolder, "Ready.txt"), "Owned interactive startup fixture");
        var SlowState = new WorkspaceState { Tabs = [new WorkspaceTabState { Settings = new AppSettings
        {
            LastPath = SlowFolder, ExitOnClose = true,
            SidebarTreeLayouts = new() { ["folder-tree"] = new() { ExpandedPaths = [SlowFolder], SelectedPaths = [SlowFolder] } }
        } }] };
        DurableJsonStore.Write(SettingsFile, JsonSerializer.Serialize(SlowState.Tabs[0].Settings));
        var SidebarRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var SlowProbe = new MainWindow(SettingsFile, RestoreWorkspace: true, InitialWorkspace: SlowState)
            { Owner = this.HostWindow, ShowActivated = false, ShowInTaskbar = false };
        var SlowPage = SlowProbe.ActiveTab!;
        SlowPage.Roots.Add(new FolderNode("Slow sidebar", SlowFolder) { LoadTask = SidebarRead.Task });
        try
        {
            SlowProbe.Show();
            var Deadline = Environment.TickCount64 + 10000;
            while (!SlowPage.IsInitialContentReady && Environment.TickCount64 < Deadline) await Task.Delay(10);
            Check(SlowPage.IsInitialContentReady && SlowPage.IsVisible && SlowPage.IsHitTestVisible
                && !SlowPage.IsInitialLayoutReady && !SlowProbe.WorkspaceRestored.IsCompleted,
                "The loaded file panel is visible and interactive while a sidebar read is still pending");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            SlowPage.FileGrid.SelectedItem = SlowPage.Entries.Single(Entry => Entry.Name == "Ready.txt");
            Check(SlowPage.SelectedEntries().Single().Name == "Ready.txt" && !SlowPage.Loading,
                "Files can be selected without waiting for startup sidebar restoration");
            Check(!SlowPage.SyncingTree, "Pending startup reads do not suppress user sidebar navigation events");
            Check(!SlowProbe.PersistWorkspaceNow(), "A partially restored startup is never persisted");
            Check(await SlowPage.NavigateAsync(NextFolder, SyncNavigationTree: false),
                "The user can navigate while startup sidebar reads are pending");
            SidebarRead.TrySetResult();
            await SlowProbe.WorkspaceRestored.WaitAsync(TimeSpan.FromSeconds(10));
            Check(SlowPage.CurrentPath == NextFolder && SlowPage.SelectedEntries().Length == 0
                && SlowProbe.CaptureWorkspace()!.Tabs[0].Settings.LastPath == NextFolder,
                "Late startup completion preserves the user's new folder instead of restoring stale selection or history");
        }
        finally { SidebarRead.TrySetResult(); SlowProbe.RequestExit(); }
        File.WriteAllText(Path.Combine(SlowFolder, "Previously selected.txt"), "Owned saved selection fixture");
        SlowState.Tabs[0].SelectedPaths = [Path.Combine(SlowFolder, "Previously selected.txt")];
        SidebarRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SlowProbe = new MainWindow(SettingsFile, RestoreWorkspace: true, InitialWorkspace: SlowState)
            { Owner = this.HostWindow, ShowActivated = false, ShowInTaskbar = false };
        SlowPage = SlowProbe.ActiveTab!;
        SlowPage.Roots.Add(new FolderNode("Slow sidebar", SlowFolder) { LoadTask = SidebarRead.Task });
        try
        {
            SlowProbe.Show();
            var Deadline = Environment.TickCount64 + 10000;
            while (!SlowPage.IsInitialContentReady && Environment.TickCount64 < Deadline) await Task.Delay(10);
            Check(SlowPage.IsInitialContentReady, "The second slow-sidebar fixture loads its content");
            SlowPage.RaiseEvent(new System.Windows.Input.KeyEventArgs(this.UnmodifiedSmokeKeyboard,
                PresentationSource.FromVisual(SlowProbe), 0, System.Windows.Input.Key.Down)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
            SlowPage.FileGrid.SelectedItem = SlowPage.Entries.Single(Entry => Entry.Name == "Ready.txt");
            SidebarRead.TrySetResult();
            await SlowProbe.WorkspaceRestored.WaitAsync(TimeSpan.FromSeconds(10));
            Check(SlowPage.SelectedEntries().Select(Entry => Entry.Name).SequenceEqual(["Ready.txt"]),
                "A user's selection in the loaded page survives late saved-workspace restoration");
            SlowPage.ShowEntries(Enumerable.Range(0, 2500).Select(Index => new FileEntry
            {
                Name = "Unique startup measurement " + Index + ".txt",
                FullPath = Path.Combine(SlowFolder, "Measurement " + Index + ".txt"), Size = Index
            }).ToArray());
            var InputServiced = false;
            var Input = Dispatcher.InvokeAsync(() => InputServiced = true, DispatcherPriority.Input);
            await SlowPage.FitVisibleColumnsAsync(SlowPage.NavigationVersion);
            Check(InputServiced, "Automatic width measurement services input before all 2500 rows finish measuring");
            await Input;
        }
        finally { SidebarRead.TrySetResult(); SlowProbe.RequestExit(); }
        var State = new WorkspaceState { Width = 1400, Height = 850, ActiveTab = 11 };
        for (var Index = 0; Index < 12; Index++)
        {
            var Folder = Directory.CreateDirectory(Path.Combine(Root, "Folder " + Index.ToString("00"))).FullName;
            for (var FileIndex = 0; FileIndex < 30; FileIndex++) File.WriteAllText(Path.Combine(Folder, "File " + FileIndex.ToString("00") + ".txt"), "Owned startup fixture");
            State.Tabs.Add(new WorkspaceTabState
            {
                Alias = "Startup " + Index, IsPinned = Index % 4 == 0,
                Settings = new AppSettings { LastPath = Folder, ExitOnClose = true, SidebarTreeLayouts = [] },
                History = [Folder], HistoryTreeKeys = ["folder-tree"], HistoryIndex = 0,
                ColumnWidths = new() { [nameof(FileEntry.Name)] = 270 + Index },
                SelectedPaths = [Path.Combine(Folder, "File 00.txt")]
            });
        }
        WorkspacePaneState Leaf(int Start) => new() { Tabs = Enumerable.Range(Start, 4).ToList(), ActiveTab = Start + 3 };
        State.Layout = new WorkspacePaneState { Split = "Horizontal", Ratio = .5, First = Leaf(0),
            Second = new WorkspacePaneState { Split = "Vertical", Ratio = .5, First = Leaf(4), Second = Leaf(8) } };
        DurableJsonStore.Write(SettingsFile, JsonSerializer.Serialize(State.Tabs[0].Settings));
        var Samples = new List<object>();
        MainWindow? Probe = null;
        try
        {
            for (var Trial = 0; Trial < 3; Trial++)
            {
                var Timer = Stopwatch.StartNew();
                double ActiveReady = -1, VisibleReady = -1, LastTick = 0, MaxGap = 0;
                var TabsAtVisibleReady = -1;
                var Ticks = 0;
                var Heartbeat = new DispatcherTimer(DispatcherPriority.Input, this.Dispatcher) { Interval = TimeSpan.FromMilliseconds(5) };
                Heartbeat.Tick += (_, _) =>
                {
                    var Now = Timer.Elapsed.TotalMilliseconds;
                    MaxGap = Math.Max(MaxGap, Now - LastTick); LastTick = Now; Ticks++;
                    if (ActiveReady < 0 && Probe?.ActiveTab is { IsInitialLayoutReady: true } Active
                        && Active.TabPath == State.Tabs[11].Settings.LastPath) ActiveReady = Now;
                    if (VisibleReady < 0 && Probe is { Panes.Count: 3 }
                        && Probe.Panes.All(Pane => Pane.Selected is { IsInitialLayoutReady: true, IsVisible: true }))
                    { VisibleReady = Now; TabsAtVisibleReady = Probe.Tabs.Count; }
                };
                Probe = new MainWindow(SettingsFile, RestoreWorkspace: true, InitialWorkspace: State)
                    { Owner = this.HostWindow, ShowActivated = false, ShowInTaskbar = false };
                var Constructor = Timer.Elapsed.TotalMilliseconds;
                var First = Probe.ActiveTab!;
                Heartbeat.Start();
                try
                {
                    Probe.Show();
                    await First.WaitUntilReadyAsync();
                    var FirstReady = Timer.Elapsed.TotalMilliseconds;
                    if (RequireActiveFirst) Check(First.TabPath == State.Tabs[11].Settings.LastPath,
                        "The first restored content is the last active folder, before other tabs finish loading");
                    await Probe.WorkspaceRestored;
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                    Probe.UpdateLayout();
                    var Complete = Timer.Elapsed.TotalMilliseconds;
                    if (ActiveReady < 0) ActiveReady = Complete;
                    Check(Probe.Tabs.Count == 12 && Probe.Panes.Count == 3 && Probe.ActiveTab == Probe.Tabs[11]
                        && Probe.Tabs.Select(Page => Page.TabAlias).SequenceEqual(State.Tabs.Select(Tab => Tab.Alias)),
                        "Startup restores all twelve tabs in order, three panes and the correct active tab: " + Trial);
                    Check(Probe.Panes.All(Pane => Pane.Selected!.IsVisible && Pane.Selected.IsInitialLayoutReady)
                        && Probe.Tabs.All(Page => Page.IsTabPinned == State.Tabs[Probe.Tabs.IndexOf(Page)].IsPinned)
                        && Probe.Tabs.All(Page => Page.SelectedEntries().Any(Entry => Entry.Name == "File 00.txt")),
                        "Concurrent startup preserves visible pane selections, pin flags and file selections: " + Trial);
                    Check(Probe.PersistWorkspaceNow() && DurableJsonStore.Read<WorkspaceState>(SettingsFile + ".workspace.json")
                        is { Tabs.Count: 12, ActiveTab: 11 }, "Only a complete twelve-tab workspace is saved after restoration: " + Trial);
                    Check(Ticks > 0, "The input dispatcher remains serviced during workspace startup: " + Trial);
                    Check(VisibleReady >= 0 && TabsAtVisibleReady < 12,
                        "All selected panes become visible before the background tabs finish restoring: " + Trial
                        + $"; visible at {VisibleReady} ms with {TabsAtVisibleReady} tabs");
                    Samples.Add(new { trial = Trial, constructor_ms = Constructor, first_ready_ms = FirstReady,
                        active_ready_ms = ActiveReady, visible_ready_ms = VisibleReady, tabs_at_visible_ready = TabsAtVisibleReady,
                        complete_ms = Complete, input_ticks = Ticks, max_input_gap_ms = MaxGap });
                }
                finally { Heartbeat.Stop(); Probe.RequestExit(); Probe = null; }
            }
            var TerminalState = new WorkspaceState { Width = 1400, Height = 850, ActiveTab = 2,
                Tabs = new[] { State.Tabs[0], State.Tabs[4], State.Tabs[11] }
                    .Select(Tab => JsonSerializer.Deserialize<WorkspaceTabState>(JsonSerializer.Serialize(Tab))!).ToList(),
                Layout = new WorkspacePaneState { Split = "Horizontal", Ratio = .5,
                    First = new WorkspacePaneState { Tabs = [0], ActiveTab = 0 },
                    Second = new WorkspacePaneState { Split = "Vertical", Ratio = .5,
                        First = new WorkspacePaneState { Tabs = [1], ActiveTab = 1 },
                        Second = new WorkspacePaneState { Tabs = [2], ActiveTab = 2 } } } };
            TerminalState.Tabs[0].TerminalVisible = true;
            Probe = new MainWindow(SettingsFile, RestoreWorkspace: true, InitialWorkspace: TerminalState)
                { Owner = this.HostWindow, ShowActivated = false, ShowInTaskbar = false };
            Probe.Show(); await Probe.WorkspaceRestored;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Check(Probe.ActiveTab == Probe.Tabs[2] && Probe.Tabs[0].TerminalPanel.Visibility == Visibility.Visible
                && Probe.Tabs[0].TerminalSession is not null,
                "Restoring a terminal in another visible pane does not steal the saved active pane");
            Check(Probe.CaptureWorkspace() is { ActiveTab: 2 } TerminalSaved && TerminalSaved.Tabs[0].TerminalVisible,
                "The restored terminal and active pane are preserved in the next complete snapshot");
            Probe.RequestExit(); Probe = null;
            var BeforeClose = File.ReadAllText(SettingsFile + ".workspace.json");
            Probe = new MainWindow(SettingsFile, RestoreWorkspace: true, InitialWorkspace: State)
                { Owner = this.HostWindow, ShowActivated = false, ShowInTaskbar = false };
            Probe.Show(); await Probe.ActiveTab!.WaitUntilReadyAsync();
            Probe.RequestExit(); await Probe.WorkspaceRestored.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!Probe.IsVisible && File.ReadAllText(SettingsFile + ".workspace.json") == BeforeClose,
                "Closing during startup preserves the previous complete workspace instead of saving a partial restoration");
            Probe = null;
            if (AppMain.ScreenshotPath is { } Output)
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(Output)!, "WorkspaceStartup.json"), JsonSerializer.Serialize(new
                { release_version = typeof(AppMain).Assembly.GetName().Version?.ToString(3), tabs = 12, panes = 3,
                    active_first = RequireActiveFirst, samples = Samples }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { Probe?.RequestExit(); }
    }
}
