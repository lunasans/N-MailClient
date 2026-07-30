using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;

namespace NMailClient.Poc.Services.Dnssec;

/// <summary>Die Felder eines RRSIG-Eintrags, aus dem RDATA gelesen.</summary>
public sealed record RrSig(
    ushort TypeCovered, byte Algorithm, byte Labels, uint OriginalTtl,
    uint Expiration, uint Inception, ushort KeyTag, DnsName Signer, byte[] Signature)
{
    /// <summary>Ist die Signatur zeitlich gültig? Zwei Stunden Toleranz für schiefe Uhren.</summary>
    public bool IsCurrent(DateTimeOffset now)
    {
        var t = now.ToUnixTimeSeconds();
        var slack = TimeSpan.FromHours(2).TotalSeconds;

        // Die Zeitfelder sind 32-bit-Sekunden und laufen 2106 über; der Vergleich
        // per Serienarithmetik (RFC 4034 §3.1.5) bliebe auch dann richtig.
        return t + slack >= Inception && t - slack <= Expiration;
    }

    public static RrSig? Parse(byte[] rdata)
    {
        if (rdata.Length < 19) return null;

        var span = rdata.AsSpan();
        var typeCovered = BinaryPrimitives.ReadUInt16BigEndian(span);
        var algorithm = rdata[2];
        var labels = rdata[3];
        var originalTtl = BinaryPrimitives.ReadUInt32BigEndian(span[4..]);
        var expiration = BinaryPrimitives.ReadUInt32BigEndian(span[8..]);
        var inception = BinaryPrimitives.ReadUInt32BigEndian(span[12..]);
        var keyTag = BinaryPrimitives.ReadUInt16BigEndian(span[16..]);

        // Der Name des Unterzeichners liegt ab Byte 18 und ist bereits ausgeschrieben.
        var end = 18;
        while (end < rdata.Length && rdata[end] != 0) end += rdata[end] + 1;
        if (end >= rdata.Length) return null;
        end++;

        var signer = DnsWire.NameFromWire(rdata.AsSpan(18, end - 18));
        if (signer is null) return null;

        return new RrSig(typeCovered, algorithm, labels, originalTtl, expiration, inception,
                         keyTag, signer, rdata[end..]);
    }

    /// <summary>Das RDATA ohne die Signatur – genau der Teil, der mitsigniert wird.</summary>
    public byte[] RdataWithoutSignature()
    {
        var signer = Signer.ToWire(canonical: true);
        var result = new byte[18 + signer.Length];
        var span = result.AsSpan();

        BinaryPrimitives.WriteUInt16BigEndian(span, TypeCovered);
        result[2] = Algorithm;
        result[3] = Labels;
        BinaryPrimitives.WriteUInt32BigEndian(span[4..], OriginalTtl);
        BinaryPrimitives.WriteUInt32BigEndian(span[8..], Expiration);
        BinaryPrimitives.WriteUInt32BigEndian(span[12..], Inception);
        BinaryPrimitives.WriteUInt16BigEndian(span[16..], KeyTag);
        signer.CopyTo(result, 18);

        return result;
    }
}

/// <summary>Ein DNSKEY-Eintrag.</summary>
public sealed record DnsKey(ushort Flags, byte Protocol, byte Algorithm, byte[] PublicKey)
{
    /// <summary>Bit 7 (0x0100): darf dieser Schlüssel überhaupt Zonendaten signieren?</summary>
    public bool IsZoneKey => (Flags & 0x0100) != 0;

    /// <summary>Bit 0 (0x0001): Schlüssel-Signierschlüssel, auf den ein DS zeigt.</summary>
    public bool IsSecureEntryPoint => (Flags & 0x0001) != 0;

    public static DnsKey? Parse(byte[] rdata)
        => rdata.Length < 5
            ? null
            : new DnsKey(BinaryPrimitives.ReadUInt16BigEndian(rdata), rdata[2], rdata[3], rdata[4..]);

    public byte[] ToRdata()
    {
        var result = new byte[4 + PublicKey.Length];
        BinaryPrimitives.WriteUInt16BigEndian(result, Flags);
        result[2] = Protocol;
        result[3] = Algorithm;
        PublicKey.CopyTo(result, 4);
        return result;
    }

    /// <summary>
    /// Die Schlüsselkennung nach RFC 4034 Anhang B: die RDATA-Bytes als Folge von
    /// 16-Bit-Wörtern aufsummieren. Kein Prüfwert im kryptografischen Sinn – zwei
    /// Schlüssel können dieselbe Kennung tragen, deshalb wird bei der Prüfung
    /// jeder passende Schlüssel durchprobiert.
    /// </summary>
    public ushort KeyTag()
    {
        var rdata = ToRdata();
        uint sum = 0;

        for (var i = 0; i < rdata.Length; i++)
            sum += (i & 1) == 0 ? (uint)(rdata[i] << 8) : rdata[i];

        sum += (sum >> 16) & 0xFFFF;
        return (ushort)(sum & 0xFFFF);
    }
}

/// <summary>Ein DS-Eintrag: der Fingerabdruck eines DNSKEY in der übergeordneten Zone.</summary>
public sealed record DsRecord(ushort KeyTag, byte Algorithm, byte DigestType, byte[] Digest)
{
    public static DsRecord? Parse(byte[] rdata)
        => rdata.Length < 5
            ? null
            : new DsRecord(BinaryPrimitives.ReadUInt16BigEndian(rdata), rdata[2], rdata[3], rdata[4..]);

    /// <summary>
    /// Passt der DS zu diesem Schlüssel? Der Fingerabdruck geht über den
    /// kanonischen Besitzernamen gefolgt vom DNSKEY-RDATA (RFC 4034 §5.1.4).
    ///
    /// SHA-1 (Typ 1) wird abgelehnt: es ist für Kollisionen gebrochen, und ein
    /// „gültig" auf dieser Grundlage anzuzeigen wäre eine falsche Zusicherung.
    /// </summary>
    public bool Matches(DnsName owner, DnsKey key)
    {
        if (KeyTag != key.KeyTag() || Algorithm != key.Algorithm) return false;

        using HashAlgorithm hash = DigestType switch
        {
            2 => SHA256.Create(),
            4 => SHA384.Create(),
            _ => null!,
        };
        if (hash is null) return false;

        var name = owner.ToWire(canonical: true);
        var rdata = key.ToRdata();
        var input = new byte[name.Length + rdata.Length];
        name.CopyTo(input, 0);
        rdata.CopyTo(input, name.Length);

        return CryptographicOperations.FixedTimeEquals(hash.ComputeHash(input), Digest);
    }
}

public static class DnsWire
{
    /// <summary>Namen aus einer bereits ausgeschriebenen Wire-Darstellung lesen.</summary>
    public static DnsName? NameFromWire(ReadOnlySpan<byte> wire)
    {
        var labels = new List<string>();
        var pos = 0;

        while (pos < wire.Length)
        {
            var len = wire[pos];
            if (len == 0) return labels.Count == 0 ? DnsName.Root : DnsName.Parse(string.Join('.', labels));
            if ((len & 0xC0) != 0) return null;                  // Zeiger sind hier unzulässig
            if (pos + 1 + len > wire.Length) return null;

            labels.Add(System.Text.Encoding.ASCII.GetString(wire.Slice(pos + 1, len)));
            pos += len + 1;
        }

        return null;
    }
}

/// <summary>
/// Kanonische Form nach RFC 4034 §6 und die daraus gebauten Signaturdaten
/// (RFC 4035 §5.3.2). Hier entscheidet sich, ob eine Prüfung überhaupt
/// funktionieren kann – ein einziges falsches Byte sieht aus wie ein Angriff.
/// </summary>
public static class DnsCanonical
{
    /// <summary>
    /// Typen, deren RDATA Domainnamen enthält, die für die kanonische Form
    /// kleingeschrieben werden (RFC 4034 §6.2). Die Liste wird bewusst nicht
    /// erweitert – für später hinzugekommene Typen gilt das nicht mehr.
    /// </summary>
    private static byte[] CanonicalRdata(ushort type, byte[] rdata)
    {
        switch (type)
        {
            case RrType.Ns:
            case RrType.CName:
            case RrType.Ptr:
                return LowerName(rdata, 0, rdata.Length);

            case RrType.Mx:
                return LowerNameAfter(rdata, 2);

            case RrType.Srv:
                return LowerNameAfter(rdata, 6);

            case RrType.Nsec:
                return LowerNameAfter(rdata, 0);

            case RrType.Soa:
            {
                var copy = (byte[])rdata.Clone();
                var pos = 0;
                LowerNameInPlace(copy, ref pos);      // mname
                LowerNameInPlace(copy, ref pos);      // rname
                return copy;
            }

            case RrType.RrSig:
            {
                var copy = (byte[])rdata.Clone();
                var pos = 18;
                LowerNameInPlace(copy, ref pos);      // Name des Unterzeichners
                return copy;
            }

            default:
                return rdata;
        }
    }

    private static byte[] LowerName(byte[] rdata, int start, int length)
    {
        var copy = (byte[])rdata.Clone();
        var pos = start;
        LowerNameInPlace(copy, ref pos);
        return copy;
    }

    /// <summary>Nur der Name ab <paramref name="offset"/>; davor bleibt alles unberührt.</summary>
    private static byte[] LowerNameAfter(byte[] rdata, int offset)
    {
        var copy = (byte[])rdata.Clone();
        var pos = offset;
        LowerNameInPlace(copy, ref pos);
        return copy;
    }

    private static void LowerNameInPlace(byte[] buffer, ref int pos)
    {
        while (pos < buffer.Length)
        {
            var len = buffer[pos];
            if (len == 0) { pos++; return; }
            if ((len & 0xC0) != 0 || pos + 1 + len > buffer.Length) return;

            for (var i = pos + 1; i <= pos + len; i++)
                if (buffer[i] is >= (byte)'A' and <= (byte)'Z') buffer[i] += 32;

            pos += len + 1;
        }
    }

    /// <summary>
    /// Der Besitzername, unter dem ein Eintrag signiert wurde. Steht im RRSIG eine
    /// kleinere Label-Zahl als der Name hat, wurde über einen Platzhalter signiert
    /// und der kanonische Name lautet <c>*.&lt;Suffix&gt;</c> (RFC 4035 §5.3.2).
    /// </summary>
    public static DnsName SignedOwner(DnsName owner, byte labels)
    {
        if (labels >= owner.LabelCount) return owner;

        var suffix = owner.Labels.Skip(owner.LabelCount - labels)
                          .Select(l => System.Text.Encoding.ASCII.GetString(l));
        return DnsName.Parse("*." + string.Join('.', suffix));
    }

    /// <summary>
    /// Die zu signierenden Bytes: RRSIG-RDATA ohne Signatur, danach jeder Eintrag
    /// der Menge in kanonischer Form, sortiert nach RDATA und ohne Doppelungen.
    /// </summary>
    public static byte[] SignedData(RrSig sig, DnsName owner, IReadOnlyList<DnsRecord> rrset)
    {
        var name = SignedOwner(owner, sig.Labels).ToWire(canonical: true);

        var entries = rrset
            .Select(r => CanonicalRdata(r.Type, r.RData))
            .Distinct(ByteArrayComparer.Instance)
            .OrderBy(r => r, ByteArrayComparer.Instance)
            .ToList();

        var stream = new MemoryStream();
        stream.Write(sig.RdataWithoutSignature());

        Span<byte> header = stackalloc byte[10];

        foreach (var rdata in entries)
        {
            stream.Write(name);

            BinaryPrimitives.WriteUInt16BigEndian(header, rrset[0].Type);
            BinaryPrimitives.WriteUInt16BigEndian(header[2..], rrset[0].Class);
            BinaryPrimitives.WriteUInt32BigEndian(header[4..], sig.OriginalTtl);
            BinaryPrimitives.WriteUInt16BigEndian(header[8..], (ushort)rdata.Length);
            stream.Write(header);

            stream.Write(rdata);
        }

        return stream.ToArray();
    }

    /// <summary>Bytefolgen vorzeichenlos vergleichen – so verlangt es §6.3.</summary>
    private sealed class ByteArrayComparer : IComparer<byte[]>, IEqualityComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();

        public int Compare(byte[]? x, byte[]? y)
            => x is null || y is null ? 0 : x.AsSpan().SequenceCompareTo(y);

        public bool Equals(byte[]? x, byte[]? y)
            => x is not null && y is not null && x.AsSpan().SequenceEqual(y);

        public int GetHashCode(byte[] obj)
        {
            var hash = new HashCode();
            hash.AddBytes(obj);
            return hash.ToHashCode();
        }
    }
}
