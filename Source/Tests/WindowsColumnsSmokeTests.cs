using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CDir.Models;
using CDir.Services;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunWindowsColumnsSmokeAsync(List<string> Passed)
    {
        var Order = this.Settings.ColumnOrder?.ToArray();
        var Hidden = this.Settings.HiddenColumns?.ToArray();
        var Initial = this.CurrentPath;
        void Check(bool Value, string Message)
        {
            if (!Value) throw new InvalidOperationException("Windows columns: " + Message);
            Passed.Add(Message);
        }
        var Fixture = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "Windows details")).FullName;
        var FilePath = Path.Combine(Fixture, "Owned detail.txt");
        File.WriteAllText(FilePath, "Owned Windows property fixture");
        try
        {
            var Native = ShellDetailColumns.ReadCatalog(Fixture);
            Check(Native.Count > 100 && Native.All(Column => this.FileGrid.Columns.Any(Grid => Grid.SortMemberPath == Column.Key))
                && this.FileGrid.Columns.Count == this.GetColumnPreferences().Count,
                "All " + Native.Count + " Windows filesystem detail columns have the same toggles in the grid and Preferences/UI");
            Check(this.GetColumnPreferences().Select(Column => Column.Key).ToHashSet().SetEquals(ColumnKeys),
                "Preferences and the detail grid share exactly one stable column catalog");
            var Extended = Native.Single(Column => Column.Key == "System.DateCreated");
            await this.NavigateAsync(Fixture);
            var InitialLanguage = CDir.Localization.LanguageService.CurrentLanguage;
            foreach (var Language in CDir.Localization.LanguageService.SupportedLanguages)
            {
                this.ChangeLanguage(Language.Code, Persist: false);
                if (Language.Code == "en-US") Check(this.FileGrid.Columns.All(Column => Column.Header!.ToString()!.All(Character => Character <= 127)),
                    "English column headers never leak the Windows installation language");
                Check(Native.All(Definition => CDir.Localization.LanguageService.GetTexts().ContainsKey(ColumnResourceKey(Definition.Key)))
                    && this.FileGrid.Columns.All(Column => Column.Header?.ToString() == CDir.Localization.LanguageService.Text(ColumnResourceKey(Column.SortMemberPath))),
                    Language.Code + " localizes every Windows detail header with the application's selected language");
                Check(this.FileGrid.Columns.Where(Column => ColumnHintResourceKey(Column.SortMemberPath) is not null)
                    .All(Column => Column.GetValue(ToolTipService.ToolTipProperty)?.ToString()
                        == CDir.Localization.LanguageService.Text(ColumnHintResourceKey(Column.SortMemberPath)!)),
                    Language.Code + " updates applicability hints with the column headers");
            }
            this.ChangeLanguage(InitialLanguage, Persist: false);
            this.ChangeColumnEnabled(Extended.Key, true);
            var ExtendedColumn = this.FileGrid.Columns.Single(Column => Column.SortMemberPath == Extended.Key);
            Check(ExtendedColumn.Visibility == Visibility.Collapsed && this.GetColumnPreferences().Single(Option => Option.Key == Extended.Key).Enabled,
                "Preferences enables a menu entry without automatically displaying the column");
            this.ColumnMenuClick(this.ColumnMenuButton, new RoutedEventArgs(Button.ClickEvent));
            var ExtendedItem = this.ColumnMenuButton.ContextMenu!.Items.OfType<MenuItem>().Single(Item => Equals(Item.Tag, Extended.Key));
            Check(!ExtendedItem.IsChecked, "The enabled column initially appears unchecked in the visibility menu");
            Check(ExtendedItem.ToolTip?.ToString() == CDir.Localization.LanguageService.Text("DateCreatedColumnHint")
                && ColumnHintResourceKey(nameof(FileEntry.OriginalLocation)) == "OriginalLocationColumnHint"
                && ColumnHintResourceKey(nameof(FileEntry.DateDeleted)) == "DateDeletedColumnHint"
                && ColumnHintResourceKey("System.DateCreated") != "RecycleColumnHint",
                "The column menu explains Date Created separately from the Recycle Bin-only location and deletion date");
            ExtendedItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Check(ExtendedColumn.Visibility == Visibility.Visible && this.IsColumnEnabled(Extended.Key),
                "The visibility menu independently shows an enabled column");
            this.ColumnMenuButton.ContextMenu.IsOpen = false;
            this.ChangeColumnVisibility(Extended.Key, false);
            Check(this.IsColumnEnabled(Extended.Key), "Hiding a column does not remove its Preferences selection");
            var SettingsFile = Path.Combine(Fixture, "Columns settings.json");
            this.SaveSettings(SettingsFile);
            var Saved = ReadSettings(SettingsFile);
            Check(Saved.EnabledColumns!.Contains(Extended.Key) && Saved.HiddenColumns!.Contains(Extended.Key),
                "Menu membership and visibility serialize as independent settings");
            this.ChangeColumnEnabled(Extended.Key, false);
            this.ColumnMenuClick(this.ColumnMenuButton, new RoutedEventArgs(Button.ClickEvent));
            Check(!this.ColumnMenuButton.ContextMenu!.Items.OfType<MenuItem>().Any(Item => Equals(Item.Tag, Extended.Key)),
                "Disabling a Preferences column removes it from the visibility menu");
            this.ColumnMenuButton.ContextMenu.IsOpen = false;
            File.Delete(SettingsFile);
            Check(this.ChangeColumnEnabled(Extended.Key, true) && this.ChangeColumnVisibility(Extended.Key, true), "A Windows detail property can be enabled");
            await this.ShellColumnsTask;
            var Entry = this.Entries.Single(Entry => Entry.FullPath == FilePath);
            // Independently compare with Explorer's automation API, rather than our reader.
            dynamic Shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            dynamic Folder = Shell.NameSpace(Fixture);
            dynamic Item = Folder.ParseName(Path.GetFileName(FilePath));
            string NativeText;
            try { NativeText = Folder.GetDetailsOf(Item, (int)Extended.Index); }
            finally
            {
                System.Runtime.InteropServices.Marshal.ReleaseComObject(Item);
                System.Runtime.InteropServices.Marshal.ReleaseComObject(Folder);
                System.Runtime.InteropServices.Marshal.ReleaseComObject(Shell);
            }
            Check(Entry[Extended.Key].Length > 0 && Entry[Extended.Key] == NativeText,
                "Date Created renders the actual formatted Windows Shell property");
            var GridColumn = this.FileGrid.Columns.Single(Column => Column.SortMemberPath == Extended.Key);
            this.FileGrid.ScrollIntoView(Entry, GridColumn);
            this.UpdateLayout(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Check(GridColumn.GetCellContent(Entry) is TextBlock Text && Text.Text == NativeText,
                "The generated detail cell binds canonical property keys containing dots correctly");
            System.Windows.Controls.Primitives.DataGridColumnHeader? FindHeader(DependencyObject Parent)
            {
                for (var Index = 0; Index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(Parent); Index++)
                {
                    var Child = System.Windows.Media.VisualTreeHelper.GetChild(Parent, Index);
                    if (Child is System.Windows.Controls.Primitives.DataGridColumnHeader Header && ReferenceEquals(Header.Column, GridColumn)) return Header;
                    if (FindHeader(Child) is { } Found) return Found;
                }
                return null;
            }
            var DetailHeader = FindHeader(this.FileGrid);
            Check(DetailHeader?.ToolTip?.ToString() == CDir.Localization.LanguageService.Text("DateCreatedColumnHint"),
                "A rendered Windows detail header exposes its applicability tooltip; found=" + (DetailHeader is not null)
                    + "; tooltip=" + DetailHeader?.ToolTip);
            var Earlier = Path.Combine(Fixture, "Earlier.txt");
            File.WriteAllText(Earlier, "Owned sortable property fixture");
            File.SetCreationTime(Earlier, new DateTime(2020, 1, 2, 12, 0, 0));
            await this.RefreshAsync();
            await this.ShellColumnsTask;
            GridColumn.SortDirection = System.ComponentModel.ListSortDirection.Descending;
            this.FileGridSorting(this.FileGrid, new DataGridSortingEventArgs(GridColumn));
            Check(this.SortProperty == Extended.Key && this.View!.Cast<FileEntry>().First().FullPath == Earlier,
                "Windows detail properties sort by their native values instead of their formatted date strings");
            var Nested = Directory.CreateDirectory(Path.Combine(Fixture, "Nested")).FullName;
            File.WriteAllText(Path.Combine(Nested, "Deep.txt"), "Owned recursive property fixture");
            this.SearchBox.Text = "/file";
            await this.SearchTask; await this.ShellColumnsTask;
            Check(this.Entries.Any(Row => Row.Name == "Deep.txt" && Row[Extended.Key].Length > 0),
                "Recursively discovered search results load their enabled Windows detail properties");
            this.SearchBox.Clear(); await this.SearchTask;
            Check(this.ChangeColumnVisibility(Extended.Key, false) && GridColumn.Visibility == Visibility.Collapsed,
                "A Windows detail property can be disabled without navigating");
            Check(this.FileGrid.Columns.All(Column => Column.MinWidth == 0), "Every regular and Recycle Bin column has the same zero minimum width");
        }
        finally { this.SearchBox.Clear(); await this.NavigateAsync(Initial); this.SortProperty = nameof(FileEntry.Name); this.SortDirection = System.ComponentModel.ListSortDirection.Ascending; this.ApplySort(); this.RestoreColumnLayout(Order, Hidden); }
    }
}
