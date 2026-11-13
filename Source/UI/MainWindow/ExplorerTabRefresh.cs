using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CDir.Localization;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private long FolderRefreshVersion;
    private readonly SemaphoreSlim FolderRefreshGate = new(1);

    // Refresh the current location without navigating, rebuilding its controls, or
    // collapsing the sidebar. A later navigation/operation always wins an old read.
    private async Task RefreshCurrentFolderAsync(bool RefreshTree, bool NavigationTreeOnly = false, bool RebuildRows = false)
    {
        // A watcher read must not supersede the refresh awaited by a completed
        // operation. Serialize reads so completion also means the visible rows are current.
        await this.FolderRefreshGate.WaitAsync();
        try { await this.RefreshCurrentFolderCoreAsync(RefreshTree, NavigationTreeOnly, RebuildRows); }
        finally { this.FolderRefreshGate.Release(); }
    }

    private async Task RefreshCurrentFolderCoreAsync(bool RefreshTree, bool NavigationTreeOnly, bool RebuildRows)
    {
        if (this.WindowClosed || this.CurrentPath.Length == 0) return;
        if (this.InlineRenameTarget is not null) { this.FolderRefreshPending = true; return; }
        if (this.IsRecycleBin) { await this.RefreshRecycleBinAsync(RebuildRows); return; }
        if (this.IsComputer) { await this.NavigateAsync(this.CurrentPath, RecordHistory: false, PreserveFilter: true); return; }
        var Request = ++this.FolderRefreshVersion;
        var Navigation = this.NavigationVersion;
        var Search = this.SearchVersion;
        var Path = this.CurrentPath;
        var ShowHidden = this.Settings.ShowHidden;
        var Roots = this.NavigationTreeKey == "folder-tree" ? this.Roots.ToArray()
            : this.NavigationSection(this.NavigationTreeKey) is { } Section ? this.CategoryFolderRoots(Section) : [];
        var Nodes = RefreshTree ? this.AllVersionNodes(NavigationTreeOnly ? Roots : null).Where(Node => Node.IsLoaded && Node.LoadTask is null)
            .Select(Node => (Node, Version: Node.LoadVersion)).ToArray() : [];
        this.RefreshTimer.Stop();
        try
        {
            var Updated = await Task.Run(() => this.Files.GetEntries(Path, ShowHidden, SortByName: false));
            bool Current() => !this.WindowClosed && !this.OperationBusy && !this.Loading && Navigation == this.NavigationVersion
                && Search == this.SearchVersion && Request == this.FolderRefreshVersion && SamePath(Path, this.CurrentPath);
            if (this.InlineRenameTarget is not null) { this.FolderRefreshPending = true; return; }
            if (!Current()) return;
            // Explicit refresh also remeasures completed folder totals. Retaining
            // row objects must not retain the previous scan's completion markers.
            if (RefreshTree && NavigationTreeOnly)
            {
                this.CancelFolderSizes();
                foreach (var Entry in this.Entries) Entry.ResetFolderSize();
            }
            this.FolderEntries = Updated;
            this.FolderRefreshPending = false;
            // The main panel must reflect deletion before scanning unrelated expanded trees.
            if (RebuildRows)
            {
                if (!await this.RebuildFileViewAsync(Current)) return;
                Search = this.SearchVersion;
            }
            else if (this.CurrentSearch.IsActive) { this.QueueSearch(Debounce: false); Search = this.SearchVersion; }
            else this.RefreshFileRows(Updated);
            this.QueueDriveUsageRefresh(Path);
            var Trees = await Task.Run(() =>
            {
                var Known = new Dictionary<string, IReadOnlyList<DirectoryEntry>?>(StringComparer.OrdinalIgnoreCase);
                IReadOnlyList<DirectoryEntry>? ReadChildren(string Directory)
                {
                    if (Known.TryGetValue(Directory, out var Cached)) return Cached;
                    try { return Known[Directory] = this.Files.GetDirectories(Directory, ShowHidden); }
                    catch (Exception Error) when (Error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                    { return Known[Directory] = null; }
                }
                return Nodes.Select(Item => (Item.Node, Item.Version, Children: ReadChildren(Item.Node.FullPath))).ToArray();
            });
            if (!Current()) return;
            var WasSyncing = this.SyncingTree;
            var WasSuppressed = this.SuppressTreeSelectionScroll;
            this.SyncingTree = true;
            this.SuppressTreeSelectionScroll = true;
            try
            {
                foreach (var Tree in Trees)
                {
                    if (Tree.Children is null || !Tree.Node.IsLoaded || Tree.Node.LoadTask is not null || Tree.Node.LoadVersion != Tree.Version) continue;
                    var Existing = Tree.Node.Children.Where(Node => !Node.IsPlaceholder)
                        .ToDictionary(Node => Node.FullPath, StringComparer.OrdinalIgnoreCase);
                    var Children = Tree.Children.Select(Child => Existing.TryGetValue(Child.FullPath, out var Node)
                        && Node.Name == Child.Name ? Node : new FolderNode(Child.Name, Child.FullPath)).ToArray();
                    ReconcileItems(Tree.Node.Children, Children);
                }
            }
            finally { this.SyncingTree = WasSyncing; this.SuppressTreeSelectionScroll = WasSuppressed; }


            this.ScheduleVersionRefresh();
        }
        catch (Exception Error) when (Error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            if (!this.WindowClosed && Navigation == this.NavigationVersion && Request == this.FolderRefreshVersion)
                this.ShowNotice(LanguageService.Text("OpenLocationError", FriendlyError(Error)), true);
        }
    }

    private async Task<bool> RebuildFileViewAsync(Func<bool> IsCurrent)
    {
        var Selected = this.SelectedEntries().Select(Entry => Entry.FullPath).ToArray();
        var Viewer = FindVisualChild<ScrollViewer>(this.ActiveFileControl);
        var Vertical = Viewer?.VerticalOffset ?? 0;
        var Horizontal = Viewer?.HorizontalOffset ?? 0;
        var PreviousView = this.View;
        this.CancelFolderSizes();
        this.EndMarqueeSelection();
        void Bind(System.Collections.IEnumerable? Items)
        {
            this.SyncingFileViews = true;
            try { this.FileTiles.ItemsSource = Items; this.FileGrid.ItemsSource = Items; }
            finally { this.SyncingFileViews = false; }
        }
        Bind(null);
        // Let WPF unload and paint the cleared view before recreating its rows and
        // icons. This is a real manual reload, without a simulated flash or delay.
        await Dispatcher.Yield(DispatcherPriority.Background);
        if (!IsCurrent())
        {
            // A newer navigation/search owns any view it has already installed.
            if (!this.WindowClosed && ReferenceEquals(this.View, PreviousView) && this.FileGrid.ItemsSource is null)
            {
                Bind(PreviousView);
                foreach (var Path in Selected) this.SelectPath(Path, Append: true, Reveal: false);
            }
            return false;
        }
        // Restore selection independently of ensuring it is visible. Revealing
        // each item here generates off-panel containers and moves the viewport.
        if (this.CurrentSearch.IsActive && !this.IsVirtualLocation) this.QueueSearch(Debounce: false, Selection: Selected, RevealSelection: false);
        else this.ShowEntries(this.FolderEntries, Selected, RevealSelection: false);
        this.ActiveFileControl.UpdateLayout();
        Viewer?.ScrollToVerticalOffset(Vertical);
        Viewer?.ScrollToHorizontalOffset(Horizontal);
        return true;
    }

    private void RefreshFileRows(IReadOnlyList<FileEntry> Updated)
    {
        this.CancelFolderSizes();
        if (this.Entries is not ObservableCollection<FileEntry> Items)
        { this.ShowEntries(Updated); return; }
        // Metadata commits can reorder a sorted/grouped view before collection
        // reconciliation. Capture the user's viewport before either can move it.
        var Viewer = FindVisualChild<ScrollViewer>(this.ActiveFileControl);
        var Vertical = Viewer?.VerticalOffset ?? 0;
        var Horizontal = Viewer?.HorizontalOffset ?? 0;
        var Selected = this.SelectedEntries().Select(Entry => Entry.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var Existing = Items.ToDictionary(Entry => Entry.FullPath, StringComparer.OrdinalIgnoreCase);
        var Desired = Updated.Select(Entry =>
        {
            if (Existing.TryGetValue(Entry.FullPath, out var Previous))
            {
                if (Previous.Name == Entry.Name && Previous.FullPath == Entry.FullPath && Previous.IsDirectory == Entry.IsDirectory
                    && Previous.TypeKey == Entry.TypeKey && Previous.Extension == Entry.Extension
                    && Previous.OriginalLocation == Entry.OriginalLocation && Previous.DateDeleted == Entry.DateDeleted)
                {
                    // Commit only changes which affect the active ordering/group.
                    // Ordinary timestamp/size updates must not remove and reinsert
                    // rows, reset the view, or disturb virtualized icon containers.
                    bool Changed(string Property) => Property == nameof(FileEntry.Modified) && Previous.Modified != Entry.Modified
                        || Property == nameof(FileEntry.Size) && !Previous.IsDirectory && Previous.Size != Entry.Size;
                    var Reposition = Changed(this.SortProperty) || Changed(this.FileGroupProperty);
                    var List = Reposition ? this.View as System.Windows.Data.ListCollectionView : null;
                    List?.EditItem(Previous);
                    try
                    {
                        Previous.UpdateMetadata(Entry);
                        Previous.Use12HourClock = this.Settings.Use12HourClock;
                        this.AssignFileGroup(Previous, DateTime.Today);
                    }
                    finally { List?.CommitEdit(); }
                    return Previous;
                }
                Entry.VersionStatus = Previous.VersionStatus;
                Entry.IsCut = Previous.IsCut;
            }
            Entry.Use12HourClock = this.Settings.Use12HourClock;
            this.AssignFileGroup(Entry, DateTime.Today);
            return Entry;
        }).ToArray();
        var WasApplying = this.ApplyingSearchSelection;
        this.ApplyingSearchSelection = true;
        try
        {
            ReconcileItems(Items, Desired);
            foreach (var Entry in Desired.Where(Entry => Selected.Contains(Entry.FullPath)))
                if (!this.FileGrid.SelectedItems.Contains(Entry)) this.FileGrid.SelectedItems.Add(Entry);
        }
        finally { this.ApplyingSearchSelection = WasApplying; }
        this.RefreshCutFeedback();
        if (Viewer is not null)
        {
            this.ActiveFileControl.UpdateLayout();
            if (Math.Abs(Viewer.VerticalOffset - Vertical) > 0.001) Viewer.ScrollToVerticalOffset(Vertical);
            if (Math.Abs(Viewer.HorizontalOffset - Horizontal) > 0.001) Viewer.ScrollToHorizontalOffset(Horizontal);
        }
        this.UpdateActions();
        this.UpdateCounts();
        this.QueueFolderSizes();
    }

    private static void ReconcileItems<T>(ObservableCollection<T> Items, IReadOnlyList<T> Desired) where T : class
    {
        var Retained = Desired.ToHashSet();
        for (var Index = Items.Count - 1; Index >= 0; Index--)
            if (!Retained.Contains(Items[Index])) Items.RemoveAt(Index);
        var Existing = Items.ToHashSet();
        for (var Index = 0; Index < Desired.Count; Index++)
        {
            if (Index < Items.Count && ReferenceEquals(Items[Index], Desired[Index])) continue;
            var Previous = Existing.Contains(Desired[Index]) ? Items.IndexOf(Desired[Index]) : -1;
            if (Previous >= 0) Items.Move(Previous, Index);
            else Items.Insert(Index, Desired[Index]);
        }
    }
}
