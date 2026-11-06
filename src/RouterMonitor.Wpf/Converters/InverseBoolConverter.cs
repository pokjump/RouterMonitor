using System.Globalization;
using System.Windows.Data;

namespace RouterMonitor.Wpf.Converters;

/// <summary>true → false and back; used to disable a row's inputs while it is busy.</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
