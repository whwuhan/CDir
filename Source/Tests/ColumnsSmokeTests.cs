using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunColumnsSmokeChecksAsync(List<string> Passed)
    {
        await this.RunGridLinesSmokeChecksAsync(Passed);
        await this.RunFolderSizeSmokeChecksAsync(Passed);
        await this.RunHeaderSortSmokeChecksAsync(Passed);
        await this.RunColumnFitSmokeChecksAsync(Passed);
        await this.RunFileViewSmokeChecksAsync(Passed);
        await this.RunFolderViewActionsSmokeAsync(Passed);
        var InitialOrder = this.FileGrid.Columns.OrderBy(Column => Column.DisplayIndex).Select(Column => Column.SortMemberPath).ToArray();
        var InitialHidden = this.Settings.HiddenColumns?.ToArray() ?? [];
        var InitialSearch = this.SearchBox.Text;
        var InitialSort = this.SortProperty;
        var InitialDirection = this.SortDirection;
        var SettingsFile = Path.Combine(Path.GetTempPath(), "CDirColumns-" + Guid.NewGuid().ToString("N") + ".json");
        string[] Order() => this.FileGrid.Columns.OrderBy(Column => Column.DisplayIndex).Select(Column => Column.SortMemberPath).ToArray();
        DataGridColumn Column(string Key) => this.FileGrid.Columns.Single(Column => Column.SortMemberPath == Key);
        async Task Settle()
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            this.UpdateLayout();
        }
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Columns UI smoke test failed: " + Name);
            Passed.Add(Name);
        }
        void Reorder(DataGridColumn Column, int Index)
        {
            Column.DisplayIndex = Index;
            this.FileGridColumnReordered(this.FileGrid, new DataGridColumnEventArgs(Column));
            this.UpdateLayout();
        }
        void Screenshot(string Name)
        {
            if (AppMain.ScreenshotPath is not { } Destination) return;
            var Bitmap = new RenderTargetBitmap((int)this.ActualWidth, (int)this.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            Bitmap.Render(this);
            var Encoder = new PngBitmapEncoder();
            Encoder.Frames.Add(BitmapFrame.Create(Bitmap));
            using var Stream = File.Create(Path.Combine(Path.GetDirectoryName(Destination)!, Name + ".png"));
            Encoder.Save(Stream);
        }
        try
        {
            this.ColumnMenuClick(this.ColumnMenuButton, new RoutedEventArgs(Button.ClickEvent));
            await Settle();
            var Menu = this.ColumnMenuButton.ContextMenu!;
            Check(this.ColumnMenuButton.IsVisible && Menu.IsOpen
                && this.ColumnMenuButton.TranslatePoint(new Point(), this).X >= this.SearchContainer.TranslatePoint(new Point(this.SearchContainer.ActualWidth, 0), this).X
                && Menu.Items.OfType<MenuItem>().Where(Item => Item.Tag is string Key && ColumnKeys.Contains(Key)).All(Item => Item.IsChecked == (Column((string)Item.Tag).Visibility == Visibility.Visible)),
                "The dropdown sits to the right of search and marks currently displayed column headers");
            Check(Menu.Items[^2] is Separator && Menu.Items[^1] is MenuItem { Tag: "ShowHidden" },
                "A separator and hidden-files toggle finish the column dropdown");
            var HiddenItem = (MenuItem)Menu.Items[^1];
            var WasHidden = this.Settings.ShowHidden;
            Check(HiddenItem.IsChecked == WasHidden, "The dropdown reflects the saved hidden-files setting");
            HiddenItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            while (!HiddenItem.IsEnabled) await Task.Delay(20);
            Check(HiddenItem.IsChecked == !WasHidden && this.Settings.ShowHidden == !WasHidden
                && this.CaptureSaveSettings().ShowHidden == !WasHidden,
                "The hidden-files dropdown refreshes and persists the same preference used by General");
            Menu.IsOpen = false;
            await this.RunPreferencesSmokeChecksAsync(Passed);
            await this.ChangeShowHiddenAsync(WasHidden);
            Check(HiddenItem.IsChecked == WasHidden, "Changing the shared preference updates the existing dropdown check");
            this.ColumnMenuClick(this.ColumnMenuButton, new RoutedEventArgs(Button.ClickEvent));
            Menu = this.ColumnMenuButton.ContextMenu!;
            var SizeItem = Menu.Items.OfType<MenuItem>().Single(Item => Equals(Item.Tag, nameof(FileEntry.Size)));
            var SizeWasVisible = Column(nameof(FileEntry.Size)).Visibility == Visibility.Visible;
            SizeItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Settle();
            Check(SizeItem.IsChecked != SizeWasVisible && (Column(nameof(FileEntry.Size)).Visibility == Visibility.Visible) != SizeWasVisible,
                "Clicking a dropdown entry toggles column visibility and its right-side check mark");
            SizeItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Settle();
            Check(SizeItem.IsChecked == SizeWasVisible, "Clicking the same entry again restores its previous visibility");
            Menu.IsOpen = false;
            Check(this.FileGrid.CanUserReorderColumns && this.FileGrid.Columns.All(Column => Column.CanUserReorder),
                "Native header dragging is enabled for every file-list column");
            Reorder(Column(nameof(FileEntry.Name)), 3);
            Check(Column(nameof(FileEntry.Name)).DisplayIndex == 3 && Order().Distinct().Count() == 5
                && this.Settings.ColumnOrder!.SequenceEqual(Order()), "Dragging a column updates its display order and saved layout");
            Check(this.SaveSettings(SettingsFile) && ReadSettings(SettingsFile).ColumnOrder!.SequenceEqual(Order()),
                "Column display order survives settings serialization");
            await this.RunPreferencesSmokeChecksAsync(Passed, VisibleColumns: new Dictionary<string, bool>
            {
                [nameof(FileEntry.Modified)] = false,
                [nameof(FileEntry.TypeName)] = false,
                [nameof(FileEntry.Size)] = false
            });
            Check(this.FileGrid.Columns.Count(Column => Column.Visibility == Visibility.Visible) == 1
                && Column(nameof(FileEntry.Name)).Visibility == Visibility.Visible,
                "UI checkboxes can reduce the file list to one browsing column");
            Check(!this.ChangeColumnVisibility(nameof(FileEntry.Name), false)
                && Column(nameof(FileEntry.Name)).Visibility == Visibility.Visible,
                "The final browsing column cannot be hidden");
            await this.RunPreferencesSmokeChecksAsync(Passed, VisibleColumns: new Dictionary<string, bool>
            {
                [nameof(FileEntry.TypeName)] = true,
                [nameof(FileEntry.Name)] = false
            });
            Check(Column(nameof(FileEntry.Name)).Visibility == Visibility.Collapsed
                && Column(nameof(FileEntry.TypeName)).Visibility == Visibility.Visible,
                "The Name column is customizable when another browsing column remains visible");
            Check(this.SaveSettings(SettingsFile), "Column visibility saves to an owned settings file");
            var Saved = ReadSettings(SettingsFile);
            this.RestoreColumnLayout(null, null);
            this.RestoreColumnLayout(Saved.ColumnOrder, Saved.HiddenColumns);
            Check(Order().SequenceEqual(Saved.ColumnOrder!)
                && this.FileGrid.Columns.Where(Column => Column.SortMemberPath != nameof(FileEntry.FullPath))
                    .All(Column => (Column.Visibility == Visibility.Collapsed) == Saved.HiddenColumns!.Contains(Column.SortMemberPath)),
                "Startup restoration rebuilds saved order and visibility using stable column keys");
            var Legacy = JsonSerializer.Deserialize<AppSettings>("{}")!;
            this.RestoreColumnLayout(Legacy.ColumnOrder, Legacy.HiddenColumns);
            Check(Order().SequenceEqual(ColumnKeys) && this.FileGrid.Columns.Count(Column => Column.Visibility == Visibility.Visible) == 4,
                "Older settings retain the default four browsing columns and default order");
            this.RestoreColumnLayout(["unknown", nameof(FileEntry.Size), nameof(FileEntry.Size)], ColumnKeys);
            Check(Order()[0] == nameof(FileEntry.Size) && Order().Distinct().Count() == 5
                && Column(nameof(FileEntry.Name)).Visibility == Visibility.Visible,
                "Unknown duplicate incomplete or all-hidden settings cannot lose columns or leave an unusable list");
            this.RestoreColumnLayout(InitialOrder, []);
            this.SearchBox.Text = "/file";
            await this.WaitForSearchSmokeAsync();
            var SearchVersion = this.SearchVersion;
            var SearchResults = this.View!.Cast<FileEntry>().Select(Entry => Entry.FullPath).ToArray();
            await this.RunPreferencesSmokeChecksAsync(Passed, VisibleColumns: new Dictionary<string, bool> { [nameof(FileEntry.FullPath)] = false });
            Check(this.SearchPathColumn.Visibility == Visibility.Collapsed && this.CurrentSearch.IsActive,
                "Absolute Path can be hidden in active search results");
            await this.RunPreferencesSmokeChecksAsync(Passed, VisibleColumns: new Dictionary<string, bool> { [nameof(FileEntry.FullPath)] = true });
            Check(this.SearchPathColumn.Visibility == Visibility.Visible && this.SearchVersion == SearchVersion
                && this.View!.Cast<FileEntry>().Select(Entry => Entry.FullPath).SequenceEqual(SearchResults),
                "Showing a column updates existing search results without rerunning the search");
            Reorder(this.SearchPathColumn, 0);
            Reorder(Column(nameof(FileEntry.Size)), 1);
            await Settle();
            Check(this.SearchPathColumn.DisplayIndex == 0 && Column(nameof(FileEntry.Size)).DisplayIndex == 1,
                "Search-only and regular columns can both move ahead of Name");
            Check(this.SaveSettings(SettingsFile) && ReadSettings(SettingsFile).ColumnOrder!.SequenceEqual(Order()),
                "Reordered search columns persist in the same layout as ordinary columns");
            Screenshot("ColumnsReordered");
            var Modified = Column(nameof(FileEntry.Modified));
            Modified.SortDirection = null;
            this.FileGridSorting(this.FileGrid, new DataGridSortingEventArgs(Modified));
            Check(this.SortProperty == nameof(FileEntry.Modified) && this.SortDirection == ListSortDirection.Descending,
                "Click sorting still selects the correct field after header reordering");
            var SearchOrder = Order();
            this.SearchBox.Clear();
            await this.WaitForSearchSmokeAsync();
            Check(this.SearchPathColumn.Visibility == Visibility.Collapsed && Order().SequenceEqual(SearchOrder)
                && !this.Settings.HiddenColumns!.Contains(nameof(FileEntry.FullPath)),
                "Leaving search hides Absolute Path while retaining its chosen visibility and display position");
            await this.RunPreferencesSmokeChecksAsync(Passed);
        }
        finally
        {
            this.SearchBox.Clear();
            await this.WaitForSearchSmokeAsync();
            this.RestoreColumnLayout(InitialOrder, InitialHidden);
            this.SortProperty = InitialSort;
            this.SortDirection = InitialDirection;
            this.ApplySort();
            this.SearchBox.Text = InitialSearch;
            await this.WaitForSearchSmokeAsync();
            await Settle();
        }
    }
}
