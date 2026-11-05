using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using CDir.Localization;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private void ColumnMenuClick(object Sender, RoutedEventArgs E)
    {
        E.Handled = true;
        var Menu = new ContextMenu { PlacementTarget = this.ColumnMenuButton, Placement = PlacementMode.Bottom };
        Menu.SetResourceReference(Control.FontSizeProperty, "UIFontMenus");
        Menu.SetResourceReference(Control.FontFamilyProperty, "InterfaceFont");
        foreach (var Column in this.FileGrid.Columns.OrderBy(Column => Column.DisplayIndex))
        {
            if (Column.SortMemberPath == nameof(FileEntry.FullPath) && (!this.CurrentSearch.IsActive || this.IsRecycleBin)) continue;
            var Item = new MenuItem { Header = Column.Header, Tag = Column.SortMemberPath, IsCheckable = true,
                IsChecked = Column.Visibility == Visibility.Visible, IsEnabled = !IsRecycleColumn(Column.SortMemberPath), StaysOpenOnClick = true,
                Style = (Style)this.FindResource("ColumnToggleMenuItem") };
            Item.Click += this.ColumnVisibilityMenuClick;
            Menu.Items.Add(Item);
        }
        this.ColumnMenuButton.ContextMenu = Menu;
        Menu.IsOpen = true;
    }

    private async void ColumnVisibilityMenuClick(object Sender, RoutedEventArgs E)
    {
        E.Handled = true;
        if (Sender is not MenuItem { Tag: string Key } || IsRecycleColumn(Key)) return;
        var Column = this.FileGrid.Columns.Single(Column => Column.SortMemberPath == Key);
        if (this.ChangeColumnVisibility(Key, Column.Visibility != Visibility.Visible))
            await this.HostWindow.SynchronizePreferencesAsync(this);
        if (this.ColumnMenuButton.ContextMenu is { } Menu)
            foreach (var Item in Menu.Items.OfType<MenuItem>())
                Item.IsChecked = this.FileGrid.Columns.Single(Column => Column.SortMemberPath == (string)Item.Tag).Visibility == Visibility.Visible;
    }

    private static readonly string[] ColumnKeys = [nameof(FileEntry.Name), nameof(FileEntry.Modified), nameof(FileEntry.TypeName), nameof(FileEntry.Size), nameof(FileEntry.FullPath)];

    private static string ColumnResourceKey(string Key) => Key switch
    {
        nameof(FileEntry.Name) => "NameColumn",
        nameof(FileEntry.Modified) => "ModifiedColumn",
        nameof(FileEntry.TypeName) => "TypeColumn",
        nameof(FileEntry.Size) => "SizeColumn",
        nameof(FileEntry.FullPath) => "AbsolutePathColumn",
        _ => throw new ArgumentException("Unknown column key", nameof(Key))
    };

    private void RestoreColumnLayout(IEnumerable<string>? Order, IEnumerable<string>? Hidden)
    {
        var RestoredOrder = (Order ?? DefaultSettings.Current.ColumnOrder).Where(Key => ColumnKeys.Contains(Key, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).ToList();
        RestoredOrder.AddRange(ColumnKeys.Where(Key => !RestoredOrder.Contains(Key, StringComparer.Ordinal)));
        var RestoredHidden = (Hidden ?? DefaultSettings.Current.HiddenColumns).Where(Key => ColumnKeys.Contains(Key, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).ToList();
        if (ColumnKeys.Where(Key => Key != nameof(FileEntry.FullPath)).All(RestoredHidden.Contains)) RestoredHidden.Remove(nameof(FileEntry.Name));
        this.Settings.ColumnOrder = RestoredOrder;
        this.Settings.HiddenColumns = RestoredHidden;
        for (var Index = 0; Index < RestoredOrder.Count; Index++)
            this.FileGrid.Columns.Single(Column => Column.SortMemberPath == RestoredOrder[Index]).DisplayIndex = Index;
        this.ApplyColumnVisibility();
    }

    private IReadOnlyList<ColumnPreference> GetColumnPreferences() => this.FileGrid.Columns.OrderBy(Column => Column.DisplayIndex)
        .Where(Column => ColumnKeys.Contains(Column.SortMemberPath, StringComparer.Ordinal))
        .Select(Column => new ColumnPreference(Column.SortMemberPath, ColumnResourceKey(Column.SortMemberPath),
            this.Settings.HiddenColumns?.Contains(Column.SortMemberPath, StringComparer.Ordinal) != true,
            Column.SortMemberPath == nameof(FileEntry.FullPath))).ToArray();

    private void ApplyColumnVisibility()
    {
        foreach (var Column in this.FileGrid.Columns)
        {
            if (IsRecycleColumn(Column.SortMemberPath)) { Column.Visibility = this.IsRecycleBin ? Visibility.Visible : Visibility.Collapsed; continue; }
            var Hidden = this.Settings.HiddenColumns?.Contains(Column.SortMemberPath, StringComparer.Ordinal) == true;
            var SearchOnly = Column.SortMemberPath == nameof(FileEntry.FullPath) && (!this.CurrentSearch.IsActive || this.IsRecycleBin);
            Column.Visibility = Hidden || SearchOnly ? Visibility.Collapsed : Visibility.Visible;
        }
        this.QueueFolderSizes();
    }

    private bool ChangeColumnVisibility(string Key, bool Visible)
    {
        if (!ColumnKeys.Contains(Key, StringComparer.Ordinal) || this.WindowClosed) return false;
        var Hidden = (this.Settings.HiddenColumns ?? []).ToHashSet(StringComparer.Ordinal);
        if (Visible) Hidden.Remove(Key);
        else Hidden.Add(Key);
        if (ColumnKeys.Where(Key => Key != nameof(FileEntry.FullPath)).All(Hidden.Contains)) return false;
        this.Settings.HiddenColumns = ColumnKeys.Where(Hidden.Contains).ToList();
        this.ApplyColumnVisibility();
        if (Hidden.Contains(this.SortProperty))
        {
            this.SortProperty = this.FileGrid.Columns.Where(Column => Column.Visibility == Visibility.Visible)
                .OrderBy(Column => Column.DisplayIndex).First().SortMemberPath;
            this.SortDirection = ListSortDirection.Ascending;
            this.ApplySort();
            foreach (var Column in this.FileGrid.Columns)
                Column.SortDirection = Column.SortMemberPath == this.SortProperty ? this.SortDirection : null;
        }
        if (!this.SaveSettings()) this.ShowNotice(LanguageService.Text("SettingsSaveError"), true);
        return true;
    }

    private void FileGridColumnReordered(object? Sender, DataGridColumnEventArgs E)
    {
        this.Settings.ColumnOrder = this.FileGrid.Columns.OrderBy(Column => Column.DisplayIndex).Select(Column => Column.SortMemberPath).Where(ColumnKeys.Contains).ToList();
        if (!this.SaveSettings()) this.ShowNotice(LanguageService.Text("SettingsSaveError"), true);
    }
}
