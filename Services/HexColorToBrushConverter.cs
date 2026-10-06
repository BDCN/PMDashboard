using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace PMDashboard.Services;

/// <summary>Converts a "#RRGGBB" string (RompStatus.Color via Romp/Site.StatusColor) to a
/// SolidColorBrush. Falls back to a neutral grey for null/blank/unparsable input rather than
/// throwing — a binding error here shouldn't take down the whole row.</summary>
[ValueConversion(typeof(string), typeof(Brush))]
public class HexColorToBrushConverter : IValueConverter
{
    private static readonly Brush Fallback = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string hex || string.IsNullOrWhiteSpace(hex)) return Fallback;
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            return new SolidColorBrush(color);
        }
        catch
        {
            return Fallback;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
