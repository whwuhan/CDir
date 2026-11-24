using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunSearchColumnFitSmokeAsync(List<string> Passed)
    {
        void Check(bool Value, string Message)
        {
            if (!Value) throw new InvalidOperationException("Search column fit: " + Message);
            Passed.Add(Message);
        }
        var Root = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "Search width regression")).FullName;
        var Nested = Directory.CreateDirectory(Path.Combine(Root, "nested folder with a long path")).FullName;
        var Match = Path.Combine(Nested, "Unique search marker with a descriptive Unicode 文件名.txt");
        File.WriteAllText(Match, "Owned recursive search result");
        for (var Index = 0; Index < 80; Index++) File.WriteAllText(Path.Combine(Root, $"Ordinary {Index:000}.txt"), "Owned fixture");
        var SettingsFile = Path.Combine(Root, "Settings.json");
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(new AppSettings { LastPath = Root, ExitOnClose = true }));
        var Probe = new MainWindow(SettingsFile) { Owner = this.HostWindow, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            Probe.Show(); await Probe.ActiveTab!.WaitUntilReadyAsync();
            var Page = Probe.ActiveTab!;
            async Task Settle() { await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); Page.UpdateLayout(); }
            await Page.NavigateAsync(Root, SyncNavigationTree: false);
            Page.SearchBox.Text = "Unique search marker";
            await Page.WaitForSearchSmokeAsync();
            var Result = Page.View!.Cast<FileEntry>().Single();
            Check(Result.FullPath == Match && Page.FileGrid.Items.Count == 1, "A descendant-only result is bound before fitting");
            var Name = Page.FileGrid.Columns.Single(Column => Column.SortMemberPath == nameof(FileEntry.Name));
            await Settle();
            var BeforeFitCell = Name.GetCellContent(Result) is not null;
            Page.CaptureAddressBreadcrumbPreview("SearchResultsBeforeFit.png");
            Name.Width = 5000; Page.SearchPathColumn.Width = 6000;
            await Settle();
            var Viewer = FindVisualChild<ScrollViewer>(Page.FileGrid)!;
            Viewer.ScrollToHorizontalOffset(8000);
            await Settle();
            Page.FileGrid.SelectedItem = Result;
            var View = Page.View;
            var SearchVersion = Page.SearchVersion;
            await Page.HandleShortcutAsync(Key.E, ModifierKeys.Alt, InText: true, InFileList: false);
            await Settle();
            var Cell = Name.GetCellContent(Result);
            var Bounds = Cell is null ? Rect.Empty : new Rect(Cell.TranslatePoint(new Point(), Page.FileGrid), Cell.RenderSize);
            var Presenter = FindVisualChild<CDir.Controls.FileListRowsPresenter>(Page.FileGrid);
            Check(Presenter is not null && ((IScrollInfo)Presenter).ViewportWidth > 0,
                "An initially empty recursive result keeps the DataGrid row host and a nonzero column viewport");
            if (AppMain.ScreenshotPath is { } Output)
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(Output)!, "SearchFitRegression.json"), JsonSerializer.Serialize(new
                { horizontal = Viewer.HorizontalOffset, scrollable = Viewer.ScrollableWidth, grid_items = Page.FileGrid.Items.Count,
                    bound = Page.FileGrid.ItemsSource is not null, name_width = Name.ActualWidth, cell_bounds = Bounds.ToString(),
                    before_fit_cell = BeforeFitCell, row_host = Presenter?.GetType().Name,
                    viewport_width = Presenter is null ? 0 : ((IScrollInfo)Presenter).ViewportWidth }));
            Page.CaptureAddressBreadcrumbPreview("SearchResultsAfterFit.png");
            Check(ReferenceEquals(View, Page.View) && ReferenceEquals(Page.FileGrid.ItemsSource, Page.View)
                && Page.SearchVersion == SearchVersion && Page.FileGrid.Items.Count == 1
                && Page.SelectedEntries().Single().FullPath == Match,
                "Manual fitting preserves the recursive result view query generation and selection");
            Check(Viewer.HorizontalOffset <= Viewer.ScrollableWidth + .5 && Cell is { IsVisible: true }
                && Bounds.IntersectsWith(new Rect(Page.FileGrid.RenderSize)),
                "A recursive result remains rendered after fitting shrinks horizontally scrolled columns");
            Viewer.ScrollToRightEnd(); await Settle();
            Check(Page.SearchPathColumn.GetCellContent(Result) is TextBlock { IsVisible: true, Text.Length: > 0 },
                "The fitted Absolute Path column remains realized when scrolling back to the right");
            Viewer.ScrollToLeftEnd(); await Settle();
            await Page.RefreshAndFitColumnsAsync(); await Page.WaitForSearchSmokeAsync(); await Settle();
            Check(Page.View!.Cast<FileEntry>().Single().FullPath == Match && Page.FileGrid.ItemsSource is not null,
                "Manual refresh retains descendant-only results after refitting");
            Page.Settings.AutoFitColumnsOnNavigation = true;
            Name.Width = 777; Page.SearchPathColumn.Width = 50;
            Page.SearchBox.Text = "descriptive Unicode";
            await Page.WaitForSearchSmokeAsync(); await Settle();
            Check(Name.ActualWidth < 777 && Page.SearchPathColumn.ActualWidth > 200
                && Page.FileGrid.Items.Count == 1 && Page.View!.Cast<FileEntry>().Single().FullPath == Match,
                "Completed recursive search automatically fits both Name and Absolute Path without losing results");
            await Page.RefreshAndFitColumnsAsync(); await Page.WaitForSearchSmokeAsync(); await Settle();
            Check(Page.FileGrid.Items.Count == 1 && Name.GetCellContent(Page.View!.Cast<FileEntry>().Single()) is { IsVisible: true }
                && Page.SearchBox.Text == "descriptive Unicode", "F5 with automatic fitting retains and renders descendant-only matches");
            Page.Settings.AutoFitColumnsOnNavigation = false;
            Name.Width = 777;
            Page.SearchBox.Text = "search marker"; await Page.WaitForSearchSmokeAsync(); await Settle();
            Check(Math.Abs(Name.ActualWidth - 777) < 1, "Search completion preserves personal widths when automatic fitting is disabled");
            Page.Settings.AutoFitColumnsOnNavigation = true;
            Page.SearchBox.Text = "No matching result"; await Page.WaitForSearchSmokeAsync(); await Settle();
            Check(Page.FileGrid.Items.Count == 0 && Name.ActualWidth < 777,
                "An empty completed search fits visible headers without detaching the result view");
            Page.SearchBox.Text = "/file";
            var OldSearch = Page.SearchTask;
            Page.SearchBox.Text = "Unique search marker";
            await OldSearch; await Page.WaitForSearchSmokeAsync(); await Settle();
            Check(Page.FileGrid.Items.Count == 1 && Page.View!.Cast<FileEntry>().Single().FullPath == Match
                && Name.GetCellContent(Page.View!.Cast<FileEntry>().Single()) is { IsVisible: true },
                "Replacing a query cancels stale sizing and leaves the latest result visibly bound");
            Page.CaptureAddressBreadcrumbPreview("SearchResultsAutomaticFit.png");
        }
        finally { Probe.RequestExit(); }
    }
}
