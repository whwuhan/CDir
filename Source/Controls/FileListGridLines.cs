using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CDir.Controls;

/// <summary>Continues the file list's row and column guides through the empty viewport.</summary>
public sealed class FileListGridLines : FrameworkElement
{
    public static readonly DependencyProperty GridOwnerProperty = DependencyProperty.Register(
        nameof(GridOwner), typeof(DataGrid), typeof(FileListGridLines),
        new PropertyMetadata(null, (Owner, _) => ((FileListGridLines)Owner).Attach()));
    public DataGrid? GridOwner { get => (DataGrid?)GetValue(GridOwnerProperty); set => SetValue(GridOwnerProperty, value); }

    private Rect Viewport;
    private double[] Columns = [];
    private double RowOrigin;
    private double RowHeight;

    public FileListGridLines()
    {
        this.IsHitTestVisible = false;
        this.Loaded += (_, _) => this.Attach();
        this.Unloaded += (_, _) => this.LayoutUpdated -= this.OwnerLayoutUpdated;
    }

    private void Attach()
    {
        this.LayoutUpdated -= this.OwnerLayoutUpdated;
        if (this.IsLoaded) this.LayoutUpdated += this.OwnerLayoutUpdated;
        this.InvalidateVisual();
    }

    private static T? Descendant<T>(DependencyObject Parent) where T : DependencyObject
    {
        for (var Index = 0; Index < VisualTreeHelper.GetChildrenCount(Parent); Index++)
        {
            var Child = VisualTreeHelper.GetChild(Parent, Index);
            if (Child is T Match) return Match;
            if (Descendant<T>(Child) is { } Found) return Found;
        }
        return null;
    }

    private void OwnerLayoutUpdated(object? Sender, EventArgs E)
    {
        if (!this.IsVisible || this.GridOwner is not { IsVisible: true } Grid) return;
        var Scroll = Descendant<ScrollViewer>(Grid);
        var Presenter = Scroll is null ? null : Descendant<ScrollContentPresenter>(Scroll);
        if (Scroll is null || Presenter is null || Presenter.ActualWidth <= 0 || Presenter.ActualHeight <= 0) return;
        var Viewport = new Rect(Presenter.TranslatePoint(new Point(), this), Presenter.RenderSize);
        Viewport.Intersect(new Rect(this.RenderSize));
        var Positions = new List<double>();
        var X = Viewport.Left - Scroll.HorizontalOffset;
        foreach (var Column in Grid.Columns.Where(Column => Column.Visibility == Visibility.Visible).OrderBy(Column => Column.DisplayIndex))
        {
            X += Column.ActualWidth;
            if (X > Viewport.Left && X <= Viewport.Right) Positions.Add(X);
        }
        // Use an actual row as the origin to follow both item and pixel scrolling.
        var FirstRow = Descendant<DataGridRow>(Grid);
        var Dpi = VisualTreeHelper.GetDpi(this);
        var RowHeight = FirstRow is { ActualHeight: > 0 } ? FirstRow.ActualHeight
            : Math.Round(Grid.RowHeight * Dpi.DpiScaleY) / Dpi.DpiScaleY;
        if (!double.IsFinite(RowHeight) || RowHeight <= 0) return;
        var RowOrigin = FirstRow is null ? Viewport.Top : FirstRow.TranslatePoint(new Point(), this).Y;
        RowOrigin = Viewport.Top + ((RowOrigin - Viewport.Top) % RowHeight + RowHeight) % RowHeight;
        var Columns = Positions.ToArray();
        if (Viewport == this.Viewport && Columns.SequenceEqual(this.Columns)
            && RowOrigin == this.RowOrigin && RowHeight == this.RowHeight) return;
        this.Viewport = Viewport;
        this.Columns = Columns;
        this.RowOrigin = RowOrigin;
        this.RowHeight = RowHeight;
        this.InvalidateVisual();
    }

    protected override void OnRender(DrawingContext Context)
    {
        base.OnRender(Context);
        if (this.Viewport.IsEmpty || this.RowHeight <= 0) return;
        var Dpi = VisualTreeHelper.GetDpi(this);
        var Brush = (Brush)this.FindResource("Line");
        var Horizontal = new Pen(Brush, 1 / Dpi.DpiScaleY);
        var Vertical = new Pen(Brush, 1 / Dpi.DpiScaleX);
        Context.PushClip(new RectangleGeometry(this.Viewport));
        foreach (var X in this.Columns)
        {
            var Aligned = (Math.Round(X * Dpi.DpiScaleX) - 0.5) / Dpi.DpiScaleX;
            Context.DrawLine(Vertical, new Point(Aligned, this.Viewport.Top), new Point(Aligned, this.Viewport.Bottom));
        }
        for (var Y = this.RowOrigin; Y <= this.Viewport.Bottom; Y += this.RowHeight)
        {
            if (Y <= this.Viewport.Top) continue;
            var Aligned = (Math.Round(Y * Dpi.DpiScaleY) - 0.5) / Dpi.DpiScaleY;
            Context.DrawLine(Horizontal, new Point(this.Viewport.Left, Aligned), new Point(this.Viewport.Right, Aligned));
        }
        Context.Pop();
    }
}
