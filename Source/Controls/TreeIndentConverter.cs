using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace CDir.Controls;

public sealed class TreeIndentConverter : IValueConverter
{
    public object Convert(object Value, Type TargetType, object Parameter, CultureInfo Culture)
    {
        var Depth = Value is int Index ? Index + 1 : 1;
        return Parameter is "Depth" ? Depth : new Thickness(19 * Depth, 0, 0, 0);
    }

    public object ConvertBack(object Value, Type TargetType, object Parameter, CultureInfo Culture) => Binding.DoNothing;
}
