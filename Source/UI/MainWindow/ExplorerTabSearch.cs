using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CDir.Localization;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private IReadOnlyList<FileEntry> FolderEntries = [];
    private SearchQuery CurrentSearch = SearchQuery.Parse("");
    private CancellationTokenSource? SearchCancellation;
    private Task SearchTask = Task.CompletedTask;
    private long SearchVersion;
    private bool Searching;
    private readonly HashSet<string> PendingSearchSelection = new(StringComparer.OrdinalIgnoreCase);
    private bool ApplyingSearchSelection;

    private (int Start, int Length, string Prefix) SearchTipToken()
    {
        var Text = this.SearchBox.Text;
        var Start = this.SearchBox.CaretIndex;
        var End = Start;
        while (Start > 0 && !char.IsWhiteSpace(Text[Start - 1])) Start--;
        while (End < Text.Length && !char.IsWhiteSpace(Text[End])) End++;
        return (Start, End - Start, Text[Start..End]);
    }

    private void UpdateSearchTips()
    {
        if (this.SearchTips is null) return;
        var Prefix = this.SearchTipToken().Prefix;
        var Empty = string.IsNullOrWhiteSpace(this.SearchBox.Text);
        bool Matches(string Command) => Empty || (Prefix.StartsWith('/') && Command.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase));
        this.SearchFileTip.Visibility = Matches("/file") ? Visibility.Visible : Visibility.Collapsed;
        this.SearchDirectoryTip.Visibility = Matches("/dir") ? Visibility.Visible : Visibility.Collapsed;
        this.SearchRegexTip.Visibility = Matches("/regex") || Matches("/r") ? Visibility.Visible : Visibility.Collapsed;
        this.SearchWildcardTip.Visibility = Matches("/wildcard") || Matches("/w") ? Visibility.Visible : Visibility.Collapsed;
        this.SearchTips.Visibility = this.SearchBox.IsFocused
            && (this.SearchFileTip.Visibility == Visibility.Visible || this.SearchDirectoryTip.Visibility == Visibility.Visible
                || this.SearchRegexTip.Visibility == Visibility.Visible || this.SearchWildcardTip.Visibility == Visibility.Visible)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SearchBoxGotFocus(object Sender, RoutedEventArgs E)
    {
        this.CancelSidebarFolderClick();
        this.UpdateSearchTips();
    }
    private void SearchBoxLostFocus(object Sender, RoutedEventArgs E) => this.SearchTips.Visibility = Visibility.Collapsed;
    private void SearchBoxSelectionChanged(object Sender, RoutedEventArgs E) => this.UpdateSearchTips();

    private void WindowInputPreviewMouseDown(object Sender, MouseButtonEventArgs E)
    {
        if (E.ChangedButton != MouseButton.Left || E.OriginalSource is DependencyObject PressSource && !this.SidebarPanel.IsAncestorOf(PressSource))
            this.CancelSidebarFolderClick();
        this.ClearSelectionOnBlankMouseDown(E);
        this.AddressEditPreviewMouseDown(E);
        if (E.Handled) return;
        if (!this.SearchBox.IsKeyboardFocusWithin && !this.SearchBox.IsFocused) return;
        for (var Source = E.OriginalSource as DependencyObject; Source is not null;)
        {
            if (ReferenceEquals(Source, this.SearchContainer) || ReferenceEquals(Source, this.SearchTips)) return;
            Source = Source is FrameworkContentElement Content ? Content.Parent : VisualTreeHelper.GetParent(Source);
        }
        // Empty panels do not take focus themselves. Move it before the click continues,
        // allowing buttons, text fields and other controls to perform their usual actions.
        FocusManager.SetFocusedElement(this, this.FileGrid);
        this.SearchTips.Visibility = Visibility.Collapsed;
    }

    private void SearchTipClick(object Sender, RoutedEventArgs E)
    {
        if (Sender is not Button { Tag: string Command }) return;
        var Token = this.SearchTipToken();
        var HasSeparator = Token.Start + Token.Length < this.SearchBox.Text.Length
            && char.IsWhiteSpace(this.SearchBox.Text[Token.Start + Token.Length]);
        this.SearchBox.Select(Token.Start, Token.Length);
        this.SearchBox.SelectedText = Command + (HasSeparator ? "" : " ");
        this.SearchBox.CaretIndex = Token.Start + Command.Length + 1;
        this.SearchTips.Visibility = Visibility.Collapsed;
    }

    private void CancelSearch()
    {
        this.SearchVersion++;
        this.SearchCancellation?.Cancel();
        this.SearchCancellation = null;
        this.Searching = false;
        this.PendingSearchSelection.Clear();
    }

    private void ShowEntries(IReadOnlyList<FileEntry> Entries, IEnumerable<string>? Selection = null)
    {
        this.EndMarqueeSelection();
        this.Entries = Entries as ObservableCollection<FileEntry> ?? new ObservableCollection<FileEntry>(Entries);
        this.RefreshCutFeedback();
        foreach (var Entry in Entries) Entry.Use12HourClock = this.Settings.Use12HourClock;
        this.View = CollectionViewSource.GetDefaultView(this.Entries);
        this.View.Filter = this.MatchesFilter;
        this.FileGrid.ItemsSource = this.View;
        // DataGrid clears a newly assigned view's sorting when ItemsSource changes.
        this.ApplySort();
        foreach (var Column in this.FileGrid.Columns)
            Column.SortDirection = Column.SortMemberPath == this.SortProperty ? this.SortDirection : null;
        if (Selection is not null)
            foreach (var Path in Selection) this.SelectPath(Path, Append: true);
        this.UpdateCounts();
        this.QueueFolderSizes();
    }

    private void SearchBoxTextChanged(object Sender, TextChangedEventArgs E)
    {
        if (this.SearchPlaceholder is null) return;
        this.SearchPlaceholder.Visibility = string.IsNullOrEmpty(this.SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        this.UpdateSearchTips();
        this.CurrentSearch = SearchQuery.Parse(this.SearchBox.Text);
        if (this.CurrentPath.Length == 0) return;
        if (this.IsRecycleBin)
        {
            this.CancelSearch();
            this.SearchTask = Task.CompletedTask;
            this.ApplyColumnVisibility();
            this.View?.Refresh();
            this.UpdateCounts();
            if (!this.CurrentSearch.IsActive && this.FolderRefreshPending) this.ScheduleRefresh();
            return;
        }
        if (!this.Loading) this.QueueSearch();
    }

    private bool MatchesFilter(object Entry) => Entry is FileEntry File && this.CurrentSearch.Matches(File);

    private void QueueSearch(bool Debounce = true, IEnumerable<string>? Selection = null)
    {
        if (this.IsRecycleBin)
        {
            this.CancelSearch();
            this.CurrentSearch = SearchQuery.Parse(this.SearchBox.Text);
            this.View?.Refresh();
            this.ApplyColumnVisibility();
            this.UpdateCounts();
            return;
        }
        var Selected = (Selection ?? this.SelectedEntries().Select(Entry => Entry.FullPath)).ToArray();
        this.CancelSearch();
        this.CurrentSearch = SearchQuery.Parse(this.SearchBox.Text);
        if (this.RefreshTimer.IsEnabled) this.FolderRefreshPending = true;
        this.RefreshTimer.Stop();
        this.ApplyColumnVisibility();
        if (!this.CurrentSearch.IsActive && this.SortProperty == nameof(FileEntry.FullPath))
        {
            this.SortProperty = nameof(FileEntry.Name);
            this.SortDirection = System.ComponentModel.ListSortDirection.Ascending;
        }
        if (!this.CurrentSearch.IsActive || this.CurrentSearch.ErrorKey is not null)
        {
            this.ShowEntries(this.CurrentSearch.ErrorKey is null ? this.FolderEntries : [], Selected);
            this.SearchTask = Task.CompletedTask;
        }
        else
        {
            this.Searching = true;
            var Items = new ObservableCollection<FileEntry>(this.FolderEntries);
            this.ShowEntries(Items, Selected);
            var Cancellation = new CancellationTokenSource();
            this.SearchCancellation = Cancellation;
            this.SearchTask = this.RunSearchAsync(this.CurrentPath, this.CurrentSearch, this.SearchVersion,
                this.Settings.ShowHidden, Items, Cancellation, Debounce);
        }
        if (!this.CurrentSearch.IsActive && this.FolderRefreshPending)
        {
            this.FolderRefreshPending = false;
            this.ScheduleRefresh();
        }
        this.UpdateActions();
        this.UpdateCounts();
    }

    private async Task RunSearchAsync(string Root, SearchQuery Query, long Version, bool ShowHidden,
        ObservableCollection<FileEntry> Items, CancellationTokenSource Cancellation, bool Debounce)
    {
        var Token = Cancellation.Token;
        var Known = this.FolderEntries.Select(Entry => Entry.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string? Failure = null;
        bool IsCurrent() => !Token.IsCancellationRequested && !this.WindowClosed && Version == this.SearchVersion;
        void Append(IReadOnlyList<FileEntry> Batch)
        {
            if (Token.IsCancellationRequested || Dispatcher.HasShutdownStarted) return;
            Dispatcher.Invoke(() =>
            {
                if (!IsCurrent()) return;
                foreach (var Entry in Batch)
                {
                    if (!Known.Add(Entry.FullPath)) continue;
                    Entry.Use12HourClock = this.Settings.Use12HourClock;
                    Entry.VersionStatus = this.VersionSnapshots.FirstOrDefault(Snapshot => Snapshot.Contains(Entry.FullPath))?.GetStatus(Entry.FullPath)
                        ?? VersionStatus.None;
                    Items.Add(Entry);
                }
                foreach (var Path in this.PendingSearchSelection.ToArray()) this.SelectPath(Path, Append: true);
                this.UpdateCounts();
            }, DispatcherPriority.Background, Token);
        }
        try
        {
            if (Debounce) await Task.Delay(250, Token);
            await Task.Run(() => this.Files.SearchEntries(Root, Query, ShowHidden, Token, Append), Token);
            if (IsCurrent()) this.ScheduleVersionRefresh();
        }
        catch (OperationCanceledException) { }
        catch (Exception Error) when (Error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            if (IsCurrent()) Failure = LanguageService.Text("SearchError", FriendlyError(Error));
        }
        finally
        {
            if (IsCurrent())
            {
                this.SearchCancellation = null;
                this.Searching = false;
                this.PendingSearchSelection.Clear();
                if (Query.ErrorKey is not null) this.ShowEntries([]);
                this.UpdateActions();
                this.UpdateCounts();
                if (Failure is not null) this.ShowNotice(Failure, true);
            }
            Cancellation.Dispose();
        }
    }
}
