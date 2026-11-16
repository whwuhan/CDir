using System.Windows;
using System.Windows.Controls;

namespace CDir.UI;

// Keep the same reading/tab order as the column list while using the available
// width. Wrap long labels rather than reducing the user's chosen font size.
internal sealed class ColumnOptionsPanel : WrapPanel
{
    protected override Size MeasureOverride(Size Available)
    {
        if (double.IsFinite(Available.Width) && Available.Width > 0)
        {
            var FontSize = this.Children.OfType<Control>().FirstOrDefault()?.FontSize ?? 13;
            var Count = Math.Clamp((int)Math.Floor(Available.Width / Math.Max(240, FontSize * 18)), 1, Math.Max(1, this.Children.Count));
            this.ItemWidth = Math.Max(1, (Available.Width - 1) / Count);
        }
        return base.MeasureOverride(Available);
    }
}
