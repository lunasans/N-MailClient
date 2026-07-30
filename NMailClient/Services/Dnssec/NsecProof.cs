using System.Security.Cryptography;
using System.Text;

namespace NMailClient.Services.Dnssec;

public enum DenialResult
{
    /// <summary>Kein brauchbarer Beleg – damit ist gar nichts bewiesen.</summary>
    NotProven,

    /// <summary>Der Name gibt es, dieser Typ aber nicht.</summary>
    ProvenNoData,

    /// <summary>Den Namen gibt es überhaupt nicht.</summary>
    ProvenNxDomain,

    /// <summary>
    /// Belegte Delegation ohne DS: ab hier ist der Bereich nachweislich
    /// unsigniert. Nur für DS-Anfragen sinnvoll.
    /// </summary>
    ProvenInsecureDelegation,
}

/// <summary>
/// Prüft die Belege für Nichtexistenz (RFC 4035 §5.4, RFC 5155 §8).
///
/// Ohne diesen Teil wäre die ganze Übung wertlos: ein Angreifer, der einfach
/// alle TLSA-Einträge aus der Antwort streicht, bekäme sonst ein „diese Domain
/// macht kein DANE" — also genau die Herabstufung, gegen die DANE schützen soll.
/// Deshalb gilt hier: nur ein <em>signierter</em> Beleg zählt.
/// </summary>
internal static class NsecProof
{
    public static DenialResult Check(
        DnsMessage response, DnsName qname, ushort qtype,
        IReadOnlyList<DnsKey> keys, DnsName zone)
    {
        var nsec = ValidatedRecords(response.Authority, RrType.Nsec, keys);
        if (nsec.Count > 0) return CheckNsec(nsec, qname, qtype);

        var nsec3 = ValidatedRecords(response.Authority, RrType.Nsec3, keys);
        if (nsec3.Count > 0) return CheckNsec3(nsec3, qname, qtype, zone);

        return DenialResult.NotProven;
    }

    /// <summary>
    /// Alle Einträge eines Typs, deren RRset-Signatur mit den übergebenen
    /// Zonenschlüsseln aufgeht. Unsignierte Belege werden verworfen.
    /// </summary>
    private static List<DnsRecord> ValidatedRecords(
        IReadOnlyList<DnsRecord> section, ushort type, IReadOnlyList<DnsKey> keys)
    {
        var result = new List<DnsRecord>();

        foreach (var group in section.Where(r => r.Type == type).GroupBy(r => r.Name.ToString()))
        {
            var rrset = group.ToList();
            var owner = rrset[0].Name;

            var signatures = section
                .Where(r => r.Type == RrType.RrSig && r.Name.Equals(owner))
                .Select(r => RrSig.Parse(r.RData))
                .OfType<RrSig>()
                .Where(s => s.TypeCovered == type);

            foreach (var sig in signatures)
            {
                if (!sig.IsCurrent(DateTimeOffset.UtcNow)) continue;
                if (!DnssecAlgorithm.IsSupported(sig.Algorithm)) continue;

                var data = DnsCanonical.SignedData(sig, owner, rrset);
                if (keys.Any(k => k.KeyTag() == sig.KeyTag && DnssecCrypto.Verify(k, sig, data)))
                {
                    result.AddRange(rrset);
                    break;
                }
            }
        }

        return result;
    }

    // ---- NSEC --------------------------------------------------------------

    private static DenialResult CheckNsec(List<DnsRecord> records, DnsName qname, ushort qtype)
    {
        foreach (var record in records)
        {
            var next = DnsWire.NameFromWire(record.RData);
            if (next is null) continue;

            var bitmapOffset = next.ToWire().Length;
            var types = ParseTypeBitmap(record.RData.AsSpan(bitmapOffset));

            // Treffer auf den Namen selbst: der Name existiert, der Typ nicht.
            if (record.Name.Equals(qname))
            {
                if (types.Contains(qtype)) return DenialResult.NotProven;

                if (qtype == RrType.Ds)
                    return types.Contains(RrType.Ns) && !types.Contains(RrType.Soa)
                        ? DenialResult.ProvenInsecureDelegation
                        : DenialResult.ProvenNoData;

                return DenialResult.ProvenNoData;
            }

            // Der Name fällt in die Lücke zwischen zwei benachbarten Namen.
            if (Covers(record.Name, next, qname)) return DenialResult.ProvenNxDomain;
        }

        return DenialResult.NotProven;
    }

    /// <summary>
    /// Liegt <paramref name="qname"/> echt zwischen Besitzer und Nachfolger? Am
    /// Ende der Zone ist der Nachfolger wieder der Zonenanfang, dann läuft das
    /// Intervall über die Grenze.
    /// </summary>
    private static bool Covers(DnsName owner, DnsName next, DnsName qname)
    {
        var afterOwner = owner.CompareTo(qname) < 0;
        var beforeNext = qname.CompareTo(next) < 0;

        return next.CompareTo(owner) <= 0 ? afterOwner || beforeNext : afterOwner && beforeNext;
    }

    // ---- NSEC3 -------------------------------------------------------------

    private sealed record Nsec3Record(
        DnsName Owner, byte Algorithm, byte Flags, int Iterations,
        byte[] Salt, byte[] NextHashed, HashSet<ushort> Types)
    {
        public bool OptOut => (Flags & 0x01) != 0;
    }

    private static DenialResult CheckNsec3(
        List<DnsRecord> records, DnsName qname, ushort qtype, DnsName zone)
    {
        var parsed = records.Select(Parse3).OfType<Nsec3Record>().ToList();
        if (parsed.Count == 0) return DenialResult.NotProven;

        // Zu viele Runden sind ein Rechenzeit-Angriff; RFC 9276 rät ohnehin zu 0.
        if (parsed.Any(n => n.Algorithm != 1 || n.Iterations > 150)) return DenialResult.NotProven;

        var first = parsed[0];

        // Treffer auf den gesuchten Namen selbst.
        var hash = Hash(qname, first.Salt, first.Iterations);
        var exact = parsed.FirstOrDefault(n => OwnerHash(n.Owner).SequenceEqual(hash));

        if (exact is not null)
        {
            if (exact.Types.Contains(qtype)) return DenialResult.NotProven;

            if (qtype == RrType.Ds)
                return exact.Types.Contains(RrType.Ns) && !exact.Types.Contains(RrType.Soa)
                    ? DenialResult.ProvenInsecureDelegation
                    : DenialResult.ProvenNoData;

            return DenialResult.ProvenNoData;
        }

        // Kein Treffer: dann muss der Name in eine belegte Lücke fallen.
        var covering = parsed.FirstOrDefault(n => Covers3(OwnerHash(n.Owner), n.NextHashed, hash));
        if (covering is null) return DenialResult.NotProven;

        // Bei gesetztem Opt-out-Bit dürfen unsignierte Delegationen in der Lücke
        // fehlen – genau der Fall bei vielen Top-Level-Domains. Für eine
        // DS-Anfrage heisst das: nachweislich unsigniert.
        if (qtype == RrType.Ds && covering.OptOut) return DenialResult.ProvenInsecureDelegation;

        return DenialResult.ProvenNxDomain;
    }

    private static Nsec3Record? Parse3(DnsRecord record)
    {
        var rdata = record.RData;
        if (rdata.Length < 6) return null;

        var algorithm = rdata[0];
        var flags = rdata[1];
        var iterations = (rdata[2] << 8) | rdata[3];

        var saltLength = rdata[4];
        if (5 + saltLength >= rdata.Length) return null;
        var salt = rdata[5..(5 + saltLength)];

        var pos = 5 + saltLength;
        var hashLength = rdata[pos++];
        if (pos + hashLength > rdata.Length) return null;
        var next = rdata[pos..(pos + hashLength)];
        pos += hashLength;

        var types = ParseTypeBitmap(rdata.AsSpan(pos));
        return new Nsec3Record(record.Name, algorithm, flags, iterations, salt, next, types);
    }

    /// <summary>Der iterierte Hash aus RFC 5155 §5 – SHA-1 über Name und Salz.</summary>
    private static byte[] Hash(DnsName name, byte[] salt, int iterations)
    {
        var buffer = name.ToWire(canonical: true).Concat(salt).ToArray();
        var digest = SHA1.HashData(buffer);

        for (var i = 0; i < iterations; i++)
            digest = SHA1.HashData(digest.Concat(salt).ToArray());

        return digest;
    }

    /// <summary>Das erste Label des NSEC3-Besitzernamens ist der Hash in Base32Hex.</summary>
    private static byte[] OwnerHash(DnsName owner)
        => owner.LabelCount == 0 ? [] : Base32HexDecode(Encoding.ASCII.GetString(owner.Labels[0]));

    private static bool Covers3(byte[] owner, byte[] next, byte[] hash)
    {
        var afterOwner = Compare(owner, hash) < 0;
        var beforeNext = Compare(hash, next) < 0;

        return Compare(next, owner) <= 0 ? afterOwner || beforeNext : afterOwner && beforeNext;
    }

    private static int Compare(byte[] a, byte[] b) => a.AsSpan().SequenceCompareTo(b);

    private const string Base32HexAlphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUV";

    private static byte[] Base32HexDecode(string text)
    {
        var bits = 0;
        var value = 0;
        var output = new List<byte>();

        foreach (var c in text.ToUpperInvariant())
        {
            var index = Base32HexAlphabet.IndexOf(c);
            if (index < 0) return [];

            value = (value << 5) | index;
            bits += 5;

            if (bits >= 8)
            {
                output.Add((byte)(value >> (bits - 8)));
                bits -= 8;
            }
        }

        return output.ToArray();
    }

    // ---- Typlisten ---------------------------------------------------------

    /// <summary>
    /// Die Typliste steht als Folge von Blöcken: Fenster-Nummer, Länge, Bits.
    /// </summary>
    private static HashSet<ushort> ParseTypeBitmap(ReadOnlySpan<byte> data)
    {
        var types = new HashSet<ushort>();
        var pos = 0;

        while (pos + 2 <= data.Length)
        {
            var window = data[pos];
            var length = data[pos + 1];
            pos += 2;

            if (length == 0 || pos + length > data.Length) break;

            for (var i = 0; i < length; i++)
            {
                var bits = data[pos + i];
                for (var bit = 0; bit < 8; bit++)
                    if ((bits & (0x80 >> bit)) != 0)
                        types.Add((ushort)((window << 8) | (i * 8 + bit)));
            }

            pos += length;
        }

        return types;
    }
}
