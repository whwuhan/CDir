using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

    private (ScrollViewer Viewer, Rect Bounds, double RowHeight, double Offset)? MarqueeViewport()
    {
        var Viewer = FindVisualChild<ScrollViewer>(this.ActiveFileControl);
        var Presenter = Viewer is null ? null : FindVisualChild<ScrollContentPresenter>(Viewer);
        if (Viewer is null || Presenter is null || Presenter.ActualWidth <= 0 || Presenter.ActualHeight <= 0) return null;
        if (!this.IsDetailsView && FindVisualChild<CDir.Controls.FileTilePanel>(this.FileTiles) is { } Tiles)
            return (Viewer, new Rect(Presenter.TranslatePoint(new Point(), this.ActiveFileControl), Presenter.RenderSize), Tiles.CellHeight, Tiles.VerticalOffset);
        var FirstRow = FindVisualChild<DataGridRow>(this.FileGrid);
        var Height = FirstRow is { ActualHeight: > 0 } ? FirstRow.ActualHeight : this.FileGrid.RowHeight;
        if (!double.IsFinite(Height) || Height <= 0) return null;
        var Bounds = new Rect(Presenter.TranslatePoint(new Point(), this.ActiveFileControl), Presenter.RenderSize);
        // Use a realized row to account for both item and pixel scrolling, including DPI rounding.
        var Offset = FirstRow is null ? 0 : this.FileGrid.ItemContainerGenerator.IndexFromContainer(FirstRow) * Height
            - (FirstRow.TranslatePoint(new Point(), this.ActiveFileControl).Y - Bounds.Top);
        return (Viewer, Bounds, Height, Offset);
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
            var First = Math.Max(0, (int)Math.Floor(Selection.Top / Viewport.RowHeight));
            var Last = Math.Min(this.FileGrid.Items.Count - 1, (int)Math.Ceiling(Selection.Bottom / Viewport.RowHeight) - 1);
            for (var Index = First; Index <= Last; Index++)
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
