using NMailClient.Poc.Models;

namespace NMailClient.Poc.Services;

/// <summary>Postfach und Ordner, aus denen eine Nachricht stammt.</summary>
public readonly record struct MessageOrigin(string AccountId, string Folder);

/// <summary>
/// Zuordnung von Nachrichten zu ihrer Herkunft.
///
/// Solange eine Liste genau einen Ordner eines Kontos zeigte, war das keine
/// Frage: Auswahl gleich Ziel. Im gemeinsamen Posteingang stehen Postfächer
/// nebeneinander, und dieselbe Annahme führte dazu, dass eine Aktion das
/// falsche Konto träfe.
///
/// Bewusst ohne Bezug zum Anzeigemodell, damit sich genau diese Zuordnung
/// prüfen lässt — sie ist die Stelle, an der ein Fehler Nachrichten im
/// falschen Postfach löschte.
/// </summary>
public static class MessageGrouping
{
    /// <summary>
    /// Die Herkunft einer Zeile. Fehlt der Vermerk — etwa bei Zeilen aus einem
    /// älteren Zwischenspeicher —, gilt die Ansicht.
    /// </summary>
    public static MessageOrigin OriginOf(
        MailSummary message, string fallbackAccountId, string fallbackFolder)
        => new(
            string.IsNullOrEmpty(message.AccountId) ? fallbackAccountId : message.AccountId,
            string.IsNullOrEmpty(message.Folder) ? fallbackFolder : message.Folder);

    /// <summary>
    /// Eine Auswahl nach Postfach und Ordner aufteilen. Jede Gruppe wird für
    /// sich ausgeführt; eine Gruppe entspricht genau einem IMAP-Befehl.
    /// </summary>
    public static List<(MessageOrigin Origin, List<MailSummary> Messages)> ByOrigin(
        IEnumerable<MailSummary> messages, string fallbackAccountId, string fallbackFolder)
        => [.. messages
            .GroupBy(m => OriginOf(m, fallbackAccountId, fallbackFolder))
            .Select(g => (g.Key, Messages: g.ToList()))];

    /// <summary>
    /// Aus mehreren Postfächern eine Liste: neueste zuerst, auf eine Seite
    /// gekürzt. Ohne die Kürzung stünden bei drei Konten dreimal so viele
    /// Zeilen da wie in jedem einzelnen Ordner.
    /// </summary>
    public static List<MailSummary> MergeNewest(IEnumerable<MailSummary> messages, int limit)
        => [.. messages.OrderByDescending(m => m.Date).Take(limit)];
}
