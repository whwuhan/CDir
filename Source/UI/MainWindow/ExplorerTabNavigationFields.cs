using System.Windows;
using System.Windows.Controls.Primitives;

namespace CDir.UI;

public partial class ExplorerTab
{
    private bool ResizingNavigationFields;
    private double LastNavigationFieldsWidth = double.NaN;

    private void NavigationFieldsSizeChanged(object Sender, SizeChangedEventArgs E)
    {
        if (!E.WidthChanged || E.NewSize.Width <= 0) return;
        var Changed = !double.IsFinite(this.LastNavigationFieldsWidth) || Math.Abs(E.NewSize.Width - this.LastNavigationFieldsWidth) > .5;
        this.LastNavigationFieldsWidth = E.NewSize.Width;
        if (Changed && !this.ResizingNavigationFields) this.ResetNavigationFieldWidths();
    }

    internal void ResetNavigationFieldWidths()
    {
        if (this.NavigationFields is null || this.SearchFieldColumn is null) return;
        var Columns = this.NavigationFields.ColumnDefinitions;
        var Reserved = Columns[0].Width.Value + Columns[2].Width.Value + Columns[4].Width.Value
            + Math.Max(Columns[5].ActualWidth, this.ColumnMenuButton.Width);
        var Width = this.NavigationFields.ActualWidth > 0 ? this.NavigationFields.ActualWidth : this.LastNavigationFieldsWidth;
        var Available = double.IsFinite(Width) ? Math.Max(0, Width - Reserved) : 200 + this.AddressFieldColumn.MinWidth;
        this.AddressFieldColumn.Width = new GridLength(1, GridUnitType.Star);
        this.SearchFieldColumn.Width = new GridLength(Math.Clamp(Available - this.AddressFieldColumn.MinWidth,
            this.SearchFieldColumn.MinWidth, 200));
    }

    private void AddressSearchDragStarted(object Sender, DragStartedEventArgs E) => this.ResizingNavigationFields = true;
    private void AddressSearchDragCompleted(object Sender, DragCompletedEventArgs E)
    {
        this.ResizingNavigationFields = false;
        this.QueueAddressLayout();
    }
}
