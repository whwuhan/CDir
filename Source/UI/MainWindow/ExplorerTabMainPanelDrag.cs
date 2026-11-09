using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private const string MainPanelFolderDragFormat = "CDir.MainPanelFolders";
    private sealed record MainPanelFolderDragPayload(ExplorerTab Owner, FileEntry[] Folders);
    private MainPanelFolderDragPayload? ActiveMainPanelFolderDrag;
    private FileEntry? MainPanelDragCandidate;
    private FileEntry? MainPanelDeferredSelection;
    private Point MainPanelDragOrigin;
    private bool MainPanelFileDragActive;

    private bool PrepareMainPanelFolderDrag(DependencyObject? Source, Point Origin, ModifierKeys Modifiers, int ClickCount)
    {
        this.MainPanelDragCandidate = null;
        this.MainPanelDeferredSelection = null;
        if (this.OperationBusy || this.Loading || ClickCount != 1
            || FindAncestor<DataGridRow>(Source) is not { Item: FileEntry Entry } Row
            || !ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(Row), this.FileGrid)) return false;
        this.MainPanelDragCandidate = Entry;
        this.MainPanelDragOrigin = Origin;
        // Keep a selected group intact until we know whether this is a click or a drag.
        if (Row.IsSelected && this.FileGrid.SelectedItems.Count > 1 && Modifiers == ModifierKeys.None)
        {
            this.MainPanelDeferredSelection = Entry;
            return true;
        }
        return false;
    }

    private MainPanelFolderDragPayload? CreateMainPanelFolderDrag()
    {
        if (this.IsVirtualLocation || this.MainPanelDragCandidate is not { } Candidate || this.OperationBusy || this.Loading
            || !this.FileGrid.SelectedItems.Contains(Candidate)) return null;
        var Selection = this.SelectedEntries();
        return Selection.Length > 0 && Selection.All(Entry => Entry.IsDirectory) ? new(this, Selection) : null;
    }

    private void FileGridDragMouseDown(object Sender, MouseButtonEventArgs E)
    {
        if (FindAncestor<System.Windows.Controls.Primitives.TextBoxBase>(E.OriginalSource as DependencyObject) is not null) return;
        if (this.PrepareMarqueeSelection(E.OriginalSource as DependencyObject, E.GetPosition(this.FileGrid), Keyboard.Modifiers, E.ClickCount))
        {
            if (!AppMain.SmokeTest) this.FileGrid.Focus();
            E.Handled = true;
            return;
        }
        if (this.PrepareMainPanelFolderDrag(E.OriginalSource as DependencyObject, E.GetPosition(this.FileGrid), Keyboard.Modifiers, E.ClickCount))
        {
            // Handling a press on an existing multi-selection bypasses DataGrid's
            // default focus transfer. Clipboard keys must still reach the file list.
            if (!AppMain.SmokeTest) this.FileGrid.Focus();
            E.Handled = true;
        }
        else if (E.ClickCount == 1 && FindAncestor<DataGridRow>(E.OriginalSource as DependencyObject) is { Item: FileEntry Entry })
        {
            if (!AppMain.SmokeTest) this.FileGrid.Focus();
            this.SelectFileByClick(Entry, Keyboard.Modifiers);
            E.Handled = true;
        }
    }

    private void FileGridDragMouseUp(object Sender, MouseButtonEventArgs E)
    {
        if (this.MarqueeOrigin is not null) { this.EndMarqueeSelection(); E.Handled = true; return; }
        var Deferred = this.MainPanelDeferredSelection;
        this.MainPanelDragCandidate = null;
        this.MainPanelDeferredSelection = null;
        if (Deferred is not null && FindAncestor<DataGridRow>(E.OriginalSource as DependencyObject)?.Item == Deferred)
        {
            this.SelectFileByClick(Deferred, ModifierKeys.None);
            E.Handled = true;
        }
        this.ResumePendingPanelTreeSync();
    }

    private void FileGridDragMouseMove(object Sender, MouseEventArgs E)
    {
        if (this.MarqueeOrigin is not null)
        {
            if (E.LeftButton != MouseButtonState.Pressed) this.EndMarqueeSelection();
            else if (this.UpdateMarqueeSelection(E.GetPosition(this.FileGrid)))
            {
                if (!this.FileGrid.IsMouseCaptured) this.StartMarqueeTracking();
                E.Handled = true;
            }
            return;
        }
        if (E.LeftButton != MouseButtonState.Pressed)
        {
            this.MainPanelDragCandidate = null;
            this.MainPanelDeferredSelection = null;
            this.ResumePendingPanelTreeSync();
            return;
        }
        if (this.MainPanelDragCandidate is null || this.MainPanelFileDragActive || this.ActiveMainPanelFolderDrag is not null) return;
        var Position = E.GetPosition(this.FileGrid);
        if (Math.Abs(Position.X - this.MainPanelDragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(Position.Y - this.MainPanelDragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var Payload = this.CreateMainPanelFolderDrag();
        var Candidate = this.MainPanelDragCandidate;
        this.MainPanelDragCandidate = null;
        if (!this.FileGrid.SelectedItems.Contains(Candidate) || this.OperationBusy || this.Loading) return;
        this.MainPanelDeferredSelection = null;
        E.Handled = true;
        if (Mouse.Captured is UIElement Captured && (ReferenceEquals(Captured, this.FileGrid)
            || ReferenceEquals(FindAncestor<DataGrid>(Captured), this.FileGrid))) Captured.ReleaseMouseCapture();
        var Data = CreateFileTransferData(this.SelectedEntries());
        if (Payload is not null) Data.SetData(MainPanelFolderDragFormat, Payload);
        this.ActiveMainPanelFolderDrag = Payload;
        this.MainPanelFileDragActive = true;
        try { DragDrop.DoDragDrop(this.FileGrid, Data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link); }
        finally { this.CompleteMainPanelFileDrag(); }
    }

    private void CompleteMainPanelFileDrag()
    {
        this.ActiveMainPanelFolderDrag = null;
        this.MainPanelFileDragActive = false;
        this.ClearSidebarDropFeedback();
        this.ClearFileTransferFeedback();
        this.ResumePendingPanelTreeSync();
    }

    private void MainPanelPointerReleased(object Sender, MouseButtonEventArgs E)
    {
        if (E.ChangedButton != MouseButton.Left || this.MainPanelFileDragActive) return;
        this.DismissFileDragFeedback();
        // A release over the sidebar must also finish a press that never became
        // an OLE drag, even when a child control handles the mouse-up event.
        this.MainPanelDragCandidate = null;
        this.MainPanelDeferredSelection = null;
        this.ResumePendingPanelTreeSync();
    }

    private MainPanelFolderDragPayload? DraggedMainPanelFolders(IDataObject Data)
    {
        // Private payloads are meaningful only inside their originating tab.
        // Other windows and processes must be read through standard FileDrop data.
        if (this.ActiveMainPanelFolderDrag is null || !Data.GetDataPresent(MainPanelFolderDragFormat, false)
            || Data.GetData(MainPanelFolderDragFormat, false) is not MainPanelFolderDragPayload Payload) return null;
        return ReferenceEquals(Payload, this.ActiveMainPanelFolderDrag) && ReferenceEquals(Payload.Owner, this)
            && !this.OperationBusy && !this.Loading && Payload.Folders.Length > 0 && Payload.Folders.All(Entry => Entry.IsDirectory) ? Payload : null;
    }

    private SidebarCategory? MainPanelDropCategory(Point Position)
    {
        if (Position.X < 0 || Position.X > this.SidebarPanel.ActualWidth || Position.Y < 0 || Position.Y > this.SidebarPanel.ActualHeight) return null;
        if (!new Rect(this.SidebarScroll.TranslatePoint(new Point(), this.SidebarPanel), this.SidebarScroll.RenderSize).Contains(Position)) return null;
        foreach (var Category in this.CustomCategories)
        {
            if (this.SidebarSectionElement(Category) is not { IsVisible: true } Section) continue;
            var Bounds = new Rect(Section.TranslatePoint(new Point(), this.SidebarPanel), Section.RenderSize);
            if (Bounds.Contains(Position)) return Category;
        }
        return null;
    }

    private SidebarCategory? FolderAdditionDropTarget(FileEntry[] Folders, Point Position)
    {
        var Category = this.MainPanelDropCategory(Position);
        return Category is not null && Folders.Any(Entry => !Category.Folders.Any(Folder => SamePath(Folder.FullPath, Entry.FullPath))) ? Category : null;
    }

    private void ShowMainPanelDropHighlight(SidebarCategory Category)
    {
        var Section = this.SidebarSectionElement(Category)!;
        var Bounds = new Rect(Section.TranslatePoint(new Point(), this.SidebarPanel), Section.RenderSize);
        Bounds.Intersect(new Rect(this.SidebarScroll.TranslatePoint(new Point(), this.SidebarPanel), this.SidebarScroll.RenderSize));
        if (Bounds.IsEmpty) return;
        this.SidebarDropMarker.Visibility = Visibility.Collapsed;
        this.SidebarCategoryDropHighlight.Margin = new Thickness(Bounds.Left, Bounds.Top, 0, 0);
        this.SidebarCategoryDropHighlight.Width = Bounds.Width;
        this.SidebarCategoryDropHighlight.Height = Bounds.Height;
        this.SidebarCategoryDropHighlight.Visibility = Visibility.Visible;
    }

    private void ClearSidebarDropFeedback()
    {
        this.SidebarDropMarker.Visibility = Visibility.Collapsed;
        this.SidebarCategoryDropHighlight.Visibility = Visibility.Collapsed;
    }
}
