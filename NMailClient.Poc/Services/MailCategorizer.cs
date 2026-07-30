using NMailClient.Poc.Models;

namespace NMailClient.Poc.Services;

/// <summary>
/// Ordnet Nachrichten in Kategorien ein – anhand von Headern, nicht anhand des
/// Inhalts. Absichtlich konservativ: im Zweifel „Allgemein", damit persönliche
/// Post nie in einem Nebenreiter verschwindet.
/// </summary>
public static class MailCategorizer
{
    /// <summary>Domänen, deren Post typischerweise sozialen Netzwerken entstammt.</summary>
    private static readonly string[] SocialDomains =
    [
        "facebook.com", "facebookmail.com", "instagram.com", "twitter.com", "x.com",
        "linkedin.com", "xing.com", "mastodon", "discord.com", "reddit.com",
    ];

    /// <summary>
    /// Einordnung. <paramref name="overrides"/> hat Vorrang – eine vom Nutzer
    /// gesetzte Kategorie je Absender schlägt jede Heuristik.
    /// </summary>
    public static MailCategory Categorize(
        MailSummary message, IReadOnlyDictionary<string, string>? overrides = null)
    {
        var sender = message.FromAddress ?? "";

        if (overrides is not null && sender.Length > 0
            && overrides.TryGetValue(sender, out var forced)
            && Enum.TryParse<MailCategory>(forced, out var parsed))
            return parsed;

        var domain = DomainOf(sender);

        if (domain.Length > 0
            && SocialDomains.Any(d => domain.Equals(d, StringComparison.OrdinalIgnoreCase)
                                      || domain.EndsWith("." + d, StringComparison.OrdinalIgnoreCase)
                                      || domain.Contains(d, StringComparison.OrdinalIgnoreCase)))
            return MailCategory.Social;

        // Werbung vor Newsletter prüfen: Massensendungen tragen oft beides,
        // und „bulk" ist die deutlichere Aussage.
        if (message.Precedence is { Length: > 0 } precedence
            && (precedence.Contains("bulk", StringComparison.OrdinalIgnoreCase)
                || precedence.Contains("junk", StringComparison.OrdinalIgnoreCase)))
            return MailCategory.Promotions;

        if (!string.IsNullOrWhiteSpace(message.ListUnsubscribe))
            return MailCategory.Newsletter;

        // Automatisch erzeugte Post ohne Abmeldelink: Benachrichtigungen,
        // Rechnungen, Systemmeldungen – keine persönliche Korrespondenz.
        if (!string.IsNullOrWhiteSpace(message.AutoSubmitted)
            && !message.AutoSubmitted!.Equals("no", StringComparison.OrdinalIgnoreCase))
            return MailCategory.Newsletter;

        var local = LocalPartOf(sender);
        if (local.Length > 0 && IsNoReply(local)) return MailCategory.Newsletter;

        return MailCategory.General;
    }

    private static bool IsNoReply(string localPart)
    {
        var normalized = localPart.Replace("-", "").Replace("_", "").Replace(".", "");
        return normalized.StartsWith("noreply", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith("donotreply", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith("nichtantworten", StringComparison.OrdinalIgnoreCase);
    }

    private static string DomainOf(string address)
    {
        var at = address.LastIndexOf('@');
        return at >= 0 && at < address.Length - 1 ? address[(at + 1)..] : "";
    }

    private static string LocalPartOf(string address)
    {
        var at = address.IndexOf('@');
        return at > 0 ? address[..at] : address;
    }

    public static string DisplayName(MailCategory category) => category switch
    {
        MailCategory.General => "Allgemein",
        MailCategory.Newsletter => "Newsletter",
        MailCategory.Promotions => "Werbung",
        MailCategory.Social => "Soziales",
        _ => category.ToString(),
    };
}
