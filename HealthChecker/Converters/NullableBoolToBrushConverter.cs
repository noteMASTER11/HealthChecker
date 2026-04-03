using System.Globalization;
using System.Windows.Data;
namespace HealthChecker.Converters;

public sealed class NullableBoolToBrushConverter : IValueConverter
{
    public required System.Windows.Media.Brush OnlineBrush { get; set; }

    public required System.Windows.Media.Brush OfflineBrush { get; set; }

    public required System.Windows.Media.Brush UnknownBrush { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool state)
        {
            return state ? OnlineBrush : OfflineBrush;
        }

        return UnknownBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
