using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NMailClient.Models;

/// <summary>Eine Zeile in der Mailliste (entspricht dem Summary-Typ der Go-Version).</summary>
public class MailSummary : INotifyPropertyChanged
{
    public uint Uid { get; init; }

    /// <summary>
    /// Konto und Ordner, aus denen die Nachricht stammt.
    ///
    /// Solange eine Liste immer genau einen Ordner eines Kontos zeigte, liess
    /// sich beides aus der Auswahl ableiten. Im gemeinsamen Posteingang stehen
    /// Nachrichten mehrerer Konten nebeneinander — dann muss jede Zeile ihre
    /// Herkunft selbst kennen, sonst landete etwa ein Löschen im falschen
    /// Postfach.
    /// </summary>
    public string AccountId { get; set; } = "";
    public string Folder { get; set; } = "";

    private string _accountColor = "";
    /// <summary>
    /// Kennfarbe des Kontos – im gemeinsamen Posteingang das einzige Merkmal,
    /// an dem sich die Herkunft einer Zeile ablesen lässt.
    /// </summary>
    public string AccountColor
    {
        get => _accountColor;
        set { if (_accountColor == value) return; _accountColor = value; Raise(nameof(AccountColor)); }
    }

    private string _accountEmail = "";
    /// <summary>Für den Tooltip: welches Postfach, ausgeschrieben.</summary>
    public string AccountEmail
    {
        get => _accountEmail;
        set { if (_accountEmail == value) return; _accountEmail = value; Raise(nameof(AccountEmail)); }
    }

    public string Subject { get; init; } = "(kein Betreff)";
    public string From { get; init; } = "";
    public string FromAddress { get; init; } = "";
    public DateTimeOffset Date { get; init; }
    public bool HasAttachments { get; init; }

    /// <summary>Message-ID dieser Nachricht (für die Konversationsbildung).</summary>
    public string? MessageId { get; init; }

    /// <summary>In-Reply-To + References, zusammengeführt.</summary>
    public IReadOnlyList<string> ThreadReferences { get; init; } = [];

    // ---- Header für die Kategorisierung ------------------------------------

    public string? ListUnsubscribe { get; init; }
    public string? Precedence { get; init; }
    public string? AutoSubmitted { get; init; }

    private MailCategory _category;
    public MailCategory Category
    {
        get => _category;
        set { if (_category == value) return; _category = value; Raise(nameof(Category)); }
    }

    // ---- Nur in der Thread-Ansicht belegt ----------------------------------

    private int _threadCount = 1;
    /// <summary>Anzahl Nachrichten der Konversation; 1 = kein Thread.</summary>
    public int ThreadCount { get => _threadCount; set { _threadCount = value; Raise(nameof(ThreadCount)); Raise(nameof(IsThread)); } }

    public bool IsThread => ThreadCount > 1;

    private DateTimeOffset? _followUpAt;
    /// <summary>Gesetzt, wenn zu dieser Nachricht eine Wiedervorlage aussteht.</summary>
    public DateTimeOffset? FollowUpAt
    {
        get => _followUpAt;
        set
        {
            if (_followUpAt == value) return;
            _followUpAt = value;
            Raise(nameof(FollowUpAt));
            Raise(nameof(HasFollowUp));
            Raise(nameof(FollowUpDisplay));
        }
    }

    public bool HasFollowUp => FollowUpAt is not null;

    public string FollowUpDisplay =>
        FollowUpAt is { } d ? $"Wiedervorlage {d.LocalDateTime:dd.MM. HH:mm}" : "";

    private bool _seen;
    public bool Seen { get => _seen; set => Set(ref _seen, value); }

    private bool _flagged;
    public bool Flagged { get => _flagged; set => Set(ref _flagged, value); }

    private bool _answered;
    /// <summary>
    /// Auf diese Nachricht wurde geantwortet (\Answered). Wird vom Server
    /// gelesen und nach dem eigenen Versand sofort gesetzt, damit man nicht
    /// zweimal antwortet.
    /// </summary>
    public bool Answered { get => _answered; set => Set(ref _answered, value); }

    /// <summary>IMAP-Keywords der Nachricht (rohe Serverwerte).</summary>
    public HashSet<string> Keywords { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    private List<LabelDef> _labels = [];
    /// <summary>Aufgelöste Etiketten (Keywords, die einer Definition entsprechen).</summary>
    public List<LabelDef> Labels
    {
        get => _labels;
        set { _labels = value; Raise(nameof(Labels)); }
    }

    public void SetKeyword(string keyword, bool on)
    {
        if (on) Keywords.Add(keyword);
        else Keywords.Remove(keyword);
    }

    public string DateDisplay => Date.LocalDateTime.Date == DateTime.Today
        ? Date.LocalDateTime.ToString("HH:mm")
        : Date.LocalDateTime.ToString("dd.MM.yyyy");

    /// <summary>
    /// Abschnitt für die Gruppierung der Liste. Die Woche beginnt Montag – nicht
    /// über <c>DayOfWeek</c> gerechnet, weil dort Sonntag 0 ist.
    /// </summary>
    public string DateGroup
    {
        get
        {
            var day = Date.LocalDateTime.Date;
            var today = DateTime.Today;
            if (day == today) return "Heute";
            if (day == today.AddDays(-1)) return "Gestern";

            int sinceMonday = ((int)today.DayOfWeek + 6) % 7;
            var weekStart = today.AddDays(-sinceMonday);
            if (day >= weekStart) return "Diese Woche";
            if (day >= weekStart.AddDays(-7)) return "Letzte Woche";
            if (day >= new DateTime(today.Year, today.Month, 1)) return "Diesen Monat";

            return day.Year == today.Year
                ? day.ToString("MMMM")
                : day.ToString("MMMM yyyy");
        }
    }

    /// <summary>Initialen für den farbigen Avatar-Kreis.</summary>
    public string Initials
    {
        get
        {
            var src = string.IsNullOrWhiteSpace(From) ? FromAddress : From;

            // Domain abschneiden: sonst liefert "rene.neuhaus@example.org" die
            // Initialen "RO" (aus "rene" und "org") statt "RN".
            var at = src.IndexOf('@');
            if (at > 0) src = src[..at];

            var parts = src.Split(new[] { ' ', '.', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "?";
            if (parts.Length == 1) return parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant();
            return $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant();
        }
    }

    /// <summary>Stabile Farbe aus der Absenderadresse – wie die Avatare der Go-Version.</summary>
    public string AvatarColor
    {
        get
        {
            var palette = new[] { "#4a7dbd", "#b5651d", "#3f7d58", "#8e5ea2", "#c04f4f", "#3d7f8c", "#7a6a3a" };
            int h = 0;
            foreach (var c in FromAddress) h = (h * 31 + c) & 0x7fffffff;
            return palette[h % palette.Length];
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set(ref bool field, bool value, [CallerMemberName] string? name = null)
    {
        if (field == value) return;
        field = value;
        Raise(name);
    }

    private void Raise(string? name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
