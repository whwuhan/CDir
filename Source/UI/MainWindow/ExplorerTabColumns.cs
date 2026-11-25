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
        if (Sender is not MenuItem { Tag: string Key } || !this.ColumnsByKey.TryGetValue(Key, out var Column)) return;
        if (this.ChangeColumnVisibility(Key, Column.Visibility != Visibility.Visible))
            await this.HostWindow.SynchronizePreferencesAsync(this);
        if (this.ColumnMenuButton.ContextMenu is { } Menu)
            foreach (var Item in Menu.Items.OfType<MenuItem>())
                if (Item.Tag is string ItemKey && this.ColumnsByKey.TryGetValue(ItemKey, out var MenuColumn))
                    Item.IsChecked = MenuColumn.Visibility == Visibility.Visible;
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
        _ when WindowsColumnsByKey.Value.TryGetValue(Key, out var Column) && Column.RecycleOnly => "RecycleColumnHint",
        _ when Key.StartsWith("System.Audio.") || Key.StartsWith("System.Music.") => "AudioColumnHint",
        _ when Key.StartsWith("System.Video.") || Key.StartsWith("System.RecordedTV.") => "VideoColumnHint",
        _ when Key.StartsWith("System.Image.") || Key.StartsWith("System.Photo.") => "ImageColumnHint",
        _ when Key.StartsWith("System.Document.") => "DocumentColumnHint",
        _ when Key.StartsWith("System.Contact.") => "ContactColumnHint",
        _ when Key.StartsWith("System.Message.") => "MessageColumnHint",
        _ when Key.StartsWith("System.VersionControl.") => "VersionControlColumnHint",
        _ when !BasicColumnKeySet.Contains(Key) => "WindowsColumnHint",
        _ => null
    };

    private void RestoreColumnLayout(IEnumerable<string>? Order, IEnumerable<string>? Hidden, IEnumerable<string>? Enabled = null)
    {
        this.InitializeShellColumns();
        var AllSet = AllColumnKeySet.Value;
        var Known = (Order ?? DefaultSettings.Current.ColumnOrder).ToHashSet(StringComparer.Ordinal);
        var RestoredOrder = new List<string>(ColumnKeys.Length);
        var RestoredOrderSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var Key in Order ?? DefaultSettings.Current.ColumnOrder)
            if (AllSet.Contains(Key) && RestoredOrderSet.Add(Key)) RestoredOrder.Add(Key);
        foreach (var Key in ColumnKeys)
            if (RestoredOrderSet.Add(Key)) RestoredOrder.Add(Key);
        var RestoredHidden = new List<string>();
        var RestoredHiddenSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var Key in Hidden ?? DefaultSettings.Current.HiddenColumns)
            if (AllSet.Contains(Key) && RestoredHiddenSet.Add(Key)) RestoredHidden.Add(Key);
        foreach (var Key in ColumnKeys)
            if (!Known.Contains(Key) && !BasicColumnKeySet.Contains(Key) && !IsRecycleColumn(Key) && RestoredHiddenSet.Add(Key))
                RestoredHidden.Add(Key);
        if (ColumnKeys.Where(Key => Key != nameof(FileEntry.FullPath) && this.ColumnAvailable(Key)).All(RestoredHiddenSet.Contains))
        {
            RestoredHidden.Remove(nameof(FileEntry.Name));
            RestoredHiddenSet.Remove(nameof(FileEntry.Name));
        }
        this.Settings.ColumnOrder = RestoredOrder;
        var RestoredEnabled = new List<string>();
        var RestoredEnabledSet = new HashSet<string>(StringComparer.Ordinal);
        IEnumerable<string> EnabledSource = Enabled ?? ColumnKeys.Where(Key => BasicColumnKeySet.Contains(Key) || IsRecycleColumn(Key) || !RestoredHiddenSet.Contains(Key));
        foreach (var Key in EnabledSource)
            if (AllSet.Contains(Key) && RestoredEnabledSet.Add(Key)) RestoredEnabled.Add(Key);
        if (!RestoredEnabled.Any(Key => Key != nameof(FileEntry.FullPath) && this.ColumnAvailable(Key) && !RestoredHiddenSet.Contains(Key)))
        {
            if (RestoredEnabledSet.Add(nameof(FileEntry.Name))) RestoredEnabled.Add(nameof(FileEntry.Name));
            RestoredHidden.Remove(nameof(FileEntry.Name));
        }
        this.Settings.EnabledColumns = RestoredEnabled;
        this.Settings.HiddenColumns = RestoredHidden;
        for (var Index = 0; Index < RestoredOrder.Count; Index++)
        {
            var Column = this.ColumnsByKey[RestoredOrder[Index]];
            if (Column.DisplayIndex != Index) Column.DisplayIndex = Index;
        }
        this.ApplyColumnVisibility();
        this.UpdateColumnHints();
    }

    private readonly Dictionary<string, Style> ColumnHeaderHintStyles = new(StringComparer.Ordinal);

    private void UpdateColumnHints()
    {
        foreach (var Column in this.FileGrid.Columns)
        {
            var Hint = ColumnHintResourceKey(Column.SortMemberPath);
            var Tip = Hint is not null ? LanguageService.Text(Hint) : null;
            if (!Equals(Column.GetValue(ToolTipService.ToolTipProperty), Tip))
                Column.SetValue(ToolTipService.ToolTipProperty, Tip);
            if (Hint is null) continue;
            if (!this.ColumnHeaderHintStyles.TryGetValue(Hint, out var HeaderStyle))
            {
                HeaderStyle = new Style(typeof(DataGridColumnHeader), (Style)this.FindResource(typeof(DataGridColumnHeader)));
                HeaderStyle.Setters.Add(new Setter(ToolTipProperty, new DynamicResourceExtension("Text" + Hint)));
                this.ColumnHeaderHintStyles[Hint] = HeaderStyle;
            }
            // Column is assigned after a virtualized header's style is applied.
            // A per-column style also works for newly realized/recycled headers.
            if (!ReferenceEquals(Column.HeaderStyle, HeaderStyle)) Column.HeaderStyle = HeaderStyle;
        }
    }

    private IReadOnlyList<ColumnPreference> GetColumnPreferences()
    {
        var Texts = LanguageService.GetTexts();
        var EnabledSet = this.Settings.EnabledColumns is { Count: > 0 } List ? new HashSet<string>(List, StringComparer.Ordinal) : null;
        return this.FileGrid.Columns.OrderBy(Column => Column.DisplayIndex)
            .Where(Column => AllColumnKeySet.Value.Contains(Column.SortMemberPath))
            .Select(Column =>
            {
                var ResourceKey = ColumnResourceKey(Column.SortMemberPath);
                return new ColumnPreference(Column.SortMemberPath, ResourceKey,
                    EnabledSet?.Contains(Column.SortMemberPath) == true,
                    Column.SortMemberPath == nameof(FileEntry.FullPath),
                    Texts.ContainsKey(ResourceKey) ? null : Column.Header?.ToString(),
                    ColumnHintResourceKey(Column.SortMemberPath));
            }).ToArray();
    }

    private void ApplyColumnVisibility()
    {
        var EnabledSet = this.Settings.EnabledColumns is { Count: > 0 } Enabled ? new HashSet<string>(Enabled, StringComparer.Ordinal) : null;
        var HiddenSet = this.Settings.HiddenColumns is { Count: > 0 } Hidden ? new HashSet<string>(Hidden, StringComparer.Ordinal) : null;
        var HideSearchPath = !this.CurrentSearch.IsActive || this.IsRecycleBin;
        foreach (var Column in this.FileGrid.Columns)
        {
            var Key = Column.SortMemberPath;
            Visibility Desired;
            if (!this.ColumnAvailable(Key) || EnabledSet?.Contains(Key) != true) Desired = Visibility.Collapsed;
            else
            {
                var IsHidden = HiddenSet?.Contains(Key) == true;
                var SearchOnly = Key == nameof(FileEntry.FullPath) && HideSearchPath;
                Desired = IsHidden || SearchOnly ? Visibility.Collapsed : Visibility.Visible;
            }
            if (Column.Visibility != Desired) Column.Visibility = Desired;
        }
        this.QueueFolderSizes();
        this.QueueShellColumnValues();
    }

    private bool IsColumnEnabled(string Key) => this.Settings.EnabledColumns?.Contains(Key, StringComparer.Ordinal) == true;

    private bool ChangeColumnEnabled(string Key, bool Enabled)
    {
        if (!AllColumnKeySet.Value.Contains(Key) || this.WindowClosed) return false;
        if (!Enabled && this.IsColumnEnabled(Key) && !this.ChangeColumnVisibility(Key, false)) return false;
        var Keys = (this.Settings.EnabledColumns ?? []).ToHashSet(StringComparer.Ordinal);
        if (Enabled) Keys.Add(Key); else Keys.Remove(Key);
        this.Settings.EnabledColumns = ColumnKeys.Where(Keys.Contains).ToList();
        this.ApplyColumnVisibility();
        if (!Enabled) this.ReleaseRemovedColumn(Key);
        if (this.ColumnMenuButton.ContextMenu is { } Menu) Menu.IsOpen = false;
        if (!this.SaveSettings()) this.ShowNotice(LanguageService.Text("SettingsSaveError"), true);
        return true;
    }

    // Removing a column from the column menu removes it from Sort By and Group By too.
    private void ReleaseRemovedColumn(string Key)
    {
        if (this.SortProperty != Key && this.FileGroupProperty != Key) return;
        if (this.FileGroupProperty == Key) { this.FileGroupProperty = ""; this.FileGroupDescending = false; }
        if (this.SortProperty == Key)
        {
            this.SortProperty = this.FileGrid.Columns.Where(Column => Column.Visibility == Visibility.Visible)
                .OrderBy(Column => Column.DisplayIndex).First().SortMemberPath;
            this.SortDirection = ListSortDirection.Ascending;
        }
        this.ApplySort();
        foreach (var Column in this.FileGrid.Columns)
            Column.SortDirection = Column.SortMemberPath == this.SortProperty ? this.SortDirection : null;
    }

    private bool ChangeColumnVisibility(string Key, bool Visible)
    {
        if (!AllColumnKeySet.Value.Contains(Key) || !this.IsColumnEnabled(Key) || this.WindowClosed) return false;
        var Hidden = (this.Settings.HiddenColumns ?? []).ToHashSet(StringComparer.Ordinal);
        if (Visible) Hidden.Remove(Key);
        else Hidden.Add(Key);
        var EnabledSet = new HashSet<string>(this.Settings.EnabledColumns ?? [], StringComparer.Ordinal);
        if (ColumnKeys.Where(Item => Item != nameof(FileEntry.FullPath) && this.ColumnAvailable(Item) && EnabledSet.Contains(Item)).All(Hidden.Contains)) return false;
        this.Settings.HiddenColumns = ColumnKeys.Where(Hidden.Contains).ToList();
        // A hidden column still orders or groups the view; only removing it from the
        // column menu releases it.
        this.ApplyColumnVisibility();
        if (!this.SaveSettings()) this.ShowNotice(LanguageService.Text("SettingsSaveError"), true);
        return true;
    }

    private void FileGridColumnReordered(object? Sender, DataGridColumnEventArgs E)
    {
        this.Settings.ColumnOrder = this.FileGrid.Columns.OrderBy(Column => Column.DisplayIndex).Select(Column => Column.SortMemberPath).Where(AllColumnKeySet.Value.Contains).ToList();
        if (!this.SaveSettings()) this.ShowNotice(LanguageService.Text("SettingsSaveError"), true);
    }
}
