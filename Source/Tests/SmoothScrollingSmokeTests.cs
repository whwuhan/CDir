using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CDir.Controls;
using CDir.Models;
using CDir.Services;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunSmoothScrollingSmokeChecksAsync(List<string> Passed)
    {
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Smooth scrolling check failed: " + Name);
            Passed.Add(Name); AppMain.RecordSmokeProgress("Smooth scrolling: " + Name);
        }
        var Root = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "Smooth scrolling")).FullName;
        var Folder = Directory.CreateDirectory(Path.Combine(Root, "Files")).FullName;
        var Config = Path.Combine(Root, "Settings.json");
        File.WriteAllText(Config, JsonSerializer.Serialize(new AppSettings { LastPath = Folder, ExitOnClose = true }));
        var Probe = new MainWindow(Config, RestoreWorkspace: true) { Owner = this.HostWindow, ShowActivated = false, ShowInTaskbar = false };
        var Measurements = new List<object>();
        async Task Settle()
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Probe.UpdateLayout();
            // Row realization can queue a deferred selection/reveal command.
            // Drain it before the next input assertion, as the tail-scroll checks do.
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        }
        async Task Finish(ScrollViewer Viewer)
        {
            var Deadline = Environment.TickCount64 + 3000;
            while (SmoothWheelScroll.IsActive(Viewer) && Environment.TickCount64 < Deadline) { await Task.Delay(16); await Settle(); }
            await Settle();
            Check(!SmoothWheelScroll.IsActive(Viewer), "The rendering callback is released after the short transition");
        }
        bool Wheel(ExplorerTab Page, int Delta, ModifierKeys Modifiers = ModifierKeys.None)
        {
            var Previous = Page.ReadFileViewWheelModifiers;
            try
            {
                Page.ReadFileViewWheelModifiers = () => Modifiers;
                var Input = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, Delta) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
                Page.MainPanelContent.RaiseEvent(Input);
                return Input.Handled;
            }
            finally { Page.ReadFileViewWheelModifiers = Previous; }
        }
        try
        {
            Probe.Show(); await Probe.WorkspaceRestored;
            var Page = Probe.ActiveTab!;
            Page.ShowEntries(Enumerable.Range(0, 10000).Select(Index => new FileEntry
            {
                Name = $"Scroll {Index:00000}.txt", FullPath = Path.Combine(Folder, $"Scroll {Index:00000}.txt"),
                TypeName = Index < 5000 ? "Text" : "Other"
            }).ToArray());
            foreach (var Mode in new[] { FileViewMode.Details, FileViewMode.LargeIcons, FileViewMode.List })
            {
                Page.ChangeFileView(Mode, FocusPanel: false, SaveImmediately: false); await Settle();
                var Viewer = FindVisualChild<ScrollViewer>(Page.ActiveFileControl)!;
                SmoothWheelScroll.Cancel(Viewer); Viewer.ScrollToTop(); Viewer.ScrollToLeftEnd(); await Settle();
                Page.SelectPath(Page.Entries[0].FullPath, Reveal: false);
                var Selected = Page.SelectedEntries();
                var View = Page.View;
                var Horizontal = Mode == FileViewMode.List;
                double Offset() => Horizontal ? Viewer.HorizontalOffset : Viewer.VerticalOffset;
                var Events = new List<double>();
                void Changed(object Sender, ScrollChangedEventArgs E) { if (ReferenceEquals(E.OriginalSource, Viewer)) Events.Add(Offset()); }
                Viewer.ScrollChanged += Changed;
                var Changes = 0;
                NotifyCollectionChangedEventHandler CollectionChanged = (_, _) => Changes++;
                View!.CollectionChanged += CollectionChanged;
                try
                {
                    var Lines = SystemParameters.WheelScrollLines;
                    if (Lines == 0) { Check(!SmoothWheelScroll.IsActive(Viewer), Mode + " respects disabled system wheel scrolling"); continue; }
                    var Distance = Lines < 0 ? (Horizontal ? Viewer.ViewportWidth : Viewer.ViewportHeight)
                        : Lines * (Mode == FileViewMode.Details ? 1 : 16d);
                    Wheel(Page, -120); Wheel(Page, 120); await Finish(Viewer);
                    Check(Math.Abs(Offset()) < .05, Mode + " cancels a same-frame reversal at the edge without a delayed jump");
                    Events.Clear();
                    var Timer = Stopwatch.StartNew();
                    Check(Wheel(Page, -120), Mode + " consumes plain wheel input without changing the view");
                    await Settle();
                    Check(Offset() > 0, Mode + " moves on the first event without waiting for the animation to finish"
                        + $" (offset={Offset()}, active={SmoothWheelScroll.IsActive(Viewer)}, max={(Horizontal ? Viewer.ScrollableWidth : Viewer.ScrollableHeight)}, updates={string.Join(',', Events)})");
                    await Finish(Viewer);
                    Check(Math.Abs(Offset() - Distance) < .05 && Events.Zip(Events.Skip(1)).All(Pair => Pair.Second >= Pair.First)
                        && Events.Any(Value => Value > 0 && Value < Distance),
                        Mode + " advances through intermediate positions and finishes at the exact wheel distance"
                        + $" (actual={Offset()}, expected={Distance}, updates={string.Join(',', Events.Select(Value => Value.ToString("0.###")))})");
                    var Full = Offset();
                    Wheel(Page, -60); await Finish(Viewer);
                    Check(Math.Abs(Offset() - Full - Distance / 2) < .05, Mode + " preserves half-detent precision");
                    Wheel(Page, -120); await Settle();
                    var BeforeReverse = Offset();
                    Wheel(Page, 120); await Finish(Viewer);
                    Check(Offset() < BeforeReverse, Mode + " reverses immediately without waiting for queued downward motion");
                    Wheel(Page, -120); await Settle();
                    if (Horizontal) Viewer.ScrollToHorizontalOffset(250); else Viewer.ScrollToVerticalOffset(25);
                    await Settle(); await Task.Delay(150); await Settle();
                    Check(!SmoothWheelScroll.IsActive(Viewer) && Math.Abs(Offset() - (Horizontal ? 250 : 25)) < .05,
                        Mode + " leaves programmatic navigation and scrollbar positioning in control");
                    Wheel(Page, -120); await Settle();
                    Page.ActiveFileControl.RaiseEvent(new KeyEventArgs(Page.UnmodifiedSmokeKeyboard,
                        PresentationSource.FromVisual(Probe), 0, Key.LeftCtrl) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                    await Settle(); var AfterKey = Offset();
                    await Task.Delay(150); await Settle();
                    Check(!SmoothWheelScroll.IsActive(Viewer) && Math.Abs(Offset() - AfterKey) < .05,
                        Mode + " cancels motion when the file control itself receives keyboard input");
                    Check(ReferenceEquals(Page.View, View) && Changes == 0 && Page.SelectedEntries().SequenceEqual(Selected),
                        Mode + " retains selection and the sorted collection throughout scrolling");
                    Check(Page.Settings.FileView == Mode, Mode + " ordinary wheel input never triggers icon zoom");
                    if (Mode != FileViewMode.Details)
                        Check(FindVisualChild<FileTilePanel>(Page.FileTiles) is { } Panel
                            && System.Windows.Media.VisualTreeHelper.GetChildrenCount(Panel) < 300,
                            Mode + " keeps 10,000 files virtualized during the transition");
                    Measurements.Add(new { mode = Mode.ToString(), files = 10000, elapsed_ms = Timer.Elapsed.TotalMilliseconds,
                        scroll_updates = Events.Count, wheel_distance = Distance, collection_changes = Changes });
                }
                finally { Viewer.ScrollChanged -= Changed; View.CollectionChanged -= CollectionChanged; SmoothWheelScroll.Cancel(Viewer); }
            }
            Page.ChangeFileView(FileViewMode.Details, FocusPanel: false, SaveImmediately: false);
            Page.FileGroupProperty = nameof(FileEntry.TypeName); Page.RefreshBackgroundLayout(); await Settle();
            var GroupedViewer = FindVisualChild<ScrollViewer>(Page.FileGrid)!;
            Wheel(Page, -120); await Finish(GroupedViewer);
            Check((SystemParameters.WheelScrollLines == 0 || GroupedViewer.VerticalOffset > 0) && Page.FileGrid.IsGrouping && Page.View!.GroupDescriptions.Count > 0,
                "Grouped Details retains its original logical scroll units and group layout");
            Page.FileGroupProperty = ""; Page.RefreshBackgroundLayout(); await Settle();
            var CurrentViewer = FindVisualChild<ScrollViewer>(Page.FileGrid)!;
            Wheel(Page, -120); await Settle();
            Check(await Page.NavigateAsync(Folder, SyncNavigationTree: false) && !SmoothWheelScroll.IsActive(CurrentViewer),
                "A new directory cancels pending wheel motion before its content is presented");
        }
        finally { Probe.RequestExit(); }
        if (AppMain.ScreenshotPath is { } Output)
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(Output)!, "SmoothScrolling.json"), JsonSerializer.Serialize(Measurements, new JsonSerializerOptions { WriteIndented = true }));
    }
}
