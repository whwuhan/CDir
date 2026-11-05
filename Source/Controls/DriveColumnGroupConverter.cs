using System.Globalization;
using System.Windows.Data;

namespace CDir.Controls;

public sealed class DriveColumnGroupConverter : IValueConverter
{
    // Only drive labels participate; long expanded folder names keep their own widths.
    public object Convert(object Value, Type TargetType, object Parameter, CultureInfo Culture)
        => Value is true ? Parameter : null!;

    public object ConvertBack(object Value, Type TargetType, object Parameter, CultureInfo Culture)
        => throw new NotSupportedException();
}
