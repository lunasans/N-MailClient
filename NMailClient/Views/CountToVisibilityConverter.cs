using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace NMailClient.Views;

/// <summary>0 -&gt; Collapsed, alles andere -&gt; Visible. Für die Anhangleiste.</summary>
public class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int n && n > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
