using MimeKit;
using NMailClient.Models;
using NMailClient.Services;
using Xunit;

namespace NMailClient.Tests;

public class AuthResultParsingTests
{
    [Theory]
    [InlineData("mx.example.org; spf=pass smtp.mailfrom=a@b.c", AuthResult.Pass)]
    [InlineData("mx; spf=fail", AuthResult.Fail)]
    [InlineData("mx; spf=softfail", AuthResult.Fail)]
    [InlineData("mx; spf=permerror", AuthResult.Fail)]
    [InlineData("mx; spf=neutral", AuthResult.Neutral)]
    [InlineData("mx; spf=none", AuthResult.Neutral)]
    [InlineData("mx; dkim=pass", AuthResult.Unknown)]   // andere Methode
    [InlineData("", AuthResult.Unknown)]
    public void ReadsSpfResult(string header, AuthResult expected)
        => Assert.Equal(expected, MailAuthentication.Read(header, "spf"));

    [Fact]
    public void ReadsSeveralMethodsFromOneHeader()
    {
        const string header = "mx.example.org; spf=pass smtp.mailfrom=a@b.c; dkim=fail; dmarc=pass";

        Assert.Equal(AuthResult.Pass, MailAuthentication.Read(header, "spf"));
        Assert.Equal(AuthResult.Fail, MailAuthentication.Read(header, "dkim"));
        Assert.Equal(AuthResult.Pass, MailAuthentication.Read(header, "dmarc"));
    }

    [Fact]
    public void IsCaseInsensitive()
        => Assert.Equal(AuthResult.Pass, MailAuthentication.Read("MX; SPF=PASS", "spf"));

    [Fact]
    public void DoesNotMatchSubstringsOfOtherTokens()
    {
        // "nospf=pass" darf nicht als SPF-Ergebnis gelesen werden.
        Assert.Equal(AuthResult.Unknown, MailAuthentication.Read("mx; xspf=pass", "spf"));
    }
}

public class MailAuthAnalysisTests
{
    private static MimeMessage Message(string from, params string[] authHeaders)
    {
        var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse(from));
        msg.To.Add(MailboxAddress.Parse("empfaenger@example.org"));
        msg.Subject = "Test";

        foreach (var h in authHeaders) msg.Headers.Add("Authentication-Results", h);
        return msg;
    }

    [Fact]
    public void MessageWithoutHeadersHasNothingToShow()
    {
        var info = MailAuthentication.Analyze(Message("a@b.c"));

        Assert.False(info.HasAnyResult);
        Assert.False(info.IsWarning);
    }

    [Fact]
    public void AllPassIsNoWarning()
    {
        var info = MailAuthentication.Analyze(
            Message("a@b.c", "mx; spf=pass; dkim=pass; dmarc=pass"));

        Assert.True(info.HasAnyResult);
        Assert.False(info.HasFailure);
        Assert.False(info.IsWarning);
    }

    [Fact]
    public void OneFailureMakesItAWarning()
    {
        var info = MailAuthentication.Analyze(Message("a@b.c", "mx; spf=pass; dkim=fail"));

        Assert.True(info.HasFailure);
        Assert.True(info.IsWarning);
        Assert.Contains("DKIM fehlgeschlagen", info.Summary);
    }

    [Fact]
    public void FailureInAnySecondHeaderCounts()
    {
        // Mehrere Server hängen je eine Zeile an – ein Fehlschlag zählt.
        var info = MailAuthentication.Analyze(
            Message("a@b.c", "mx1; spf=pass", "mx2; spf=fail"));

        Assert.Equal(AuthResult.Fail, info.Spf);
    }

    [Fact]
    public void PassBeatsUnknownAcrossHeaders()
    {
        var info = MailAuthentication.Analyze(
            Message("a@b.c", "mx1; dkim=pass", "mx2; spf=pass"));

        Assert.Equal(AuthResult.Pass, info.Dkim);
        Assert.Equal(AuthResult.Pass, info.Spf);
    }
}

public class AuthBadgeTests
{
    private static MailAuthInfo Info(AuthResult spf, AuthResult dkim, AuthResult dmarc)
        => new(spf, dkim, dmarc, false, null);

    [Fact]
    public void OneBadgePerKnownResult()
    {
        var badges = Info(AuthResult.Pass, AuthResult.Fail, AuthResult.Neutral).Badges;

        Assert.Equal(["SPF", "DKIM", "DMARC"], badges.Select(b => b.Label).ToArray());
    }

    [Fact]
    public void UnknownResultsGetNoBadge()
    {
        // „SPF unbekannt" sagt dem Leser nichts – solche Marken bleiben weg.
        var badges = Info(AuthResult.Pass, AuthResult.Unknown, AuthResult.Unknown).Badges;

        var badge = Assert.Single(badges);
        Assert.Equal("SPF", badge.Label);
    }

    [Fact]
    public void NoResultsMeansNoBadges()
        => Assert.Empty(MailAuthInfo.None.Badges);

    [Theory]
    [InlineData(AuthResult.Pass, true, false)]
    [InlineData(AuthResult.Fail, false, true)]
    [InlineData(AuthResult.Neutral, false, false)]
    public void BadgeStateDrivesTheColouring(AuthResult state, bool pass, bool fail)
    {
        var badge = new AuthBadge("SPF", state);

        Assert.Equal(pass, badge.IsPass);
        Assert.Equal(fail, badge.IsFail);
    }

    [Fact]
    public void TooltipExplainsTheState()
    {
        Assert.Contains("bestanden", new AuthBadge("SPF", AuthResult.Pass).Tooltip);
        Assert.Contains("fehlgeschlagen", new AuthBadge("DKIM", AuthResult.Fail).Tooltip);
        Assert.Contains("DMARC", new AuthBadge("DMARC", AuthResult.Neutral).Tooltip);
    }
}

public class DisplayNameSpoofTests
{
    private static MimeMessage From(string raw)
    {
        var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse(raw));
        return msg;
    }

    [Fact]
    public void PlainDisplayNameIsFine()
        => Assert.False(MailAuthentication.CheckDisplayName(From("Rene Neuhaus <rene@example.org>")).Spoof);

    [Fact]
    public void NoDisplayNameIsFine()
        => Assert.False(MailAuthentication.CheckDisplayName(From("rene@example.org")).Spoof);

    [Fact]
    public void MatchingAddressInDisplayNameIsFine()
    {
        // Viele Clients setzen die Adresse selbst als Anzeigenamen ein.
        var (spoof, _) = MailAuthentication.CheckDisplayName(
            From("\"rene@example.org\" <rene@example.org>"));

        Assert.False(spoof);
    }

    [Fact]
    public void ForeignAddressInDisplayNameIsFlagged()
    {
        // Der klassische Phishing-Trick.
        var (spoof, detail) = MailAuthentication.CheckDisplayName(
            From("\"service@sparkasse.de\" <betrug@example.ru>"));

        Assert.True(spoof);
        Assert.Contains("sparkasse.de", detail);
        Assert.Contains("example.ru", detail);
    }

    [Fact]
    public void SubdomainCountsAsDifferent()
    {
        var (spoof, _) = MailAuthentication.CheckDisplayName(
            From("\"a@example.org\" <b@mail.example.org>"));

        Assert.True(spoof);
    }
}

public class TrustedSenderTests
{
    [Fact]
    public void UnknownSenderIsNotTrusted()
        => Assert.False(new AppSettings().IsTrusted("a@b.c"));

    [Fact]
    public void TrustIsCaseInsensitive()
    {
        var s = new AppSettings { TrustedSenders = ["Newsletter@Example.ORG"] };
        Assert.True(s.IsTrusted("newsletter@example.org"));
    }

    [Fact]
    public void EmptyAddressIsNeverTrusted()
    {
        var s = new AppSettings { TrustedSenders = ["a@b.c"] };

        Assert.False(s.IsTrusted(""));
        Assert.False(s.IsTrusted(null));
    }

    [Fact]
    public void TrustDoesNotExtendToTheDomain()
    {
        // Vertrauen gilt dem Absender, nicht allen unter derselben Domain.
        var s = new AppSettings { TrustedSenders = ["gut@example.org"] };
        Assert.False(s.IsTrusted("boese@example.org"));
    }
}
