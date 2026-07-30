using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NMailClient.Services.Dnssec;

namespace NMailClient.Services.Transport;

public enum DaneStatus
{
    /// <summary>Nicht geprüft oder nicht prüfbar.</summary>
    Unknown,

    /// <summary>Nachweislich kein DANE – die Domain macht es schlicht nicht.</summary>
    NotOffered,

    /// <summary>TLSA-Einträge vorhanden, aber ohne gesicherte Kette: wertlos.</summary>
    Unsigned,

    /// <summary>Das Zertifikat passt zu einem TLSA-Eintrag.</summary>
    Valid,

    /// <summary>TLSA-Einträge da, aber keiner passt zum Zertifikat.</summary>
    Mismatch,

    /// <summary>Die DNS-Antwort war manipuliert oder fehlerhaft signiert.</summary>
    Bogus,
}

public record DaneResult(DaneStatus Status, int RecordCount, string? Detail)
{
    public static readonly DaneResult Unknown = new(DaneStatus.Unknown, 0, null);

    public bool IsWarning => Status is DaneStatus.Mismatch or DaneStatus.Bogus;

    public string? Display => Status switch
    {
        DaneStatus.Valid => "DANE: Zertifikat bestätigt",
        DaneStatus.Mismatch => "DANE: Zertifikat passt NICHT zum Eintrag",
        DaneStatus.Bogus => "DANE: DNS-Antwort nicht vertrauenswürdig",
        DaneStatus.Unsigned => "DANE-Einträge ohne DNSSEC (wirkungslos)",
        _ => null,        // „kein DANE" ist der Normalfall und kein Hinweis wert
    };
}

/// <summary>
/// DANE für SMTP nach RFC 7672: Der TLSA-Eintrag unter
/// <c>_25._tcp.&lt;MX-Name&gt;</c> sagt, welches Zertifikat der annehmende Server
/// vorweisen muss — beglaubigt durch DNSSEC statt durch eine
/// Zertifizierungsstelle.
///
/// Nur die Nutzungsarten 2 (DANE-TA) und 3 (DANE-EE) werden ausgewertet; 0 und 1
/// sind für SMTP ausdrücklich zu ignorieren (RFC 7672 §3.1.3), weil dort kein
/// Anwender eingreifen kann, wenn die PKIX-Prüfung scheitert.
/// </summary>
public class Dane(DnssecValidator? validator = null)
{
    private readonly DnssecValidator _dnssec = validator ?? new DnssecValidator();

    private const byte UsageDaneTa = 2;
    private const byte UsageDaneEe = 3;

    /// <summary>
    /// Prüft das vom Server vorgelegte Zertifikat gegen die TLSA-Einträge.
    /// </summary>
    /// <param name="mxHost">Der MX-Name – er, nicht die Maildomain, trägt den Eintrag.</param>
    /// <param name="presented">Das Blattzertifikat und, falls vorhanden, die Kette darüber.</param>
    public async Task<DaneResult> CheckAsync(
        string mxHost, IReadOnlyList<X509Certificate2> presented, CancellationToken ct)
    {
        DnsName query;
        try { query = DnsName.Parse($"_25._tcp.{mxHost.TrimEnd('.')}"); }
        catch (FormatException) { return DaneResult.Unknown; }

        var answer = await _dnssec.ResolveAsync(query, RrType.Tlsa, ct);

        switch (answer.Status)
        {
            case DnssecStatus.Bogus:
                return new DaneResult(DaneStatus.Bogus, 0, answer.Reason);

            case DnssecStatus.Indeterminate:
                return new DaneResult(DaneStatus.Unknown, 0, answer.Reason);

            case DnssecStatus.Insecure:
                // Ohne gesicherte Kette ist ein TLSA-Eintrag beliebig fälschbar
                // und darf nicht als Bestätigung durchgehen.
                return answer.Records.Count == 0
                    ? new DaneResult(DaneStatus.NotOffered, 0, answer.Reason)
                    : new DaneResult(DaneStatus.Unsigned, answer.Records.Count, answer.Reason);
        }

        if (answer.Records.Count == 0)
            return new DaneResult(DaneStatus.NotOffered, 0, "nachweislich keine TLSA-Einträge");

        if (presented.Count == 0)
            return new DaneResult(DaneStatus.Unknown, answer.Records.Count, "kein Zertifikat vorgelegt");

        var usable = 0;

        foreach (var record in answer.Records)
        {
            var tlsa = TlsaRecord.Parse(record.RData);
            if (tlsa is null) continue;
            if (tlsa.Usage is not (UsageDaneTa or UsageDaneEe)) continue;

            usable++;

            // Nutzungsart 3 bindet das Blattzertifikat, 2 einen Anker darüber.
            var candidates = tlsa.Usage == UsageDaneEe ? presented.Take(1) : presented;

            if (candidates.Any(tlsa.Matches))
                return new DaneResult(DaneStatus.Valid, answer.Records.Count,
                                      $"Nutzungsart {tlsa.Usage}");
        }

        return usable == 0
            ? new DaneResult(DaneStatus.NotOffered, answer.Records.Count,
                             "nur für SMTP unzulässige Nutzungsarten")
            : new DaneResult(DaneStatus.Mismatch, answer.Records.Count, null);
    }
}

/// <summary>Ein TLSA-Eintrag samt Abgleichlogik.</summary>
public sealed record TlsaRecord(byte Usage, byte Selector, byte MatchingType, byte[] Data)
{
    public static TlsaRecord? Parse(byte[] rdata)
        => rdata.Length < 4 ? null : new TlsaRecord(rdata[0], rdata[1], rdata[2], rdata[3..]);

    /// <summary>
    /// Passt dieses Zertifikat? Der Selektor bestimmt, <em>was</em> verglichen
    /// wird (ganzes Zertifikat oder nur der öffentliche Schlüssel), die
    /// Abgleichart <em>wie</em> (roh oder als Fingerabdruck).
    /// </summary>
    public bool Matches(X509Certificate2 certificate)
    {
        var subject = Selector switch
        {
            0 => certificate.RawData,
            1 => SubjectPublicKeyInfo(certificate),
            _ => null,
        };
        if (subject is null) return false;

        var comparand = MatchingType switch
        {
            0 => subject,
            1 => SHA256.HashData(subject),
            2 => SHA512.HashData(subject),
            _ => null,
        };
        if (comparand is null) return false;

        return CryptographicOperations.FixedTimeEquals(comparand, Data);
    }

    /// <summary>
    /// Der SubjectPublicKeyInfo-Block in DER — genau das, was Selektor 1 meint.
    /// </summary>
    private static byte[]? SubjectPublicKeyInfo(X509Certificate2 certificate)
    {
        try { return certificate.PublicKey.ExportSubjectPublicKeyInfo(); }
        catch (CryptographicException) { return null; }
    }
}
