using System.Text;
using System.Text.Json.Serialization;

namespace NMailClient.Poc.Models;

/// <summary>
/// Ein Etikett: IMAP-Keyword (serverseitig, ASCII) plus Anzeigename und Farbe (lokal).
/// Andere Clients sehen das Keyword, Anzeige und Farbe sind Sache dieses Clients.
/// </summary>
public class LabelDef
{
    [JsonPropertyName("keyword")] public string Keyword { get; set; } = "";
    [JsonPropertyName("display")] public string Display { get; set; } = "";
    [JsonPropertyName("color")] public string Color { get; set; } = "#4A7DBD";

    /// <summary>
    /// IMAP-Keywords sind Atome: kein Leerzeichen, kein Nicht-ASCII. Umlaute werden
    /// transliteriert, alles andere Unerlaubte fällt weg.
    /// </summary>
    public static string MakeKeyword(string display)
    {
        var s = display.Trim()
            .Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue")
            .Replace("Ä", "Ae").Replace("Ö", "Oe").Replace("Ü", "Ue")
            .Replace("ß", "ss");

        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-')
                sb.Append(c);
            else if (c == ' ') sb.Append('_');
        }
        return sb.Length > 0 ? sb.ToString() : "Etikett";
    }
}

/// <summary>
/// Globale Optionen (im Gegensatz zu den kontobezogenen in <see cref="Account"/>).
/// Bewusst nur Felder, die auch verwendet werden – Platzhalter für geplante Features
/// würden hier nur suggerieren, es gäbe sie schon.
/// </summary>
public class AppSettings
{
    /// <summary>"Light", "Dark" oder "System".</summary>
    [JsonPropertyName("theme")] public string Theme { get; set; } = "System";

    /// <summary>Nachrichten pro Seite in der Mailliste ("Mehr laden").</summary>
    [JsonPropertyName("pageSize")] public int PageSize { get; set; } = 50;

    /// <summary>Vor dem Löschen rückfragen.</summary>
    [JsonPropertyName("confirmBeforeDelete")]
    public bool ConfirmBeforeDelete { get; set; } = true;

    /// <summary>Mailliste nach Zeitraum gruppieren (Heute / Gestern / Diese Woche …).</summary>
    [JsonPropertyName("groupByDate")] public bool GroupByDate { get; set; } = true;

    /// <summary>Konversationsansicht: Nachrichten eines Threads zusammenfassen.</summary>
    [JsonPropertyName("threadView")] public bool ThreadView { get; set; }

    /// <summary>Reiter für Kategorien über der Mailliste zeigen.</summary>
    [JsonPropertyName("showCategories")] public bool ShowCategories { get; set; }

    /// <summary>
    /// Kategorie je Absenderadresse – überschreibt die Heuristik. Der Nutzer
    /// korrigiert damit dauerhaft, was falsch einsortiert wurde.
    /// </summary>
    [JsonPropertyName("categoryOverrides")]
    public Dictionary<string, string> CategoryOverrides { get; set; } = new();

    /// <summary>"Compact", "Normal" oder "Comfortable".</summary>
    [JsonPropertyName("listDensity")] public string ListDensity { get; set; } = "Normal";

    /// <summary>Zeilenabstand der Mailliste, abgeleitet aus <see cref="ListDensity"/>.</summary>
    [JsonIgnore]
    public double RowSpacing => ListDensity switch
    {
        "Compact" => 3,
        "Comfortable" => 12,
        _ => 7,
    };

    /// <summary>Kantenlänge des Absender-Avatars.</summary>
    [JsonIgnore]
    public double AvatarSize => ListDensity switch
    {
        "Compact" => 24,
        "Comfortable" => 38,
        _ => 32,
    };

    /// <summary>
    /// Definierte Etiketten. Die Vorgaben greifen nur bei fabrikneuer Konfiguration –
    /// wer alle löscht, behält eine leere Liste.
    /// </summary>
    [JsonPropertyName("labels")]
    public List<LabelDef> Labels { get; set; } =
    [
        new() { Keyword = "Wichtig", Display = "Wichtig", Color = "#C04F4F" },
        new() { Keyword = "Arbeit", Display = "Arbeit", Color = "#4A7DBD" },
        new() { Keyword = "Privat", Display = "Privat", Color = "#3F7D58" },
    ];

    /// <summary>
    /// Wartefrist vor dem Versand, in der zurückgeholt werden kann. 0 = sofort senden.
    /// </summary>
    [JsonPropertyName("undoSendSeconds")] public int UndoSendSeconds { get; set; } = 10;

    [JsonIgnore] public int EffectiveUndoSeconds => Math.Clamp(UndoSendSeconds, 0, 120);

    /// <summary>
    /// Adresse einer LibreTranslate-Instanz, z.B. https://translate.example.org.
    /// Leer = Übersetzung aus. Der API-Schlüssel liegt im Anmeldeinformations-
    /// verwalter, nicht hier.
    /// </summary>
    [JsonPropertyName("translateUrl")] public string TranslateUrl { get; set; } = "";

    /// <summary>Zielsprache der Übersetzung.</summary>
    [JsonPropertyName("translateTarget")] public string TranslateTarget { get; set; } = "de";

    /// <summary>
    /// Wurzel des Anhang-Archivs. Leer = Vorgabe unter „Dokumente".
    /// </summary>
    [JsonPropertyName("archiveDir")] public string ArchiveDir { get; set; } = "";

    /// <summary>
    /// Absender, deren externe Bilder ohne Nachfrage geladen werden.
    /// Bewusst je Adresse, nicht je Domain: Vertrauen gilt dem Absender.
    /// </summary>
    [JsonPropertyName("trustedSenders")]
    public List<string> TrustedSenders { get; set; } = [];

    public bool IsTrusted(string? address)
        => !string.IsNullOrWhiteSpace(address)
           && TrustedSenders.Contains(address, StringComparer.OrdinalIgnoreCase);

    /// <summary>Textbausteine für den Composer.</summary>
    [JsonPropertyName("templates")] public List<TemplateDef> Templates { get; set; } = [];

    // ---- Infobereich und Meldungen -----------------------------------------

    /// <summary>
    /// Sprache der Oberfläche: „de", „en" oder „system" für die Windows-Vorgabe.
    /// </summary>
    [JsonPropertyName("language")] public string Language { get; set; } = "system";

    /// <summary>Schliessen legt die Anwendung in den Infobereich statt sie zu beenden.</summary>
    [JsonPropertyName("minimizeToTray")] public bool MinimizeToTray { get; set; } = true;

    /// <summary>Sprechblase bei neuer Post.</summary>
    [JsonPropertyName("notifyOnNewMail")] public bool NotifyOnNewMail { get; set; } = true;

    [JsonPropertyName("quietHoursEnabled")] public bool QuietHoursEnabled { get; set; }

    /// <summary>Als „HH:mm" gespeichert – in der Datei lesbar und zeitzonenfrei.</summary>
    [JsonPropertyName("quietFrom")] public string QuietFrom { get; set; } = "22:00";

    [JsonPropertyName("quietTo")] public string QuietTo { get; set; } = "07:00";

    /// <summary>
    /// Die Ruhezeiten als geprüfte Werte. Unlesbare Zeitangaben in der Datei
    /// führen zur Vorgabe statt zu einer Ausnahme beim Start.
    /// </summary>
    [JsonIgnore]
    public Services.Shell.QuietHours QuietHours => new(
        QuietHoursEnabled,
        ParseTime(QuietFrom, new TimeOnly(22, 0)),
        ParseTime(QuietTo, new TimeOnly(7, 0)));

    private static TimeOnly ParseTime(string value, TimeOnly fallback)
        => TimeOnly.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var t)
            ? t : fallback;

    /// <summary>Schemaversion – erlaubt spätere Migrationen ohne Rateverfahren.</summary>
    [JsonPropertyName("schema")] public int Schema { get; set; } = 1;

    /// <summary>Abgeleitet – gehört nicht in die Datei.</summary>
    [JsonIgnore] public int EffectivePageSize => Math.Clamp(PageSize, 10, 200);
}
