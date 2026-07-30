using NMailClient.Poc.Services.Dnssec;
using Xunit;

namespace NMailClient.Poc.Tests;

/// <summary>
/// Prüft die Kette gegen die echte Wurzelzone. Braucht Netz und ist deshalb
/// standardmässig ausgeschlossen — der eigentliche Beweis, dass die Rechnerei
/// stimmt, lässt sich aber nur hier führen.
/// </summary>
[Trait("Category", "Network")]
public class DnssecChainTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RootDelegationToOrgIsSecure()
    {
        var answer = await new DnssecValidator().ResolveAsync(
            DnsName.Parse("org"), RrType.Ds, Ct);

        Assert.Equal(DnssecStatus.Secure, answer.Status);
        Assert.NotEmpty(answer.Records);
    }

    [Fact]
    public async Task SignedDomainValidates()
    {
        var answer = await new DnssecValidator().ResolveAsync(
            DnsName.Parse("ietf.org"), RrType.Mx, Ct);

        Assert.Equal(DnssecStatus.Secure, answer.Status);
        Assert.NotEmpty(answer.Records);
    }

    /// <summary>
    /// Verisign betreibt diese Domain mit absichtlich kaputter Signatur. Wenn
    /// unsere Prüfung sie durchwinkt, ist die ganze Arbeit wertlos.
    /// </summary>
    [Fact]
    public async Task DeliberatelyBrokenDomainIsRejected()
    {
        var answer = await new DnssecValidator().ResolveAsync(
            DnsName.Parse("dnssec-failed.org"), RrType.A, Ct);

        Assert.NotEqual(DnssecStatus.Secure, answer.Status);
    }

    [Fact]
    public async Task AbsenceOfARecordTypeIsProven()
    {
        // ietf.org hat kein TLSA am Zonenscheitel. Das muss belegt sein, nicht
        // bloss behauptet – sonst könnte jemand die Einträge unterschlagen.
        var answer = await new DnssecValidator().ResolveAsync(
            DnsName.Parse("ietf.org"), RrType.Tlsa, Ct);

        Assert.Equal(DnssecStatus.Secure, answer.Status);
        Assert.Empty(answer.Records);
    }

    [Fact]
    public async Task ChainIsReusedAcrossQueries()
    {
        // Zweite Anfrage in derselben Instanz: Schlüssel und DS liegen im
        // Zwischenspeicher, es darf keine erneute Kettenprüfung nötig sein.
        var validator = new DnssecValidator();

        await validator.ResolveAsync(DnsName.Parse("ietf.org"), RrType.Mx, Ct);

        var start = DateTimeOffset.UtcNow;
        var second = await validator.ResolveAsync(DnsName.Parse("ietf.org"), RrType.A, Ct);
        var elapsed = DateTimeOffset.UtcNow - start;

        Assert.Equal(DnssecStatus.Secure, second.Status);
        Assert.True(elapsed < TimeSpan.FromSeconds(2), $"zweite Anfrage dauerte {elapsed}");
    }
}
