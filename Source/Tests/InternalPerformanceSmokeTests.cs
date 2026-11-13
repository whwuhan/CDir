using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CDir.Localization;
using CDir.Models;
using CDir.Services;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunInternalPerformanceSmokeChecksAsync(List<string> Passed)
    {
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Internal performance check failed: " + Name);
            Passed.Add(Name);
        }
        (double Ms, long Bytes) Measure(Action Work)
        {
            var Before = GC.GetAllocatedBytesForCurrentThread(); var Timer = Stopwatch.StartNew(); Work();
            return (Timer.Elapsed.TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - Before);
        }
        var Metrics = new Dictionary<string, object>();
        var Root = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "Internal performance")).FullName;
        var Rows = Enumerable.Range(0, 10000).Select(Index => new FileEntry
        {
            Name = $"Entry {Index:00000}.bin", FullPath = Path.Combine(Root, $"Entry {Index:00000}.bin"),
            Size = (long)Index * 32768, Modified = new DateTime(2026, 1, 1).AddSeconds(Index % 120),
            TypeName = "Repeated type " + Index % 20, Use12HourClock = Index % 2 == 0
        }).ToArray();
        string LegacySize(long Bytes)
        {
            if (Bytes < 1024) return Bytes.ToString("N0", LanguageService.Culture) + " B";
            string[] Units = ["KB", "MB", "GB", "TB", "PB", "EB"];
            double Value = Bytes; var Unit = -1;
            do { Value /= 1024; Unit++; } while (Value >= 1024 && Unit < Units.Length - 1);
            return Value.ToString("0.#", LanguageService.Culture) + " " + Units[Unit];
        }
        string LegacyDate(FileEntry Row) => Row.Modified.ToString(Row.Use12HourClock ? "yyyy/MM/dd hh:mm:ss tt" : "yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture);
        Check(Rows.All(Row => Row.DisplaySize == LegacySize(Row.Size!.Value) && Row.DisplayModified == LegacyDate(Row)),
            "All 10,000 cached metadata strings match the original formatting exactly");
        long BeforeLength = 0, AfterLength = 0;
        var BeforeFormat = Measure(() => { for (var Pass = 0; Pass < 16; Pass++) foreach (var Row in Rows) BeforeLength += LegacySize(Row.Size!.Value).Length + LegacyDate(Row).Length; });
        var AfterFormat = Measure(() => { for (var Pass = 0; Pass < 16; Pass++) foreach (var Row in Rows) AfterLength += Row.DisplaySize.Length + Row.DisplayModified.Length; });
        Check(BeforeLength == AfterLength && AfterFormat.Bytes < BeforeFormat.Bytes / 4,
            "Repeated metadata reads preserve output and remove over 75 percent of formatting allocations");
        Metrics["metadata_formatting"] = new { entries = Rows.Length, passes = 16, baseline_ms = BeforeFormat.Ms, final_ms = AfterFormat.Ms,
            baseline_bytes = BeforeFormat.Bytes, final_bytes = AfterFormat.Bytes };

        var Tree = new FolderNode("Root", Root);
        Tree.Children.Clear();
        for (var Parent = 0; Parent < 100; Parent++)
        {
            var Branch = new FolderNode("Branch " + Parent, Path.Combine(Root, "Branch " + Parent)); Branch.Children.Clear();
            for (var Child = 0; Child < 100; Child++) Branch.Children.Add(new FolderNode("Child " + Child, Path.Combine(Branch.FullPath, "Child " + Child)));
            Tree.Children.Add(Branch);
        }
        static IEnumerable<FolderNode> LegacyVisit(FolderNode Node)
        {
            if (!Node.IsPlaceholder) yield return Node;
            foreach (var Child in Node.Children) foreach (var Descendant in LegacyVisit(Child)) yield return Descendant;
        }
        Check(this.AllVersionNodes([Tree]).SequenceEqual(LegacyVisit(Tree)), "Iterative tree traversal preserves the exact node reference order and placeholder handling");
        var OldCount = 0; var NewCount = 0;
        var BeforeTree = Measure(() => { for (var Pass = 0; Pass < 8; Pass++) OldCount += LegacyVisit(Tree).Count(); });
        var AfterTree = Measure(() => { for (var Pass = 0; Pass < 8; Pass++) NewCount += this.AllVersionNodes([Tree]).Count(); });
        Check(OldCount == NewCount && AfterTree.Bytes < BeforeTree.Bytes / 2,
            "Repeated 10,101-node traversals keep their results while allocating less than half the recursive traversal");
        Metrics["tree_traversal"] = new { nodes = NewCount / 8, passes = 8, baseline_ms = BeforeTree.Ms, final_ms = AfterTree.Ms,
            baseline_bytes = BeforeTree.Bytes, final_bytes = AfterTree.Bytes };

        var Snapshot = new VersionSnapshot(Root, "Git", []);
        bool LegacyCovers(string Input)
        {
            Input = Path.GetFullPath(Input);
            return string.Equals(Snapshot.CoverageRoot, Path.TrimEndingDirectorySeparator(Input), StringComparison.OrdinalIgnoreCase)
                || Input.StartsWith(Snapshot.CoverageRoot.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }
        var OldCovered = 0; var NewCovered = 0;
        var BeforeCoverage = Measure(() => { for (var Pass = 0; Pass < 8; Pass++) foreach (var Row in Rows) if (LegacyCovers(Row.FullPath)) OldCovered++; });
        var AfterCoverage = Measure(() => { for (var Pass = 0; Pass < 8; Pass++) foreach (var Row in Rows) if (Snapshot.Covers(Row.FullPath)) NewCovered++; });
        Check(OldCovered == NewCovered && AfterCoverage.Bytes < BeforeCoverage.Bytes,
            "Repository coverage keeps identical boundaries without allocating the same root prefix for every file");
        Metrics["repository_coverage"] = new { paths = Rows.Length, passes = 8, baseline_ms = BeforeCoverage.Ms, final_ms = AfterCoverage.Ms,
            baseline_bytes = BeforeCoverage.Bytes, final_bytes = AfterCoverage.Bytes };

        var Config = Path.Combine(Root, "Settings.json");
        File.WriteAllText(Config, JsonSerializer.Serialize(new AppSettings { LastPath = Root, ExitOnClose = true }));
        var Probe = new MainWindow(Config, RestoreWorkspace: true) { Owner = this.HostWindow, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            Probe.Show(); await Probe.WorkspaceRestored;
            var Page = Probe.ActiveTab!; Page.ShowEntries(Rows);
            var Column = Page.FileGrid.Columns.Single(Item => Item.SortMemberPath == nameof(FileEntry.TypeName));
            var Typeface = new Typeface(Page.FileGrid.FontFamily, Page.FileGrid.FontStyle, Page.FileGrid.FontWeight, Page.FileGrid.FontStretch);
            var Dpi = VisualTreeHelper.GetDpi(Page.FileGrid).PixelsPerDip;
            double TextWidth(string Value, double FontSize) => new FormattedText(Value, LanguageService.Culture, Page.FileGrid.FlowDirection, Typeface, FontSize, Brushes.Black, Dpi).WidthIncludingTrailingWhitespace;
            var Header = TextWidth(Convert.ToString(Column.Header, LanguageService.Culture) ?? "", (double)Page.FindResource("UIFontHeaders")) + 39;
            var LegacyWidth = Header;
            var BeforeFit = Measure(() => { foreach (var Row in Page.FileGrid.Items.OfType<FileEntry>()) LegacyWidth = Math.Max(LegacyWidth, TextWidth(Row.TypeName, Page.FileGrid.FontSize) + 21); });
            var AfterFit = Measure(() => Page.AutoFitColumn(Column));
            Check(Column.Width.Value == Math.Min(Column.MaxWidth, Math.Ceiling(LegacyWidth)) && AfterFit.Bytes < BeforeFit.Bytes / 4,
                "Fitting 10,000 repeated type labels retains the exact column width with over 75 percent fewer measurement allocations");
            Metrics["column_fit"] = new { entries = Rows.Length, distinct_labels = 20, baseline_ms = BeforeFit.Ms, final_ms = AfterFit.Ms,
                baseline_bytes = BeforeFit.Bytes, final_bytes = AfterFit.Bytes, expected_width = Math.Ceiling(LegacyWidth), final_width = Column.Width.Value };
        }
        finally { Probe.RequestExit(); }
        if (AppMain.ScreenshotPath is { } Output)
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(Output)!, "InternalPerformance.json"), JsonSerializer.Serialize(Metrics, new JsonSerializerOptions { WriteIndented = true }));
    }
}
