using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace CDir.UI;

public partial class MainWindow
{
    internal const string TabDragFormat = "CDir.TabHeader";
    internal sealed record TabDragPayload(MainWindow Owner, ExplorerTab Page);
    private (ExplorerTab Page, Point Origin)? TabDragCandidate;
    private long LastTabDragScrollAt;
    internal TabDragPayload? ActiveTabDrag;
    internal Func<DependencyObject, object, DragDropEffects, DragDropEffects> StartTabDragDrop = DragDrop.DoDragDrop;

    private TabHeader? DragHeaderFromSource(DependencyObject? Source)
    {
        TabHeader? Found = null;
        var Element = Source;
        for (; Element is not null && !ReferenceEquals(Element, this.TabBar);
            Element = Element is Visual ? VisualTreeHelper.GetParent(Element) : LogicalTreeHelper.GetParent(Element))
        {
            if (Element is ScrollBar || Element is Button { Name: "PinTabButton" or "CloseTabButton" or "NewTabButton" }) return null;
            if (Element is FrameworkElement { DataContext: TabHeader Header }) Found = Header;
        }
        return ReferenceEquals(Element, this.TabBar) ? Found : null;
    }

    internal bool PrepareTabDrag(DependencyObject Source, int ClickCount, Point Position)
    {
        this.TabDragCandidate = null;
        if (this.PaneFromSource(Source) is { } Pane) this.ActivatePane(Pane);
        if (ClickCount != 1 || this.DragHeaderFromSource(Source) is not { } Header || !this.Tabs.Contains(Header.Page)) return false;
        this.TabDragCandidate = (Header.Page, Position);
        return true;
    }

    internal bool TryStartTabDrag(Point Position, bool LeftPressed)
    {
        if (!LeftPressed) { this.TabDragCandidate = null; return false; }
        if (this.ActiveTabDrag is not null || this.TabDragCandidate is not { } Candidate) return false;
        if (Math.Abs(Position.X - Candidate.Origin.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(Position.Y - Candidate.Origin.Y) < SystemParameters.MinimumVerticalDragDistance) return false;
        this.TabDragCandidate = null;
        if (!this.Tabs.Contains(Candidate.Page)) return false;
        var Payload = new TabDragPayload(this, Candidate.Page);
        var Data = new DataObject();
        Data.SetData(TabDragFormat, Payload);
        this.ActiveTabDrag = Payload;
        try
        {
            if (Mouse.Captured is UIElement Captured && this.DragHeaderFromSource(Captured)?.Page == Candidate.Page)
                Captured.ReleaseMouseCapture();
            this.StartTabDragDrop(this.TabBar, Data, DragDropEffects.Move);
        }
        finally { this.ActiveTabDrag = null; this.ClearTabDropFeedback(); this.ClearSplitFeedback(); }
        return true;
    }

    private void TabHeaderDragMouseDown(object Sender, MouseButtonEventArgs E)
    {
        if (E.OriginalSource is DependencyObject Source) this.PrepareTabDrag(Source, E.ClickCount, E.GetPosition(this.TabBar));
    }

    private void TabHeaderDragMouseMove(object Sender, MouseEventArgs E)
    {
        if (this.TryStartTabDrag(E.GetPosition(this.TabBar), E.LeftButton == MouseButtonState.Pressed)) E.Handled = true;
    }

    private void TabHeaderDragMouseUp(object Sender, MouseButtonEventArgs E) => this.TabDragCandidate = null;

    private bool TryTabDropPlan(IDataObject Data, bool PinnedRow, Point Position,
        out ExplorerTab Page, out bool Pinned, out int Destination, out double MarkerX)
    {
        Page = null!; Pinned = false; Destination = 0; MarkerX = 0;
        if (!Data.GetDataPresent(TabDragFormat, false)
            || Data.GetData(TabDragFormat, false) is not TabDragPayload Payload
            || !ReferenceEquals(Payload, this.ActiveTabDrag) || !ReferenceEquals(Payload.Owner, this)
            || !this.Tabs.Contains(Payload.Page)) return false;
        if (PinnedRow && (!this.SeparatePinnedTabs || this.PinnedHeaders.Count == 0)) return false;
        Page = Payload.Page;
        // A single row keeps both groups intact. Separate rows also allow pin/unpin.
        Pinned = this.SeparatePinnedTabs ? PinnedRow : Page.IsTabPinned;
        var Row = PinnedRow ? this.PinnedTabRow : this.NormalTabRow;
        var Items = PinnedRow ? this.PinnedTabHeaders : this.TabHeaders;
        Items.UpdateLayout();
        var DraggedPage = Page;
        var TargetPinned = Pinned;
        var Others = this.Headers.Where(Header => ReferenceEquals(Header.Page.Parent, this.TabPages) && Header.Page.IsTabPinned == TargetPinned
            && !ReferenceEquals(Header.Page, DraggedPage)).ToArray();
        var Slot = 0;
        foreach (var Header in Others)
        {
            if (Items.ItemContainerGenerator.ContainerFromItem(Header) is not FrameworkElement Container) return false;
            var Left = Container.TranslatePoint(new Point(), Row).X;
            if (Position.X < Left + Container.ActualWidth / 2) break;
            Slot++;
        }
        Destination = (Pinned ? 0 : this.PaneTabs(this.ActivePane).Count(Tab => Tab.IsTabPinned && !ReferenceEquals(Tab, DraggedPage))) + Slot;
        if (Slot < Others.Length)
            MarkerX = ((FrameworkElement)Items.ItemContainerGenerator.ContainerFromItem(Others[Slot])).TranslatePoint(new Point(), Row).X;
        else if (Others.Length > 0)
        {
            var Last = (FrameworkElement)Items.ItemContainerGenerator.ContainerFromItem(Others[^1]);
            MarkerX = Last.TranslatePoint(new Point(Last.ActualWidth, 0), Row).X;
        }
        else if (Items.ItemContainerGenerator.ContainerFromItem(this.Headers.Single(Header => ReferenceEquals(Header.Page, DraggedPage))) is FrameworkElement Self)
            MarkerX = Self.TranslatePoint(new Point(), Row).X;
        else MarkerX = Pinned ? 0 : this.NewTabButton.TranslatePoint(new Point(), Row).X;
        return true;
    }

    internal DragDropEffects UpdateTabDragFeedback(IDataObject Data, bool PinnedRow, Point Position)
    {
        this.ClearTabDropFeedback();
        this.ClearSplitFeedback();
        if (!this.TryTabDropPlan(Data, PinnedRow, Position, out _, out _, out _, out var MarkerX)) return DragDropEffects.None;
        var Row = PinnedRow ? this.PinnedTabRow : this.NormalTabRow;
        var Marker = PinnedRow ? this.PinnedTabDropMarker : this.NormalTabDropMarker;
        var Scroll = PinnedRow ? this.PinnedTabScroll : this.TabScroll;
        if (Scroll.ScrollableWidth > 0 && Environment.TickCount64 - this.LastTabDragScrollAt >= 50)
        {
            var Step = Position.X < 24 ? -24 : Position.X > Row.ActualWidth - 24 ? 24 : 0;
            if (Step != 0)
            {
                this.LastTabDragScrollAt = Environment.TickCount64;
                Scroll.ScrollToHorizontalOffset(Scroll.HorizontalOffset + Step);
                Scroll.UpdateLayout();
                this.TryTabDropPlan(Data, PinnedRow, Position, out _, out _, out _, out MarkerX);
            }
        }
        Marker.Margin = new Thickness(Math.Clamp(MarkerX - Row.BorderThickness.Left, 0,
            Math.Max(0, Row.ActualWidth - Row.BorderThickness.Left - Row.BorderThickness.Right - Marker.Width)), 0, 0, 0);
        Marker.Visibility = Visibility.Visible;
        return DragDropEffects.Move;
    }

    internal bool CompleteTabDrop(IDataObject Data, bool PinnedRow, Point Position)
    {
        try
        {
            if (!this.TryTabDropPlan(Data, PinnedRow, Position, out var Page, out var Pinned, out var Destination, out _)) return false;
            var Source = this.PaneFor(Page);
            var Target = this.ActivePane;
            var Transferred = !ReferenceEquals(Source, Target);
            if (Transferred) this.TransferTab(Page, Source, Target);
            var Moved = this.MoveTab(Page, Pinned, Destination);
            if (Transferred) this.SelectTab(Page);
            return Transferred || Moved;
        }
        finally { this.ClearTabDropFeedback(); }
    }

    private void ClearTabDropFeedback()
    {
        foreach (var Pane in this.Panes)
        {
            Pane.PinnedTabDropMarker.Visibility = Visibility.Collapsed;
            Pane.NormalTabDropMarker.Visibility = Visibility.Collapsed;
        }
    }

    internal void TabRowDragOver(object Sender, DragEventArgs E)
    {
        E.Effects = (E.AllowedEffects & DragDropEffects.Move) != 0
            ? this.UpdateTabDragFeedback(E.Data, ReferenceEquals(Sender, this.PinnedTabRow), E.GetPosition((IInputElement)Sender))
            : DragDropEffects.None;
        E.Handled = true;
    }

    internal void TabRowDragLeave(object Sender, DragEventArgs E)
    {
        var Row = (FrameworkElement)Sender;
        if (!new Rect(Row.RenderSize).Contains(E.GetPosition(Row))) this.ClearTabDropFeedback();
    }

    internal void TabRowDrop(object Sender, DragEventArgs E)
    {
        E.Effects = (E.AllowedEffects & DragDropEffects.Move) != 0
            && this.CompleteTabDrop(E.Data, ReferenceEquals(Sender, this.PinnedTabRow), E.GetPosition((IInputElement)Sender))
            ? DragDropEffects.Move : DragDropEffects.None;
        E.Handled = true;
    }
}
