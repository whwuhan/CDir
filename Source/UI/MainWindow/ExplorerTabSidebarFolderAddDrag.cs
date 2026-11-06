using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private const string SidebarFolderAddDragFormat = "CDir.SidebarFolderAddition";
    private sealed record SidebarFolderAddSource(FileEntry Folder, FrameworkElement Element);
    private sealed record SidebarFolderAddPayload(ExplorerTab Owner, FileEntry Folder);
    private SidebarFolderAddSource? SidebarFolderAddDragCandidate;
    private SidebarFolderAddPayload? ActiveSidebarFolderAddDrag;

    private SidebarFolderAddSource? SidebarFolderAddFromSource(DependencyObject? Source, int ClickCount)
    {
        if (this.OperationBusy) return null;
        if (FindAncestor<TreeViewItem>(Source) is { DataContext: FolderNode { IsPlaceholder: false } Node } Item
            && this.IsSidebarFolderTree(FindAncestor<TreeView>(Item)) && FindAncestor<ToggleButton>(Source) is null)
            return new(new FileEntry { Name = Node.Name, FullPath = Node.FullPath, IsDirectory = true }, Item);
        if (!this.Loading && FindAncestor<Button>(Source) is { DataContext: FavoriteLocation Location } Button
            && ReferenceEquals(FindAncestor<ItemsControl>(Button), this.FavoritesList))
            return new(new FileEntry { Name = Location.Name, FullPath = Location.FullPath, IsDirectory = true }, Button);
        return null;
    }

    private Task CompleteSidebarFolderClick(SidebarFolderAddSource? Candidate, DependencyObject? Source)
    {
        if (Candidate?.Element is not TreeViewItem Item || !ReferenceEquals(FindAncestor<TreeViewItem>(Source), Item)) return Task.CompletedTask;
        return this.OpenSidebarFolderClickAsync(() => this.OpenSidebarTreeFolderAsync(Item, Navigate: true), Item);
    }

    private async Task OpenSidebarTreeFolderAsync(TreeViewItem Item, bool Navigate, bool ToggleExpansion = false)
    {
        if (this.OperationBusy || this.WindowClosed || Item.DataContext is not FolderNode Node) return;
        ++this.PanelFocusVersion;
        this.PanelTreeSyncRequested = false;
        // Mouse selection must not run the keyboard-selection navigation handler.
        var WasSyncing = this.SyncingTree;
        var WasSuppressed = this.SuppressTreeSelectionScroll;
        this.SyncingTree = true;
        this.SuppressTreeSelectionScroll = true;
        var Section = this.CategorySection(Item) ?? this.DirectoryTreeSection;
        // WPF focuses the entire header, which can horizontally scroll a long name
        // even though the user clicked a visible part of it. Mouse clicks keep that viewport.
        RequestBringIntoViewEventHandler KeepViewport = (_, E) => E.Handled = true;
        Item.AddHandler(RequestBringIntoViewEvent, KeepViewport, handledEventsToo: true);
        try
        {
            if (Navigate)
            {
                Item.Focus();
                this.SelectSidebarNode(Node);
                Item.IsSelected = true;
            }
            if (!Navigate || ToggleExpansion)
            {
                Item.IsExpanded = !Item.IsExpanded;
                if (!Item.IsExpanded) ++this.TreeExpansionVersion;
            }
        }
        finally
        {
            Item.RemoveHandler(RequestBringIntoViewEvent, KeepViewport);
            this.SyncingTree = WasSyncing;
            this.SuppressTreeSelectionScroll = WasSuppressed;
        }
        var Navigation = Navigate ? this.NavigateFromTreeAsync(Node, this.TreeKeyForSection(Section)) : Task.CompletedTask;
        if ((!Navigate || ToggleExpansion) && Node.IsExpanded) await this.EnsureChildrenAsync(Node);
        await Navigation;
    }

    private bool IsSidebarFolderTree(TreeView? Tree) => Tree is not null && (ReferenceEquals(Tree, this.FolderTree)
        || Tree.Tag?.ToString() == "SidebarFolderChildren" && FindAncestor<Expander>(Tree) is { } Section
            && (ReferenceEquals(Section, this.FavoritesSection) || Section.DataContext is SidebarCategory Category && this.CustomCategories.Contains(Category)));

    private void StartSidebarFolderAddDrag(SidebarFolderAddSource Source)
    {
        if (this.OperationBusy || this.Loading || this.WindowClosed) return;
        if (Source.Element is Button Button) Button.ReleaseMouseCapture();
        if (Mouse.Captured is UIElement Captured && this.IsSidebarFolderTree(FindAncestor<TreeView>(Captured))) Captured.ReleaseMouseCapture();
        var Payload = new SidebarFolderAddPayload(this, Source.Folder);
        var Data = CreateFileTransferData([Source.Folder]);
        Data.SetData(SidebarFolderAddDragFormat, Payload);
        this.ActiveSidebarFolderAddDrag = Payload;
        try { DragDrop.DoDragDrop(Source.Element, Data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link); }
        finally
        {
            this.ActiveSidebarFolderAddDrag = null;
            this.SidebarFolderAddDragCandidate = null;
            this.ClearSidebarDropFeedback();
            this.ClearFileTransferFeedback();
        }
    }

    private FileEntry[]? SidebarFolderAddition(IDataObject Data)
    {
        if (this.DraggedMainPanelFolders(Data) is { } MainPanel) return MainPanel.Folders;
        if (this.WindowClosed || this.OperationBusy || this.Loading) return null;
        try
        {
            if (this.ActiveSidebarFolderAddDrag is not null && Data.GetDataPresent(SidebarFolderAddDragFormat, false)
                && Data.GetData(SidebarFolderAddDragFormat, false) is SidebarFolderAddPayload Payload
                && ReferenceEquals(Payload, this.ActiveSidebarFolderAddDrag) && ReferenceEquals(Payload.Owner, this)
                && Payload.Folder.IsDirectory) return [Payload.Folder];
            // FileDrop is shared by Explorer, other CDir windows and other tabs.
            // Categories contain directory shortcuts; files still drop into concrete folders.
            if (!HasFileDrop(Data) || Data.GetData(DataFormats.FileDrop, true) is not string[] { Length: > 0 } Paths
                || Paths.Any(SourcePath => !Path.IsPathFullyQualified(SourcePath))) return null;
            var Folders = Paths.Select(SourcePath => Path.TrimEndingDirectorySeparator(Path.GetFullPath(SourcePath)))
                .Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists)
                .Select(SourcePath => new FileEntry { FullPath = SourcePath, IsDirectory = true }).ToArray();
            return Folders.Length > 0 ? Folders : null;
        }
        catch (Exception Error) when (Error is ArgumentException or NotSupportedException or IOException or COMException)
        {
            return null;
        }
    }

    private bool AddDroppedFolders(FileEntry[] Folders, Point Position)
    {
        if (this.OperationBusy || this.Loading || this.WindowClosed || this.FolderAdditionDropTarget(Folders, Position) is not { } Category
            || !this.AddFoldersToCategory(Category, Folders)) return false;
        if (this.SidebarSectionElement(Category) is { } Section) Section.IsExpanded = true;
        // A category shortcut drop does not browse the source or reveal its ancestors.
        ++this.PanelFocusVersion;
        this.PanelTreeSyncRequested = false;
        return true;
    }

}
