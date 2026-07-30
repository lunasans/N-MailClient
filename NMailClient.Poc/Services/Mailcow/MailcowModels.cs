using System.Text.Json;
using System.Text.Json.Serialization;

namespace NMailClient.Poc.Services.Mailcow;

/// <summary>
/// Das Ergebnis einer ändernden Anfrage.
///
/// mailcow antwortet darauf mit einem <em>Feld</em> von Meldungen, und
/// <c>msg</c> ist mal eine Zeichenkette, mal ein Feld aus Zeichenkette und
/// Platzhaltern. Beides muss hier ankommen, sonst steht in der Oberfläche
/// nichts Brauchbares.
/// </summary>
public sealed record MailcowResult(bool Success, string Message)
{
    public static MailcowResult Failed(string message) => new(false, message);

    /// <summary>
    /// Antwort auswerten. Enthält sie mehrere Meldungen, entscheidet die
    /// schlechteste – bei einer Teilablehnung von Erfolg zu sprechen wäre falsch.
    /// </summary>
    public static MailcowResult Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Failed("leere Antwort");

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            // Einzelobjekt oder Feld – mailcow liefert beides.
            var entries = root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray().ToList()
                : [root];

            if (entries.Count == 0) return Failed("keine Meldung erhalten");

            var messages = new List<string>();
            var success = true;

            foreach (var entry in entries)
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;

                var type = entry.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (!string.Equals(type, "success", StringComparison.OrdinalIgnoreCase)) success = false;

                if (entry.TryGetProperty("msg", out var msg)) messages.Add(Flatten(msg));
            }

            var text = messages.Count > 0
                ? string.Join(" · ", messages.Where(m => m.Length > 0))
                : success ? "Erledigt." : "Abgelehnt.";

            return new MailcowResult(success, text);
        }
        catch (JsonException ex)
        {
            return Failed($"Antwort nicht lesbar: {ex.Message}");
        }
    }

    /// <summary>Aus „msg" eine Zeile machen, egal ob Text oder verschachteltes Feld.</summary>
    private static string Flatten(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? "",
        JsonValueKind.Number => element.ToString(),
        JsonValueKind.Array => string.Join(" ",
            element.EnumerateArray().Select(Flatten).Where(s => s.Length > 0)),
        _ => "",
    };
}

/// <summary>Postfach mit Speicherbelegung.</summary>
public sealed record MailcowMailbox(
    string Address, string Name, bool Active, long QuotaBytes, long UsedBytes, int MessageCount)
{
    /// <summary>Belegung in Prozent; 0 bei unbegrenztem Postfach.</summary>
    public double UsedPercent => QuotaBytes <= 0 ? 0 : Math.Min(100, UsedBytes * 100.0 / QuotaBytes);

    /// <summary>Ab 90 Prozent wird es eng – dann soll es auffallen.</summary>
    public bool IsNearlyFull => QuotaBytes > 0 && UsedPercent >= 90;

    public string QuotaDisplay => QuotaBytes <= 0
        ? $"{Size(UsedBytes)} belegt (unbegrenzt)"
        : $"{Size(UsedBytes)} von {Size(QuotaBytes)} ({UsedPercent:0.#} %)";

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024L * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
    };

    public static MailcowMailbox? Parse(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        var address = Text(element, "username");
        if (address.Length == 0) return null;

        return new MailcowMailbox(
            address,
            Text(element, "name"),
            Flag(element, "active"),
            Number(element, "quota"),
            Number(element, "quota_used"),
            (int)Number(element, "messages"));
    }

    // ---- Feldzugriff -------------------------------------------------------

    internal static string Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";

    /// <summary>
    /// mailcow liefert Zahlen mal als Zahl, mal als Zeichenkette. Beides muss
    /// gelesen werden, sonst steht die Belegung dauerhaft auf null.
    /// </summary>
    internal static long Number(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return 0;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt64(out var n) ? n : 0,
            JsonValueKind.String => long.TryParse(value.GetString(), out var s) ? s : 0,
            _ => 0,
        };
    }

    /// <summary>Wahrheitswerte kommen als 1/0, "1"/"0" oder true/false.</summary>
    internal static bool Flag(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return false;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.TryGetInt32(out var n) && n != 0,
            JsonValueKind.String => value.GetString() is "1" or "true" or "True",
            _ => false,
        };
    }
}

/// <summary>Ein Alias, der auf ein oder mehrere Ziele zeigt.</summary>
public sealed record MailcowAlias(int Id, string Address, string GoTo, bool Active)
{
    public IReadOnlyList<string> Targets =>
        GoTo.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public string Display => Active
        ? $"{Address}  →  {string.Join(", ", Targets)}"
        : $"{Address}  →  {string.Join(", ", Targets)}  (aus)";

    public static MailcowAlias? Parse(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        var address = MailcowMailbox.Text(element, "address");
        if (address.Length == 0) return null;

        return new MailcowAlias(
            (int)MailcowMailbox.Number(element, "id"),
            address,
            MailcowMailbox.Text(element, "goto"),
            MailcowMailbox.Flag(element, "active"));
    }
}

/// <summary>Ein App-Passwort für ein einzelnes Gerät.</summary>
public sealed record MailcowAppPassword(int Id, string Name, bool Active, string Protocols)
{
    public string Display => Active ? Name : $"{Name}  (aus)";

    public static MailcowAppPassword? Parse(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        var name = MailcowMailbox.Text(element, "name");
        if (name.Length == 0) return null;

        var protocols = new List<string>();
        foreach (var (field, label) in new[]
                 {
                     ("imap_access", "IMAP"), ("smtp_access", "SMTP"),
                     ("dav_access", "DAV"), ("eas_access", "EAS"), ("pop3_access", "POP3"),
                 })
        {
            if (MailcowMailbox.Flag(element, field)) protocols.Add(label);
        }

        return new MailcowAppPassword(
            (int)MailcowMailbox.Number(element, "id"),
            name,
            MailcowMailbox.Flag(element, "active"),
            protocols.Count > 0 ? string.Join(", ", protocols) : "—");
    }
}

/// <summary>Eine Nachricht in der Quarantäne.</summary>
public sealed record MailcowQuarantineItem(
    int Id, string Sender, string Recipient, string Subject, DateTimeOffset Received, double Score)
{
    public string ScoreDisplay => Score.ToString("0.0");

    public string Display => $"{Received.LocalDateTime:dd.MM. HH:mm}  {Sender}  {Subject}";

    public static MailcowQuarantineItem? Parse(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        var id = (int)MailcowMailbox.Number(element, "id");
        if (id == 0) return null;

        var seconds = MailcowMailbox.Number(element, "created");

        return new MailcowQuarantineItem(
            id,
            MailcowMailbox.Text(element, "sender"),
            MailcowMailbox.Text(element, "rcpt"),
            Subject: MailcowMailbox.Text(element, "subject") is { Length: > 0 } s
                ? s : "(kein Betreff)",
            Received: seconds > 0
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : DateTimeOffset.MinValue,
            Score: ReadScore(element));
    }

    private static double ReadScore(JsonElement element)
    {
        if (!element.TryGetProperty("score", out var value)) return 0;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String => double.TryParse(value.GetString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0,
            _ => 0,
        };
    }
}

/// <summary>Was mit einer Nachricht aus der Quarantäne geschehen soll.</summary>
public enum QuarantineAction
{
    /// <summary>Zustellen und als erwünscht lernen.</summary>
    Release,

    /// <summary>Löschen.</summary>
    Delete,

    /// <summary>Als Spam lernen und löschen.</summary>
    LearnSpam,
}
