namespace NMailClient.Models;

/// <summary>
/// Die Farben für Konten und Etiketten, an einer Stelle.
///
/// Vorher standen sie im Code des Einstellungsfensters und galten nur für
/// Etiketten; Konten hatten stattdessen einen festen Vorgabewert. Damit bekam
/// jedes weitere Konto dieselbe Farbe wie das erste — im gemeinsamen
/// Posteingang wäre die Farbe dann kein Unterscheidungsmerkmal mehr, sondern
/// eine Irreführung.
/// </summary>
public static class ColorPalette
{
    /// <summary>
    /// Bewusst kräftig und gut unterscheidbar, auch nebeneinander als kleiner
    /// Punkt. Die letzte ist ein neutrales Grau — als Ausweichfarbe brauchbar,
    /// deshalb steht sie hinten.
    /// </summary>
    public static readonly string[] Colors =
    [
        "#4A7DBD", "#C04F4F", "#3F7D58", "#B5651D",
        "#8E5EA2", "#3D7F8C", "#7A6A3A", "#6A737D",
    ];

    public static string Default => Colors[0];

    /// <summary>
    /// Die erste noch nicht vergebene Farbe. Sind alle in Gebrauch, wird
    /// reihum weitergezählt — mehr als acht Konten sind selten, und zwei
    /// gleiche Farben sind dann immer noch besser als gar keine Vergabe.
    /// </summary>
    public static string NextFree(IEnumerable<string?> inUse)
    {
        var taken = new HashSet<string>(
            inUse.Where(c => !string.IsNullOrWhiteSpace(c))!,
            StringComparer.OrdinalIgnoreCase);

        foreach (var color in Colors)
            if (!taken.Contains(color)) return color;

        return Colors[taken.Count % Colors.Length];
    }

    /// <summary>
    /// Die Farbe aus der Tafel, die zu <paramref name="color"/> passt – für die
    /// Auswahlliste, die nur Werte aus der Tafel kennt. Unbekannte Angaben
    /// (etwa von Hand in die Einstellungen geschrieben) fallen auf die erste.
    /// </summary>
    public static string Nearest(string? color)
        => Colors.FirstOrDefault(
               c => string.Equals(c, color, StringComparison.OrdinalIgnoreCase))
           ?? Default;
}
