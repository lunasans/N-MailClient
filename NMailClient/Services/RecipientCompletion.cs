using NMailClient.Models;

namespace NMailClient.Services;

/// <summary>
/// Rechenkern der Empfänger-Autovervollständigung: findet heraus, welches Wort
/// gerade getippt wird, und setzt die Auswahl wieder ein.
///
/// Bewusst ohne WPF, damit die Zeichenpositionen prüfbar sind – genau dort
/// entstehen die Fehler (falscher Bereich ersetzt, Cursor springt).
/// </summary>
public static class RecipientCompletion
{
    private static readonly char[] Separators = [',', ';'];

    /// <summary>
    /// Der Eintrag, in dem die Einfügemarke steht: Anfang, Länge und Text ohne
    /// umgebende Leerzeichen.
    /// </summary>
    public static (int Start, int Length, string Text) CurrentToken(string text, int caret)
    {
        text ??= "";
        caret = Math.Clamp(caret, 0, text.Length);

        var start = text.LastIndexOfAny(Separators, Math.Max(0, caret - 1));
        start = start < 0 ? 0 : start + 1;

        var end = text.IndexOfAny(Separators, caret);
        if (end < 0) end = text.Length;

        return (start, end - start, text[start..end].Trim());
    }

    /// <summary>Ersetzt den Eintrag an der Einfügemarke und liefert Text und neue Position.</summary>
    public static (string Text, int Caret) Replace(string text, int caret, string replacement)
    {
        text ??= "";
        var (start, length, _) = CurrentToken(text, caret);

        // Der Bereich beginnt direkt hinter dem Trennzeichen und enthält damit
        // den Abstand davor. Den behalten, sonst klebt der Empfänger am Komma.
        var raw = text.Substring(start, length);
        var indent = raw.Length - raw.TrimStart().Length;

        var before = text[..(start + indent)];
        var after = text[(start + length)..];

        // Nach der Einfügung ein Trennzeichen anbieten, damit direkt weitergetippt
        // werden kann – aber nur, wenn nicht schon eines folgt.
        var suffix = after.TrimStart().StartsWith(',') || after.TrimStart().StartsWith(';')
            ? ""
            : ", ";

        var inserted = replacement + suffix;
        return (before + inserted + after, before.Length + inserted.Length);
    }

    /// <summary>
    /// Passende Kontakte zum getippten Text. Ohne Adresse taugt ein Kontakt nicht
    /// als Empfänger und wird weggelassen.
    /// </summary>
    public static List<Contact> Suggest(IEnumerable<Contact> contacts, string term, int max = 8)
    {
        if (string.IsNullOrWhiteSpace(term) || term.Length < 2) return [];

        return contacts
            .Where(c => c.Emails.Count > 0 && c.Matches(term))
            // Treffer am Wortanfang zuerst – die meint man üblicherweise.
            .OrderByDescending(c => StartsWith(c, term))
            .ThenBy(c => c.Label, StringComparer.CurrentCultureIgnoreCase)
            .Take(max)
            .ToList();
    }

    private static bool StartsWith(Contact contact, string term)
        => contact.Label.StartsWith(term, StringComparison.CurrentCultureIgnoreCase)
           || contact.Emails.Any(e => e.StartsWith(term, StringComparison.OrdinalIgnoreCase));
}
