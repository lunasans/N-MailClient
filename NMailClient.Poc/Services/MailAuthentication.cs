using System.Text.RegularExpressions;
using MimeKit;

namespace NMailClient.Poc.Services;

public enum AuthResult { Unknown, Pass, Fail, Neutral }

/// <summary>
/// Auswertung der Kopfzeile <c>Authentication-Results</c> (RFC 8601) plus eine
/// Prüfung auf vorgetäuschte Absender.
/// </summary>
public record MailAuthInfo(
    AuthResult Spf, AuthResult Dkim, AuthResult Dmarc,
    bool DisplayNameSpoof, string? SpoofDetail)
{
    public static readonly MailAuthInfo None =
        new(AuthResult.Unknown, AuthResult.Unknown, AuthResult.Unknown, false, null);

    /// <summary>Mindestens eine Prüfung ist fehlgeschlagen.</summary>
    public bool HasFailure => Spf == AuthResult.Fail
                              || Dkim == AuthResult.Fail
                              || Dmarc == AuthResult.Fail;

    /// <summary>Gibt es überhaupt etwas anzuzeigen?</summary>
    public bool HasAnyResult => Spf != AuthResult.Unknown
                                || Dkim != AuthResult.Unknown
                                || Dmarc != AuthResult.Unknown;

    public bool IsWarning => HasFailure || DisplayNameSpoof;

    /// <summary>
    /// Die Einzelergebnisse als Marken für die Anzeige. Unbekanntes wird
    /// weggelassen – „SPF unbekannt" sagt dem Leser nichts.
    /// </summary>
    public IReadOnlyList<AuthBadge> Badges
    {
        get
        {
            var list = new List<AuthBadge>();
            if (Spf != AuthResult.Unknown) list.Add(new AuthBadge("SPF", Spf));
            if (Dkim != AuthResult.Unknown) list.Add(new AuthBadge("DKIM", Dkim));
            if (Dmarc != AuthResult.Unknown) list.Add(new AuthBadge("DMARC", Dmarc));
            return list;
        }
    }

    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (Spf != AuthResult.Unknown) parts.Add($"SPF {Label(Spf)}");
            if (Dkim != AuthResult.Unknown) parts.Add($"DKIM {Label(Dkim)}");
            if (Dmarc != AuthResult.Unknown) parts.Add($"DMARC {Label(Dmarc)}");

            var text = parts.Count > 0 ? string.Join(" · ", parts) : "Keine Prüfergebnisse";
            return SpoofDetail is null ? text : $"{text} — {SpoofDetail}";
        }
    }

    private static string Label(AuthResult r) => r switch
    {
        AuthResult.Pass => "bestanden",
        AuthResult.Fail => "fehlgeschlagen",
        AuthResult.Neutral => "neutral",
        _ => "unbekannt",
    };
}

/// <summary>Eine farbige Marke je Prüfverfahren.</summary>
public record AuthBadge(string Label, AuthResult State)
{
    public bool IsPass => State == AuthResult.Pass;
    public bool IsFail => State == AuthResult.Fail;

    public string Tooltip => State switch
    {
        AuthResult.Pass => $"{Label}: Prüfung bestanden",
        AuthResult.Fail => $"{Label}: Prüfung fehlgeschlagen",
        AuthResult.Neutral => $"{Label}: kein eindeutiges Ergebnis",
        _ => $"{Label}: unbekannt",
    };
}

public static class MailAuthentication
{
    /// <summary>
    /// Wertet alle <c>Authentication-Results</c>-Zeilen aus. Mehrere sind erlaubt
    /// (jeder Server hängt eine an); ein Fehlschlag irgendwo zählt als Fehlschlag.
    /// </summary>
    public static MailAuthInfo Analyze(MimeMessage message)
    {
        var spf = AuthResult.Unknown;
        var dkim = AuthResult.Unknown;
        var dmarc = AuthResult.Unknown;

        foreach (var header in message.Headers)
        {
            if (!header.Field.Equals("Authentication-Results", StringComparison.OrdinalIgnoreCase))
                continue;

            spf = Merge(spf, Read(header.Value, "spf"));
            dkim = Merge(dkim, Read(header.Value, "dkim"));
            dmarc = Merge(dmarc, Read(header.Value, "dmarc"));
        }

        var (spoof, detail) = CheckDisplayName(message);
        return new MailAuthInfo(spf, dkim, dmarc, spoof, detail);
    }

    /// <summary>Fehlschlag hat Vorrang, danach Bestanden – Unbekannt verliert immer.</summary>
    private static AuthResult Merge(AuthResult current, AuthResult next)
    {
        if (current == AuthResult.Fail || next == AuthResult.Fail) return AuthResult.Fail;
        if (current == AuthResult.Pass || next == AuthResult.Pass) return AuthResult.Pass;
        if (current == AuthResult.Neutral || next == AuthResult.Neutral) return AuthResult.Neutral;
        return AuthResult.Unknown;
    }

    public static AuthResult Read(string headerValue, string method)
    {
        // Beispiel: "mx.example.org; spf=pass smtp.mailfrom=a@b.c; dkim=fail"
        var match = Regex.Match(headerValue,
            $@"\b{Regex.Escape(method)}\s*=\s*(?<value>[a-z]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (!match.Success) return AuthResult.Unknown;

        return match.Groups["value"].Value.ToLowerInvariant() switch
        {
            "pass" => AuthResult.Pass,
            "fail" or "softfail" or "permerror" or "reject" => AuthResult.Fail,
            "neutral" or "none" or "temperror" or "policy" => AuthResult.Neutral,
            _ => AuthResult.Unknown,
        };
    }

    /// <summary>
    /// Erkennt Anzeigenamen, die eine fremde Adresse vortäuschen – der häufigste
    /// Trick bei Phishing: „Sparkasse &lt;betrug@example.ru&gt;“.
    /// </summary>
    public static (bool Spoof, string? Detail) CheckDisplayName(MimeMessage message)
    {
        var from = message.From.Mailboxes.FirstOrDefault();
        if (from is null || string.IsNullOrWhiteSpace(from.Name)) return (false, null);

        // Enthält der Anzeigename selbst eine Adresse, muss sie zur echten passen.
        var embedded = Regex.Match(from.Name,
            @"[\w.+-]+@(?<domain>[\w.-]+\.[a-z]{2,})", RegexOptions.IgnoreCase);

        if (!embedded.Success) return (false, null);

        var claimed = embedded.Groups["domain"].Value;
        var actual = DomainOf(from.Address);

        if (claimed.Equals(actual, StringComparison.OrdinalIgnoreCase)) return (false, null);

        return (true, $"Der Anzeigename nennt '{claimed}', gesendet wurde von '{actual}'");
    }

    private static string DomainOf(string address)
    {
        var at = address.LastIndexOf('@');
        return at >= 0 && at < address.Length - 1 ? address[(at + 1)..] : address;
    }
}
