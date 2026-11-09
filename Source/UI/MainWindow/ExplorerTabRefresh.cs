using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CDir.Localization;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private long FolderRefreshVersion;
    private readonly SemaphoreSlim FolderRefreshGate = new(1);

    // Refresh the current location without navigating, rebuilding its controls, or
    // collapsing the sidebar. A later navigation/operation always wins an old read.
    private async Task RefreshCurrentFolderAsync(bool RefreshTree)
    {
        // A watcher read must not supersede the refresh awaited by a completed
        // operation. Serialize reads so completion also means the visible rows are current.
        await this.FolderRefreshGate.WaitAsync();
        try { await this.RefreshCurrentFolderCoreAsync(RefreshTree); }
        finally { this.FolderRefreshGate.Release(); }
    }

    private async Task RefreshCurrentFolderCoreAsync(bool RefreshTree)
    {
        if (this.WindowClosed || this.CurrentPath.Length == 0) return;
        if (this.InlineRenameTarget is not null) { this.FolderRefreshPending = true; return; }
        if (this.IsRecycleBin) { await this.RefreshRecycleBinAsync(); return; }
        if (this.IsComputer) { await this.NavigateAsync(this.CurrentPath, RecordHistory: false, PreserveFilter: true); return; }
        var Request = ++this.FolderRefreshVersion;
        var Navigation = this.NavigationVersion;
        var Search = this.SearchVersion;
        var Path = this.CurrentPath;
        var ShowHidden = this.Settings.ShowHidden;
        var Nodes = RefreshTree ? this.AllVersionNodes().Where(Node => Node.IsLoaded && Node.LoadTask is null)
            .Select(Node => (Node, Version: Node.LoadVersion)).ToArray() : [];
        this.RefreshTimer.Stop();
        try
        {
            var Updated = await Task.Run(() => this.Files.GetEntries(Path, ShowHidden));
            bool Current() => !this.WindowClosed && !this.OperationBusy && !this.Loading && Navigation == this.NavigationVersion
                && Search == this.SearchVersion && Request == this.FolderRefreshVersion && SamePath(Path, this.CurrentPath);
            if (this.InlineRenameTarget is not null) { this.FolderRefreshPending = true; return; }
            if (!Current()) return;
            this.FolderEntries = Updated;
            this.FolderRefreshPending = false;
            // The main panel must reflect deletion before scanning unrelated expanded trees.
            if (this.CurrentSearch.IsActive) { this.QueueSearch(Debounce: false); Search = this.SearchVersion; }
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

    private void RefreshFileRows(IReadOnlyList<FileEntry> Updated)
    {
        this.CancelFolderSizes();
        if (this.Entries is not ObservableCollection<FileEntry> Items)
        { this.ShowEntries(Updated); return; }
        var Selected = this.SelectedEntries().Select(Entry => Entry.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var Existing = Items.ToDictionary(Entry => Entry.FullPath, StringComparer.OrdinalIgnoreCase);
        var Desired = Updated.Select(Entry =>
        {
            if (Existing.TryGetValue(Entry.FullPath, out var Previous))
            {
                if (Previous.Name == Entry.Name && Previous.FullPath == Entry.FullPath && Previous.IsDirectory == Entry.IsDirectory
                    && (Previous.IsDirectory || Previous.Size == Entry.Size) && Previous.Modified == Entry.Modified && Previous.IsHidden == Entry.IsHidden
                    && Previous.TypeKey == Entry.TypeKey && Previous.Extension == Entry.Extension
                    && Previous.OriginalLocation == Entry.OriginalLocation && Previous.DateDeleted == Entry.DateDeleted) return Previous;
                Entry.VersionStatus = Previous.VersionStatus;
                Entry.IsCut = Previous.IsCut;
            }
            Entry.Use12HourClock = this.Settings.Use12HourClock;
            return Entry;
        }).ToArray();
        var Viewer = FindVisualChild<ScrollViewer>(this.FileGrid);
        var Vertical = Viewer?.VerticalOffset ?? 0;
        var Horizontal = Viewer?.HorizontalOffset ?? 0;
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
            this.FileGrid.UpdateLayout();
            if (Math.Abs(Viewer.VerticalOffset - Vertical) > 0.5) Viewer.ScrollToVerticalOffset(Vertical);
            if (Math.Abs(Viewer.HorizontalOffset - Horizontal) > 0.5) Viewer.ScrollToHorizontalOffset(Horizontal);
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
        for (var Index = 0; Index < Desired.Count; Index++)
        {
            if (Index < Items.Count && ReferenceEquals(Items[Index], Desired[Index])) continue;
            var Previous = Items.IndexOf(Desired[Index]);
            if (Previous >= 0) Items.Move(Previous, Index);
            else Items.Insert(Index, Desired[Index]);
        }
    }
}
