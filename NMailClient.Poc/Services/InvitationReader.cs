using System.IO;
using MimeKit;
using NMailClient.Poc.Models;
using NMailClient.Poc.Services.Dav;

namespace NMailClient.Poc.Services;

/// <summary>
/// Erkennt Termin-Einladungen in Mails (iMIP, RFC 6047).
///
/// Zwei Formen kommen vor: <c>text/calendar</c> als eigener Teil mit
/// <c>method=REQUEST</c>, oder schlicht eine angehängte <c>.ics</c>-Datei.
/// Beide werden gleich behandelt – für den Nutzer ist es dasselbe.
/// </summary>
public static class InvitationReader
{
    /// <summary>Findet die erste Einladung; null, wenn die Mail keine enthält.</summary>
    public static CalendarItem? Find(MimeMessage message)
    {
        foreach (var part in message.BodyParts.OfType<MimePart>())
        {
            if (!IsCalendar(part)) continue;

            var text = ReadText(part);
            if (string.IsNullOrWhiteSpace(text)) continue;

            // Zeitraum weit fassen: der Termin kann Jahre entfernt liegen.
            var items = CalDavService.ParseEvents(text, DateTime.MinValue, DateTime.MaxValue);
            if (items.Count > 0) return items[0];
        }
        return null;
    }

    private static bool IsCalendar(MimePart part)
    {
        if (part.ContentType.IsMimeType("text", "calendar")) return true;

        // Manche Absender schicken .ics als application/octet-stream.
        var name = part.FileName;
        return !string.IsNullOrEmpty(name)
               && name.EndsWith(".ics", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadText(MimePart part)
    {
        try
        {
            if (part is TextPart text) return text.Text;

            using var stream = new MemoryStream();
            part.Content?.DecodeTo(stream);
            stream.Position = 0;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or FormatException)
        {
            AppLog.Warn($"Einladung nicht lesbar: {ex.Message}");
            return null;
        }
    }
}
