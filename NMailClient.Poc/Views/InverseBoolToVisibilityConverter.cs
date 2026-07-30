using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace NMailClient.Poc.Views;

/// <summary>
/// true -&gt; Collapsed, false -&gt; Visible.
///
/// Für Merkmale, die in der <b>Verneinung</b> etwas zeigen — etwa das
/// „Neu"-Kennzeichen an einer Nachricht, die <c>Seen</c> noch nicht ist. Ein
/// zusätzliches Merkmal „IstUngelesen" im Modell wäre dieselbe Aussage ein
/// zweites Mal und liefe irgendwann auseinander.
/// </summary>
public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
