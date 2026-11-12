using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CDir.Controls;
using CDir.Models;
using CDir.Services;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunGroupingMenuSmokeAsync(List<string> Passed)
    {
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Grouping menu check failed: " + Name);
            Passed.Add(Name);
        }
        IEnumerable<T> Visuals<T>(DependencyObject Parent) where T : DependencyObject
        {
            for (var Index = 0; Index < VisualTreeHelper.GetChildrenCount(Parent); Index++)
            {
                var Child = VisualTreeHelper.GetChild(Parent, Index);
                if (Child is T Match) yield return Match;
                foreach (var Nested in Visuals<T>(Child)) yield return Nested;
            }
        }
        var Root = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "Grouping Menu")).FullName;
        var Alpha = Path.Combine(Root, "Alpha.txt");
        File.WriteAllText(Alpha, "Owned grouping fixture");
        File.WriteAllBytes(Path.Combine(Root, "Beta.bin"), new byte[20000]);
        Directory.CreateDirectory(Path.Combine(Root, "Child"));
        var Configuration = Path.Combine(AppMain.SmokeRoot!, "GroupingSettings.json");
        File.WriteAllText(Configuration, JsonSerializer.Serialize(new AppSettings { LastPath = Root, ExitOnClose = true, ShowGridLines = true }));
        MainWindow? Probe = null;
        async Task Open()
        {
            Probe = new MainWindow(Configuration, RestoreWorkspace: true) { Owner = this.HostWindow, ShowActivated = false, ShowInTaskbar = false };
            Probe.Show(); await Probe.WorkspaceRestored; await Probe.ActiveTab!.WaitUntilReadyAsync();
        }
        async Task Settle() { await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); Probe!.UpdateLayout(); }
        MenuItem OpenMenu(ExplorerTab Page)
        {
            Page.ColumnMenuClick(Page.ColumnMenuButton, new RoutedEventArgs(Button.ClickEvent));
            return Page.ColumnMenuButton.ContextMenu!.Items.OfType<MenuItem>().Single(Item => Equals(Item.Tag, "GroupBy"));
        }
        MenuItem Command(MenuItem Menu, string Key) => Menu.Items.OfType<MenuItem>().Single(Item => Equals(Item.Tag, "CDir.Group." + Key));
        void Click(MenuItem Menu, string Key) => Command(Menu, Key).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        var Measurements = new List<object>();
        try
        {
            await Open(); var Page = Probe!.ActiveTab!;
            Page.SelectPath(Alpha); await Settle();
            var GroupMenu = OpenMenu(Page);
            Check(GroupMenu.Items.OfType<MenuItem>().Count() == 7 && Command(GroupMenu, "None").IsChecked
                && !Command(GroupMenu, "Ascending").IsEnabled && !Command(GroupMenu, "Descending").IsEnabled,
                "The top-right dropdown offers four grouping fields, both directions and None with accurate initial states");
            GroupMenu.IsSubmenuOpen = true; await Settle();
            var Popup = (Popup)GroupMenu.Template.FindName("PART_Popup", GroupMenu);
            Check(Popup is { IsOpen: true, Child.IsVisible: true } && Command(GroupMenu, "Name").ActualHeight > 0
                && Command(GroupMenu, "Name").FontSize == GroupMenu.FontSize
                && Command(GroupMenu, "Name").Foreground is SolidColorBrush TextBrush && TextBrush.Color != Colors.White,
                "The compact grouping submenu opens a visible popup with inherited readable menu fonts");
            if (AppMain.ScreenshotPath is { } Output)
            {
                var Child = (FrameworkElement)Popup.Child;
                var Bitmap = new RenderTargetBitmap((int)Math.Ceiling(Child.ActualWidth), (int)Math.Ceiling(Child.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                Bitmap.Render(Child);
                var Encoder = new PngBitmapEncoder(); Encoder.Frames.Add(BitmapFrame.Create(Bitmap));
                using var Stream = File.Create(Path.Combine(Path.GetDirectoryName(Output)!, "Grouping-Submenu.png")); Encoder.Save(Stream);
            }
            foreach (var Key in BackgroundSortKeys)
            {
                Click(GroupMenu, Key); await Settle();
                Check(Page.FileGroupProperty == Key && Page.View!.Groups?.Count > 0
                    && Command(GroupMenu, Key).IsChecked && !Command(GroupMenu, "None").IsChecked
                    && Page.SelectedEntries().Single().FullPath == Alpha,
                    "Dropdown grouping by " + Key + " displays actual groups, updates checks and retains selection");
            }
            Click(GroupMenu, "Descending"); await Settle();
            Check(Page.FileGroupDescending && Command(GroupMenu, "Descending").IsChecked && !Command(GroupMenu, "Ascending").IsChecked,
                "Descending and Ascending are mutually exclusive grouping directions");
            var Ranks = Page.View!.Cast<FileEntry>().Select(Entry => Entry.ViewGroupOrder).ToArray();
            Check(Ranks.Zip(Ranks.Skip(1)).All(Pair => Pair.First >= Pair.Second), "Size groups follow numeric bucket order rather than translated label order");
            await Page.ExecuteBackgroundCommandAsync("CDir.Group.TypeName"); await Settle();
            Check(Command(GroupMenu, "TypeName").IsChecked && !Command(GroupMenu, "Size").IsChecked,
                "Native background grouping commands synchronize the open dropdown");
            Click(GroupMenu, "None"); await Settle();
            Check(Page.View!.GroupDescriptions.Count == 0 && Command(GroupMenu, "None").IsChecked,
                "None removes all group headers and restores the flat file list");
            Click(GroupMenu, "Modified"); Click(GroupMenu, "Ascending"); await Settle();
            Ranks = Page.View.Cast<FileEntry>().Select(Entry => Entry.ViewGroupOrder).ToArray();
            Check(Ranks.Zip(Ranks.Skip(1)).All(Pair => Pair.First <= Pair.Second), "Date groups follow chronological bucket order");
            Page.SearchBox.Text = "/file"; await Page.WaitForSearchSmokeAsync(); await Settle();
            Check(Page.FileGroupProperty == nameof(FileEntry.Modified) && Page.View.Cast<FileEntry>().Count() == 2,
                "Grouping retains the active search filter");
            Page.SearchBox.Clear(); await Page.WaitForSearchSmokeAsync();
            GroupMenu.IsSubmenuOpen = false; Page.ColumnMenuButton.ContextMenu!.IsOpen = false;
            await Page.NavigateAsync(ShellLocations.Computer);
            GroupMenu = OpenMenu(Page);
            Check(!GroupMenu.IsEnabled, "My Computer preserves fixed drive ordering and disables dropdown grouping");
            Page.ColumnMenuButton.ContextMenu!.IsOpen = false;
            await Page.NavigateAsync(Root); Page.SelectPath(Alpha);
            GroupMenu = OpenMenu(Page); Click(GroupMenu, "Size"); Click(GroupMenu, "Descending");
            Page.ColumnMenuButton.ContextMenu!.IsOpen = false;
            Probe.PersistWorkspaceNow(); Probe.RequestExit(); Probe = null;
            await Open(); Page = Probe!.ActiveTab!; await Settle(); GroupMenu = OpenMenu(Page);
            Check(Page.FileGroupProperty == nameof(FileEntry.Size) && Page.FileGroupDescending
                && Command(GroupMenu, "Size").IsChecked && Command(GroupMenu, "Descending").IsChecked
                && Page.SelectedEntries().Single().FullPath == Alpha,
                "Restart restores grouping field, direction, menu checks and file selection");
            Page.ColumnMenuButton.ContextMenu!.IsOpen = false;

            Page.FileGroupProperty = ""; Page.FileGroupDescending = false;
            var Entries = Enumerable.Range(0, 10000).Select(Index => new FileEntry
            {
                Name = $"{(char)('A' + Index % 26)} File {Index:00000}.txt", FullPath = Path.Combine(Root, $"Synthetic {Index:00000}.txt"),
                TypeName = $"Type {Index % 20:00}", Size = new long[] { 0, 1024, 20000, 2000000, 20000000 }[Index % 5],
                Modified = DateTime.Today.AddDays(-Index % 400)
            }).ToArray();
            Page.ShowEntries(Entries);
            Page.RefreshingFileLayout = true;
            try { foreach (var Entry in Entries.Take(200)) Page.FileGrid.SelectedItems.Add(Entry); }
            finally { Page.RefreshingFileLayout = false; }
            Page.SyncTileSelection();
            var Selected = Page.SelectedEntries().ToHashSet();
            foreach (var Mode in new[] { FileViewMode.Details, FileViewMode.LargeIcons })
            {
                Page.ChangeFileView(Mode); await Settle();
                GroupMenu = OpenMenu(Page);
                var View = Page.View;
                var Changes = 0;
                NotifyCollectionChangedEventHandler Changed = (_, _) => Changes++;
                View!.CollectionChanged += Changed;
                try
                {
                    foreach (var Key in BackgroundSortKeys.Concat(new[] { "None" }))
                    {
                        Changes = 0;
                        var Stages = new Dictionary<string, double>();
                        var Timer = Stopwatch.StartNew();
                        var PreviousMs = 0d;
                        Page.FileLayoutStage = Stage => { var Now = Timer.Elapsed.TotalMilliseconds; Stages[Stage] = Now - PreviousMs; PreviousMs = Now; };
                        Click(GroupMenu, Key);
                        Page.FileLayoutStage = null;
                        var HandlerMs = Timer.Elapsed.TotalMilliseconds;
                        await Settle();
                        var TotalMs = Timer.Elapsed.TotalMilliseconds;
                        Check(Changes == 1 && ReferenceEquals(View, Page.View) && ReferenceEquals(Page.Entries[0], Entries[0]),
                            $"10,000-file {Mode} grouping by {Key} refreshes once without reloading the directory");
                        Check(Page.SelectedEntries().ToHashSet().SetEquals(Selected),
                            $"10,000-file {Mode} grouping by {Key} preserves all 200 selected files");
                        var Realized = Mode == FileViewMode.Details ? Visuals<DataGridRow>(Page.FileGrid).Count()
                            : VisualTreeHelper.GetChildrenCount(FindVisualChild<FileTilePanel>(Page.FileTiles)!);
                        Check(Realized > 0 && Realized < 300, $"10,000-file {Mode} grouping by {Key} realizes only a bounded viewport");
                        Changes = 0; Click(GroupMenu, Key); await Settle();
                        Check(Changes == 0 && Command(GroupMenu, Key).IsChecked,
                            $"Repeating {Mode} grouping by {Key} performs no redundant sorting");
                        Measurements.Add(new { mode = Mode.ToString(), field = Key, files = Entries.Length, selected = Selected.Count,
                            handler_ms = HandlerMs, settled_ms = TotalMs, realized_items = Realized, collection_refreshes = 1, stages = Stages });
                        if (Key == nameof(FileEntry.TypeName) && AppMain.ScreenshotPath is { } LayoutOutput)
                        {
                            GroupMenu.IsSubmenuOpen = false; Page.ColumnMenuButton.ContextMenu!.IsOpen = false;
                            var Bitmap = new RenderTargetBitmap((int)Probe.ActualWidth, (int)Probe.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                            Bitmap.Render(Probe);
                            var Encoder = new PngBitmapEncoder(); Encoder.Frames.Add(BitmapFrame.Create(Bitmap));
                            using var Stream = File.Create(Path.Combine(Path.GetDirectoryName(LayoutOutput)!, "Grouping-" + Mode + ".png")); Encoder.Save(Stream);
                        }
                        if (Mode == FileViewMode.Details && Key != "None")
                        {
                            var Viewer = FindVisualChild<ScrollViewer>(Page.FileGrid)!;
                            Viewer.ScrollToEnd(); await Settle();
                            Check(Visuals<DataGridRow>(Page.FileGrid).Any(Row => ReferenceEquals(Row.Item, Page.FileGrid.Items[^1]))
                                && Visuals<DataGridRow>(Page.FileGrid).Count() < 300,
                                "Virtualized grouped Details scrolling reaches the last file for " + Key);
                            Viewer.ScrollToTop(); await Settle();
                        }
                    }
                }
                finally { View.CollectionChanged -= Changed; Page.ColumnMenuButton.ContextMenu!.IsOpen = false; }
            }
            if (AppMain.ScreenshotPath is { } PerformanceOutput)
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(PerformanceOutput)!, "GroupingPerformance.json"), JsonSerializer.Serialize(Measurements));
        }
        finally { Probe?.RequestExit(); }
    }
}
