using System.Text.Json.Serialization;

namespace NMailClient.Poc.Models;

public enum ReminderKind
{
    /// <summary>Nachricht bis zum Zeitpunkt ausblenden.</summary>
    Snooze,

    /// <summary>Nachricht bleibt sichtbar, meldet sich aber zum Zeitpunkt.</summary>
    FollowUp,
}

/// <summary>
/// Eine lokale Erinnerung zu einer Nachricht.
///
/// Bewusst nur lokal: IMAP kennt kein Snooze. Ein anderer Client sieht die
/// Nachricht weiterhin normal – das ist der Preis dafür, ohne Serverunterstützung
/// auszukommen und ohne die Mail zu verschieben.
/// </summary>
public class Reminder
{
    [JsonPropertyName("accountId")] public string AccountId { get; set; } = "";
    [JsonPropertyName("folder")] public string Folder { get; set; } = "";
    [JsonPropertyName("uid")] public uint Uid { get; set; }

    [JsonPropertyName("kind")] public ReminderKind Kind { get; set; }
    [JsonPropertyName("dueAt")] public DateTimeOffset DueAt { get; set; }

    /// <summary>Nur zur Anzeige in der Meldung.</summary>
    [JsonPropertyName("subject")] public string Subject { get; set; } = "";

    [JsonIgnore] public bool IsDue => DateTimeOffset.Now >= DueAt;

    /// <summary>Blendet die Nachricht gerade aus?</summary>
    [JsonIgnore] public bool HidesMessage => Kind == ReminderKind.Snooze && !IsDue;

    /// <summary>Identität einer Nachricht – UIDs sind nur je Ordner eindeutig.</summary>
    public bool Matches(string accountId, string folder, uint uid)
        => Uid == uid
           && string.Equals(AccountId, accountId, StringComparison.Ordinal)
           && string.Equals(Folder, folder, StringComparison.OrdinalIgnoreCase);
}
