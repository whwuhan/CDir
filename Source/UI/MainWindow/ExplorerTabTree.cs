using CDir.Models;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace CDir.UI;

public partial class ExplorerTab
{
    private int TreeExpansionVersion;
    private Task TreeExpansionTask = Task.CompletedTask;
    private Task SidebarClickTask = Task.CompletedTask;
    private DependencyObject? SidebarClickTarget;
    private long SidebarClickDeadline;
    private Task PanelTreeSyncTask = Task.CompletedTask;
    private bool PanelTreeSyncRequested;
    private int PanelFocusVersion;
    private int CollapseFocusVersion;
    private bool RestoringCollapseFocus;
    private string NavigationTreeKey = "folder-tree";
    private string? NavigatingTreeKey;
    private readonly List<string> HistoryTreeKeys = [];

    private string TreeKeyForSection(Expander Section) => this.SidebarSectionKey(
        Section.DataContext is SidebarCategory Category ? Category : Section);

    private Expander? NavigationSection(string Key) => this.SidebarSections
        .Where(Section => this.SidebarSectionKey(Section) == Key).Select(this.SidebarSectionElement).FirstOrDefault();

    private void UseNavigationTree(string Key)
    {
        this.NavigationTreeKey = Key;
        ++this.PanelFocusVersion;
        this.PanelTreeSyncRequested = false;
        if (this.HistoryIndex >= 0 && this.HistoryIndex < this.HistoryTreeKeys.Count)
            this.HistoryTreeKeys[this.HistoryIndex] = Key;
    }

    private Task NavigateFromTreeAsync(FolderNode Node, string Key)
    {
        this.SelectSidebarNode(Node);
        var NeedsNavigation = this.Loading
            ? !SamePath(Node.FullPath, this.NavigatingPath ?? "") || this.NavigatingTreeKey != Key
            : !SamePath(Node.FullPath, this.CurrentPath);
        if (NeedsNavigation)
            return this.NavigateAsync(Node.FullPath, RevealTree: false, SyncNavigationTree: false, TreeKey: Key);
        if (!this.Loading)
        {
            this.UseNavigationTree(Key);
        }
        return Task.CompletedTask;
    }

    private void SelectSidebarNode(FolderNode? Selected)
    {
        var WasSyncing = this.SyncingTree;
        this.SyncingTree = true;
        try
        {
            // Each category has its own tree control, but the sidebar has one selection.
            // Only visit loaded nodes so changing highlight never expands another tree.
            foreach (var Root in this.Roots.Concat(this.SidebarBookmarkRoots())) ClearTreeSelection(Root, Except: Selected);
            if (Selected is not null)
            {
                Selected.IsSelected = true;
            }
        }
        finally { this.SyncingTree = WasSyncing; }
    }

    private Task OpenSidebarFolderClickAsync(Func<Task> Open, DependencyObject? Target = null)
    {
        this.CancelSidebarFolderClick();
        if (this.WindowClosed) return this.SidebarClickTask = Task.CompletedTask;
        // Start navigation on the first click. Track its label only so a matching
        // second press can toggle expansion without launching another navigation.
        var Navigation = Open();
        this.SidebarClickTarget = Target;
        this.SidebarClickDeadline = Environment.TickCount64 + GetDoubleClickTime();
        return this.SidebarClickTask = Navigation;
    }

    private void CancelSidebarFolderClick()
    {
        this.SidebarClickTarget = null;
    }

    private bool ConsumeSidebarDoubleClick(DependencyObject? Target, int ClickCount)
    {
        var DoubleClick = ClickCount == 2 && Target is not null && ReferenceEquals(Target, this.SidebarClickTarget)
            && Environment.TickCount64 <= this.SidebarClickDeadline;
        this.CancelSidebarFolderClick();
        return DoubleClick;
    }

    private static bool IsCollapsedAncestorSelection(object? Previous, FolderNode Selected) =>
        Previous is FolderNode Old && !Selected.IsExpanded && !Old.IsPlaceholder
        && Old.FullPath.StartsWith(Selected.FullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private async void FileGridKeyboardFocusWithinChanged(object Sender, DependencyPropertyChangedEventArgs E)
    {
        if (this.IsVirtualLocation) return;
        if (E.NewValue is true) this.CancelSidebarFolderClick();
        var FocusVersion = ++this.PanelFocusVersion;
        if (this.RestoringTabFocus || this.RestoringCollapseFocus) { this.PanelTreeSyncRequested = false; return; }
        // OLE dragging can temporarily move focus away from the file list. Keep
        // its pending reveal until the pointer gesture has actually finished.
        if (E.NewValue is not true && this.PanelTreeSyncDeferred) return;
        this.PanelTreeSyncRequested = E.NewValue is true;
        if (!this.PanelTreeSyncRequested) return;
        await (this.PanelTreeSyncTask = SynchronizeAfterFocusAsync());

        async Task SynchronizeAfterFocusAsync()
        {
            // A preview click can briefly focus the grid on its way from the search
            // field to a sidebar button. Wait until the click's real focus is settled.
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            if (FocusVersion != this.PanelFocusVersion || this.WindowClosed) return;
            if (!this.ActiveFileControl.IsKeyboardFocusWithin && !ReferenceEquals(FocusManager.GetFocusedElement(this), this.ActiveFileControl))
            {
                this.PanelTreeSyncRequested = false;
                return;
            }
            // Navigation synchronizes its final path if focus arrives while loading.
            if (!this.Loading && this.CurrentPath.Length > 0) await this.SyncPanelTreesAsync(this.NavigationVersion);
        }
    }

    private async Task SyncPanelTreesAsync(int Version)
    {
        if (this.PanelTreeSyncDeferred) { this.PanelTreeSyncRequested = true; return; }
        this.PanelTreeSyncRequested = false;
        await this.SyncNavigationTreeAsync(this.NavigationTreeKey, this.CurrentPath, Version, Reveal: true);
    }

    private bool PanelTreeSyncDeferred => this.MainPanelDragCandidate is not null || this.MainPanelFileDragActive;

    private void ResumePendingPanelTreeSync()
    {
        if (this.PanelTreeSyncDeferred || !this.PanelTreeSyncRequested || this.Loading
            || this.WindowClosed || this.CurrentPath.Length == 0) return;
        this.PanelTreeSyncTask = this.SyncPanelTreesAsync(this.NavigationVersion);
    }

    private async Task SyncNavigationTreeAsync(string Key, string Current, int Version, bool Reveal)
    {
        if (this.IsVirtualLocation) return;
        if (Version != this.NavigationVersion || this.WindowClosed) return;
        if (Key == "folder-tree")
        {
            await this.SyncTreeAsync(Current, Version, Reveal);
            return;
        }
        if (Version != this.NavigationVersion || this.WindowClosed) return;
        if (this.NavigationSection(Key) is not { } Section) return;
        this.SyncingTree = true;
        try
        {
            FolderNode? Selected = null;
            foreach (var Root in this.CategoryFolderRoots(Section))
            {
                var Relative = Path.GetRelativePath(Root.FullPath, Current);
                var Matches = Relative == "." || !Path.IsPathRooted(Relative) && Relative != ".."
                    && !Relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
                if (!Matches) continue;
                if (Reveal) Section.IsExpanded = true;
                var Node = Root;
                if (Relative != ".")
                    foreach (var Part in Relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
                    {
                        await this.EnsureChildrenAsync(Node);
                        if (Version != this.NavigationVersion || this.WindowClosed) return;
                        Node.IsExpanded = true;
                        var Next = Node.Children.FirstOrDefault(Child => string.Equals(Child.Name, Part, StringComparison.OrdinalIgnoreCase));
                        if (Next is null)
                        {
                            Next = new FolderNode(Part, Path.Combine(Node.FullPath, Part));
                            Node.Children.Add(Next);
                        }
                        Node = Next;
                    }
                if (Reveal)
                {
                    await this.EnsureChildrenAsync(Node);
                    if (Version != this.NavigationVersion || this.WindowClosed) return;
                    Node.IsExpanded = true;
                }
                Selected = Node;
            }
            if (Version != this.NavigationVersion || Key != this.NavigationTreeKey || this.WindowClosed) return;
            this.SelectSidebarNode(Selected);
        }
        finally { if (Version == this.NavigationVersion) this.SyncingTree = false; }
    }

    private void FolderTreeContextOpened(object Sender, RoutedEventArgs E)
    {
        if (Sender is not ContextMenu Menu) return;
        if (TreeMenuFolder(Menu.DataContext) is not { IsPlaceholder: false }) { Menu.IsOpen = false; return; }
        UpdateMenuHeaders(Menu);
        foreach (var Item in Menu.Items.OfType<MenuItem>())
            if (Item.Tag is "NewFolder") Item.IsEnabled = !this.WindowClosed && !this.OperationBusy && !this.Loading;
    }

    private async void ExpandAllSubfoldersClick(object Sender, RoutedEventArgs E)
    {
        E.Handled = true;
        var Node = (Sender as FrameworkElement)?.DataContext switch { FolderNode Folder => Folder, CategoryFolder Folder => Folder.Node, _ => null };
        CloseSidebarMenu(Sender);
        if (Node is null || Node.IsPlaceholder) return;
        this.TreeExpansionTask = this.ExpandTreeBranchAsync(Node, ++this.TreeExpansionVersion);
        await this.TreeExpansionTask;
    }

    private async Task ExpandTreeBranchAsync(FolderNode Root, int Version)
    {
        var Pending = new Stack<FolderNode>();
        Pending.Push(Root);
        while (Pending.TryPop(out var Node))
        {
            if (Version != this.TreeExpansionVersion || this.WindowClosed) return;
            // Do not follow junctions or symbolic links back into an ancestor or another drive.
            var CanExpand = await Task.Run(() =>
            {
                try { return (File.GetAttributes(Node.FullPath) & FileAttributes.ReparsePoint) == 0; }
                catch (Exception Error) when (Error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
            });
            if (Version != this.TreeExpansionVersion || this.WindowClosed) return;
            if (!CanExpand) continue;
            await this.EnsureChildrenAsync(Node);
            if (Version != this.TreeExpansionVersion || this.WindowClosed) return;
            Node.IsExpanded = true;
            foreach (var Child in Node.Children.Reverse())
                if (!Child.IsPlaceholder) Pending.Push(Child);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
    }

    private void CollapseAllSubfoldersClick(object Sender, RoutedEventArgs E)
    {
        E.Handled = true;
        var Root = (Sender as FrameworkElement)?.DataContext switch { FolderNode Folder => Folder, CategoryFolder Folder => Folder.Node, _ => null };
        CloseSidebarMenu(Sender);
        if (Root is null || Root.IsPlaceholder) return;
        ++this.TreeExpansionVersion;
        this.CollapseTreeBranches([Root]);
    }

    private void SuppressCollapseFocusSync()
    {
        // Closing a popup and hiding the selected row can restore focus to the
        // file list. Do not let that restoration immediately reopen this branch.
        var FocusGuard = ++this.CollapseFocusVersion;
        this.RestoringCollapseFocus = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (FocusGuard == this.CollapseFocusVersion) this.RestoringCollapseFocus = false;
        }));
        ++this.PanelFocusVersion;
        this.PanelTreeSyncRequested = false;
    }

    private void CollapseTreeBranches(IEnumerable<FolderNode> Roots)
    {
        this.SuppressCollapseFocusSync();
        var WasSyncing = this.SyncingTree;
        this.SyncingTree = true;
        try
        {
            // Only visit loaded nodes: collapse must never enumerate more directories.
            // Suppress WPF's ancestor-selection navigation when hiding the selected folder.
            var Pending = new Stack<FolderNode>();
            foreach (var Root in Roots) Pending.Push(Root);
            while (Pending.TryPop(out var Node))
            {
                Node.IsExpanded = false;
                foreach (var Child in Node.Children)
                    if (!Child.IsPlaceholder) Pending.Push(Child);
            }
        }
        finally { this.SyncingTree = WasSyncing; }
    }
}
