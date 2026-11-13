using System.IO;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CDir.Controls;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunViewZoomSmokeAsync(List<string> Passed)
    {
        await this.RunVersionBadgeScaleSmokeChecksAsync(Passed);
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("View zoom check failed: " + Name);
            Passed.Add(Name);
        }
        var Root = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "View Zoom")).FullName;
        var Other = Directory.CreateDirectory(Path.Combine(Root, "Other")).FullName;
        var Deep = Directory.CreateDirectory(Path.Combine(Root, "Child", "Grandchild", "Deepest")).FullName;
        for (var Index = 0; Index < 80; Index++) File.WriteAllText(Path.Combine(Root, $"Sample {Index:000}.txt"), "Owned zoom fixture");
        var Native = new NativeShellView();
        var Reference = new Window { Owner = this.HostWindow, Content = Native, Width = 600, Height = 400, ShowActivated = false, ShowInTaskbar = false };
        var Results = new List<object>();
        try
        {
            Reference.Show(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Native.UseModernZoomView();
            await Native.NavigateAsync(Root);
            foreach (var Size in Enumerable.Range(16, 241))
                foreach (var Delta in new[] { -120, 120 })
                {
                    var Actual = Native.ProbeZoom(1, Size, Delta);
                    var Expected = FileViewZoom.Step(FileViewMode.MediumIcons, Size, Delta > 0);
                    // Explorer's outer view selector continues into List at the
                    // smallest size; the embedded icon view itself clamps to 16.
                    Check(Expected.Size == Actual.Size, $"Icon zoom matches Windows Shell: {Size}, delta {Delta}, result {Actual.Size}");
                    Results.Add(new { Size, Delta, ActualMode = Actual.Mode, ActualSize = Actual.Size });
                }
            if (AppMain.ScreenshotPath is { } Output)
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(Output)!, "NativeViewZoom.json"), JsonSerializer.Serialize(Results));
        }
        finally { Reference.Close(); }

        var Configuration = Path.Combine(AppMain.SmokeRoot!, "ZoomSettings.json");
        File.WriteAllText(Configuration, JsonSerializer.Serialize(new AppSettings { LastPath = Root, ExitOnClose = true }));
        MainWindow? Probe = null;
        async Task Open()
        {
            Probe = new MainWindow(Configuration, RestoreWorkspace: true) { Owner = this.HostWindow, ShowActivated = false, ShowInTaskbar = false };
            Probe.Show(); await Probe.WorkspaceRestored; await Probe.ActiveTab!.WaitUntilReadyAsync();
        }
        async Task Settle() { await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); Probe!.UpdateLayout(); }
        bool Wheel(ExplorerTab Page, int Delta, ModifierKeys Modifiers = ModifierKeys.Control, UIElement? Target = null)
        {
            var Read = Page.ReadFileViewWheelModifiers;
            try
            {
                Page.ReadFileViewWheelModifiers = () => Modifiers;
                var Input = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, Delta) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
                (Target ?? Page.MainPanelContent).RaiseEvent(Input);
                return Input.Handled;
            }
            finally { Page.ReadFileViewWheelModifiers = Read; }
        }
        try
        {
            await Open(); var Page = Probe!.ActiveTab!;
            var Selected = Page.Entries.First(Entry => !Entry.IsDirectory);
            Page.SelectPath(Selected.FullPath);
            var Widths = Page.FileGrid.Columns.Select(Column => Column.Width).ToArray();
            FocusManager.SetFocusedElement(Probe, Page.SearchBox);
            Check(Wheel(Page, 120) && Page.Settings.FileView == FileViewMode.List,
                "Ctrl+wheel up over Details changes the real main pane to List");
            Check(ReferenceEquals(FocusManager.GetFocusedElement(Probe), Page.SearchBox),
                "Hover zoom preserves the current logical keyboard focus");
            Wheel(Page, 120, Target: Page.SearchBox); Wheel(Page, 120, Target: Page.FolderTree);
            Check(Page.Settings.FileView == FileViewMode.List,
                "Wheel zoom is scoped to the main file pane, excluding search and the directory tree");
            Wheel(Page, 120, ModifierKeys.None);
            Check(!Wheel(Page, 120, ModifierKeys.Control | ModifierKeys.Alt)
                && Page.Settings.FileView == FileViewMode.List, "Ordinary scrolling and other modifier combinations do not change the file view");
            Check(Wheel(Page, 60) && Page.Settings.FileView == FileViewMode.List
                && Wheel(Page, 60) && Page.Settings.FileView == FileViewMode.SmallIcons,
                "High-resolution wheel deltas accumulate to one 120-unit detent");
            Wheel(Page, -240);
            Check(Page.IsDetailsView, "A multi-detent wheel event processes every view transition");
            Wheel(Page, -240);
            Check(Page.Settings.FileView == FileViewMode.Content && Wheel(Page, -120)
                && Page.Settings.FileView == FileViewMode.Content, "The minimum Content view clamps without cycling");
            var Seen = new HashSet<FileViewMode> { Page.Settings.FileView };
            for (var Index = 0; Index < 70; Index++) { Wheel(Page, 120); Seen.Add(Page.Settings.FileView); }
            await Settle();
            Check(Seen.SetEquals(Enum.GetValues<FileViewMode>()) && Page.Settings.FileView == FileViewMode.ExtraLargeIcons
                && Page.Settings.FileViewIconSize == 256, "Wheel zoom traverses every layout and clamps at 256-pixel extra-large icons");
            Check(Page.SelectedEntries().Single() == Selected && Page.FileGrid.Columns.Select(Column => Column.Width).SequenceEqual(Widths),
                "Zoom preserves file selection and existing detail-column widths");
            Page.ChangeFileView(FileViewMode.MediumIcons); Wheel(Page, 120); await Settle();
            var Pixels = Page.Settings.FileViewIconSize;
            Check(Pixels == 53 && Page.FileTiles.Resources["FileViewIconPixels"] is int ActualPixels && ActualPixels == Pixels
                && FindVisualChild<FileTilePanel>(Page.FileTiles)!.CellWidth == Pixels + 48,
                "Intermediate native icon sizes update both the thumbnail request and actual cell geometry");
            await Task.Delay(350); await Settle();
            Check(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Configuration))!.FileViewIconSize == Pixels,
                "Wheel gestures persist their exact icon size after the short write debounce");
            Page.ColumnMenuClick(Page.ColumnMenuButton, new RoutedEventArgs(Button.ClickEvent));
            var Menu = Page.ColumnMenuButton.ContextMenu!;
            Check(Menu.Items.OfType<MenuItem>().Single(Item => Item.Tag is FileViewMode Mode && Mode == Page.Settings.FileView).IsChecked,
                "The view dropdown reflects the layout reached by wheel zoom");
            Menu.IsOpen = false;
            Page.SearchBox.Text = "/file"; await Page.WaitForSearchSmokeAsync();
            var SearchWheelHandled = Wheel(Page, 120); var SearchSize = Page.Settings.FileViewIconSize;
            Check(SearchSize > Pixels && Page.FileTiles.IsVisible && Page.FileTiles.Resources["FileViewIconPixels"] is int SearchPixels && SearchPixels == SearchSize,
                "Search results use and update the same zoomed view as their current folder"
                + $" (handled={SearchWheelHandled}, before={Pixels}, after={SearchSize}, visible={Page.FileTiles.IsVisible}, mode={Page.Settings.FileView}, loading={Page.Loading}, busy={Page.OperationBusy})");
            Page.SearchBox.Clear(); await Page.WaitForSearchSmokeAsync();
            Check(Page.Settings.FileViewIconSize == SearchSize, "Leaving search preserves the folder's exact icon size");
            await Page.NavigateAsync(Other); Page.ChangeFileView(FileViewMode.Content);
            await Page.NavigateAsync(Root);
            Check(Page.Settings.FileViewIconSize == SearchSize, "Returning to a folder restores its exact intermediate zoom");
            Page.HostWindow.ApplyFolderViewRecursively(Root, Page.Settings.FileView, SearchSize);
            Check(Probe.FolderIconSizeFor(Deep) == SearchSize
                && Probe.FolderIconSizeFor(Path.Combine(Deep, "Future")) == SearchSize,
                "Recursive Apply includes intermediate icon sizes at every depth and in future folders");
            await Page.NavigateAsync(Other); Page.ChangeFileView(FileViewMode.Content);
            await Page.NavigateAsync(Root); Page.SaveSettings(); Probe.PersistWorkspaceNow();
            Probe.RequestExit(); Probe = null;
            await Open(); Page = Probe!.ActiveTab!;
            Check(Page.Settings.FileViewIconSize == SearchSize && Probe.FolderIconSizeFor(Deep) == SearchSize,
                "Restart restores current-folder zoom and inherited descendant icon sizes");
            await Page.NavigateAsync(Other);
            Check(Page.Settings.FileView == FileViewMode.Content && Page.Settings.FileViewIconSize == 0,
                "Restart retains independent folder layouts without leaking icon sizes into Content");
            Probe.ApplyFolderViewRecursively(Root, FileViewMode.Details);
            Check(Probe.FolderIconSizeFor(Deep) == 0, "Reset removes descendant icon-size overrides along with their view choices");
            Page.ChangeFileView(FileViewMode.LargeIcons); Wheel(Page, 60);
            Page.ChangeFileView(FileViewMode.MediumIcons); Wheel(Page, 60);
            Check(Page.Settings.FileView == FileViewMode.MediumIcons && Page.Settings.FileViewIconSize == 0,
                "Selecting a menu view clears an incomplete previous wheel gesture");

            // In-memory entries keep this large-folder regression inside the owned
            // test scope without creating or enumerating thousands of real files.
            Page.ShowEntries(Enumerable.Range(0, 10000).Select(Index => new FileEntry
            {
                Name = $"Zoom {Index:00000}.txt", FullPath = Path.Combine(Other, $"Zoom {Index:00000}.txt"),
                TypeName = Index < 5000 ? "Text" : "Other"
            }).ToArray());
            var Timings = new List<object>();
            foreach (var Grouped in new[] { false, true })
            {
                Page.FileGroupProperty = Grouped ? nameof(FileEntry.TypeName) : "";
                Page.RefreshBackgroundLayout();
                Page.ChangeFileView(FileViewMode.LargeIcons, 70); await Settle();
                var Item = (FileEntry)Page.FileTiles.Items[7500];
                Page.ClearFileSelection(); Page.SelectPath(Item.FullPath); await Settle();
                var Panel = FindVisualChild<FileTilePanel>(Page.FileTiles)!;
                var Container = (ListBoxItem)Page.FileTiles.ItemContainerGenerator.ContainerFromItem(Item);
                var Icon = FindVisualChild<ShellIcon>(Container);
                var Resets = 0;
                NotifyCollectionChangedEventHandler Changed = (_, _) => Resets++;
                Page.View!.CollectionChanged += Changed;
                var Samples = new List<double>();
                var DispatchSamples = new List<double>();
                try
                {
                    for (var Index = 0; Index < 24; Index++)
                    {
                        var Timer = Stopwatch.StartNew();
                        Wheel(Page, Index % 2 == 0 ? 120 : -120, Target: Container);
                        DispatchSamples.Add(Timer.Elapsed.TotalMilliseconds);
                        await Settle();
                        Samples.Add(Timer.Elapsed.TotalMilliseconds);
                    }
                    Check(Resets == 0, $"10,000-file zoom (grouped={Grouped}) never refreshes or re-sorts the collection");
                    Check(ReferenceEquals(Panel, FindVisualChild<FileTilePanel>(Page.FileTiles))
                        && ReferenceEquals(Container, Page.FileTiles.ItemContainerGenerator.ContainerFromItem(Item))
                        && ReferenceEquals(Icon, FindVisualChild<ShellIcon>(Container)),
                        $"10,000-file zoom (grouped={Grouped}) reuses the panel, anchored container and icon");
                    Check(Page.SelectedEntries().Single() == Item && Panel.VerticalOffset > 0
                        && VisualTreeHelper.GetChildrenCount(Panel) < 300,
                        $"10,000-file zoom (grouped={Grouped}) preserves off-screen selection and bounded virtualization");
                    var Bounds = Panel.ItemBounds(Page.FileTiles.Items.IndexOf(Item));
                    Check(Bounds.Top >= Panel.VerticalOffset && Bounds.Bottom <= Panel.VerticalOffset + Panel.ViewportHeight,
                        $"10,000-file zoom (grouped={Grouped}) keeps the wheel anchor visible after resizing");
                    Timings.Add(new { grouped = Grouped, files = 10000, events = Samples.Count,
                        average_ms = Samples.Average(), maximum_ms = Samples.Max(), collection_changes = Resets,
                        dispatch_average_ms = DispatchSamples.Average(), dispatch_maximum_ms = DispatchSamples.Max(), samples_ms = Samples,
                        realized_items = VisualTreeHelper.GetChildrenCount(Panel) });
                }
                finally { Page.View.CollectionChanged -= Changed; }
                if (Grouped)
                {
                    var Start = 0;
                    var Top = 0d;
                    var Header = Math.Max(28, Page.FileTiles.FontSize + 12);
                    var Across = Math.Max(1, (int)(Panel.ViewportWidth / Panel.CellWidth));
                    while (Start < Page.FileTiles.Items.Count)
                    {
                        var Label = ((FileEntry)Page.FileTiles.Items[Start]).ViewGroup;
                        var End = Start + 1;
                        while (End < Page.FileTiles.Items.Count && ((FileEntry)Page.FileTiles.Items[End]).ViewGroup == Label) End++;
                        Top += Header;
                        Check(Enumerable.Range(Start, End - Start).All(Index => Panel.ItemBounds(Index) == new Rect(
                            (Index - Start) % Across * Panel.CellWidth, Top + (Index - Start) / Across * Panel.CellHeight,
                            Panel.CellWidth, Panel.CellHeight)), "Cached group ranges match every file's expected grid position");
                        Top += Math.Ceiling((double)(End - Start) / Across) * Panel.CellHeight;
                        Start = End;
                    }
                    Page.ChangeFileView(FileViewMode.Details); await Settle();
                    Check(Page.View.GroupDescriptions.Count > 0, "Zoom optimization retains native Details grouping");
                    Page.ChangeFileView(FileViewMode.MediumIcons); await Settle();
                    Check(Page.View.GroupDescriptions.Count == 0 && FindVisualChild<FileTilePanel>(Page.FileTiles)!.Grouped,
                        "Returning from Details restores flat virtualized icon groups");
                    ((System.Collections.ObjectModel.ObservableCollection<FileEntry>)Page.Entries).Remove((FileEntry)Page.FileTiles.Items[0]); await Settle();
                    Check(Page.FileTiles.Items.Count == 9999 && double.IsFinite(Panel.ExtentHeight),
                        "Grouped geometry invalidates after collection membership changes");
                }
            }
            if (AppMain.ScreenshotPath is { } PerformanceOutput)
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(PerformanceOutput)!, "ViewZoomPerformance.json"), JsonSerializer.Serialize(Timings));
        }
        finally { Probe?.RequestExit(); }
    }
}
