using System.Globalization;
using System.Windows.Data;

namespace PMDashboard.Services;

/// <summary>Returns true when the value is non-null, false when null.</summary>
[ValueConversion(typeof(object), typeof(bool))]
public class NullToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is not null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
