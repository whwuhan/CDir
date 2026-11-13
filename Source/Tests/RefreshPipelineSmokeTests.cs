using System.Collections.Specialized;
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
    private async Task RunRefreshPipelineSmokeChecksAsync(List<string> Passed)
    {
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Refresh pipeline check failed: " + Name);
            Passed.Add(Name);
        }
        var Root = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "Refresh pipeline")).FullName;
        var Config = Path.Combine(Root, "Settings.json");
        File.WriteAllText(Config, JsonSerializer.Serialize(new AppSettings { LastPath = Root, ExitOnClose = true, CalculateFolderSizes = false }));
        var Probe = new MainWindow(Config, RestoreWorkspace: true) { Owner = this.HostWindow, ShowActivated = false, ShowInTaskbar = false };
        var Metrics = new Dictionary<string, object>();
        try
        {
            Probe.Show(); await Probe.WorkspaceRestored;
            var Page = Probe.ActiveTab!;
            var Rows = Enumerable.Range(0, 10000).Select(Index => new FileEntry
            {
                Name = $"File {Index:00000}.txt", FullPath = Path.Combine(Root, $"File {Index:00000}.txt"),
                Size = Index, Modified = new DateTime(2026, 1, 1)
            }).ToArray();
            Page.ShowEntries(Rows);
            var Changes = 0;
            foreach (var Row in Rows) Row.PropertyChanged += (_, _) => Changes++;
            ((INotifyCollectionChanged)Page.Entries).CollectionChanged += (_, _) => Changes++;
            using var ExistingScan = new CancellationTokenSource();
            Page.FolderSizeCancellation = ExistingScan;
            var Timer = Stopwatch.StartNew();
            var Allocated = GC.GetAllocatedBytesForCurrentThread();
            for (var Index = 0; Index < 16; Index++) Page.RefreshFileRows(Rows);
            Metrics["unchanged_refresh"] = new { files = Rows.Length, refreshes = 16, elapsed_ms = Timer.Elapsed.TotalMilliseconds,
                allocated_bytes = GC.GetAllocatedBytesForCurrentThread() - Allocated, notifications = Changes };
            Check(Changes == 0 && !ExistingScan.IsCancellationRequested && Page.Entries.SequenceEqual(Rows),
                "Sixteen unchanged 10,000-file refreshes retain every row without notifications or canceling in-flight folder totals");
            Page.FolderSizeCancellation = null;
            var Updated = Rows.ToArray();
            Updated[321] = new FileEntry { Name = Rows[321].Name, FullPath = Rows[321].FullPath, Size = 123456, Modified = DateTime.Now };
            Page.RefreshFileRows(Updated);
            Check(Rows[321].Size == 123456 && ReferenceEquals(Page.Entries[321], Rows[321]) && Changes > 0,
                "An actual metadata change still updates its existing bound row");
            var Snapshot = new VersionSnapshot(Root, "Git", []);
            foreach (var Row in Rows) Snapshot.Set(Row.Name, Row.Size % 10 == 0 ? VersionState.Modified : VersionState.Clean);
            Snapshot.Aggregate(); Page.VersionSnapshots = [Snapshot];
            Allocated = GC.GetAllocatedBytesForCurrentThread(); Timer.Restart();
            Page.ApplyVersionSnapshots();
            var ColdBytes = GC.GetAllocatedBytesForCurrentThread() - Allocated;
            var ColdMs = Timer.Elapsed.TotalMilliseconds;
            Allocated = GC.GetAllocatedBytesForCurrentThread(); Timer.Restart();
            for (var Index = 0; Index < 16; Index++) Page.ApplyVersionSnapshots();
            var WarmBytes = GC.GetAllocatedBytesForCurrentThread() - Allocated;
            Metrics["cached_badges"] = new { files = Rows.Length, repeats = 16, cold_ms = ColdMs, cold_bytes = ColdBytes,
                warm_total_ms = Timer.Elapsed.TotalMilliseconds, warm_bytes = WarmBytes };
            Check(WarmBytes < ColdBytes / 4 && Rows.All(Row => Row.VersionStatus.State == (Row.Size % 10 == 0 ? VersionState.Modified : VersionState.Clean)),
                "Repeated 10,000-file badge application reuses exact statuses and allocates less than one quarter of the first pass"
                + $" (first={ColdBytes}, repeated={WarmBytes})");
            Snapshot = new VersionSnapshot(Root, "SVN", []); Snapshot.Set(Rows[321].Name, VersionState.Conflict); Snapshot.Aggregate();
            Page.VersionSnapshots = [Snapshot]; Page.ApplyVersionSnapshots();
            Check(Rows[321].VersionStatus.State == VersionState.Conflict && Rows[320].VersionStatus.State == VersionState.None,
                "Replacing a repository snapshot immediately invalidates cached badges, including stale clean states");
            var Dispatches = Page.RefreshDispatchCount;
            for (var Index = 0; Index < 10000; Index++) Page.ScheduleRefresh();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Check(Page.RefreshDispatchCount - Dispatches == 1 && Page.RefreshTimer.IsEnabled,
                "10,000 filesystem notifications queue one background dispatcher callback");
            Page.RefreshTimer.Stop();
            await Page.NavigateAsync(Root, SyncNavigationTree: false);
            var Live = Path.Combine(Root, "Continuous writes.txt");
            var RefreshedDuringWrites = false;
            for (var Wave = 1; Wave <= 30; Wave++)
            {
                File.WriteAllText(Live, new string('x', Wave)); Page.ScheduleRefresh();
                await Task.Delay(60);
                if (Wave < 30 && Page.Entries.Any(Row => Row.FullPath == Live && Row.Size > 0)) RefreshedDuringWrites = true;
            }
            Check(RefreshedDuringWrites, "Continuous writes become visible before the writer stops instead of starving the refresh timer");
        }
        finally { Probe.RequestExit(); }
        if (AppMain.ScreenshotPath is { } Output)
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(Output)!, "RefreshPipelinePerformance.json"), JsonSerializer.Serialize(Metrics, new JsonSerializerOptions { WriteIndented = true }));
    }
}
