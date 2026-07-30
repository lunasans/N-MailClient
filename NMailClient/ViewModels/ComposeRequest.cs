using System.Text;
using MimeKit;
using NMailClient.Models;

namespace NMailClient.ViewModels;

/// <summary>Vorbelegung für den Composer (neu / Antwort / Weiterleitung).</summary>
public class ComposeRequest
{
    public string To { get; init; } = "";
    public string Cc { get; init; } = "";
    public string Subject { get; init; } = "";
    public string Body { get; init; } = "";

    // ---- Antwort-Kontext ---------------------------------------------------

    /// <summary>Message-ID des Originals – wird zu In-Reply-To der Antwort.</summary>
    public string? InReplyTo { get; init; }

    /// <summary>References des Originals; die Antwort hängt dessen ID hinten an.</summary>
    public IReadOnlyList<string> References { get; init; } = [];

    /// <summary>Ordner + UID des Originals – für das \Answered-Flag nach dem Senden.</summary>
    public string? ReplyToFolder { get; set; }
    public uint? ReplyToUid { get; init; }

    // ---- Entwurf -----------------------------------------------------------

    /// <summary>
    /// UID eines geladenen Entwurfs. Gesetzt heisst: der Composer schreibt daran
    /// weiter und ersetzt ihn beim Speichern, statt einen zweiten anzulegen.
    /// </summary>
    public uint? DraftUid { get; init; }

    /// <summary>Vorhandene Anhänge des Entwurfs (temporäre Dateipfade).</summary>
    public IReadOnlyList<string> Attachments { get; init; } = [];

    /// <summary>Signatur nicht erneut anhängen – der Entwurf hat sie schon.</summary>
    public bool SuppressSignature { get; init; }

    public static ComposeRequest Blank() => new();

    public static ComposeRequest FromDraft(DraftContent d) => new()
    {
        To = d.To,
        Cc = d.Cc,
        Subject = d.Subject,
        Body = d.Body,
        InReplyTo = d.InReplyTo,
        References = d.References,
        DraftUid = d.Uid,
        Attachments = d.AttachmentPaths,
        SuppressSignature = true,
    };

    /// <param name="own">
    /// Eigene Adressen des Kontos samt Aliasen. Bei „Allen antworten" fallen sie
    /// aus der Kopie heraus — sonst schickte man sich jede Antwort selbst mit.
    /// </param>
    public static ComposeRequest Reply(
        MailBody src, bool all, IEnumerable<string>? own = null)
    {
        var mine = new HashSet<string>(
            (own ?? []).Where(a => !string.IsNullOrWhiteSpace(a)),
            StringComparer.OrdinalIgnoreCase);

        // Reply-To schlägt From: der Absender bestimmt damit ausdrücklich, wohin
        // die Antwort gehen soll. Bei Verteilern ist das die Liste.
        var target = string.IsNullOrWhiteSpace(src.ReplyTo) ? src.From : src.ReplyTo;

        return new ComposeRequest
        {
            To = ExtractAddresses(target),

            // Der Empfänger der Antwort gehört nicht zusätzlich in die Kopie.
            Cc = all
                ? ExtractAddresses(src.To, mine.Concat(Addresses(target)))
                : "",

            Subject = Prefix(src.Subject, "Re: "),
            Body = Quote(src),
            InReplyTo = src.MessageId,
            References = src.References,
            ReplyToUid = src.Uid,
        };
    }

    public static ComposeRequest Forward(MailBody src) => new()
    {
        Subject = Prefix(src.Subject, "Fwd: "),
        Body = Quote(src),
    };

    /// <summary>
    /// Kürzel, die eine <b>Antwort</b> kennzeichnen. Sprachabhängig: deutsche
    /// Programme schicken „AW:", englische „Re:". Ohne diese Liste entstünde bei
    /// jeder Runde ein weiteres Kürzel — „Re: AW: Re: AW: Rechnung".
    /// </summary>
    private static readonly string[] ReplyPrefixes =
        ["Re:", "AW:", "Antwort:", "Antw:", "Sv:", "Odp:", "Ref:"];

    /// <summary>Dasselbe für <b>Weiterleitungen</b>: englisch „Fwd:", deutsch „WG:".</summary>
    private static readonly string[] ForwardPrefixes =
        ["Fwd:", "Fw:", "WG:", "Wtr:", "Tr:"];

    private static string Prefix(string subject, string prefix)
    {
        var known = prefix.StartsWith("Re", StringComparison.OrdinalIgnoreCase)
            ? ReplyPrefixes
            : ForwardPrefixes;

        var trimmed = subject.TrimStart();

        // Steht schon eines der bekannten Kürzel davor, bleibt der Betreff, wie
        // er ist — gleich in welcher Sprache es gesetzt wurde.
        foreach (var candidate in known)
        {
            if (trimmed.StartsWith(candidate, StringComparison.OrdinalIgnoreCase))
                return subject;
        }

        return prefix + subject;
    }

    /// <summary>
    /// „Name &lt;a@b&gt;, x@y" wird zu „a@b, x@y".
    ///
    /// Im Empfängerfeld gehört die Adresse, nicht der Anzeigename: nur sie sagt,
    /// wohin die Antwort tatsächlich geht. Der Name ist frei wählbar und im
    /// Zweifel gerade das, was einen täuschen soll.
    ///
    /// Doppelte Adressen fallen weg — bei „Allen antworten" steht derselbe
    /// Empfänger sonst mehrfach im Feld, einmal aus An und einmal aus Kopie.
    ///
    /// Lässt sich der Kopfzeileninhalt nicht deuten, bleibt er unverändert
    /// stehen: lieber etwas Unschönes zum Nachbessern als ein leeres Feld.
    /// </summary>
    private static string ExtractAddresses(
        string headerValue, IEnumerable<string>? exclude = null)
    {
        if (string.IsNullOrWhiteSpace(headerValue)) return "";

        var unwanted = new HashSet<string>(exclude ?? [], StringComparer.OrdinalIgnoreCase);

        var addresses = Addresses(headerValue)
            .Where(a => !unwanted.Contains(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (addresses.Count > 0) return string.Join(", ", addresses);

        // Nichts übrig: entweder war alles auszuschliessen — dann ist ein leeres
        // Feld richtig — oder die Kopfzeile war nicht zu deuten. Im zweiten Fall
        // bleibt sie zum Nachbessern stehen, statt kommentarlos zu verschwinden.
        return unwanted.Count > 0 ? "" : headerValue;
    }

    /// <summary>Die reinen Adressen einer Kopfzeile; Gruppen werden aufgelöst.</summary>
    private static List<string> Addresses(string headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue)) return [];
        if (!InternetAddressList.TryParse(headerValue, out var parsed)) return [];

        return [.. parsed.Mailboxes
            .Select(m => m.Address)
            .Where(a => !string.IsNullOrWhiteSpace(a))];
    }

    private static string Quote(MailBody src)
    {
        var text = string.IsNullOrWhiteSpace(src.Text) ? StripTags(src.Html ?? "") : src.Text;
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"Am {src.Date.LocalDateTime:dd.MM.yyyy HH:mm} schrieb {src.From}:");
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            sb.AppendLine("> " + line);
        return sb.ToString();
    }

    private static string StripTags(string html)
        => System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", "");
}
