using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace CDir.Controls;

// Expose the complete, fixed scroll range up front. The base presenter still
// realizes only a viewport of real rows; the final blank area is a render offset.
public sealed class FileListRowsPresenter : DataGridRowsPresenter, IScrollInfo
{
    private readonly TranslateTransform Shift = new();
    private double Top;
    private double AvailableHeight;
    private int UpdatingBase;
    private DataGrid? Grid => ItemsControl.GetItemsOwner(this) as DataGrid;
    private int Count => this.Grid?.Items.Count ?? 0;
    private double RowHeight
    {
        get
        {
            var Actual = this.InternalChildren.OfType<DataGridRow>().FirstOrDefault()?.ActualHeight ?? 0;
            if (Actual > 0) return Actual;
            var Height = this.Grid?.RowHeight ?? 22;
            if (!double.IsFinite(Height) || Height <= 0) Height = 22;
            var Dpi = VisualTreeHelper.GetDpi(this).DpiScaleY;
            return Math.Max(1, Math.Round(Height * Dpi) / Dpi);
        }
    }
    private double ViewportPixels
    {
        get
        {
            for (var Parent = VisualTreeHelper.GetParent(this); Parent is not null; Parent = VisualTreeHelper.GetParent(Parent))
                if (Parent is ScrollContentPresenter { ActualHeight: > 0 } Viewport) return Viewport.ActualHeight;
            return this.AvailableHeight;
        }
    }
    private double RowsPerPage => Math.Max(1, this.ViewportPixels / this.RowHeight);
    private double Maximum => Math.Max(0, this.Count - 1);

    public FileListRowsPresenter() => this.RenderTransform = this.Shift;

    private void Synchronize()
    {
        this.Top = Math.Clamp(this.Top, 0, this.Maximum);
        var BaseTop = Math.Floor(Math.Min(this.Top, Math.Max(0, base.ExtentHeight - base.ViewportHeight)));
        if (Math.Abs(base.VerticalOffset - BaseTop) > .001)
        {
            this.UpdatingBase++;
            try { base.SetVerticalOffset(BaseTop); }
            finally { this.UpdatingBase--; }
        }
        this.Shift.Y = -Math.Max(0, this.Top - base.VerticalOffset) * this.RowHeight;
        base.ScrollOwner?.InvalidateScrollInfo();
    }
    private void SetTop(double Value)
    {
        if (double.IsNaN(Value)) throw new ArgumentOutOfRangeException(nameof(Value));
        this.Top = Math.Clamp(Value, 0, this.Maximum);
        this.Synchronize();
        this.InvalidateArrange();
    }
    protected override Size MeasureOverride(Size Available)
    {
        if (double.IsFinite(Available.Height)) this.AvailableHeight = Available.Height;
        this.UpdatingBase++;
        Size Result;
        try { Result = base.MeasureOverride(Available); }
        finally { this.UpdatingBase--; }
        this.Synchronize();
        return Result;
    }
    protected override Size ArrangeOverride(Size Final)
    {
        var Result = base.ArrangeOverride(Final);
        this.Synchronize();
        return Result;
    }
    protected override void OnViewportOffsetChanged(Vector Old, Vector Current)
    {
        base.OnViewportOffsetChanged(Old, Current);
        if (this.UpdatingBase != 0) return;
        this.Top = Math.Clamp(Current.Y, 0, this.Maximum);
        this.Shift.Y = 0;
        base.ScrollOwner?.InvalidateScrollInfo();
    }
    protected override void OnItemsChanged(object Sender, ItemsChangedEventArgs E)
    {
        this.UpdatingBase++;
        try { base.OnItemsChanged(Sender, E); }
        finally { this.UpdatingBase--; }
        if (E.Action == NotifyCollectionChangedAction.Reset) this.Top = 0;
        this.Synchronize();
    }
    private double TopFor(int Index) => Index < this.Top ? Index
        : Index + 1 > this.Top + this.RowsPerPage ? Index + 1 - this.RowsPerPage : this.Top;
    protected override void BringIndexIntoView(int Index)
    {
        var Target = this.TopFor(Index);
        this.UpdatingBase++;
        try { base.BringIndexIntoView(Index); }
        finally { this.UpdatingBase--; }
        this.SetTop(Target);
    }
    private Rect MakeRowVisible(Visual Visual, Rect Bounds)
    {
        var Target = this.Top;
        for (DependencyObject? Node = Visual; Node is not null && !ReferenceEquals(Node, this); Node = VisualTreeHelper.GetParent(Node))
            if (Node is DataGridRow Row && this.Grid is { } Grid)
            {
                var Index = Grid.ItemContainerGenerator.IndexFromContainer(Row);
                if (Index >= 0) Target = this.TopFor(Index);
                break;
            }
        this.UpdatingBase++;
        Rect Result;
        try { Result = base.MakeVisible(Visual, Bounds); }
        finally { this.UpdatingBase--; }
        this.SetTop(Target);
        if (!Result.IsEmpty) Result.Offset(0, this.Shift.Y);
        return Result;
    }
    private double WheelRows => SystemParameters.WheelScrollLines < 0 ? this.RowsPerPage : SystemParameters.WheelScrollLines;
    public override void LineUp() => this.SetTop(this.Top - 1);
    public override void LineDown() => this.SetTop(this.Top + 1);
    public override void PageUp() => this.SetTop(this.Top - this.RowsPerPage);
    public override void PageDown() => this.SetTop(this.Top + this.RowsPerPage);
    public override void MouseWheelUp() => this.SetTop(this.Top - this.WheelRows);
    public override void MouseWheelDown() => this.SetTop(this.Top + this.WheelRows);

    bool IScrollInfo.CanHorizontallyScroll { get => base.CanHorizontallyScroll; set => base.CanHorizontallyScroll = value; }
    bool IScrollInfo.CanVerticallyScroll { get => base.CanVerticallyScroll; set => base.CanVerticallyScroll = value; }
    double IScrollInfo.ExtentWidth => base.ExtentWidth;
    double IScrollInfo.ExtentHeight => this.Count == 0 ? 0 : this.Maximum + this.RowsPerPage;
    double IScrollInfo.ViewportWidth => base.ViewportWidth;
    double IScrollInfo.ViewportHeight => this.RowsPerPage;
    double IScrollInfo.HorizontalOffset => base.HorizontalOffset;
    double IScrollInfo.VerticalOffset => this.Top;
    ScrollViewer IScrollInfo.ScrollOwner { get => base.ScrollOwner; set => base.ScrollOwner = value; }
    void IScrollInfo.SetHorizontalOffset(double Offset) => base.SetHorizontalOffset(Offset);
    void IScrollInfo.SetVerticalOffset(double Offset) => this.SetTop(Offset);
    Rect IScrollInfo.MakeVisible(Visual Visual, Rect Bounds) => this.MakeRowVisible(Visual, Bounds);
    void IScrollInfo.LineUp() => this.LineUp();
    void IScrollInfo.LineDown() => this.LineDown();
    void IScrollInfo.PageUp() => this.PageUp();
    void IScrollInfo.PageDown() => this.PageDown();
    void IScrollInfo.MouseWheelUp() => this.MouseWheelUp();
    void IScrollInfo.MouseWheelDown() => this.MouseWheelDown();
    void IScrollInfo.LineLeft() => base.LineLeft();
    void IScrollInfo.LineRight() => base.LineRight();
    void IScrollInfo.PageLeft() => base.PageLeft();
    void IScrollInfo.PageRight() => base.PageRight();
    void IScrollInfo.MouseWheelLeft() => base.MouseWheelLeft();
    void IScrollInfo.MouseWheelRight() => base.MouseWheelRight();
}
