using System.Globalization;
using System.Windows.Data;

namespace PMDashboard.Services;

/// <summary>
/// Coerces a boxed cell value (from CustomDataRow.Values, typed object? since the column
/// set is dynamic) to/from the CLR type each editing control expects. ConverterParameter
/// carries the column's ColumnType ("Text" | "Number" | "Date" | "Bool") since a single
/// stateless instance is shared across every dynamically-built column.
/// </summary>
public class CustomCellValueConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var type = parameter as string ?? "Text";
        return type switch
        {
            "Bool" => value is bool b && b,
            "Date" => value as DateTime?,
            "Number" => value switch
            {
                double d => d.ToString("G", culture),
                null     => "",
                _        => value.ToString() ?? "",
            },
            _ => value?.ToString() ?? "",
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var type = parameter as string ?? "Text";
        return type switch
        {
            "Bool"   => value is bool b && b,
            "Date"   => value as DateTime?,
            "Number" => double.TryParse(value as string, NumberStyles.Any, culture, out var d) ? d : (object?)null,
            _        => value as string ?? "",
        };
    }
}
