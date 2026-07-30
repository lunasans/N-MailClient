using NMailClient.Poc.Services;
using Xunit;

namespace NMailClient.Poc.Tests;

/// <summary>
/// Nur die netzunabhängige Logik. Die Auflösung selbst braucht DNS und HTTPS und
/// gehört damit nicht in Unit-Tests – siehe <see cref="AutodiscoverNetworkTests"/>.
/// </summary>
public class AutodiscoverTests
{
    [Theory]
    [InlineData("kaputt-ohne-at")]
    [InlineData("")]
    [InlineData("nur@")]
    public async Task InvalidAddressYieldsDefaultPortsWithoutSource(string email)
    {
        var r = await Autodiscover.RunAsync(email, allowProbe: false, TestContext.Current.CancellationToken);

        Assert.Equal(993, r.ImapPort);
        Assert.Equal(465, r.SmtpPort);
        Assert.Equal("", r.Source);
        Assert.False(r.IsComplete);
        Assert.False(r.IsVerified);
    }

    [Theory]
    [InlineData("autoconfig", true)]
    [InlineData("srv", true)]
    [InlineData("guess", true)]
    [InlineData("fallback", false)]
    [InlineData("", false)]
    public void IsVerifiedReflectsSource(string source, bool expected)
    {
        var r = new ProbeResult { Source = source };
        Assert.Equal(expected, r.IsVerified);
    }

    [Fact]
    public void IsCompleteRequiresBothHosts()
    {
        Assert.False(new ProbeResult { ImapHost = "a" }.IsComplete);
        Assert.False(new ProbeResult { SmtpHost = "b" }.IsComplete);
        Assert.True(new ProbeResult { ImapHost = "a", SmtpHost = "b" }.IsComplete);
    }

    [Theory]
    [InlineData("autoconfig", "Anbieter-Autoconfig")]
    [InlineData("srv", "DNS-SRV-Einträge")]
    [InlineData("fallback", "Standardnamen, ungeprüft")]
    [InlineData("quatsch", "unbekannt")]
    public void SourceDisplayIsHumanReadable(string source, string expected)
        => Assert.Equal(expected, new ProbeResult { Source = source }.SourceDisplay);

    [Theory]
    [InlineData("mx-autoconfig", true)]
    [InlineData("mx", true)]
    public void MxSourcesCountAsVerified(string source, bool expected)
        => Assert.Equal(expected, new ProbeResult { Source = source }.IsVerified);

    // ---- Domain aus einem MX-Namen -----------------------------------------

    [Theory]
    [InlineData("mail.neuhaus.or.at", "neuhaus.or.at")]
    [InlineData("mx1.example.com", "example.com")]
    [InlineData("aspmx.l.google.com", "l.google.com")]
    public void TheFirstLabelIsDropped(string host, string expected)
        => Assert.Equal(expected, Autodiscover.BaseDomain(host));

    [Theory]
    [InlineData("example.com")]
    [InlineData("posteo.de")]
    public void ATwoPartNameStaysAsItIs(string host)
        => Assert.Equal(host, Autodiscover.BaseDomain(host));

    [Fact]
    public void ATrailingDotDoesNotProduceAnEmptyLabel()
        => Assert.Equal("neuhaus.or.at", Autodiscover.BaseDomain("mail.neuhaus.or.at."));
}

/// <summary>
/// Greift auf externe Server zu. Über das Trait ausschließbar:
/// <c>dotnet test --filter Category!=Network</c>
/// </summary>
[Trait("Category", "Network")]
public class AutodiscoverNetworkTests
{
    [Fact]
    public async Task ResolvesKnownProviderViaAutoconfig()
    {
        var r = await Autodiscover.RunAsync("test@gmail.com", allowProbe: false, TestContext.Current.CancellationToken);

        Assert.Equal("autoconfig", r.Source);
        Assert.True(r.IsComplete);
        Assert.Contains("gmail.com", r.ImapHost);
    }

    [Fact]
    public async Task UnresolvableDomainIsMarkedUnverified()
    {
        var r = await Autodiscover.RunAsync("test@nichtexistent-xyzq.invalid", allowProbe: false, TestContext.Current.CancellationToken);

        Assert.False(r.IsVerified);
        Assert.Equal("fallback", r.Source);
        // Trotzdem ein brauchbarer Vorschlag, statt leerer Felder.
        Assert.Equal("mail.nichtexistent-xyzq.invalid", r.ImapHost);
    }

    [Fact]
    public async Task AMailboxUnderAnotherDomainIsFoundViaMx()
    {
        // Der gemeldete Fehler: liegt das Postfach nicht unter der Domain der
        // Adresse, kam „mail.<domain>" heraus — ein Name, den es hier gar nicht
        // gibt. Der MX-Eintrag nennt den richtigen Rechner.
        var r = await Autodiscover.RunAsync(
            "rene@rene-neuhaus.eu", allowProbe: false, TestContext.Current.CancellationToken);

        Assert.True(r.IsVerified, $"Quelle war '{r.Source}' – MX-Stufe hat nicht gegriffen.");
        Assert.NotEqual("mail.rene-neuhaus.eu", r.ImapHost);
    }

    [Fact]
    public async Task TheProposedHostActuallyExists()
    {
        // Die allgemeine Eigenschaft dahinter: ein Vorschlag, den das DNS nicht
        // kennt, ist wertlos. Gilt für jede Domain mit MX-Eintrag.
        var r = await Autodiscover.RunAsync(
            "rene@rene-neuhaus.eu", allowProbe: false, TestContext.Current.CancellationToken);

        var addresses = await System.Net.Dns.GetHostAddressesAsync(
            r.ImapHost, TestContext.Current.CancellationToken);

        Assert.NotEmpty(addresses);
    }

    [Fact]
    public async Task CachesPerDomain()
    {
        // Erster Aufruf füllt den Cache, zweiter muss praktisch sofort antworten.
        await Autodiscover.RunAsync("a@posteo.de", allowProbe: false, TestContext.Current.CancellationToken);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Autodiscover.RunAsync("b@posteo.de", allowProbe: false, TestContext.Current.CancellationToken);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 50,
            $"Zweiter Aufruf dauerte {sw.ElapsedMilliseconds} ms – Cache greift nicht.");
    }
}
