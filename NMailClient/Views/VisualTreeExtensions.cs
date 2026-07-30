using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace NMailClient.Views;

public static class VisualTreeExtensions
{
    /// <summary>
    /// Sucht den nächsten Vorfahren des angegebenen Typs. Wird gebraucht, um bei
    /// Mausereignissen zu unterscheiden, ob eine Zeile oder die Leerfläche
    /// darunter getroffen wurde.
    /// </summary>
    public static T? FindAncestor<T>(this DependencyObject? start) where T : DependencyObject
    {
        var current = start;
        while (current is not null)
        {
            if (current is T match) return match;

            // VisualTreeHelper scheitert an ContentElement (z.B. Run in TextBlock) –
            // dort über den logischen Elternteil weitergehen.
            current = current is Visual or Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }
}
