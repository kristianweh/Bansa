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

/// Maps a 0-100 progress value to a pixel width capped at the track's ActualWidth.
/// Bindings: [0] = int progress (0–100), [1] = double trackWidth
public sealed class ProgressWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        // Guard: either value can be UnsetValue (MS.Internal.NamedObject) during layout
        if (values is not { Length: >= 2 }) return 0d;
        if (values[0] is not int and not double and not float) return 0d;
        if (values[1] is not double trackWidth || trackWidth <= 0) return 0d;

        var progress = System.Convert.ToDouble(values[0]);
        return Math.Max(0, Math.Min(trackWidth, trackWidth * progress / 100.0));
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
