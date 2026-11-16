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
        var Menu = new ContextMenu { PlacementTarget = this.ColumnMenuButton, Placement = PlacementMode.Bottom,
            MaxHeight = SystemParameters.WorkArea.Height * .8, Style = (Style)this.FindResource("ScrollableColumnContextMenu") };
        Menu.SetResourceReference(Control.FontSizeProperty, "UIFontMenus");
        Menu.SetResourceReference(Control.FontFamilyProperty, "InterfaceFont");
        foreach (var Column in this.FileGrid.Columns.OrderBy(Column => Column.DisplayIndex))
        {
            if (!this.ColumnAvailable(Column.SortMemberPath) || !this.IsColumnEnabled(Column.SortMemberPath)) continue;
            if (Column.SortMemberPath == nameof(FileEntry.FullPath) && (!this.CurrentSearch.IsActive || this.IsRecycleBin)) continue;
            var Item = new MenuItem { Header = Column.Header, Tag = Column.SortMemberPath, IsCheckable = true,
                IsChecked = Column.Visibility == Visibility.Visible, IsEnabled = true, StaysOpenOnClick = true,
                Style = (Style)this.FindResource("ColumnToggleMenuItem") };
            if (ColumnHintResourceKey(Column.SortMemberPath) is { } Hint)
                Item.SetResourceReference(ToolTipProperty, "Text" + Hint);
            Item.Click += this.ColumnVisibilityMenuClick;
            Menu.Items.Add(Item);
        }
        Menu.Items.Add(new Separator());
        foreach (var Mode in Enum.GetValues<FileViewMode>())
        {
            var Item = new MenuItem { Tag = Mode, IsCheckable = true, IsChecked = Mode == this.Settings.FileView,
                StaysOpenOnClick = true, Style = (Style)this.FindResource("ColumnToggleMenuItem") };
            Item.SetResourceReference(HeaderedItemsControl.HeaderProperty, "TextView" + Mode);
            Item.Click += (_, Args) => { Args.Handled = true; this.ChangeFileView(Mode); };
            Menu.Items.Add(Item);
        }
        Menu.Items.Add(new Separator());
        Menu.Items.Add(this.CreateGroupingMenu());
        Menu.Items.Add(new Separator());
        foreach (var Reset in new[] { false, true })
        {
            var Action = new MenuItem { Tag = Reset ? "ResetFolderViews" : "ApplyViewToSubfolders", IsEnabled = !this.IsVirtualLocation,
                Style = (Style)this.FindResource("ColumnToggleMenuItem") };
            Action.SetResourceReference(HeaderedItemsControl.HeaderProperty, Reset ? "TextResetFolderViews" : "TextApplyViewToSubfolders");
            Action.Click += (_, Args) =>
            {
                Args.Handled = true;
                this.HostWindow.ApplyFolderViewRecursively(this.CurrentPath, Reset ? FileViewMode.Details : this.Settings.FileView,
                    Reset ? 0 : this.Settings.FileViewIconSize);
                if (!this.SaveSettings()) this.ShowNotice(LanguageService.Text("SettingsSaveError"), true);
                else this.ShowNotice(LanguageService.Text(Reset ? "FolderViewsReset" : "FolderViewApplied"));
            };
            Menu.Items.Add(Action);
        }
        Menu.Items.Add(new Separator());
        var HiddenItems = new MenuItem { Tag = "ShowHidden", IsCheckable = true, IsChecked = this.Settings.ShowHidden,
            StaysOpenOnClick = true, Style = (Style)this.FindResource("ColumnToggleMenuItem") };
        HiddenItems.SetResourceReference(HeaderedItemsControl.HeaderProperty, "TextHiddenItems");
        HiddenItems.SetResourceReference(ToolTipProperty, "TextHiddenItemsTooltip");
        HiddenItems.Click += async (_, E) =>
        {
            E.Handled = true;
            HiddenItems.IsEnabled = false;
            try
            {
                await this.ChangeShowHiddenAsync(!this.Settings.ShowHidden);
                await this.HostWindow.SynchronizePreferencesAsync(this);
            }
            finally { HiddenItems.IsEnabled = true; this.UpdateHiddenItemsMenu(); }
        };
        Menu.Items.Add(HiddenItems);
        this.ColumnMenuButton.ContextMenu = Menu;
        this.UpdateFileViewMenu();
        Menu.IsOpen = true;
    }

    private async void ColumnVisibilityMenuClick(object Sender, RoutedEventArgs E)
    {
        E.Handled = true;
        if (Sender is not MenuItem { Tag: string Key }) return;
        var Column = this.FileGrid.Columns.Single(Column => Column.SortMemberPath == Key);
        if (this.ChangeColumnVisibility(Key, Column.Visibility != Visibility.Visible))
            await this.HostWindow.SynchronizePreferencesAsync(this);
        if (this.ColumnMenuButton.ContextMenu is { } Menu)
            foreach (var Item in Menu.Items.OfType<MenuItem>().Where(Item => Item.Tag is string Key && ColumnKeys.Contains(Key)))
                Item.IsChecked = this.FileGrid.Columns.Single(Column => Column.SortMemberPath == (string)Item.Tag).Visibility == Visibility.Visible;
    }

    private void UpdateHiddenItemsMenu()
    {
        if (this.ColumnMenuButton.ContextMenu is { } Menu)
            foreach (var Item in Menu.Items.OfType<MenuItem>().Where(Item => Equals(Item.Tag, "ShowHidden")))
                Item.IsChecked = this.Settings.ShowHidden;
    }

    private static string ColumnResourceKey(string Key) => Key switch
    {
        nameof(FileEntry.Name) => "NameColumn",
        nameof(FileEntry.Modified) => "ModifiedColumn",
        nameof(FileEntry.TypeName) => "TypeColumn",
        nameof(FileEntry.Size) => "SizeColumn",
        nameof(FileEntry.FullPath) => "AbsolutePathColumn",
        nameof(FileEntry.OriginalLocation) => "OriginalLocationColumn",
        nameof(FileEntry.DateDeleted) => "DateDeletedColumn",
        _ => "DetailColumn_" + Key
    };

    private static string? ColumnHintResourceKey(string Key) => Key switch
    {
        nameof(FileEntry.FullPath) => "SearchColumnHint",
        nameof(FileEntry.OriginalLocation) => "OriginalLocationColumnHint",
        nameof(FileEntry.DateDeleted) => "DateDeletedColumnHint",
        "System.DateCreated" => "DateCreatedColumnHint",
        _ when WindowsColumns.Value.FirstOrDefault(Column => Column.Key == Key)?.RecycleOnly == true => "RecycleColumnHint",
        _ when Key.StartsWith("System.Audio.") || Key.StartsWith("System.Music.") => "AudioColumnHint",
        _ when Key.StartsWith("System.Video.") || Key.StartsWith("System.RecordedTV.") => "VideoColumnHint",
        _ when Key.StartsWith("System.Image.") || Key.StartsWith("System.Photo.") => "ImageColumnHint",
        _ when Key.StartsWith("System.Document.") => "DocumentColumnHint",
        _ when Key.StartsWith("System.Contact.") => "ContactColumnHint",
        _ when Key.StartsWith("System.Message.") => "MessageColumnHint",
        _ when Key.StartsWith("System.VersionControl.") => "VersionControlColumnHint",
        _ when !BasicColumnKeys.Contains(Key) => "WindowsColumnHint",
        _ => null
    };

    private void RestoreColumnLayout(IEnumerable<string>? Order, IEnumerable<string>? Hidden, IEnumerable<string>? Enabled = null)
    {
        this.InitializeShellColumns();
        var Known = (Order ?? DefaultSettings.Current.ColumnOrder).ToHashSet(StringComparer.Ordinal);
        var RestoredOrder = (Order ?? DefaultSettings.Current.ColumnOrder).Where(Key => ColumnKeys.Contains(Key, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).ToList();
        RestoredOrder.AddRange(ColumnKeys.Where(Key => !RestoredOrder.Contains(Key, StringComparer.Ordinal)));
        var RestoredHidden = (Hidden ?? DefaultSettings.Current.HiddenColumns).Where(Key => ColumnKeys.Contains(Key, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).ToList();
        RestoredHidden.AddRange(ColumnKeys.Where(Key => !Known.Contains(Key) && !BasicColumnKeys.Contains(Key) && !IsRecycleColumn(Key) && !RestoredHidden.Contains(Key)));
        if (ColumnKeys.Where(Key => Key != nameof(FileEntry.FullPath) && this.ColumnAvailable(Key)).All(RestoredHidden.Contains)) RestoredHidden.Remove(nameof(FileEntry.Name));
        this.Settings.ColumnOrder = RestoredOrder;
        var RestoredEnabled = (Enabled ?? ColumnKeys.Where(Key => BasicColumnKeys.Contains(Key) || IsRecycleColumn(Key) || !RestoredHidden.Contains(Key)))
            .Where(ColumnKeys.Contains).Distinct(StringComparer.Ordinal).ToList();
        if (!RestoredEnabled.Any(Key => Key != nameof(FileEntry.FullPath) && this.ColumnAvailable(Key) && !RestoredHidden.Contains(Key)))
        { if (!RestoredEnabled.Contains(nameof(FileEntry.Name))) RestoredEnabled.Add(nameof(FileEntry.Name)); RestoredHidden.Remove(nameof(FileEntry.Name)); }
        this.Settings.EnabledColumns = RestoredEnabled;
        this.Settings.HiddenColumns = RestoredHidden;
        for (var Index = 0; Index < RestoredOrder.Count; Index++)
            this.FileGrid.Columns.Single(Column => Column.SortMemberPath == RestoredOrder[Index]).DisplayIndex = Index;
        this.ApplyColumnVisibility();
        this.UpdateColumnHints();
    }

    private readonly Dictionary<string, Style> ColumnHeaderHintStyles = new(StringComparer.Ordinal);

    private void UpdateColumnHints()
    {
        foreach (var Column in this.FileGrid.Columns)
        {
            var Hint = ColumnHintResourceKey(Column.SortMemberPath);
            Column.SetValue(ToolTipService.ToolTipProperty, Hint is not null ? LanguageService.Text(Hint) : null);
            if (Hint is null) continue;
            if (!this.ColumnHeaderHintStyles.TryGetValue(Hint, out var HeaderStyle))
            {
                HeaderStyle = new Style(typeof(DataGridColumnHeader), (Style)this.FindResource(typeof(DataGridColumnHeader)));
                HeaderStyle.Setters.Add(new Setter(ToolTipProperty, new DynamicResourceExtension("Text" + Hint)));
                this.ColumnHeaderHintStyles[Hint] = HeaderStyle;
            }
            // Column is assigned after a virtualized header's style is applied.
            // A per-column style also works for newly realized/recycled headers.
            Column.HeaderStyle = HeaderStyle;
        }
    }

    private IReadOnlyList<ColumnPreference> GetColumnPreferences() => this.FileGrid.Columns.OrderBy(Column => Column.DisplayIndex)
        .Where(Column => ColumnKeys.Contains(Column.SortMemberPath, StringComparer.Ordinal))
        .Select(Column => new ColumnPreference(Column.SortMemberPath, ColumnResourceKey(Column.SortMemberPath),
            this.IsColumnEnabled(Column.SortMemberPath),
            Column.SortMemberPath == nameof(FileEntry.FullPath),
            LanguageService.GetTexts().ContainsKey(ColumnResourceKey(Column.SortMemberPath)) ? null : Column.Header?.ToString(),
            ColumnHintResourceKey(Column.SortMemberPath))).ToArray();

    private void ApplyColumnVisibility()
    {
        foreach (var Column in this.FileGrid.Columns)
        {
            if (!this.ColumnAvailable(Column.SortMemberPath) || !this.IsColumnEnabled(Column.SortMemberPath)) { Column.Visibility = Visibility.Collapsed; continue; }
            var Hidden = this.Settings.HiddenColumns?.Contains(Column.SortMemberPath, StringComparer.Ordinal) == true;
            var SearchOnly = Column.SortMemberPath == nameof(FileEntry.FullPath) && (!this.CurrentSearch.IsActive || this.IsRecycleBin);
            Column.Visibility = Hidden || SearchOnly ? Visibility.Collapsed : Visibility.Visible;
        }
        this.QueueFolderSizes();
        this.QueueShellColumnValues();
    }

    private bool IsColumnEnabled(string Key) => this.Settings.EnabledColumns?.Contains(Key, StringComparer.Ordinal) == true;

    private bool ChangeColumnEnabled(string Key, bool Enabled)
    {
        if (!ColumnKeys.Contains(Key) || this.WindowClosed) return false;
        if (!Enabled && this.IsColumnEnabled(Key) && !this.ChangeColumnVisibility(Key, false)) return false;
        var Keys = (this.Settings.EnabledColumns ?? []).ToHashSet(StringComparer.Ordinal);
        if (Enabled) Keys.Add(Key); else Keys.Remove(Key);
        this.Settings.EnabledColumns = ColumnKeys.Where(Keys.Contains).ToList();
        this.ApplyColumnVisibility();
        if (this.ColumnMenuButton.ContextMenu is { } Menu) Menu.IsOpen = false;
        if (!this.SaveSettings()) this.ShowNotice(LanguageService.Text("SettingsSaveError"), true);
        return true;
    }

    private bool ChangeColumnVisibility(string Key, bool Visible)
    {
        if (!ColumnKeys.Contains(Key, StringComparer.Ordinal) || !this.IsColumnEnabled(Key) || this.WindowClosed) return false;
        var Hidden = (this.Settings.HiddenColumns ?? []).ToHashSet(StringComparer.Ordinal);
        if (Visible) Hidden.Remove(Key);
        else Hidden.Add(Key);
        if (ColumnKeys.Where(Key => Key != nameof(FileEntry.FullPath) && this.ColumnAvailable(Key) && this.IsColumnEnabled(Key)).All(Hidden.Contains)) return false;
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
