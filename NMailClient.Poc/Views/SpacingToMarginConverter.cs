using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace NMailClient.Poc.Views;

/// <summary>
/// Zeilenabstand (aus der Listendichte) in einen Rand umrechnen.
/// Nur oben/unten – seitlich bleibt der Abstand konstant, sonst „wandern" die
/// Zeilen beim Umschalten der Dichte horizontal.
/// </summary>
public class SpacingToMarginConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var spacing = value is double d ? d : 7;
        return new Thickness(2, spacing, 0, spacing);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
