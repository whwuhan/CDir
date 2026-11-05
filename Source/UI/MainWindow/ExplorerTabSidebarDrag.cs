using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private const string SidebarDragFormat = "CDir.SidebarSection";
    private sealed record SidebarDragPayload(ExplorerTab Owner, object Section);
    private object? SidebarDragCandidate;
    private Point SidebarDragOrigin;
    private SidebarDragPayload? ActiveSidebarDrag;
    private long SidebarLastDragScroll;

    private string SidebarSectionKey(object Section) => ReferenceEquals(Section, this.FavoritesSection) ? "quick-access"
        : ReferenceEquals(Section, this.DirectoryTreeSection) ? "folder-tree" : "custom:" + ((SidebarCategory)Section).Id;

    private void SyncCustomCategoryOrder()
    {
        var Ordered = this.SidebarSections.OfType<SidebarCategory>().ToArray();
        for (var Index = 0; Index < Ordered.Length; Index++)
        {
            var OldIndex = this.CustomCategories.IndexOf(Ordered[Index]);
            if (OldIndex != Index) this.CustomCategories.Move(OldIndex, Index);
        }
    }

    private void ChangeSidebarLayout(Action Change)
    {
        var WasSyncing = this.SyncingTree;
        var SelectedNode = this.FolderTree.SelectedItem as FolderNode;
        this.SyncingTree = true;
        try
        {
            Change();
            if (this.IsLoaded)
            {
                this.SidebarSectionsList.UpdateLayout();
                if (SelectedNode is not null) SelectedNode.IsSelected = true;
            }
        }
        finally { this.SyncingTree = WasSyncing; }
    }

    private bool TryMoveSidebarSection(object Section, int InsertionIndex)
    {
        var OldIndex = this.SidebarSections.IndexOf(Section);
        if (OldIndex < 0 || InsertionIndex < 0 || InsertionIndex > this.SidebarSections.Count) return false;
        var NewIndex = InsertionIndex > OldIndex ? InsertionIndex - 1 : InsertionIndex;
        if (NewIndex == OldIndex) return false;
        this.ChangeSidebarLayout(() => this.SidebarSections.Move(OldIndex, NewIndex));
        this.SyncCustomCategoryOrder();
        this.SaveSidebarCategories();
        return true;
    }

    private Expander? SidebarSectionElement(object Section)
    {
        if (Section is Expander BuiltIn) return BuiltIn;
        var Container = this.SidebarSectionsList.ItemContainerGenerator.ContainerFromItem(Section);
        return Container is null ? null : FindVisualChild<Expander>(Container);
    }

    private object? SidebarSectionFromHeader(DependencyObject? Source)
    {
        // A tree node's expander or a folder entry must never start a category drag.
        if (FindAncestor<ToggleButton>(Source)?.TemplatedParent is not Expander Section) return null;
        object? Item = ReferenceEquals(Section, this.FavoritesSection) || ReferenceEquals(Section, this.DirectoryTreeSection)
            ? Section : Section.DataContext as SidebarCategory;
        return Item is not null && this.SidebarSections.Contains(Item) ? Item : null;
    }

    private int SidebarInsertionIndex(double Y)
    {
        for (var Index = 0; Index < this.SidebarSections.Count; Index++)
        {
            var Section = this.SidebarSectionElement(this.SidebarSections[Index]);
            var Header = Section is null ? null : FindVisualChild<ToggleButton>(Section);
            if (Header is not null && Y < Header.TranslatePoint(new Point(), this.SidebarPanel).Y + Header.ActualHeight / 2) return Index;
        }
        return this.SidebarSections.Count;
    }

    private void ShowSidebarDropMarker(int InsertionIndex)
    {
        if (this.SidebarSections.Count == 0) return;
        var Section = this.SidebarSectionElement(this.SidebarSections[Math.Min(InsertionIndex, this.SidebarSections.Count - 1)]);
        if (Section is null) return;
        var Y = Section.TranslatePoint(new Point(), this.SidebarPanel).Y;
        if (InsertionIndex == this.SidebarSections.Count) Y += Section.ActualHeight;
        this.SidebarDropMarker.Margin = new Thickness(12, Math.Clamp(Y, 0, Math.Max(0, this.SidebarPanel.ActualHeight - 2)), 10, 0);
        this.SidebarDropMarker.Visibility = Visibility.Visible;
    }

    private object? SidebarDraggedSection(IDataObject Data)
    {
        if (!Data.GetDataPresent(SidebarDragFormat, false) || Data.GetData(SidebarDragFormat, false) is not SidebarDragPayload Payload) return null;
        return ReferenceEquals(Payload, this.ActiveSidebarDrag) && ReferenceEquals(Payload.Owner, this)
            && this.SidebarSections.Contains(Payload.Section) ? Payload.Section : null;
    }

    private async void SidebarDragMouseDown(object Sender, MouseButtonEventArgs E)
    {
        // A sidebar gesture wins over a focus reveal that was queued before it.
        ++this.PanelFocusVersion;
        this.PanelTreeSyncRequested = false;
        var Source = E.OriginalSource as DependencyObject;
        DependencyObject? Target = FindAncestor<TreeViewItem>(Source) is { } Item
            && this.IsSidebarFolderTree(FindAncestor<TreeView>(Item)) && FindAncestor<ToggleButton>(Source) is null ? Item
            : FindAncestor<Button>(Source) is { DataContext: CategoryFolder or FavoriteLocation } Button ? Button : null;
        if (this.ConsumeSidebarDoubleClick(Target, E.ClickCount))
        {
            E.Handled = true;
            this.SidebarFolderAddDragCandidate = null;
            this.CategoryFolderDragCandidate = null;
            this.SidebarDragCandidate = null;
            if (Target is Button Header) Header.ReleaseMouseCapture();
            var FirstClick = this.SidebarClickTask;
            var DoubleClick = Target is TreeViewItem FolderItem
                ? this.OpenSidebarTreeFolderAsync(FolderItem, Navigate: true, ToggleExpansion: true)
                : this.OpenBookmarkFolderAsync((Button)Target!, Navigate: true, ToggleExpansion: true);
            this.SidebarClickTask = Task.WhenAll(FirstClick, DoubleClick);
            await this.SidebarClickTask;
            return;
        }
        if (this.PrepareSidebarDragSource(E.OriginalSource as DependencyObject, E.ClickCount, E.GetPosition(this.SidebarPanel))) E.Handled = true;
    }

    private bool PrepareSidebarDragSource(DependencyObject? Source, int ClickCount, Point Origin)
    {
        this.SidebarFolderAddDragCandidate = this.SidebarFolderAddFromSource(Source, ClickCount);
        this.CategoryFolderDragCandidate = this.CategoryFolderFromSource(Source);
        this.SidebarDragCandidate = this.CategoryFolderDragCandidate is null && this.SidebarFolderAddDragCandidate is null
            ? this.SidebarSectionFromHeader(Source) : null;
        this.SidebarDragOrigin = Origin;
        // Defer tree navigation until release, so dragging an unselected node does not open it.
        return this.SidebarFolderAddDragCandidate?.Element is TreeViewItem;
    }

    private async void SidebarDragMouseUp(object Sender, MouseButtonEventArgs E)
    {
        var Candidate = this.SidebarFolderAddDragCandidate;
        this.SidebarFolderAddDragCandidate = null;
        this.SidebarDragCandidate = null;
        this.CategoryFolderDragCandidate = null;
        if (Candidate?.Element is TreeViewItem)
        {
            E.Handled = true;
            await this.CompleteSidebarFolderClick(Candidate, E.OriginalSource as DependencyObject);
        }
    }

    private void SidebarDragMouseMove(object Sender, MouseEventArgs E)
    {
        if (E.LeftButton != MouseButtonState.Pressed) { this.SidebarDragCandidate = null; this.CategoryFolderDragCandidate = null; this.SidebarFolderAddDragCandidate = null; return; }
        if (this.ActiveSidebarDrag is not null || this.ActiveCategoryFolderDrag is not null || this.ActiveSidebarFolderAddDrag is not null) return;
        if (this.SidebarDragCandidate is null && this.CategoryFolderDragCandidate is null && this.SidebarFolderAddDragCandidate is null) return;
        var Position = E.GetPosition(this.SidebarPanel);
        if (Math.Abs(Position.X - this.SidebarDragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(Position.Y - this.SidebarDragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        E.Handled = true;
        if (this.SidebarFolderAddDragCandidate is { } AdditionCandidate)
        {
            this.SidebarFolderAddDragCandidate = null;
            this.StartSidebarFolderAddDrag(AdditionCandidate);
            return;
        }
        if (this.CategoryFolderDragCandidate is { } FolderCandidate)
        {
            this.CategoryFolderDragCandidate = null;
            this.StartCategoryFolderDrag(FolderCandidate);
            return;
        }
        if (this.SidebarDragCandidate is not { } Candidate || !this.SidebarSections.Contains(Candidate)
            || this.SidebarSectionElement(Candidate) is not { } Element) return;
        this.SidebarDragCandidate = null;
        var Header = FindVisualChild<ToggleButton>(Element);
        Header?.ReleaseMouseCapture();
        var Payload = new SidebarDragPayload(this, Candidate);
        var Data = new DataObject();
        Data.SetData(SidebarDragFormat, Payload);
        this.ActiveSidebarDrag = Payload;
        try { DragDrop.DoDragDrop(this.SidebarPanel, Data, DragDropEffects.Move); }
        finally
        {
            this.ActiveSidebarDrag = null;
            this.SidebarDragCandidate = null;
            this.SidebarDropMarker.Visibility = Visibility.Collapsed;
        }
    }

    private void SidebarDragOver(object Sender, DragEventArgs E)
    {
        this.ClearFileTransferFeedback();
        this.ClearSidebarDropFeedback();
        if (this.IsSidebarFileTransfer(E)) { this.FileTransferDragOver(Sender, E); return; }
        E.Handled = true;
        E.Effects = this.UpdateSidebarDragFeedback(E.Data, E.GetPosition(this.SidebarPanel));
    }

    private DragDropEffects UpdateSidebarDragFeedback(IDataObject Data, Point Position)
    {
        this.SidebarCategoryDropHighlight.Visibility = Visibility.Collapsed;
        var Folder = this.DraggedCategoryFolder(Data);
        var Addition = this.SidebarFolderAddition(Data);
        if (Addition is null && Folder is null && this.SidebarDraggedSection(Data) is null) { this.ClearSidebarDropFeedback(); return DragDropEffects.None; }
        if (Environment.TickCount64 - this.SidebarLastDragScroll >= 80)
        {
            if (Position.Y < 32) this.SidebarScroll.ScrollToVerticalOffset(this.SidebarScroll.VerticalOffset - 20);
            else if (Position.Y > this.SidebarPanel.ActualHeight - 32) this.SidebarScroll.ScrollToVerticalOffset(this.SidebarScroll.VerticalOffset + 20);
            this.SidebarLastDragScroll = Environment.TickCount64;
        }
        if (Addition is not null)
        {
            if (this.FolderAdditionDropTarget(Addition, Position) is not { } Category) { this.ClearSidebarDropFeedback(); return DragDropEffects.None; }
            this.ShowMainPanelDropHighlight(Category);
            return DragDropEffects.Link;
        }
        else if (Folder is not null)
        {
            if (this.MainPanelDropCategory(Position) is { } Other && !ReferenceEquals(Other, Folder.Category))
            {
                if (this.OperationBusy || this.Loading || this.FolderAdditionDropTarget(CategoryFolderAddition(Folder), Position) is null) { this.ClearSidebarDropFeedback(); return DragDropEffects.None; }
                this.ShowMainPanelDropHighlight(Other);
                return DragDropEffects.Link;
            }
            if (this.CategoryFolderInsertion(Folder, Position) is not { } Target) { this.SidebarDropMarker.Visibility = Visibility.Collapsed; return DragDropEffects.None; }
            this.ShowCategoryFolderDropMarker(Target);
        }
        else
        {
            this.ShowSidebarDropMarker(this.SidebarInsertionIndex(Position.Y));
        }
        return DragDropEffects.Move;
    }

    private void SidebarDragLeave(object Sender, DragEventArgs E)
    {
        this.FileTransferDragLeave(Sender, E);
        var Position = E.GetPosition(this.SidebarPanel);
        if (Position.X < 0 || Position.X > this.SidebarPanel.ActualWidth || Position.Y < 0 || Position.Y > this.SidebarPanel.ActualHeight)
            this.ClearSidebarDropFeedback();
    }

    private void SidebarDrop(object Sender, DragEventArgs E)
    {
        this.ClearFileTransferFeedback();
        this.ClearSidebarDropFeedback();
        if (this.IsSidebarFileTransfer(E)) { this.FileTransferDrop(Sender, E); return; }
        E.Handled = true;
        E.Effects = DragDropEffects.None;
        var Position = E.GetPosition(this.SidebarPanel);
        var IsAddition = this.SidebarFolderAddition(E.Data) is not null
            || (this.DraggedCategoryFolder(E.Data) is { } Folder && this.MainPanelDropCategory(Position) is { } Category && !ReferenceEquals(Category, Folder.Category));
        if (this.CompleteSidebarDrop(E.Data, Position)) E.Effects = IsAddition ? DragDropEffects.Link : DragDropEffects.Move;
    }

    private bool IsSidebarFileTransfer(DragEventArgs E) => HasFileDrop(E.Data)
        // A concrete directory always wins over the surrounding category. Even an
        // invalid transfer must not fall back to creating a category shortcut.
        && (this.FileTransferTarget(E.OriginalSource as DependencyObject, Sidebar: true) is not null
            || this.ActiveMainPanelFolderDrag is null && this.ActiveSidebarFolderAddDrag is null
            || this.MainPanelDropCategory(E.GetPosition(this.SidebarPanel)) is null);

    private bool CompleteSidebarDrop(IDataObject Data, Point Position)
    {
        try
        {
            if (Position.X < 0 || Position.X > this.SidebarPanel.ActualWidth || Position.Y < 0 || Position.Y > this.SidebarPanel.ActualHeight) return false;
            if (this.SidebarFolderAddition(Data) is { } Addition) return this.AddDroppedFolders(Addition, Position);
            if (this.DraggedCategoryFolder(Data) is { } Folder)
            {
                if (this.MainPanelDropCategory(Position) is { } Other && !ReferenceEquals(Other, Folder.Category))
                    return this.AddDroppedFolders(CategoryFolderAddition(Folder), Position);
                return this.CategoryFolderInsertion(Folder, Position) is { } Target
                    && this.TryMoveCategoryFolder(Folder.Category, Folder.Folder, Target.Index);
            }
            return this.SidebarDraggedSection(Data) is { } Section && this.TryMoveSidebarSection(Section, this.SidebarInsertionIndex(Position.Y));
        }
        finally { this.ClearSidebarDropFeedback(); }
    }
}
