using System.Windows;

namespace CDir.UI;

public partial class MainWindow
{
    internal void DistributePanesEvenly()
    {
        if (this.WorkspaceClosed || this.Panes.Count < 2) return;
        var Spans = new Dictionary<PaneNode, (int Columns, int Rows)>();
        (int Columns, int Rows) Measure(PaneNode Node)
        {
            if (Node.Pane is not null) return Spans[Node] = (1, 1);
            var First = Measure(Node.First!);
            var Second = Measure(Node.Second!);
            // Stacked rows do not increase their column's width weight, and
            // side-by-side columns do not increase their row's height weight.
            return Spans[Node] = Node.Horizontal
                ? (First.Columns + Second.Columns, Math.Max(First.Rows, Second.Rows))
                : (Math.Max(First.Columns, Second.Columns), First.Rows + Second.Rows);
        }
        void Balance(PaneNode Node, Size Bounds)
        {
            if (Node.Pane is not null) return;
            var First = Spans[Node.First!];
            var Second = Spans[Node.Second!];
            var FirstWeight = Node.Horizontal ? First.Columns : First.Rows;
            var SecondWeight = Node.Horizontal ? Second.Columns : Second.Rows;
            var Surface = Node.Surface!;
            var Divider = Node.Horizontal ? Surface.ColumnDefinitions[1].Width.Value : Surface.RowDefinitions[1].Height.Value;
            var Length = Node.Horizontal ? Bounds.Width : Bounds.Height;
            var Available = Math.Max(0, Length - Divider);
            // Include internal dividers in each branch's share, rather than
            // letting accumulated divider widths shrink deeply nested panes.
            var FirstLength = (Length + Divider) * FirstWeight / (FirstWeight + SecondWeight) - Divider;
            var FirstMinimum = Node.Horizontal ? Surface.ColumnDefinitions[0].MinWidth : Surface.RowDefinitions[0].MinHeight;
            var SecondMinimum = Node.Horizontal ? Surface.ColumnDefinitions[2].MinWidth : Surface.RowDefinitions[2].MinHeight;
            if (Available >= FirstMinimum + SecondMinimum)
                FirstLength = Math.Clamp(FirstLength, FirstMinimum, Available - SecondMinimum);
            FirstLength = Math.Clamp(FirstLength, 0, Available);
            Node.Ratio = Available > 0 ? FirstLength / Available : (double)FirstWeight / (FirstWeight + SecondWeight);
            if (Node.Horizontal)
            {
                Surface.ColumnDefinitions[0].Width = new GridLength(Node.Ratio, GridUnitType.Star);
                Surface.ColumnDefinitions[2].Width = new GridLength(1 - Node.Ratio, GridUnitType.Star);
                Balance(Node.First!, new Size(FirstLength, Bounds.Height));
                Balance(Node.Second!, new Size(Available - FirstLength, Bounds.Height));
            }
            else
            {
                Surface.RowDefinitions[0].Height = new GridLength(Node.Ratio, GridUnitType.Star);
                Surface.RowDefinitions[2].Height = new GridLength(1 - Node.Ratio, GridUnitType.Star);
                Balance(Node.First!, new Size(Bounds.Width, FirstLength));
                Balance(Node.Second!, new Size(Bounds.Width, Available - FirstLength));
            }
        }
        Measure(this.LayoutRoot);
        Balance(this.LayoutRoot, this.LayoutRoot.View.RenderSize);
        this.UpdateLayout();
        this.PersistWorkspaceNow();
    }
}
