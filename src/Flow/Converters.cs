using System;
using System.Globalization;
using System.Windows.Data;

namespace Flow;

/// <summary>
/// Splits a formatted speed string ("12.3 MB/s") into its numeric and unit halves.
/// SpeedValueConverter → "12.3"   (everything before the last space)
/// SpeedUnitConverter  → "MB/s"   (everything after  the last space)
/// Used in hero cards and sidebar speed boxes to display the number large and the
/// unit smaller without requiring separate ViewModel properties.
/// </summary>
public sealed class SpeedValueConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var s = value as string ?? "";
        var idx = s.LastIndexOf(' ');
        return idx >= 0 ? s[..idx] : s;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class SpeedUnitConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var s = value as string ?? "";
        var idx = s.LastIndexOf(' ');
        return idx >= 0 ? s[(idx + 1)..] : "";
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
