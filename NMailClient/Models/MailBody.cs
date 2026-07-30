namespace NMailClient.Models;

public class MailBody
{
    public uint Uid { get; init; }

    /// <summary>Message-ID der Nachricht – für In-Reply-To/References beim Antworten.</summary>
    public string? MessageId { get; init; }

    /// <summary>References-Header der Nachricht (Thread-Kette).</summary>
    public IReadOnlyList<string> References { get; init; } = [];

    /// <summary>Termin-Einladung (iMIP/.ics), sofern die Mail eine enthält.</summary>
    public CalendarItem? Invitation { get; init; }

    public bool HasInvitation => Invitation is not null;

    /// <summary>Was OpenPGP an der Nachricht ergeben hat (ver-/entschlüsselt, Signatur).</summary>
    public Services.Pgp.PgpInfo Pgp { get; init; } = Services.Pgp.PgpInfo.None;

    /// <summary>Ergebnis von SPF/DKIM/DMARC und die Spoofing-Prüfung.</summary>
    public Services.MailAuthInfo Authentication { get; init; } = Services.MailAuthInfo.None;

    /// <summary>Absenderadresse – für Vertrauensliste und Anzeige.</summary>
    public string FromAddress { get; init; } = "";
    public string Subject { get; init; } = "";
    public string From { get; init; } = "";
    public string To { get; init; } = "";

    /// <summary>
    /// Reply-To der Nachricht, falls gesetzt. Der Absender bestimmt damit, wohin
    /// eine Antwort gehen soll — bei Verteilern ist das die Liste und nicht die
    /// Person, die zuletzt geschrieben hat. Wer das übergeht, antwortet einem
    /// Einzelnen, obwohl die Runde gemeint war, oder umgekehrt.
    /// </summary>
    public string ReplyTo { get; init; } = "";
    public DateTimeOffset Date { get; init; }
    public string? Html { get; init; }
    public string Text { get; init; } = "";
    public List<AttachmentInfo> Attachments { get; init; } = new();
}

public class AttachmentInfo
{
    public string FileName { get; init; } = "";
    public string MimeType { get; init; } = "";
    public long Size { get; init; }
    /// <summary>Index innerhalb der MIME-Struktur – zum späteren Nachladen.</summary>
    public int Index { get; init; }

    public string SizeDisplay => Size switch
    {
        < 1024 => $"{Size} B",
        < 1024 * 1024 => $"{Size / 1024.0:0.#} KB",
        _ => $"{Size / 1024.0 / 1024.0:0.#} MB",
    };

    public string Display => $"{FileName} ({SizeDisplay})";

    /// <summary>Kann der eingebaute Betrachter das anzeigen?</summary>
    public bool IsPdf =>
        MimeType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase)
        || FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
}
