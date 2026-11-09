using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CDir.Models;
using CDir.Localization;

namespace CDir.UI;

public partial class ExplorerTab
{
    internal void ShowRecoveryError() => this.ShowNotice(LanguageService.Text("SettingsSaveError"), true);
    internal void ActivateRecoveredPreferences()
    {
        this.ChangeLanguage(this.Settings.Language ?? DefaultSettings.Current.Language, Persist: false);
        this.ApplyUIFonts();
        this.HostWindow.UpdatePinnedTabLayout(this.Settings.SeparatePinnedTabs);
    }
    internal WorkspaceTabState CaptureWorkspaceTab()
    {
        var Settings = this.CaptureSaveSettings();
        Settings.LastPath = this.CurrentPath;
        Settings.ShowSidebar = this.Settings.ShowSidebar;
        Settings.SidebarWidth = this.Settings.ShowSidebar ? this.SidebarColumn.Width.Value : this.ExpandedSidebarWidth.Value;
        Settings.SidebarScrollOffset = this.SidebarScroll.VerticalOffset;
        Settings.NavigationTreeKey = this.NavigationTreeKey;
        Settings.SidebarTreeLayouts = this.CaptureSidebarTreeLayouts();
        Settings.CustomCategories = this.CustomCategories.Select(Category => Category.Clone()).ToList();
        Settings.SidebarOrder = this.SidebarSections.Select(this.SidebarSectionKey).ToList();
        Settings.ColumnOrder = this.FileGrid.Columns.OrderBy(Column => Column.DisplayIndex).Select(Column => Column.SortMemberPath).ToList();
        Settings.FavoritesExpanded = this.FavoritesSection.IsExpanded;
        Settings.DirectoryTreeExpanded = this.DirectoryTreeSection.IsExpanded;
        return new WorkspaceTabState
        {
            Settings = Settings, Alias = this.TabAlias, IsPinned = this.IsTabPinned, Search = this.SearchBox.Text,
            Sort = this.SortProperty, Descending = this.SortDirection == ListSortDirection.Descending,
            History = this.History.ToList(), HistoryTreeKeys = this.HistoryTreeKeys.ToList(), HistoryIndex = this.HistoryIndex,
            ColumnWidths = this.FileGrid.Columns.ToDictionary(Column => Column.SortMemberPath, Column => Column.ActualWidth),
            SelectedPaths = this.SelectedEntries().Select(Entry => Entry.FullPath).ToList(),
            ScrollOffset = FindVisualChild<ScrollViewer>(this.FileGrid)?.VerticalOffset ?? 0,
            TerminalVisible = this.TerminalPanel.Visibility == Visibility.Visible,
            TerminalHeight = this.TerminalPanel.Visibility == Visibility.Visible ? this.TerminalRow.ActualHeight : this.SavedTerminalHeight
        };
    }

    internal async Task RestoreWorkspaceTabAsync(WorkspaceTabState State)
    {
        await this.WaitUntilReadyAsync();
        this.SetTabAlias(State.Alias ?? "");
        this.HostWindow.SetTabPinned(this, State.IsPinned);
        if (State.History is { Count: > 0 } History && State.HistoryTreeKeys?.Count == History.Count
            && State.HistoryIndex >= 0 && State.HistoryIndex < History.Count
            && string.Equals(History[State.HistoryIndex], this.CurrentPath, StringComparison.OrdinalIgnoreCase))
        {
            this.History.Clear(); this.History.AddRange(History);
            this.HistoryTreeKeys.Clear(); this.HistoryTreeKeys.AddRange(State.HistoryTreeKeys);
            this.HistoryIndex = State.HistoryIndex;
            this.UpdateActions();
        }
        foreach (var Column in this.FileGrid.Columns)
            if (State.ColumnWidths?.TryGetValue(Column.SortMemberPath, out var Width) == true && double.IsFinite(Width) && Width >= 24 && Width <= 10000)
            {
                if (Width < Column.MinWidth) Column.MinWidth = 0;
                Column.Width = new DataGridLength(Width);
            }
        if (!string.IsNullOrEmpty(State.Search)) { this.SearchBox.Text = State.Search; await this.SearchTask; }
        // An enabled automatic fit takes precedence over saved widths when reopening a folder.
        if (this.Settings.AutoFitColumnsOnNavigation && !this.IsVirtualLocation) this.FitVisibleColumns();
        if (ColumnKeys.Contains(State.Sort)) this.SortProperty = State.Sort;
        this.SortDirection = State.Descending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        this.ApplySort();
        foreach (var Column in this.FileGrid.Columns) Column.SortDirection = Column.SortMemberPath == this.SortProperty ? this.SortDirection : null;
        foreach (var Path in State.SelectedPaths ?? []) this.SelectPath(Path, Append: true);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        if (double.IsFinite(State.ScrollOffset) && State.ScrollOffset >= 0)
            FindVisualChild<ScrollViewer>(this.FileGrid)?.ScrollToVerticalOffset(State.ScrollOffset);
        this.SavedTerminalHeight = double.IsFinite(State.TerminalHeight) ? Math.Clamp(State.TerminalHeight, 96, 2000) : 240;
        if (State.TerminalVisible) await this.ToggleTerminalAsync();
    }
}
