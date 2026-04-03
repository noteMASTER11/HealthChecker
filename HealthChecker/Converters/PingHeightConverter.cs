using System.Globalization;
using System.Windows.Data;

namespace HealthChecker.Converters;

public sealed class PingHeightConverter : IMultiValueConverter
{
    private const double MinHeight = 4;
    private const double MaxHeight = 76;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2)
        {
            return MinHeight;
        }

        var ping = values[0] as long?;
        if (values[0] is long pingLong)
        {
            ping = pingLong;
        }

        if (!double.TryParse(values[1]?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var maxPing))
        {
            maxPing = 100;
        }

        maxPing = Math.Max(maxPing, 60);

        if (!ping.HasValue || ping.Value <= 0)
        {
            return MinHeight;
        }

        var ratio = Math.Min(1.0, ping.Value / maxPing);
        return MinHeight + ((MaxHeight - MinHeight) * ratio);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
