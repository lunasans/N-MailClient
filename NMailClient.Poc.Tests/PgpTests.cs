using System.IO;
using MimeKit;
using NMailClient.Poc.Services.Pgp;
using Xunit;

namespace NMailClient.Poc.Tests;

/// <summary>
/// Reine Kopfrechnerei ohne Schlüsselbund — läuft überall schnell.
/// </summary>
public class PgpKeyInfoTests
{
    [Theory]
    [InlineData("Rene <rene@example.org>", "rene@example.org")]
    [InlineData("Rene Neuhaus (privat) <rene@example.org>", "rene@example.org")]
    [InlineData("rene@example.org", "rene@example.org")]
    [InlineData("Rene Neuhaus", "")]                       // keine Adresse: lieber nichts
    [InlineData("", "")]
    public void AddressIsTakenFromTheUserId(string userId, string expected)
        => Assert.Equal(expected, PgpKeyInfo.AddressOf(userId));

    [Fact]
    public void KeyIdIsSixteenUppercaseHexDigits()
    {
        // Negative Kennungen sind der Normalfall (long vs. unsigned) und dürfen
        // nicht als "-1234..." erscheinen.
        var id = PgpKeyInfo.FormatKeyId(-1);

        Assert.Equal("FFFFFFFFFFFFFFFF", id);
        Assert.Equal(16, id.Length);
    }

    [Fact]
    public void KeyWithoutExpiryIsNotExpired()
    {
        var key = Sample(expires: null);

        Assert.False(key.IsExpired);
        Assert.Equal("unbegrenzt", key.ExpiryDisplay);
    }

    [Fact]
    public void PastExpiryCountsAsExpired()
        => Assert.True(Sample(DateTime.UtcNow.AddDays(-1)).IsExpired);

    [Fact]
    public void FutureExpiryIsFine()
        => Assert.False(Sample(DateTime.UtcNow.AddYears(1)).IsExpired);

    private static PgpKeyInfo Sample(DateTime? expires) => new(
        "0123456789ABCDEF", "Rene <rene@example.org>", "rene@example.org",
        DateTime.UtcNow.AddYears(-1), expires, 4096, IsSecret: true);
}

public class PgpInfoTests
{
    [Fact]
    public void PlainMessageIsNotRelevant()
        => Assert.False(PgpInfo.None.IsRelevant);

    [Fact]
    public void EncryptedButNotOpenedIsAWarning()
    {
        var info = new PgpInfo(PgpKind.Encrypted, false, PgpSignatureState.None, null, null);

        Assert.True(info.IsWarning);
        Assert.False(info.IsGood);
        Assert.Contains("kein passender", info.Summary);
    }

    [Fact]
    public void BrokenSignatureIsAWarningEvenWhenDecrypted()
    {
        // Der wichtigste Fall: entschlüsselt heisst nicht vertrauenswürdig.
        var info = new PgpInfo(PgpKind.Encrypted, true, PgpSignatureState.Invalid, "wer", null);

        Assert.True(info.IsWarning);
        Assert.False(info.IsGood);
    }

    [Fact]
    public void MissingKeyIsNeitherGoodNorBad()
    {
        var info = new PgpInfo(PgpKind.Signed, false, PgpSignatureState.UnknownKey, null, null);

        Assert.False(info.IsWarning);
        Assert.False(info.IsGood);
        Assert.Contains("nicht prüfbar", info.Summary);
    }

    [Fact]
    public void ValidSignatureNamesTheSigner()
    {
        var info = new PgpInfo(PgpKind.Signed, false, PgpSignatureState.Valid,
                               "Rene <rene@example.org>", null);

        Assert.True(info.IsGood);
        Assert.Contains("rene@example.org", info.Summary);
    }
}

/// <summary>
/// Klassifizierung ohne Schlüsselbund: die Leseansicht muss auch dann richtig
/// entscheiden, wenn PGP gar nicht eingerichtet ist.
/// </summary>
public class PgpClassifyTests
{
    [Fact]
    public void PlainTextIsNotPgp()
        => Assert.Equal(PgpKind.None, PgpService.Classify(new TextPart("plain")));

    [Fact]
    public void MissingBodyIsNotPgp()
        => Assert.Equal(PgpKind.None, PgpService.Classify(null));

    [Fact]
    public void OrdinaryMultipartIsNotPgp()
        => Assert.Equal(PgpKind.None, PgpService.Classify(new Multipart("mixed")));
}

/// <summary>
/// Der echte Durchstich: Schlüssel erzeugen, verschlüsseln, signieren und alles
/// wieder auspacken. Braucht kein Netz, aber Rechenzeit für die Schlüssel —
/// deshalb eine einzige Sammlung mit gemeinsamem Schlüsselbund.
/// </summary>
public class PgpRoundTripTests : IDisposable
{
    private const string Password = "mantra-fuer-den-test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "pgp-tests-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly PgpService _pgp;
    private readonly MailboxAddress _alice = new("Alice", "alice@example.org");
    private readonly MailboxAddress _bob = new("Bob", "bob@example.org");

    public PgpRoundTripTests()
    {
        _pgp = new PgpService(_dir)
        {
            // Statt des Anmeldeinformationsverwalters: der Rückfrage-Haken.
            AskPassword = _ => Password,
        };

        _pgp.GenerateKey(_alice, Password);
        _pgp.GenerateKey(_bob, Password);
    }

    public void Dispose()
    {
        try { System.IO.Directory.Delete(_dir, true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private static MimeEntity Body(string text) => new TextPart("plain") { Text = text };

    private static MimeMessage Wrap(MimeEntity body)
    {
        var msg = new MimeMessage { Subject = "Test", Body = body };
        msg.From.Add(new MailboxAddress("Alice", "alice@example.org"));
        msg.To.Add(new MailboxAddress("Bob", "bob@example.org"));
        return msg;
    }

    [Fact]
    public void GeneratedKeysAppearInTheList()
    {
        var keys = _pgp.ListKeys();

        Assert.Equal(2, keys.Count);
        Assert.All(keys, k => Assert.True(k.IsSecret));
        Assert.Contains(keys, k => k.Address == "alice@example.org");
        Assert.Contains(keys, k => k.Address == "bob@example.org");
    }

    [Fact]
    public void EmptyKeyringReportsNoKeys()
    {
        var empty = new PgpService(Path.Combine(_dir, "leer"));

        Assert.False(empty.HasAnyKey());
        Assert.Empty(empty.ListKeys());
    }

    [Fact]
    public void EncryptedMessageComesBackAsText()
    {
        var protectedBody = _pgp.Protect(_alice, [_bob], Body("streng geheim"),
                                         sign: false, encrypt: true, Ct);

        Assert.Equal(PgpKind.Encrypted, PgpService.Classify(protectedBody));

        var opened = _pgp.OpenMessage(Wrap(protectedBody), Ct);

        Assert.True(opened.Info.Decrypted);
        Assert.Equal("streng geheim", Assert.IsAssignableFrom<TextPart>(opened.Content).Text);
    }

    [Fact]
    public void SignedMessageVerifies()
    {
        var protectedBody = _pgp.Protect(_alice, [_bob], Body("offen, aber echt"),
                                         sign: true, encrypt: false, Ct);

        Assert.Equal(PgpKind.Signed, PgpService.Classify(protectedBody));

        var opened = _pgp.OpenMessage(Wrap(protectedBody), Ct);

        Assert.Equal(PgpSignatureState.Valid, opened.Info.Signature);
        Assert.Contains("alice@example.org", opened.Info.Signer);
    }

    [Fact]
    public void SignedAndEncryptedGivesBothResults()
    {
        var protectedBody = _pgp.Protect(_alice, [_bob], Body("beides"),
                                         sign: true, encrypt: true, Ct);

        var opened = _pgp.OpenMessage(Wrap(protectedBody), Ct);

        Assert.True(opened.Info.Decrypted);
        Assert.Equal(PgpSignatureState.Valid, opened.Info.Signature);
        Assert.True(opened.Info.IsGood);
        Assert.Equal("beides", Assert.IsAssignableFrom<TextPart>(opened.Content).Text);
    }

    [Fact]
    public void ProtectWithoutAnyOptionLeavesTheBodyAlone()
    {
        var body = Body("unverändert");

        Assert.Same(body, _pgp.Protect(_alice, [_bob], body, sign: false, encrypt: false, Ct));
    }

    [Fact]
    public void TamperedSignatureIsRejected()
    {
        // Der Kern der ganzen Übung: eine nachträglich veränderte Nachricht
        // darf NICHT als gültig signiert durchgehen.
        var signed = (MimeKit.Cryptography.MultipartSigned)
            _pgp.Protect(_alice, [_bob], Body("Überweise 100 Euro."), sign: true, encrypt: false, Ct);

        ((TextPart)signed[0]).Text = "Überweise 100000 Euro.";

        var opened = _pgp.OpenMessage(Wrap(signed), Ct);

        Assert.NotEqual(PgpSignatureState.Valid, opened.Info.Signature);
    }

    [Fact]
    public void MessageForSomeoneElseStaysClosed()
    {
        var protectedBody = _pgp.Protect(_alice, [_bob], Body("nicht für dich"),
                                         sign: false, encrypt: true, Ct);

        // Ein fremder, leerer Schlüsselbund kommt nicht heran.
        var stranger = new PgpService(Path.Combine(_dir, "fremd"));
        var opened = stranger.OpenMessage(Wrap(protectedBody), Ct);

        Assert.False(opened.Info.Decrypted);
        Assert.True(opened.Info.IsWarning);
        Assert.Null(opened.Content);
    }

    [Fact]
    public void PlainMessageIsLeftUntouched()
    {
        var opened = _pgp.OpenMessage(Wrap(Body("ganz normal")), Ct);

        Assert.Equal(PgpInfo.None, opened.Info);
        Assert.Null(opened.Content);
    }

    [Fact]
    public void SigningNeedsASecretKey()
    {
        Assert.True(_pgp.CanSign(_alice));
        Assert.False(_pgp.CanSign(new MailboxAddress("Fremd", "fremd@example.org")));
    }

    [Fact]
    public void MissingRecipientKeysAreNamed()
    {
        var fremd = new MailboxAddress("Fremd", "fremd@example.org");

        var missing = _pgp.RecipientsWithoutKey([_bob, fremd]);

        Assert.Equal(["fremd@example.org"], missing);
    }

    [Fact]
    public void ExportAndImportCarriesTheKeyToAnotherKeyring()
    {
        var file = Path.Combine(_dir, "bob.asc");
        var bobKey = _pgp.ListKeys().First(k => k.Address == "bob@example.org");

        _pgp.ExportPublicKey(bobKey.KeyId, file, Ct);

        var other = new PgpService(Path.Combine(_dir, "andere"));
        Assert.Equal(1, other.ImportKeys(file, Ct));

        var imported = Assert.Single(other.ListKeys());
        Assert.Equal("bob@example.org", imported.Address);
        Assert.False(imported.IsSecret);          // nur der öffentliche Teil
        Assert.True(other.RecipientsWithoutKey([_bob]).Count == 0);
    }

    [Fact]
    public void ExportedFileIsArmoredAndCarriesNoSecret()
    {
        var file = Path.Combine(_dir, "armored.asc");
        var key = _pgp.ListKeys().First(k => k.Address == "alice@example.org");

        _pgp.ExportPublicKey(key.KeyId, file, Ct);
        var text = File.ReadAllText(file);

        Assert.Contains("BEGIN PGP PUBLIC KEY BLOCK", text);
        Assert.DoesNotContain("PRIVATE KEY", text);
    }

    [Fact]
    public void ImportingRubbishIsRejected()
    {
        var file = Path.Combine(_dir, "kein-schluessel.txt");
        File.WriteAllText(file, "nur Text, kein Schlüssel");

        Assert.Throws<InvalidDataException>(() => _pgp.ImportKeys(file, Ct));
    }

    [Fact]
    public void DeletedKeyIsGoneForGood()
    {
        var key = _pgp.ListKeys().First(k => k.Address == "bob@example.org");

        _pgp.DeleteKey(key.KeyId);

        Assert.DoesNotContain(_pgp.ListKeys(), k => k.Address == "bob@example.org");
        Assert.False(_pgp.CanSign(_bob));
    }

    [Fact]
    public void ExportingAnUnknownKeyFails()
        => Assert.Throws<InvalidOperationException>(
            () => _pgp.ExportPublicKey("0000000000000000", Path.Combine(_dir, "x.asc"), Ct));

    [Fact]
    public void KeyringFilesLiveInTheGivenDirectory()
    {
        // Nicht in %APPDATA%\gnupg: der GnuPG-Bestand des Benutzers bleibt unberührt.
        Assert.True(File.Exists(Path.Combine(_dir, "pubring.gpg")));
        Assert.True(File.Exists(Path.Combine(_dir, "secring.gpg")));
    }
}
