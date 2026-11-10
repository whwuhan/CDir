using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunFileListScrollTailSmokeChecksAsync(List<string> Passed)
    {
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("File-list scroll tail check failed: " + Name);
            Passed.Add(Name);
        }
        var Root = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "Scroll tail")).FullName;
        var Folder = Directory.CreateDirectory(Path.Combine(Root, "Files")).FullName;
        var Empty = Directory.CreateDirectory(Path.Combine(Root, "Empty")).FullName;
        for (var Index = 0; Index < 80; Index++) File.WriteAllText(Path.Combine(Folder, $"Item {Index:000}.txt"), "Owned scroll fixture");
        var SettingsFile = Path.Combine(Root, "Settings.json");
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(new AppSettings { LastPath = Folder, ExitOnClose = true }));
        var Probe = new MainWindow(SettingsFile) { Owner = this.HostWindow, ShowActivated = false, ShowInTaskbar = false,
            Width = 1000, Height = 620 };
        async Task Settle()
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Probe.UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        }
        try
        {
            Probe.Show();
            var Page = Probe.ActiveTab!;
            await Page.WaitUntilReadyAsync(); await Settle();
            var Viewer = FindVisualChild<ScrollViewer>(Page.FileGrid)!;
            var Viewport = FindVisualChild<ScrollContentPresenter>(Viewer)!;
            async Task EndAtTop(string Case)
            {
                Viewer.ScrollToTop(); await Settle();
                var Last = Page.FileGrid.Items[Page.FileGrid.Items.Count - 1];
                var Metrics = (Viewer.ExtentHeight, Viewer.ViewportHeight, Viewer.ScrollableHeight);
                var Bar = Viewer.Template.FindName("PART_VerticalScrollBar", Viewer) as ScrollBar;
                var Thumb = Bar is null ? null : FindVisualChild<Track>(Bar)?.Thumb;
                var ThumbHeight = Thumb?.ActualHeight;
                Check(Viewer.ComputedVerticalScrollBarVisibility == Visibility.Visible && Thumb is { ActualHeight: > 0 }
                    && Math.Abs(Viewer.ScrollableHeight - (Page.FileGrid.Items.Count - 1)) < .01,
                    Case + ": the complete trailing-space range and scrollbar are available before scrolling");
                var StableScrollbar = true;
                for (var Step = 1; Step <= 20; Step++)
                {
                    var Target = (Page.FileGrid.Items.Count - 1) * Step / 20.0;
                    Viewer.ScrollToVerticalOffset(Target); await Settle();
                    StableScrollbar &= Metrics == (Viewer.ExtentHeight, Viewer.ViewportHeight, Viewer.ScrollableHeight)
                        && Thumb?.ActualHeight == ThumbHeight && Math.Abs(Viewer.VerticalOffset - Target) < .01;
                }
                var Row = Page.FileGrid.ItemContainerGenerator.ContainerFromItem(Last) as DataGridRow;
                Check(Row is not null && Math.Abs(Row.TranslatePoint(new Point(), Viewport).Y) <= 1.5 && StableScrollbar,
                    Case + ": dragging through the full range reaches the last row at the top without changing thumb length");
                Viewer.ScrollToTop(); await Settle();
                var Presenter = FindVisualChild<CDir.Controls.FileListRowsPresenter>(Page.FileGrid)!;
                var PreviousOffset = Viewer.VerticalOffset;
                var Monotonic = true;
                for (var Tick = 0; Tick < 50; Tick++)
                {
                    Presenter.MouseWheelDown();
                    await Settle();
                    Monotonic &= Viewer.VerticalOffset >= PreviousOffset && Viewer.VerticalOffset <= Page.FileGrid.Items.Count - 1;
                    StableScrollbar &= Metrics == (Viewer.ExtentHeight, Viewer.ViewportHeight, Viewer.ScrollableHeight)
                        && Thumb?.ActualHeight == ThumbHeight;
                    PreviousOffset = Viewer.VerticalOffset;
                }
                Row = Page.FileGrid.ItemContainerGenerator.ContainerFromItem(Last) as DataGridRow;
                Check(Row is not null && Math.Abs(Row.TranslatePoint(new Point(), Viewport).Y) <= 1.5,
                    Case + ": wheel scrolling aligns the final real row with the top of the file viewport");
                Check(Monotonic && StableScrollbar,
                    Case + ": fifty downward wheel steps never bounce or change scrollbar range or thumb size");
                var Offset = Viewer.VerticalOffset;
                Viewer.LineDown(); Viewer.PageDown(); await Settle();
                Check(Math.Abs(Viewer.VerticalOffset - Offset) < .01 && Page.FileGrid.Items.OfType<FileEntry>().Count() == Page.FileGrid.Items.Count,
                    Case + $": further downward scrolling stops without adding selectable placeholder items (offset {Offset} -> {Viewer.VerticalOffset})");
                var Blank = Page.FileGrid.InputHitTest(Viewport.TranslatePoint(new Point(30, Viewport.ActualHeight - 15), Page.FileGrid)) as DependencyObject;
                Check(FindAncestor<DataGridRow>(Blank) is null, Case + ": the space below the final row is an actual blank hit area");
                Presenter.MouseWheelUp(); await Settle();
                Check(Viewer.VerticalOffset < Offset && Thumb?.ActualHeight == ThumbHeight,
                    Case + ": upward wheel scrolling moves the scrollbar through the same fixed range");
                Viewer.ScrollToEnd(); await Settle();
                Check(Math.Abs(((DataGridRow)Page.FileGrid.ItemContainerGenerator.ContainerFromItem(Last)).TranslatePoint(new Point(), Viewport).Y) <= 1.5,
                    Case + ": the normal scrollbar end command also places the last row at the top");
            }
            await EndAtTop("Long list");
            Check(Page.FileGrid.ItemContainerGenerator.ContainerFromIndex(0) is null,
                "Scrolling to the final row keeps distant rows virtualized");
            Viewer.ScrollToTop(); await Settle();
            Check(Viewer.VerticalOffset == 0 && (Page.FileGrid.ItemContainerGenerator.ContainerFromIndex(0) as DataGridRow)?.Margin.Bottom == 0,
                "Scrolling back recycles rows without retaining the final row's padding");
            Page.SearchBox.Text = "Item 07"; await Page.SearchTask; await Settle();
            await EndAtTop("Filtered list");
            Probe.Height = 800; await Settle(); await EndAtTop("Resized list");
            Page.SearchBox.Clear(); await Page.SearchTask;
            Page.SortDirection = System.ComponentModel.ListSortDirection.Descending;
            Page.ApplySort(); await Settle(); await EndAtTop("Reverse-sorted list");
            Check(await Page.NavigateAsync(Empty), "An empty folder remains navigable after scrolling to the end");
            await Settle();
            Check(Page.FileGrid.Items.Count == 0 && Viewer.VerticalOffset == 0 && Viewer.ScrollableHeight == 0,
                "An empty folder has no trailing scroll range");
        }
        finally { Probe.RequestExit(); }
    }
}
