using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private FileEntry? MarqueeClickEntry;
    private Point? MarqueeOrigin;
    private Point MarqueeDocumentOrigin;
    private Point MarqueePointer;
    private bool MarqueeActive;
    private bool ApplyingMarqueeSelection;
    private ModifierKeys MarqueeModifiers;
    private HashSet<FileEntry> MarqueeInitialSelection = [];
    private DispatcherTimer? MarqueeScrollTimer;
    private double MarqueeGroupHeaderHeight;

    private (ScrollViewer Viewer, Rect Bounds, double RowHeight, double Offset, (int Start, int Count)[]? Groups, double HeaderHeight)? MarqueeViewport()
    {
        var Viewer = FindVisualChild<ScrollViewer>(this.ActiveFileControl);
        var Presenter = Viewer is null ? null : FindVisualChild<ScrollContentPresenter>(Viewer);
        if (Viewer is null || Presenter is null || Presenter.ActualWidth <= 0 || Presenter.ActualHeight <= 0) return null;
        if (!this.IsDetailsView && FindVisualChild<CDir.Controls.FileTilePanel>(this.FileTiles) is { } Tiles)
            return (Viewer, new Rect(Presenter.TranslatePoint(new Point(), this.ActiveFileControl), Presenter.RenderSize), Tiles.CellHeight, Tiles.VerticalOffset, null, 0);
        var FirstRow = FindVisualChild<DataGridRow>(this.FileGrid);
        var Height = FirstRow is { ActualHeight: > 0 } ? FirstRow.ActualHeight : this.FileGrid.RowHeight;
        if (!double.IsFinite(Height) || Height <= 0) return null;
        var Bounds = new Rect(Presenter.TranslatePoint(new Point(), this.ActiveFileControl), Presenter.RenderSize);
        var Groups = this.MarqueeRowGroups();
        if (Groups is not null && FindVisualChild<GroupItem>(this.FileGrid) is { } Group
            && Group.Template?.FindName("PART_Header", Group) is FrameworkElement { ActualHeight: > 0 } Header)
            this.MarqueeGroupHeaderHeight = Header.ActualHeight;
        var HeaderHeight = Groups is null ? 0 : this.MarqueeGroupHeaderHeight;
        // Use a realized row to account for both item and pixel scrolling, including DPI rounding.
        // Grouped rows belong to their GroupItem's generator, so locate them in the flat view order.
        var Index = FirstRow is null ? -1 : Groups is null ? this.FileGrid.ItemContainerGenerator.IndexFromContainer(FirstRow)
            : this.FileGrid.Items.IndexOf(FirstRow.Item);
        var Offset = Index < 0 ? 0 : MarqueeRowTop(Index, Height, Groups, HeaderHeight)
            - (FirstRow!.TranslatePoint(new Point(), this.ActiveFileControl).Y - Bounds.Top);
        return (Viewer, Bounds, Height, Offset, Groups, HeaderHeight);
    }

    // Details groups are laid out as one header followed by their rows, in the
    // same order as the flat view. Returns null when the grid is not grouped.
    private (int Start, int Count)[]? MarqueeRowGroups()
    {
        if (this.FileGroupProperty.Length == 0 || this.View?.Groups is not { Count: > 0 } Groups) return null;
        var Result = new (int Start, int Count)[Groups.Count];
        var Start = 0;
        for (var Index = 0; Index < Groups.Count; Index++)
        {
            var Count = Groups[Index] is CollectionViewGroup Group ? Group.ItemCount : 0;
            Result[Index] = (Start, Count);
            Start += Count;
        }
        return Result;
    }

    private static double MarqueeRowTop(int Index, double RowHeight, (int Start, int Count)[]? Groups, double HeaderHeight)
    {
        if (Groups is null) return Index * RowHeight;
        var Group = 0;
        while (Group < Groups.Length - 1 && Index >= Groups[Group].Start + Groups[Group].Count) Group++;
        return (Group + 1) * HeaderHeight + Index * RowHeight;
    }

    private static IEnumerable<int> MarqueeRowsIn(Rect Selection, int ItemCount, double RowHeight, (int Start, int Count)[]? Groups, double HeaderHeight)
    {
        foreach (var (Start, Count, Top) in Groups is null ? [(0, ItemCount, 0d)]
            : Groups.Select((Group, Index) => (Group.Start, Group.Count, (Index + 1) * HeaderHeight + Group.Start * RowHeight)))
        {
            if (Count <= 0) continue;
            var First = Start + (int)Math.Clamp(Math.Floor((Selection.Top - Top) / RowHeight), 0, Count);
            var Last = Start + (int)Math.Clamp(Math.Ceiling((Selection.Bottom - Top) / RowHeight), 0, Count) - 1;
            for (var Index = First; Index <= Math.Min(Last, ItemCount - 1); Index++) yield return Index;
        }
    }

    private bool CanStartMarqueeSelection(DependencyObject? Source, Point Origin, ModifierKeys Modifiers, int ClickCount)
    {
        if (this.Loading || this.OperationBusy || ClickCount != 1 || (Modifiers & (ModifierKeys.Alt | ModifierKeys.Windows)) != 0
            || !this.IsFileInput(Source) || FindAncestor<ScrollBar>(Source) is not null
            || FindAncestor<DataGridColumnHeader>(Source) is not null || this.MarqueeViewport() is not { } Viewport
            || !Viewport.Bounds.Contains(Origin)) return false;
        if (!this.IsDetailsView) return this.FileItemContainer(Source) is null;
        if (FindAncestor<DataGridRow>(Source) is not null)
        {
            // A drag on the icon or text keeps the existing click/open/folder-drag behavior.
            var Cell = FindAncestor<DataGridCell>(Source);
            if (Cell?.Column.SortMemberPath != nameof(FileEntry.Name) || FindVisualChild<TextBlock>(Cell) is not { } Text) return false;
            var Measure = new FormattedText(Text.Text, System.Globalization.CultureInfo.GetCultureInfo(Text.Language.IetfLanguageTag),
                Text.FlowDirection, new Typeface(Text.FontFamily, Text.FontStyle, Text.FontWeight, Text.FontStretch),
                Text.FontSize, Text.Foreground, VisualTreeHelper.GetDpi(Text).PixelsPerDip);
            if (Origin.X <= Text.TranslatePoint(new Point(), this.ActiveFileControl).X + Math.Min(Text.ActualWidth, Measure.WidthIncludingTrailingWhitespace) + 4) return false;
        }
        return true;
    }

    private bool PrepareMarqueeSelection(DependencyObject? Source, Point Origin, ModifierKeys Modifiers, int ClickCount)
    {
        if (!this.CanStartMarqueeSelection(Source, Origin, Modifiers, ClickCount) || this.MarqueeViewport() is not { } Viewport) return false;
        this.EndMarqueeSelection();
        this.MarqueeClickEntry = this.FileItemContainer(Source)?.DataContext as FileEntry;
        this.MarqueeInitialSelection = this.SelectedEntries().ToHashSet();
        this.MarqueeModifiers = Modifiers;
        this.MarqueeOrigin = Origin;
        this.MarqueePointer = Origin;
        this.MarqueeDocumentOrigin = new Point(Origin.X + Viewport.Viewer.HorizontalOffset,
            Origin.Y - Viewport.Bounds.Top + Viewport.Offset);
        this.MainPanelDragCandidate = null;
        this.MainPanelDeferredSelection = null;
        if (Modifiers == ModifierKeys.None && this.MarqueeClickEntry is null) this.FileGrid.SelectedItems.Clear();
        return true;
    }

    private bool UpdateMarqueeSelection(Point Position)
    {
        if (this.MarqueeOrigin is not { } Origin || this.MarqueeViewport() is not { } Viewport) return false;
        this.MarqueePointer = Position;
        if (!this.MarqueeActive && Math.Abs(Position.X - Origin.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(Position.Y - Origin.Y) < SystemParameters.MinimumVerticalDragDistance) return false;
        this.MarqueeActive = true;
        var Pointer = new Point(Math.Clamp(Position.X, Viewport.Bounds.Left, Viewport.Bounds.Right),
            Math.Clamp(Position.Y, Viewport.Bounds.Top, Viewport.Bounds.Bottom));
        var DocumentPoint = new Point(Pointer.X + Viewport.Viewer.HorizontalOffset, Pointer.Y - Viewport.Bounds.Top + Viewport.Offset);
        var Selection = new Rect(this.MarqueeDocumentOrigin, DocumentPoint);
        var Desired = this.MarqueeModifiers == ModifierKeys.None ? new HashSet<FileEntry>() : new(this.MarqueeInitialSelection);
        var ContentRight = Viewport.Bounds.Left + this.FileGrid.Columns.Where(Column => Column.Visibility == Visibility.Visible).Sum(Column => Column.ActualWidth);
        if (!this.IsDetailsView && FindVisualChild<CDir.Controls.FileTilePanel>(this.FileTiles) is { } Tiles)
        {
            var TileSelection = Selection;
            TileSelection.Offset(-Viewport.Bounds.Left, 0);
            for (var Index = 0; Index < this.FileTiles.Items.Count; Index++)
            {
                if (!TileSelection.IntersectsWith(Tiles.ItemBounds(Index)) || this.FileTiles.Items[Index] is not FileEntry Entry) continue;
                if (this.MarqueeModifiers.HasFlag(ModifierKeys.Control) && this.MarqueeInitialSelection.Contains(Entry)) Desired.Remove(Entry);
                else Desired.Add(Entry);
            }
        }
        else if (Selection.Width > 0 && Selection.Height > 0 && Selection.Right > Viewport.Bounds.Left && Selection.Left < ContentRight)
        {
            foreach (var Index in MarqueeRowsIn(Selection, this.FileGrid.Items.Count, Viewport.RowHeight, Viewport.Groups, Viewport.HeaderHeight))
            {
                if (this.FileGrid.Items[Index] is not FileEntry Entry) continue;
                if (this.MarqueeModifiers.HasFlag(ModifierKeys.Control) && this.MarqueeInitialSelection.Contains(Entry)) Desired.Remove(Entry);
                else Desired.Add(Entry);
            }
        }
        this.ApplyMarqueeSelection(Desired);
        var DisplayOrigin = new Point(this.MarqueeDocumentOrigin.X - Viewport.Viewer.HorizontalOffset,
            this.MarqueeDocumentOrigin.Y - Viewport.Offset + Viewport.Bounds.Top);
        var Display = new Rect(DisplayOrigin, Pointer);
        Display.Intersect(Viewport.Bounds);
        if (!Display.IsEmpty)
        {
            Canvas.SetLeft(this.FileSelectionRectangle, Display.Left);
            Canvas.SetTop(this.FileSelectionRectangle, Display.Top);
            this.FileSelectionRectangle.Width = Display.Width;
            this.FileSelectionRectangle.Height = Display.Height;
            this.FileSelectionRectangle.Visibility = Visibility.Visible;
        }
        return true;
    }

    private void ApplyMarqueeSelection(HashSet<FileEntry> Desired)
    {
        var Current = this.SelectedEntries().ToHashSet();
        this.ApplyingMarqueeSelection = true;
        try
        {
            foreach (var Entry in Current.Except(Desired)) this.FileGrid.SelectedItems.Remove(Entry);
            foreach (var Entry in Desired.Except(Current)) this.FileGrid.SelectedItems.Add(Entry);
        }
        finally { this.ApplyingMarqueeSelection = false; }
        // Refresh counts once per pointer update rather than once per selected row.
        this.UpdateActions();
        this.UpdateCounts();
    }

    private void ScrollMarqueeSelection()
    {
        if (!this.MarqueeActive || this.MarqueeViewport() is not { } Viewport) return;
        if (!this.IsDetailsView && this.Settings.FileView == FileViewMode.List)
        {
            if (this.MarqueePointer.X <= Viewport.Bounds.Left + 20) Viewport.Viewer.LineLeft();
            else if (this.MarqueePointer.X >= Viewport.Bounds.Right - 20) Viewport.Viewer.LineRight();
        }
        else if (this.MarqueePointer.Y <= Viewport.Bounds.Top + 20) Viewport.Viewer.LineUp();
        else if (this.MarqueePointer.Y >= Viewport.Bounds.Bottom - 20) Viewport.Viewer.LineDown();
        this.ActiveFileControl.UpdateLayout();
        this.UpdateMarqueeSelection(this.MarqueePointer);
    }

    private void EndMarqueeSelection(bool Cancel = false)
    {
        if (this.MarqueeOrigin is null) return;
        this.MarqueeScrollTimer?.Stop();
        this.MarqueeOrigin = null;
        var Click = !Cancel && !this.MarqueeActive ? this.MarqueeClickEntry : null;
        this.MarqueeClickEntry = null;
        this.MarqueeActive = false;
        this.FileSelectionRectangle.Visibility = Visibility.Collapsed;
        if (this.ActiveFileControl.IsMouseCaptured) this.ActiveFileControl.ReleaseMouseCapture();
        if (Cancel) this.ApplyMarqueeSelection(this.MarqueeInitialSelection);
        else if (Click is not null) this.SelectFileByClick(Click, this.MarqueeModifiers);
        this.MarqueeInitialSelection = [];
    }

    private void FileGridLostMouseCapture(object Sender, MouseEventArgs E)
    {
        if (!this.ActiveFileControl.IsMouseCaptured) this.EndMarqueeSelection();
    }

    private void StartMarqueeTracking()
    {
        if (!this.ActiveFileControl.CaptureMouse()) { this.EndMarqueeSelection(Cancel: true); return; }
        this.MarqueeScrollTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Input,
            (_, _) => this.ScrollMarqueeSelection(), this.Dispatcher);
        this.MarqueeScrollTimer.Start();
    }
}
