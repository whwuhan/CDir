using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CDir.Localization;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunPerformanceSmokeChecksAsync(List<string> Passed)
    {
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Performance regression check failed: " + Name);
            Passed.Add(Name);
        }
        var Root = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "Performance")).FullName;
        var Config = Path.Combine(Root, "Settings.json");
        File.WriteAllText(Config, JsonSerializer.Serialize(new AppSettings { LastPath = Root, ExitOnClose = true, CalculateFolderSizes = false }));
        var Entries = Enumerable.Range(0, 10000).Select(Index => new FileEntry
        {
            Name = $"File {Index:00000}.ext", FullPath = Path.Combine(Root, $"File {Index:00000}.ext"),
            TypeKey = "TypeExtensionFile", TypeArguments = [$"EXT{Index % 20:00}"], Size = Index, Modified = new DateTime(2026, 1, 1)
        }).Reverse().ToArray();
        var Measurements = new Dictionary<string, object>();
        (double Ms, long Bytes) Measure(Action Action)
        {
            var Before = GC.GetAllocatedBytesForCurrentThread();
            var Timer = Stopwatch.StartNew(); Action(); Timer.Stop();
            return (Timer.Elapsed.TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - Before);
        }
        var Comparison = LanguageService.Culture.CompareInfo;
        int Compare(FileEntry A, FileEntry B, bool Cached)
        {
            var Order = Comparison.Compare(Cached ? A.TypeName : LanguageService.Text(A.TypeKey!, A.TypeArguments),
                Cached ? B.TypeName : LanguageService.Text(B.TypeKey!, B.TypeArguments));
            return Order != 0 ? Order : StringComparer.Ordinal.Compare(A.Name, B.Name);
        }
        foreach (var Entry in Entries) _ = Entry.TypeName;
        FileEntry[] BaselineRows = [], OptimizedRows = [];
        var BeforeSort = Measure(() => { BaselineRows = Entries.ToArray(); Array.Sort(BaselineRows, (A, B) => Compare(A, B, false)); });
        var AfterSort = Measure(() => { OptimizedRows = Entries.ToArray(); Array.Sort(OptimizedRows, (A, B) => Compare(A, B, true)); });
        Check(BaselineRows.SequenceEqual(OptimizedRows) && AfterSort.Bytes < BeforeSort.Bytes / 4,
            "10,000-entry type sorting preserves exact order while reducing warmed formatting allocations by over 75 percent");
        Measurements["type_sort"] = new { entries = Entries.Length, baseline_ms = BeforeSort.Ms, optimized_ms = AfterSort.Ms, baseline_bytes = BeforeSort.Bytes, optimized_bytes = AfterSort.Bytes };
        var NewRows = Entries.Take(8000).ToArray();
        var BeforeMerge = new ObservableCollection<FileEntry>();
        var SlowMerge = Measure(() =>
        {
            for (var Index = 0; Index < NewRows.Length; Index++)
            {
                var Previous = BeforeMerge.IndexOf(NewRows[Index]);
                if (Previous >= 0) BeforeMerge.Move(Previous, Index); else BeforeMerge.Insert(Index, NewRows[Index]);
            }
        });
        var AfterMerge = new ObservableCollection<FileEntry>();
        var FastMerge = Measure(() => ReconcileItems(AfterMerge, NewRows));
        Check(BeforeMerge.SequenceEqual(AfterMerge), "8,000 newly generated entries reconcile in the same order without scanning for each new row");
        Measurements["new_row_reconciliation"] = new { entries = NewRows.Length, baseline_ms = SlowMerge.Ms, optimized_ms = FastMerge.Ms };
        var Probe = new MainWindow(Config, RestoreWorkspace: true) { Owner = this.HostWindow, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            Probe.Show(); await Probe.WorkspaceRestored; var Page = Probe.ActiveTab!; await Page.WaitUntilReadyAsync();
            Page.ShowEntries(Entries); Page.UpdateCounts();
            var BeforeCounts = Measure(() =>
            {
                for (var Index = 0; Index < 128; Index++)
                {
                    var Rows = Page.View!.Cast<FileEntry>().ToArray();
                    _ = Rows.Count(Entry => Entry.IsDirectory); _ = Rows.Count(Entry => !Entry.IsDirectory);
                }
            });
            var AfterCounts = Measure(() => { for (var Index = 0; Index < 128; Index++) Page.UpdateCounts(); });
            Check(Page.VisibleFileCount == Entries.Length && Page.VisibleDirectoryCount == 0 && AfterCounts.Bytes < BeforeCounts.Bytes / 4,
                "Repeated status updates reuse visible counts and reduce full-list snapshot allocations by over 75 percent");
            Measurements["status_counts"] = new { entries = Entries.Length, updates = 128, baseline_ms = BeforeCounts.Ms, optimized_ms = AfterCounts.Ms, baseline_bytes = BeforeCounts.Bytes, optimized_bytes = AfterCounts.Bytes };
            var Items = (ObservableCollection<FileEntry>)Page.Entries;
            var Folder = new FileEntry { Name = "Folder", FullPath = Path.Combine(Root, "Folder"), IsDirectory = true, TypeKey = "FolderType" };
            Items.Add(Folder); Page.UpdateCounts();
            Check(Page.VisibleFileCount == 10001 && Page.VisibleDirectoryCount == 1, "Visible count cache updates after additions");
            Page.CurrentSearch = SearchQuery.Parse("/file"); Page.View!.Refresh(); Page.UpdateCounts();
            Check(Page.VisibleFileCount == 10000 && Page.VisibleDirectoryCount == 0, "Visible count cache updates after filter resets");
            Page.CurrentSearch = SearchQuery.Parse(""); Page.View.Refresh(); Items.Remove(Folder); Page.UpdateCounts();
            Check(Page.VisibleFileCount == 10000 && Page.VisibleDirectoryCount == 0, "Visible count cache updates after removals and clearing filters");
            Page.ShowEntries([Folder]);
            Check(Page.VisibleFileCount == 1 && Page.VisibleDirectoryCount == 1, "Replacing the view detaches the old count subscription and recounts the new view");
        }
        finally { Probe.RequestExit(); }
        if (AppMain.ScreenshotPath is { } Output)
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(Output)!, "Performance.json"), JsonSerializer.Serialize(Measurements, new JsonSerializerOptions { WriteIndented = true }));
    }
}
