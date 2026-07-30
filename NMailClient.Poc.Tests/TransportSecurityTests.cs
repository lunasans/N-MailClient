using NMailClient.Poc.Services.Dnssec;
using NMailClient.Poc.Services.Transport;
using Xunit;

namespace NMailClient.Poc.Tests;

public class MtaStsPolicyTests
{
    private const string Enforce = """
        version: STSv1
        mode: enforce
        mx: mail.example.org
        mx: *.backup.example.org
        max_age: 604800
        """;

    [Fact]
    public void ParsesModeMxAndMaxAge()
    {
        var policy = MtaStsPolicy.Parse(Enforce);

        Assert.Equal(StsMode.Enforce, policy.Mode);
        Assert.True(policy.IsEnforced);
        Assert.Equal(604800, policy.MaxAgeSeconds);
        Assert.Equal(["mail.example.org", "*.backup.example.org"], policy.Mx);
    }

    [Fact]
    public void TestingModeIsNotEnforced()
    {
        var policy = MtaStsPolicy.Parse("version: STSv1\r\nmode: testing\r\nmx: a.example.org");

        Assert.Equal(StsMode.Testing, policy.Mode);
        Assert.False(policy.IsEnforced);
        Assert.Contains("Testbetrieb", policy.Display);
    }

    [Fact]
    public void PolicyWithoutVersionLineIsRejected()
    {
        // Ohne STSv1 ist es keine Richtlinie. Sie trotzdem als Schutz anzuzeigen
        // wäre die gefährlichere Auslegung.
        var policy = MtaStsPolicy.Parse("mode: enforce\r\nmx: mail.example.org");

        Assert.Equal(MtaStsPolicy.None, policy);
    }

    [Fact]
    public void WrongVersionIsRejected()
        => Assert.Equal(MtaStsPolicy.None,
                        MtaStsPolicy.Parse("version: STSv2\r\nmode: enforce"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("völliger Unsinn ohne Doppelpunkt")]
    public void RubbishBecomesNone(string text)
        => Assert.Equal(MtaStsPolicy.None, MtaStsPolicy.Parse(text));

    [Fact]
    public void NullBecomesNone() => Assert.Equal(MtaStsPolicy.None, MtaStsPolicy.Parse(null));

    [Fact]
    public void UnknownModeCountsAsNone()
    {
        var policy = MtaStsPolicy.Parse("version: STSv1\r\nmode: vielleicht");

        Assert.Equal(StsMode.None, policy.Mode);
    }

    [Fact]
    public void ParsingIgnoresCaseAndSurroundingSpace()
    {
        var policy = MtaStsPolicy.Parse("Version:  STSv1 \r\n MODE : Enforce \r\nMX:  Mail.Example.ORG ");

        Assert.Equal(StsMode.Enforce, policy.Mode);
        Assert.Equal(["mail.example.org"], policy.Mx);
    }

    [Fact]
    public void LineFeedOnlyPoliciesAreAccepted()
    {
        // Nicht jede Domain liefert CRLF, auch wenn die Norm es verlangt.
        var policy = MtaStsPolicy.Parse("version: STSv1\nmode: enforce\nmx: mail.example.org");

        Assert.Equal(StsMode.Enforce, policy.Mode);
    }

    [Theory]
    [InlineData("mail.example.org", true)]
    [InlineData("MAIL.EXAMPLE.ORG", true)]              // Namen sind schreibungsunabhängig
    [InlineData("mail.example.org.", true)]             // abschliessender Punkt aus dem DNS
    [InlineData("mx.backup.example.org", true)]         // Platzhalter, eine Ebene
    [InlineData("a.b.backup.example.org", false)]       // Platzhalter deckt nur eine Ebene
    [InlineData("backup.example.org", false)]           // der Platzhalter braucht ein Label
    [InlineData("fremd.example.org", false)]
    public void HostMatchingFollowsTheWildcardRule(string host, bool expected)
        => Assert.Equal(expected, MtaStsPolicy.Parse(Enforce).CoversHost(host));

    [Fact]
    public void PolicyWithoutMxCoversNothing()
        => Assert.False(MtaStsPolicy.Parse("version: STSv1\r\nmode: enforce")
                                    .CoversHost("mail.example.org"));
}

public class DomainSecurityTests
{
    private static DomainSecurity Result(bool startTls, bool trusted, StsMode sts = StsMode.None,
                                         DaneResult? dane = null)
        => new("example.org", "mx.example.org", startTls, startTls ? "TLS 1.3" : null, trusted,
               new MtaStsPolicy(sts, ["mx.example.org"], 86400), null,
               dane ?? DaneResult.Unknown);

    [Fact]
    public void EncryptedAndTrustedIsGood()
    {
        var r = Result(startTls: true, trusted: true);

        Assert.Equal(TransportGrade.Good, r.Grade);
        Assert.False(r.IsWarning);
        Assert.Contains("TLS 1.3", r.Summary);
    }

    [Fact]
    public void EncryptedButUntrustedIsOnlyWeak()
    {
        // Der Kernpunkt: Verschlüsselung ohne prüfbares Zertifikat schützt nicht
        // gegen einen Zwischenmann und darf nicht wie „sicher" aussehen.
        var r = Result(startTls: true, trusted: false);

        Assert.Equal(TransportGrade.Weak, r.Grade);
        Assert.True(r.IsWarning);
        Assert.Contains("nicht vertrauenswürdig", r.Summary);
    }

    [Fact]
    public void WithoutStartTlsItIsBad()
    {
        var r = Result(startTls: false, trusted: false);

        Assert.Equal(TransportGrade.Bad, r.Grade);
        Assert.True(r.IsWarning);
        Assert.Contains("unverschlüsselt", r.Summary);
    }

    [Fact]
    public void UnreachableDomainIsUnknownNotBad()
    {
        // Nicht erreichbar heisst nicht unsicher — eine rote Warnung wäre gelogen.
        var r = DomainSecurity.Unknown("example.org", "kein MX-Eintrag");

        Assert.Equal(TransportGrade.Unknown, r.Grade);
        Assert.False(r.IsWarning);
        Assert.Contains("nicht prüfbar", r.Summary);
        Assert.Contains("kein MX-Eintrag", r.Summary);
    }

    [Fact]
    public void ConfirmedDaneOutweighsAMissingCertificateChain()
    {
        // Genau der Sinn von DANE-EE: der DNS-Eintrag benennt das Zertifikat
        // unmittelbar, eine Zertifizierungsstelle wird nicht gebraucht.
        var r = Result(startTls: true, trusted: false,
                       dane: new DaneResult(DaneStatus.Valid, 1, "Nutzungsart 3"));

        Assert.Equal(TransportGrade.Good, r.Grade);
        Assert.DoesNotContain("nicht vertrauenswürdig", r.Summary);
        Assert.Contains("DANE: Zertifikat bestätigt", r.Summary);
    }

    [Fact]
    public void DaneMismatchOutweighsEverythingElse()
    {
        // Ein Zertifikat, das dem TLSA-Eintrag widerspricht, ist der stärkste
        // Hinweis auf einen Zwischenmann – auch wenn die CA-Kette sauber ist.
        var r = Result(startTls: true, trusted: true,
                       dane: new DaneResult(DaneStatus.Mismatch, 2, null));

        Assert.Equal(TransportGrade.Bad, r.Grade);
        Assert.True(r.IsWarning);
        Assert.Contains("passt NICHT", r.Summary);
    }

    [Fact]
    public void BogusDnsIsTreatedAsAnAttack()
        => Assert.Equal(TransportGrade.Bad,
                        Result(true, true, dane: new DaneResult(DaneStatus.Bogus, 0, null)).Grade);

    [Fact]
    public void TlsaRecordsWithoutDnssecAreCalledOutAsUseless()
    {
        var r = Result(startTls: true, trusted: true,
                       dane: new DaneResult(DaneStatus.Unsigned, 1, null));

        Assert.Contains("wirkungslos", r.Summary);
    }

    [Fact]
    public void AbsentDaneIsNotMentionedAtAll()
    {
        // „Kein DANE" bei fast jeder Domain anzuzeigen wäre nur Rauschen.
        var r = Result(startTls: true, trusted: true,
                       dane: new DaneResult(DaneStatus.NotOffered, 0, null));

        Assert.DoesNotContain("DANE", r.Summary);
    }

    [Fact]
    public void EnforcedPolicyIsMentioned()
        => Assert.Contains("verbindlich", Result(true, true, StsMode.Enforce).Summary);

    [Fact]
    public void AbsentPolicyIsNotMentioned()
    {
        // „kein MTA-STS" bei jeder Domain anzuzeigen wäre nur Rauschen.
        var r = Result(startTls: true, trusted: true);

        Assert.DoesNotContain("MTA-STS", r.Summary);
    }

    [Theory]
    [InlineData("rene@example.org", "example.org")]
    [InlineData("Rene <rene@Example.ORG>", "example.org>")]   // roher Text, nicht geparst
    [InlineData("rene@example.org.", "example.org")]
    [InlineData("ohne-at", "")]
    [InlineData("endet-mit@", "")]
    public void DomainIsTakenFromTheAddress(string address, string expected)
        => Assert.Equal(expected, RecipientSecurity.DomainOf(address));

    [Fact]
    public void NothingIsCachedBeforeTheFirstCheck()
        => Assert.Null(new RecipientSecurity().Cached("example.org"));

    [Fact]
    public async Task EmptyDomainIsRejectedWithoutAnyConnection()
    {
        var result = await new RecipientSecurity().InspectAsync(
            "   ", TestContext.Current.CancellationToken);

        Assert.Equal(TransportGrade.Unknown, result.Grade);
    }
}

/// <summary>
/// Der TLSA-Abgleich, geprüft an einem selbst erzeugten Zertifikat: alle vier
/// Kombinationen aus Selektor und Abgleichart.
/// </summary>
public class TlsaMatchingTests
{
    private static System.Security.Cryptography.X509Certificates.X509Certificate2 Cert()
    {
        using var key = System.Security.Cryptography.ECDsa.Create(
            System.Security.Cryptography.ECCurve.NamedCurves.nistP256);

        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=mx.example.org", key, System.Security.Cryptography.HashAlgorithmName.SHA256);

        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),
                                        DateTimeOffset.UtcNow.AddDays(30));
    }

    [Fact]
    public void FullCertificateBySha256Matches()
    {
        using var cert = Cert();
        var digest = System.Security.Cryptography.SHA256.HashData(cert.RawData);

        Assert.True(new TlsaRecord(3, 0, 1, digest).Matches(cert));
    }

    [Fact]
    public void PublicKeyBySha256Matches()
    {
        using var cert = Cert();
        var digest = System.Security.Cryptography.SHA256.HashData(
            cert.PublicKey.ExportSubjectPublicKeyInfo());

        Assert.True(new TlsaRecord(3, 1, 1, digest).Matches(cert));
    }

    [Fact]
    public void FullCertificateExactMatches()
    {
        using var cert = Cert();

        Assert.True(new TlsaRecord(3, 0, 0, cert.RawData).Matches(cert));
    }

    [Fact]
    public void Sha512Matches()
    {
        using var cert = Cert();
        var digest = System.Security.Cryptography.SHA512.HashData(cert.RawData);

        Assert.True(new TlsaRecord(3, 0, 2, digest).Matches(cert));
    }

    [Fact]
    public void ADifferentCertificateDoesNotMatch()
    {
        using var mine = Cert();
        using var other = Cert();
        var digest = System.Security.Cryptography.SHA256.HashData(other.RawData);

        Assert.False(new TlsaRecord(3, 0, 1, digest).Matches(mine));
    }

    [Fact]
    public void SelectorAndMatchingTypeMustBeSwappedCorrectly()
    {
        // Fingerabdruck des Schlüssels, aber als „ganzes Zertifikat" deklariert:
        // darf nicht passen, sonst wären die Felder vertauschbar.
        using var cert = Cert();
        var keyDigest = System.Security.Cryptography.SHA256.HashData(
            cert.PublicKey.ExportSubjectPublicKeyInfo());

        Assert.False(new TlsaRecord(3, 0, 1, keyDigest).Matches(cert));
    }

    [Theory]
    [InlineData(0, 9)]      // unbekannte Abgleichart
    [InlineData(9, 1)]      // unbekannter Selektor
    public void UnknownFieldsNeverMatch(byte selector, byte matching)
    {
        using var cert = Cert();

        Assert.False(new TlsaRecord(3, selector, matching, cert.RawData).Matches(cert));
    }

    [Fact]
    public void ShortRdataIsRejected()
        => Assert.Null(TlsaRecord.Parse([3, 1]));

    [Fact]
    public void RdataIsSplitIntoItsFields()
    {
        var record = TlsaRecord.Parse([3, 1, 1, 0xAB, 0xCD]);

        Assert.NotNull(record);
        Assert.Equal(3, record.Usage);
        Assert.Equal(1, record.Selector);
        Assert.Equal(1, record.MatchingType);
        Assert.Equal([0xAB, 0xCD], record.Data);
    }
}

/// <summary>
/// Prüft die Sonde gegen echte Server. Standardmässig ausgeschlossen — und
/// bewusst gegen grosse, öffentliche Anbieter statt gegen den eigenen Server.
/// </summary>
[Trait("Category", "Network")]
public class RecipientSecurityNetworkTests
{
    [Fact]
    public async Task GmailOffersStartTlsWithAValidCertificate()
    {
        var result = await new RecipientSecurity().InspectAsync(
            "gmail.com", TestContext.Current.CancellationToken);

        Assert.NotNull(result.MxHost);
        Assert.True(result.StartTls, result.Summary);
        Assert.True(result.CertificateTrusted, result.Summary);
        Assert.Equal(TransportGrade.Good, result.Grade);
    }

    [Fact]
    public async Task GmailPublishesAnEnforcingMtaStsPolicy()
    {
        var result = await new RecipientSecurity().InspectAsync(
            "gmail.com", TestContext.Current.CancellationToken);

        Assert.Equal(StsMode.Enforce, result.Sts.Mode);
        Assert.True(result.Sts.CoversHost(result.MxHost!), result.Sts.Display);
    }

    [Fact]
    public async Task DomainWithoutMailIsReportedAsUnknown()
    {
        var result = await new RecipientSecurity().InspectAsync(
            "example.com", TestContext.Current.CancellationToken);

        Assert.Equal(TransportGrade.Unknown, result.Grade);
    }

    /// <summary>
    /// posteo.de setzt DANE seit Jahren ein. Wenn hier nichts bestätigt wird,
    /// hakt es irgendwo zwischen MX-Auflösung, DNSSEC-Kette und TLSA-Abgleich.
    /// </summary>
    [Fact]
    public async Task DaneEnabledProviderIsConfirmed()
    {
        var result = await new RecipientSecurity().InspectAsync(
            "posteo.de", TestContext.Current.CancellationToken);

        Assert.Equal(DnssecStatus.Secure, result.MxDnssec);
        Assert.Equal(DaneStatus.Valid, result.Dane.Status);
        Assert.Equal(TransportGrade.Good, result.Grade);
        Assert.Contains("DANE", result.Summary);
    }

    /// <summary>
    /// mailbox.org signiert mit RSA/SHA-1 (Verfahren 7), das wir nicht anerkennen.
    /// Das Ergebnis muss „nicht feststellbar" lauten — <b>nicht</b> „manipuliert":
    /// ein veraltetes Verfahren ist kein Angriff, und ein Fehlalarm hier würde die
    /// Anzeige unglaubwürdig machen.
    /// </summary>
    [Fact]
    public async Task DomainSignedWithSha1IsIndeterminateNotBogus()
    {
        var result = await new RecipientSecurity().InspectAsync(
            "mailbox.org", TestContext.Current.CancellationToken);

        Assert.Equal(DnssecStatus.Indeterminate, result.MxDnssec);
        Assert.NotEqual(DaneStatus.Bogus, result.Dane.Status);
        Assert.NotEqual(TransportGrade.Bad, result.Grade);
    }

    /// <summary>
    /// Der eigene Server des Anwenders: vollständige Kette, DANE bestätigt.
    /// Hier zeigt sich, ob die Prüfung auch bei einer kleinen, selbst betriebenen
    /// Domain trägt und nicht nur bei den grossen Anbietern.
    /// </summary>
    [Fact]
    public async Task OwnServerIsFullyProtected()
    {
        var result = await new RecipientSecurity().InspectAsync(
            "neuhaus.or.at", TestContext.Current.CancellationToken);

        Assert.Equal("mail.neuhaus.or.at", result.MxHost);
        Assert.Equal(DnssecStatus.Secure, result.MxDnssec);
        Assert.Equal(DaneStatus.Valid, result.Dane.Status);
        Assert.Equal(TransportGrade.Good, result.Grade);
    }

    /// <summary>
    /// gmail.com hat kein DANE. Wichtig ist, dass das <em>belegt</em> ist und
    /// nicht bloss aus einer leeren Antwort geschlossen wird.
    /// </summary>
    [Fact]
    public async Task ProviderWithoutDaneIsReportedAsNotOffered()
    {
        var result = await new RecipientSecurity().InspectAsync(
            "gmail.com", TestContext.Current.CancellationToken);

        Assert.NotEqual(DaneStatus.Valid, result.Dane.Status);
        Assert.NotEqual(DaneStatus.Mismatch, result.Dane.Status);
        Assert.Equal(TransportGrade.Good, result.Grade);
    }

    [Fact]
    public async Task RepeatedChecksComeFromTheCache()
    {
        var svc = new RecipientSecurity();
        var ct = TestContext.Current.CancellationToken;

        var first = await svc.InspectAsync("gmail.com", ct);
        Assert.NotNull(svc.Cached("gmail.com"));

        // Zweiter Aufruf darf keine Verbindung mehr aufbauen – deshalb sofort da.
        var start = DateTimeOffset.UtcNow;
        var second = await svc.InspectAsync("GMAIL.COM", ct);

        Assert.Equal(first, second);
        Assert.True(DateTimeOffset.UtcNow - start < TimeSpan.FromMilliseconds(200));
    }
}
