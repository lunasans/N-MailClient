using System.Security.Cryptography;
using NMailClient.Services.Dnssec;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using Xunit;

namespace NMailClient.Tests;

/// <summary>
/// Prüfvektoren aus der Wurzelzone. Der Schlüssel KSK-2017 mit der Kennung 20326
/// ist seit Jahren veröffentlicht und über den IANA-Vertrauensanker belegt —
/// damit lassen sich Kennungsberechnung und DS-Abgleich gegen etwas prüfen,
/// das ich mir nicht selbst ausgedacht habe.
/// </summary>
public class RootTrustAnchorTests
{
    private const string RootKsk2017 =
        "AwEAAaz/tAm8yTn4Mfeh5eyI96WSVexTBAvkMgJzkKTOiW1vkIbzxeF3+/4RgWOq7HrxRixH"
        + "lFlExOLAJr5emLvN7SWXgnLh4+B5xQlNVz8Og8kvArMtNROxVQuCaSnIDdD5LKyWbRd2n9WG"
        + "e2R8PzgCmr3EgVLrjyBxWezF0jLHwVN8efS3rCj/EWgvIWgb9tarpVUDK/b58Da+sqqls3eN"
        + "buv7pr+eoZG+SrDK6nWeL3c6H5Apxz7LjVc1uTIdsIXxuOLYA4/ilBmSVIzuDWfdRUfhHdY6"
        + "+cn8HFRm+2hM8AnXGXws9555KrUB5qihylGa8subX2Nn6UwNR1AkUTV74bU=";

    private static DnsKey Ksk2017() => new(257, 3, DnssecAlgorithm.RsaSha256,
                                           Convert.FromBase64String(RootKsk2017));

    [Fact]
    public void KeyTagMatchesThePublishedValue()
        => Assert.Equal(20326, Ksk2017().KeyTag());

    [Fact]
    public void PublishedDsDigestMatchesTheKey()
    {
        // Der Vertrauensanker der IANA für KSK-2017, Digest-Typ 2 (SHA-256).
        var ds = new DsRecord(20326, DnssecAlgorithm.RsaSha256, 2,
            Convert.FromHexString("E06D44B80B8F1D39A95C0B0D7C65D08458E880409BBC683457104237C7F8EC8D"));

        Assert.True(ds.Matches(DnsName.Root, Ksk2017()));
    }

    [Fact]
    public void DigestOfADifferentOwnerNameDoesNotMatch()
    {
        // Der Besitzername geht in den Fingerabdruck ein – sonst liesse sich ein
        // Schlüssel aus einer fremden Zone unterschieben.
        var ds = new DsRecord(20326, DnssecAlgorithm.RsaSha256, 2,
            Convert.FromHexString("E06D44B80B8F1D39A95C0B0D7C65D08458E880409BBC683457104237C7F8EC8D"));

        Assert.False(ds.Matches(DnsName.Parse("org"), Ksk2017()));
    }

    [Fact]
    public void Sha1DigestsAreRefusedEvenIfTheyWouldMatch()
    {
        // Digest-Typ 1 ist SHA-1. Wir lehnen ihn grundsätzlich ab, statt eine
        // Zusicherung auf gebrochener Grundlage abzugeben.
        var key = Ksk2017();
        var name = DnsName.Root.ToWire(canonical: true);
        var rdata = key.ToRdata();
        var input = name.Concat(rdata).ToArray();
        var sha1 = SHA1.HashData(input);

        Assert.False(new DsRecord(20326, DnssecAlgorithm.RsaSha256, 1, sha1).Matches(DnsName.Root, key));
    }

    [Fact]
    public void WrongKeyTagIsRejectedBeforeHashing()
        => Assert.False(new DsRecord(1, DnssecAlgorithm.RsaSha256, 2, new byte[32])
                            .Matches(DnsName.Root, Ksk2017()));

    [Fact]
    public void KeyFlagsAreInterpreted()
    {
        var ksk = Ksk2017();

        Assert.True(ksk.IsZoneKey);            // Bit 7
        Assert.True(ksk.IsSecureEntryPoint);   // Bit 0 – darauf zeigt der DS

        var zsk = ksk with { Flags = 256 };
        Assert.True(zsk.IsZoneKey);
        Assert.False(zsk.IsSecureEntryPoint);
    }

    [Fact]
    public void KeyRdataSurvivesTheRoundTrip()
    {
        var key = Ksk2017();
        var back = DnsKey.Parse(key.ToRdata());

        // Feldweise vergleichen: ein record mit byte[]-Mitgliedern vergleicht die
        // Felder über die Referenz, nicht über den Inhalt.
        Assert.NotNull(back);
        Assert.Equal(key.Flags, back.Flags);
        Assert.Equal(key.Protocol, back.Protocol);
        Assert.Equal(key.Algorithm, back.Algorithm);
        Assert.Equal(key.PublicKey, back.PublicKey);
        Assert.Equal(key.KeyTag(), back.KeyTag());
    }
}

/// <summary>
/// Selbst erzeugte Signaturen: beweist, dass die Schlüsselformate aus dem
/// DNSKEY-RDATA richtig gelesen und die Signaturen richtig geprüft werden.
/// Ohne Netz und ohne fremde Prüfvektoren.
/// </summary>
public class DnssecSignatureTests
{
    private static readonly DnsName Owner = DnsName.Parse("example.org");

    private static List<DnsRecord> ARecordSet() =>
    [
        new(Owner, RrType.A, 1, 3600, [192, 0, 2, 1]),
        new(Owner, RrType.A, 1, 3600, [192, 0, 2, 2]),
    ];

    /// <summary>Baut ein RRSIG-Gerüst; die Signatur wird danach eingesetzt.</summary>
    private static RrSig Template(byte algorithm) => new(
        RrType.A, algorithm, (byte)Owner.LabelCount, 3600,
        (uint)DateTimeOffset.UtcNow.AddDays(10).ToUnixTimeSeconds(),
        (uint)DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds(),
        0, Owner, []);

    [Fact]
    public void RsaSha256RoundTrip()
    {
        using var rsa = RSA.Create(2048);
        var p = rsa.ExportParameters(false);

        // DNSKEY-Format nach RFC 3110: Exponentenlänge, Exponent, Modulus.
        var publicKey = new byte[] { (byte)p.Exponent!.Length }
            .Concat(p.Exponent).Concat(p.Modulus!).ToArray();
        var key = new DnsKey(256, 3, DnssecAlgorithm.RsaSha256, publicKey);

        var sig = Template(DnssecAlgorithm.RsaSha256) with { KeyTag = 0 };
        var data = DnsCanonical.SignedData(sig, Owner, ARecordSet());
        var signed = sig with
        {
            Signature = rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
        };

        Assert.True(DnssecCrypto.Verify(key, signed, data));
    }

    [Fact]
    public void RsaWithLongExponentEncodingIsUnderstood()
    {
        // Exponenten ab 256 Bytes werden mit führender Null und zwei Längenbytes
        // kodiert. Ein RSA-Schlüssel mit so langem Exponenten ist selten, aber die
        // Kodierung muss trotzdem stimmen – deshalb hier von Hand nachgebaut.
        using var rsa = RSA.Create(2048);
        var p = rsa.ExportParameters(false);

        var publicKey = new byte[] { 0, 0, (byte)p.Exponent!.Length }
            .Concat(p.Exponent).Concat(p.Modulus!).ToArray();
        var key = new DnsKey(256, 3, DnssecAlgorithm.RsaSha256, publicKey);

        var sig = Template(DnssecAlgorithm.RsaSha256);
        var data = DnsCanonical.SignedData(sig, Owner, ARecordSet());
        var signed = sig with
        {
            Signature = rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
        };

        Assert.True(DnssecCrypto.Verify(key, signed, data));
    }

    [Theory]
    [InlineData(DnssecAlgorithm.EcdsaP256Sha256, 32)]
    [InlineData(DnssecAlgorithm.EcdsaP384Sha384, 48)]
    public void EcdsaRoundTrip(byte algorithm, int fieldSize)
    {
        var curve = fieldSize == 32 ? ECCurve.NamedCurves.nistP256 : ECCurve.NamedCurves.nistP384;
        var hash = fieldSize == 32 ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA384;

        using var ecdsa = ECDsa.Create(curve);
        var q = ecdsa.ExportParameters(false).Q;

        // Im DNSKEY stehen die blanken Koordinaten, ohne Formatbyte davor.
        var key = new DnsKey(256, 3, algorithm, q.X!.Concat(q.Y!).ToArray());

        var sig = Template(algorithm);
        var data = DnsCanonical.SignedData(sig, Owner, ARecordSet());
        var signed = sig with
        {
            Signature = ecdsa.SignData(data, hash,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation),
        };

        Assert.True(DnssecCrypto.Verify(key, signed, data));
        Assert.Equal(fieldSize * 2, key.PublicKey.Length);
    }

    [Fact]
    public void Ed25519RoundTrip()
    {
        var generator = new Ed25519KeyPairGenerator();
        generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var pair = generator.GenerateKeyPair();

        var publicKey = ((Ed25519PublicKeyParameters)pair.Public).GetEncoded();
        var key = new DnsKey(256, 3, DnssecAlgorithm.Ed25519, publicKey);

        var sig = Template(DnssecAlgorithm.Ed25519);
        var data = DnsCanonical.SignedData(sig, Owner, ARecordSet());

        var signer = new Ed25519Signer();
        signer.Init(true, pair.Private);
        signer.BlockUpdate(data, 0, data.Length);

        Assert.True(DnssecCrypto.Verify(key, sig with { Signature = signer.GenerateSignature() }, data));
    }

    [Fact]
    public void TamperedDataFailsVerification()
    {
        // Der eigentliche Zweck der Übung: geänderte Daten dürfen nicht durchgehen.
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var q = ecdsa.ExportParameters(false).Q;
        var key = new DnsKey(256, 3, DnssecAlgorithm.EcdsaP256Sha256, q.X!.Concat(q.Y!).ToArray());

        var sig = Template(DnssecAlgorithm.EcdsaP256Sha256);
        var data = DnsCanonical.SignedData(sig, Owner, ARecordSet());
        var signed = sig with
        {
            Signature = ecdsa.SignData(data, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation),
        };

        // Eine einzige geänderte Adresse in der Datenmenge.
        var tampered = DnsCanonical.SignedData(
            sig, Owner, [new DnsRecord(Owner, RrType.A, 1, 3600, [192, 0, 2, 9])]);

        Assert.False(DnssecCrypto.Verify(key, signed, tampered));
    }

    [Fact]
    public void KeyOfADifferentAlgorithmIsRejected()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var q = ecdsa.ExportParameters(false).Q;
        var key = new DnsKey(256, 3, DnssecAlgorithm.EcdsaP256Sha256, q.X!.Concat(q.Y!).ToArray());

        var sig = Template(DnssecAlgorithm.RsaSha256);

        Assert.False(DnssecCrypto.Verify(key, sig, [1, 2, 3]));
    }

    [Fact]
    public void MalformedKeysFailInsteadOfThrowing()
    {
        var sig = Template(DnssecAlgorithm.RsaSha256) with { Signature = new byte[256] };

        Assert.False(DnssecCrypto.Verify(new DnsKey(256, 3, DnssecAlgorithm.RsaSha256, []), sig, [1]));
        Assert.False(DnssecCrypto.Verify(new DnsKey(256, 3, DnssecAlgorithm.RsaSha256, [0, 0, 0]), sig, [1]));
        Assert.False(DnssecCrypto.Verify(new DnsKey(256, 3, DnssecAlgorithm.EcdsaP256Sha256, [1, 2]), sig, [1]));
    }

    [Theory]
    [InlineData(DnssecAlgorithm.RsaSha1, false)]
    [InlineData(DnssecAlgorithm.RsaSha1Nsec3, false)]
    [InlineData(DnssecAlgorithm.Ed448, false)]
    [InlineData(DnssecAlgorithm.RsaSha256, true)]
    [InlineData(DnssecAlgorithm.RsaSha512, true)]
    [InlineData(DnssecAlgorithm.EcdsaP256Sha256, true)]
    [InlineData(DnssecAlgorithm.EcdsaP384Sha384, true)]
    [InlineData(DnssecAlgorithm.Ed25519, true)]
    public void SupportedAlgorithmsAreDeclaredHonestly(byte algorithm, bool supported)
        => Assert.Equal(supported, DnssecAlgorithm.IsSupported(algorithm));

    [Fact]
    public void SignatureValidityWindowIsChecked()
    {
        var now = DateTimeOffset.UtcNow;
        var sig = new RrSig(RrType.A, 8, 2, 3600,
            (uint)now.AddDays(1).ToUnixTimeSeconds(),
            (uint)now.AddDays(-1).ToUnixTimeSeconds(), 0, Owner, []);

        Assert.True(sig.IsCurrent(now));
        Assert.False(sig.IsCurrent(now.AddDays(3)));     // abgelaufen
        Assert.False(sig.IsCurrent(now.AddDays(-3)));    // noch nicht gültig
    }
}
