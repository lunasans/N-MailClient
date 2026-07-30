using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace NMailClient.Services.Dnssec;

/// <summary>Die DNSSEC-Signaturverfahren, die wir anerkennen.</summary>
public static class DnssecAlgorithm
{
    public const byte RsaSha1 = 5;
    public const byte RsaSha1Nsec3 = 7;
    public const byte RsaSha256 = 8;
    public const byte RsaSha512 = 10;
    public const byte EcdsaP256Sha256 = 13;
    public const byte EcdsaP384Sha384 = 14;
    public const byte Ed25519 = 15;
    public const byte Ed448 = 16;

    /// <summary>
    /// SHA-1-Verfahren (5 und 7) gelten hier als <b>nicht</b> unterstützt. Sie sind
    /// für Kollisionen gebrochen; eine Zone, die noch damit signiert, bekommt von
    /// uns „nicht prüfbar" statt eines grünen Hakens, den sie nicht verdient.
    /// Ed448 fehlt, weil weder .NET noch BouncyCastle es hier ohne Weiteres bieten
    /// — und es praktisch nirgends eingesetzt wird.
    /// </summary>
    public static bool IsSupported(byte algorithm) => algorithm is
        RsaSha256 or RsaSha512 or EcdsaP256Sha256 or EcdsaP384Sha384 or Ed25519;

    public static string Name(byte algorithm) => algorithm switch
    {
        RsaSha1 => "RSA/SHA-1",
        RsaSha1Nsec3 => "RSA/SHA-1 (NSEC3)",
        RsaSha256 => "RSA/SHA-256",
        RsaSha512 => "RSA/SHA-512",
        EcdsaP256Sha256 => "ECDSA P-256/SHA-256",
        EcdsaP384Sha384 => "ECDSA P-384/SHA-384",
        Ed25519 => "Ed25519",
        Ed448 => "Ed448",
        _ => $"Verfahren {algorithm}",
    };
}

/// <summary>
/// Prüft eine RRSIG-Signatur gegen einen DNSKEY.
///
/// Die öffentlichen Schlüssel stecken im DNSKEY-RDATA in verfahrensabhängigen
/// Rohformaten – hier werden sie in das übersetzt, was .NET beziehungsweise
/// BouncyCastle erwarten.
/// </summary>
public static class DnssecCrypto
{
    public static bool Verify(DnsKey key, RrSig sig, byte[] signedData)
    {
        if (key.Algorithm != sig.Algorithm) return false;

        try
        {
            return key.Algorithm switch
            {
                DnssecAlgorithm.RsaSha256 =>
                    VerifyRsa(key.PublicKey, signedData, sig.Signature, HashAlgorithmName.SHA256),
                DnssecAlgorithm.RsaSha512 =>
                    VerifyRsa(key.PublicKey, signedData, sig.Signature, HashAlgorithmName.SHA512),
                DnssecAlgorithm.EcdsaP256Sha256 =>
                    VerifyEcdsa(key.PublicKey, signedData, sig.Signature,
                                ECCurve.NamedCurves.nistP256, HashAlgorithmName.SHA256, 32),
                DnssecAlgorithm.EcdsaP384Sha384 =>
                    VerifyEcdsa(key.PublicKey, signedData, sig.Signature,
                                ECCurve.NamedCurves.nistP384, HashAlgorithmName.SHA384, 48),
                DnssecAlgorithm.Ed25519 =>
                    VerifyEd25519(key.PublicKey, signedData, sig.Signature),
                _ => false,
            };
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException
                                      or IndexOutOfRangeException or FormatException)
        {
            // Ein missgebildeter Schlüssel ist kein Absturzgrund, sondern schlicht
            // eine fehlgeschlagene Prüfung.
            return false;
        }
    }

    /// <summary>
    /// RSA-Schlüssel nach RFC 3110: erst die Länge des Exponenten (ein Byte, oder
    /// eine Null gefolgt von zwei Bytes für lange Exponenten), dann der Exponent,
    /// der Rest ist der Modulus.
    /// </summary>
    private static bool VerifyRsa(byte[] publicKey, byte[] data, byte[] signature,
                                  HashAlgorithmName hash)
    {
        if (publicKey.Length < 3) return false;

        int exponentLength = publicKey[0];
        var offset = 1;

        if (exponentLength == 0)
        {
            exponentLength = (publicKey[1] << 8) | publicKey[2];
            offset = 3;
        }

        if (exponentLength == 0 || offset + exponentLength >= publicKey.Length) return false;

        var parameters = new RSAParameters
        {
            Exponent = publicKey[offset..(offset + exponentLength)],
            Modulus = publicKey[(offset + exponentLength)..],
        };

        using var rsa = RSA.Create();
        rsa.ImportParameters(parameters);
        return rsa.VerifyData(data, signature, hash, RSASignaturePadding.Pkcs1);
    }

    /// <summary>
    /// ECDSA-Schlüssel sind die blanken Koordinaten X‖Y, die Signatur ist r‖s in
    /// fester Länge – also genau das P1363-Format, das .NET direkt versteht.
    /// </summary>
    private static bool VerifyEcdsa(byte[] publicKey, byte[] data, byte[] signature,
                                    ECCurve curve, HashAlgorithmName hash, int fieldSize)
    {
        if (publicKey.Length != fieldSize * 2 || signature.Length != fieldSize * 2) return false;

        var parameters = new ECParameters
        {
            Curve = curve,
            Q = new ECPoint
            {
                X = publicKey[..fieldSize],
                Y = publicKey[fieldSize..],
            },
        };

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportParameters(parameters);
        return ecdsa.VerifyData(data, signature, hash, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    /// <summary>
    /// Ed25519 kann .NET nicht; BouncyCastle liegt über MimeKit ohnehin bei.
    /// </summary>
    private static bool VerifyEd25519(byte[] publicKey, byte[] data, byte[] signature)
    {
        if (publicKey.Length != 32 || signature.Length != 64) return false;

        var verifier = new Ed25519Signer();
        verifier.Init(false, new Ed25519PublicKeyParameters(publicKey, 0));
        verifier.BlockUpdate(data, 0, data.Length);
        return verifier.VerifySignature(signature);
    }
}
