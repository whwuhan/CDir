using System.Windows;
using System.Windows.Controls;
using System.Runtime.InteropServices;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private const string CategoryFolderDragFormat = "CDir.CategoryFolder";
    private sealed record CategoryFolderDragPayload(ExplorerTab Owner, SidebarCategory Category, CategoryFolder Folder);
    private sealed record CategoryFolderDropTarget(int Index, double MarkerY);
    private CategoryFolderDragPayload? CategoryFolderDragCandidate;
    private CategoryFolderDragPayload? ActiveCategoryFolderDrag;

    private CategoryFolderDragPayload? CategoryFolderFromSource(DependencyObject? Source)
    {
        if (FindAncestor<Button>(Source)?.DataContext is not CategoryFolder Folder) return null;
        var Category = this.CustomCategories.FirstOrDefault(Category => Category.Folders.Contains(Folder));
        return Category is null ? null : new(this, Category, Folder);
    }

    private void StartCategoryFolderDrag(CategoryFolderDragPayload Payload)
    {
        if (!this.CustomCategories.Contains(Payload.Category) || !Payload.Category.Folders.Contains(Payload.Folder)) return;
        var List = FindVisualChild<ItemsControl>(this.SidebarSectionElement(Payload.Category)!);
        if (List?.ItemContainerGenerator.ContainerFromItem(Payload.Folder) is DependencyObject Container)
            FindVisualChild<Button>(Container)?.ReleaseMouseCapture();
        var Data = new DataObject();
        Data.SetData(CategoryFolderDragFormat, Payload);
        this.ActiveCategoryFolderDrag = Payload;
        var RemoveOnRelease = false;
        QueryContinueDragEventHandler Query = (_, E) =>
        {
            var Decision = this.QueryCategoryFolderDrag(E.EscapePressed, E.KeyStates, this.CategoryDragSidebarPosition());
            E.Action = Decision.Action;
            E.Handled = true;
            RemoveOnRelease = Decision.RemoveOnRelease;
        };
        this.SidebarPanel.QueryContinueDrag += Query;
        try
        {
            DragDrop.DoDragDrop(this.SidebarPanel, Data, DragDropEffects.Move | DragDropEffects.Link);
            this.FinishCategoryFolderDrag(Payload, RemoveOnRelease);
        }
        finally
        {
            this.SidebarPanel.QueryContinueDrag -= Query;
            this.ActiveCategoryFolderDrag = null;
            this.CategoryFolderDragCandidate = null;
            this.ClearSidebarDropFeedback();
        }
    }

    private (DragAction Action, bool RemoveOnRelease) QueryCategoryFolderDrag(bool EscapePressed, DragDropKeyStates KeyStates, Point? SidebarPosition)
    {
        if (EscapePressed || this.WindowClosed || (KeyStates & (DragDropKeyStates.RightMouseButton | DragDropKeyStates.MiddleMouseButton)) != 0)
            return (DragAction.Cancel, false);
        if ((KeyStates & DragDropKeyStates.LeftMouseButton) != 0) return (DragAction.Continue, false);
        if (SidebarPosition is not { } Position) return (DragAction.Cancel, false);
        var Outside = Position.X < 0 || Position.X > this.SidebarPanel.ActualWidth || Position.Y < 0 || Position.Y > this.SidebarPanel.ActualHeight;
        // Cancel the native drop outside the sidebar so no other application receives it.
        // Commit shortcut removal after the drag loop exits, including releases outside our window.
        return (Outside ? DragAction.Cancel : DragAction.Drop, Outside);
    }

    private bool FinishCategoryFolderDrag(CategoryFolderDragPayload Payload, bool RemoveOnRelease)
    {
        if (!RemoveOnRelease || this.WindowClosed || !ReferenceEquals(Payload, this.ActiveCategoryFolderDrag)
            || !ReferenceEquals(Payload.Owner, this) || !this.CustomCategories.Contains(Payload.Category)
            || !Payload.Category.Folders.Remove(Payload.Folder)) return false;
        this.SaveSidebarCategories();
        return true;
    }

    private Point? CategoryDragSidebarPosition()
    {
        if (this.WindowClosed || PresentationSource.FromVisual(this.SidebarPanel) is null || !GetCategoryDragCursor(out var Cursor)) return null;
        return this.SidebarPanel.PointFromScreen(new Point(Cursor.X, Cursor.Y));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CategoryDragCursorPoint { public int X; public int Y; }

    [DllImport("user32.dll", EntryPoint = "GetCursorPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCategoryDragCursor(out CategoryDragCursorPoint Point);

    private CategoryFolderDragPayload? DraggedCategoryFolder(IDataObject Data)
    {
        if (this.ActiveCategoryFolderDrag is null || !Data.GetDataPresent(CategoryFolderDragFormat, false)
            || Data.GetData(CategoryFolderDragFormat, false) is not CategoryFolderDragPayload Payload) return null;
        return ReferenceEquals(Payload, this.ActiveCategoryFolderDrag) && ReferenceEquals(Payload.Owner, this)
            && this.CustomCategories.Contains(Payload.Category) && Payload.Category.Folders.Contains(Payload.Folder) ? Payload : null;
    }

    private CategoryFolderDropTarget? CategoryFolderInsertion(CategoryFolderDragPayload Payload, Point Position)
    {
        if (Position.X < 0 || Position.X > this.SidebarPanel.ActualWidth || Position.Y < 0 || Position.Y > this.SidebarPanel.ActualHeight) return null;
        var Section = this.SidebarSectionElement(Payload.Category);
        var List = Section is null ? null : FindVisualChild<ItemsControl>(Section);
        if (Section is not { IsExpanded: true } || List is not { IsVisible: true }) return null;
        var Top = List.TranslatePoint(new Point(), this.SidebarPanel).Y;
        if (Position.Y < Top || Position.Y > Top + List.ActualHeight) return null;
        for (var Index = 0; Index < Payload.Category.Folders.Count; Index++)
        {
            if (List.ItemContainerGenerator.ContainerFromIndex(Index) is not FrameworkElement Container
                || FindVisualChild<Button>(Container) is not { } Row) return null;
            var Y = Row.TranslatePoint(new Point(), this.SidebarPanel).Y;
            if (Position.Y < Y + Row.ActualHeight / 2) return new(Index, Y);
        }
        return new(Payload.Category.Folders.Count, Top + List.ActualHeight);
    }

    private void ShowCategoryFolderDropMarker(CategoryFolderDropTarget Target)
    {
        this.SidebarDropMarker.Margin = new Thickness(12, Math.Clamp(Target.MarkerY, 0, Math.Max(0, this.SidebarPanel.ActualHeight - 2)), 10, 0);
        this.SidebarDropMarker.Visibility = Visibility.Visible;
    }
}
