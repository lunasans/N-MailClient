using System.Text.Json.Serialization;

namespace NMailClient.Models;

/// <summary>
/// Eine wartende Sendung. Die Nachricht selbst liegt als .eml-Datei daneben –
/// nur die Verwaltungsdaten stehen in der JSON-Datei.
/// </summary>
public class OutboxItem
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("accountId")] public string AccountId { get; set; } = "";

    /// <summary>Zeitpunkt, ab dem gesendet werden darf.</summary>
    [JsonPropertyName("sendAt")] public DateTimeOffset SendAt { get; set; }

    /// <summary>Nur zur Anzeige in der Warteschlange.</summary>
    [JsonPropertyName("subject")] public string Subject { get; set; } = "";
    [JsonPropertyName("to")] public string To { get; set; } = "";

    /// <summary>Entwurf, der nach erfolgreichem Versand entfernt wird.</summary>
    [JsonPropertyName("draftUid")] public uint? DraftUid { get; set; }

    /// <summary>Original einer Antwort – bekommt danach \Answered.</summary>
    [JsonPropertyName("replyFolder")] public string? ReplyFolder { get; set; }
    [JsonPropertyName("replyUid")] public uint? ReplyUid { get; set; }

    /// <summary>Fehlgeschlagene Versuche; nach mehreren wird nicht mehr probiert.</summary>
    [JsonPropertyName("attempts")] public int Attempts { get; set; }

    [JsonPropertyName("lastError")] public string? LastError { get; set; }

    /// <summary>Wartet auf die Rückgängig-Frist (kein geplanter Versand)?</summary>
    [JsonIgnore] public bool IsPending => DateTimeOffset.Now < SendAt;

    [JsonIgnore] public int SecondsLeft => Math.Max(0, (int)Math.Ceiling((SendAt - DateTimeOffset.Now).TotalSeconds));

    [JsonIgnore]
    public string Display => string.IsNullOrWhiteSpace(Subject) ? "(kein Betreff)" : Subject;
}
