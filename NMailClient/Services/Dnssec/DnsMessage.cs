using System.Buffers.Binary;

namespace NMailClient.Services.Dnssec;

/// <summary>Die für uns interessanten Record-Typen.</summary>
public static class RrType
{
    public const ushort A = 1;
    public const ushort Ns = 2;
    public const ushort CName = 5;
    public const ushort Soa = 6;
    public const ushort Ptr = 12;
    public const ushort Mx = 15;
    public const ushort Txt = 16;
    public const ushort Aaaa = 28;
    public const ushort Srv = 33;
    public const ushort Ds = 43;
    public const ushort RrSig = 46;
    public const ushort Nsec = 47;
    public const ushort DnsKey = 48;
    public const ushort Nsec3 = 50;
    public const ushort Tlsa = 52;

    public static string Name(ushort type) => type switch
    {
        A => "A", Ns => "NS", CName => "CNAME", Soa => "SOA", Ptr => "PTR",
        Mx => "MX", Txt => "TXT", Aaaa => "AAAA", Srv => "SRV", Ds => "DS",
        RrSig => "RRSIG", Nsec => "NSEC", DnsKey => "DNSKEY", Nsec3 => "NSEC3",
        Tlsa => "TLSA", _ => $"TYPE{type}",
    };
}

/// <summary>
/// Ein Eintrag mit <b>rohen</b> RDATA-Bytes. Enthaltene Namen sind bereits
/// dekomprimiert – komprimierte Zeiger sind nur innerhalb einer Nachricht gültig
/// und in der kanonischen Form verboten.
/// </summary>
public sealed record DnsRecord(DnsName Name, ushort Type, ushort Class, uint Ttl, byte[] RData);

public enum DnsRcode { NoError = 0, FormErr = 1, ServFail = 2, NxDomain = 3, NotImp = 4, Refused = 5 }

public sealed class DnsMessage
{
    public ushort Id { get; init; }
    public DnsRcode Rcode { get; init; }
    public bool Truncated { get; init; }
    public bool AuthenticData { get; init; }
    public IReadOnlyList<DnsRecord> Answers { get; init; } = [];
    public IReadOnlyList<DnsRecord> Authority { get; init; } = [];
    public IReadOnlyList<DnsRecord> Additional { get; init; } = [];

    /// <summary>Alle Einträge eines Typs aus Antwort- und Autoritätsteil.</summary>
    public IEnumerable<DnsRecord> OfType(ushort type)
        => Answers.Concat(Authority).Where(r => r.Type == type);

    // ---- Bauen -------------------------------------------------------------

    /// <summary>
    /// Anfrage mit EDNS0 und gesetztem DO-Bit (wir wollen die Signaturen) sowie
    /// gesetztem CD-Bit: der Resolver soll <em>nicht</em> selbst validieren und
    /// nichts wegfiltern – wir prüfen ja selbst und brauchen auch die Rohdaten
    /// eines Zustands, den er als ungültig verwerfen würde.
    /// </summary>
    public static byte[] BuildQuery(DnsName name, ushort type, ushort id, int udpBufferSize = 4096)
    {
        var qname = name.ToWire();
        var message = new byte[12 + qname.Length + 4 + 11];
        var span = message.AsSpan();

        BinaryPrimitives.WriteUInt16BigEndian(span[..2], id);

        // RD = Rekursion erwünscht (0x0100), CD = Prüfung abgeschaltet (0x0010)
        BinaryPrimitives.WriteUInt16BigEndian(span[2..4], 0x0110);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..6], 1);      // eine Frage
        BinaryPrimitives.WriteUInt16BigEndian(span[10..12], 1);    // ein Zusatzeintrag (OPT)

        var pos = 12;
        qname.CopyTo(message, pos);
        pos += qname.Length;

        BinaryPrimitives.WriteUInt16BigEndian(span[pos..], type);
        pos += 2;
        BinaryPrimitives.WriteUInt16BigEndian(span[pos..], 1);     // IN
        pos += 2;

        // OPT-Pseudo-Record: leerer Name, Typ 41, Klasse = Puffergrösse,
        // im TTL-Feld das DO-Bit (0x8000 im oberen Wort).
        message[pos++] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(span[pos..], 41);
        pos += 2;
        BinaryPrimitives.WriteUInt16BigEndian(span[pos..], (ushort)udpBufferSize);
        pos += 2;
        BinaryPrimitives.WriteUInt32BigEndian(span[pos..], 0x00008000);
        pos += 4;
        BinaryPrimitives.WriteUInt16BigEndian(span[pos..], 0);     // keine Optionen

        return message;
    }

    // ---- Lesen -------------------------------------------------------------

    public static DnsMessage Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12) throw new FormatException("DNS-Antwort zu kurz.");

        var flags = BinaryPrimitives.ReadUInt16BigEndian(data[2..4]);
        var counts = new int[4];
        for (var i = 0; i < 4; i++)
            counts[i] = BinaryPrimitives.ReadUInt16BigEndian(data[(4 + i * 2)..]);

        var pos = 12;

        // Fragen überspringen – wir kennen sie ja.
        for (var i = 0; i < counts[0]; i++)
        {
            ReadName(data, ref pos);
            pos += 4;
        }

        var answers = ReadRecords(data, ref pos, counts[1]);
        var authority = ReadRecords(data, ref pos, counts[2]);
        var additional = ReadRecords(data, ref pos, counts[3]);

        return new DnsMessage
        {
            Id = BinaryPrimitives.ReadUInt16BigEndian(data[..2]),
            Rcode = (DnsRcode)(flags & 0x000F),
            Truncated = (flags & 0x0200) != 0,
            AuthenticData = (flags & 0x0020) != 0,
            Answers = answers,
            Authority = authority,
            Additional = additional,
        };
    }

    private static List<DnsRecord> ReadRecords(ReadOnlySpan<byte> data, ref int pos, int count)
    {
        var list = new List<DnsRecord>(count);

        for (var i = 0; i < count; i++)
        {
            if (pos >= data.Length) break;

            var name = ReadName(data, ref pos);
            if (pos + 10 > data.Length) break;

            var type = BinaryPrimitives.ReadUInt16BigEndian(data[pos..]);
            var klass = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 2)..]);
            var ttl = BinaryPrimitives.ReadUInt32BigEndian(data[(pos + 4)..]);
            var length = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 8)..]);
            pos += 10;

            if (pos + length > data.Length) break;
            var rdata = ExpandRData(data, pos, length, type);
            pos += length;

            // OPT (41) ist ein Pseudo-Record und gehört nicht in die Datenmenge.
            if (type != 41) list.Add(new DnsRecord(name, type, klass, ttl, rdata));
        }

        return list;
    }

    /// <summary>
    /// RDATA übernehmen und dabei enthaltene Namen ausschreiben. Nur die Typen,
    /// die überhaupt Namen enthalten können, werden angefasst – alles andere wird
    /// unverändert kopiert, damit unbekannte Typen nicht beschädigt werden.
    /// </summary>
    private static byte[] ExpandRData(ReadOnlySpan<byte> data, int start, int length, ushort type)
    {
        switch (type)
        {
            case RrType.Ns:
            case RrType.CName:
            case RrType.Ptr:
            {
                var pos = start;
                return ReadName(data, ref pos).ToWire();
            }

            case RrType.Mx:
            {
                var pos = start + 2;
                var exchange = ReadName(data, ref pos).ToWire();
                var result = new byte[2 + exchange.Length];
                data.Slice(start, 2).CopyTo(result);
                exchange.CopyTo(result, 2);
                return result;
            }

            case RrType.Srv:
            {
                var pos = start + 6;
                var target = ReadName(data, ref pos).ToWire();
                var result = new byte[6 + target.Length];
                data.Slice(start, 6).CopyTo(result);
                target.CopyTo(result, 6);
                return result;
            }

            case RrType.Soa:
            {
                var pos = start;
                var mname = ReadName(data, ref pos).ToWire();
                var rname = ReadName(data, ref pos).ToWire();
                var rest = data.Slice(pos, start + length - pos);

                var result = new byte[mname.Length + rname.Length + rest.Length];
                mname.CopyTo(result, 0);
                rname.CopyTo(result, mname.Length);
                rest.CopyTo(result.AsSpan(mname.Length + rname.Length));
                return result;
            }

            case RrType.RrSig:
            {
                // 18 feste Bytes, dann der Name des Unterzeichners, dann die Signatur.
                var pos = start + 18;
                var signer = ReadName(data, ref pos).ToWire();
                var signature = data.Slice(pos, start + length - pos);

                var result = new byte[18 + signer.Length + signature.Length];
                data.Slice(start, 18).CopyTo(result);
                signer.CopyTo(result, 18);
                signature.CopyTo(result.AsSpan(18 + signer.Length));
                return result;
            }

            case RrType.Nsec:
            {
                var pos = start;
                var next = ReadName(data, ref pos).ToWire();
                var bitmap = data.Slice(pos, start + length - pos);

                var result = new byte[next.Length + bitmap.Length];
                next.CopyTo(result, 0);
                bitmap.CopyTo(result.AsSpan(next.Length));
                return result;
            }

            default:
                return data.Slice(start, length).ToArray();
        }
    }

    /// <summary>
    /// Namen lesen und dabei Komprimierungszeiger auflösen. Die Sprungzahl ist
    /// begrenzt: eine Antwort mit einem Zeigerkreis darf uns nicht aufhängen.
    /// </summary>
    private static DnsName ReadName(ReadOnlySpan<byte> data, ref int pos)
    {
        var labels = new List<string>();
        var jumps = 0;
        var current = pos;
        var followed = false;

        while (true)
        {
            if (current >= data.Length) throw new FormatException("Name reicht über die Antwort hinaus.");

            var len = data[current];

            if (len == 0)
            {
                current++;
                if (!followed) pos = current;
                break;
            }

            if ((len & 0xC0) == 0xC0)
            {
                if (current + 1 >= data.Length) throw new FormatException("Abgeschnittener Zeiger.");
                if (++jumps > 16) throw new FormatException("Zu viele Komprimierungszeiger.");

                var target = ((len & 0x3F) << 8) | data[current + 1];
                if (!followed) pos = current + 2;
                followed = true;
                current = target;
                continue;
            }

            if ((len & 0xC0) != 0) throw new FormatException("Unbekannte Label-Art.");

            current++;
            if (current + len > data.Length) throw new FormatException("Label reicht über die Antwort hinaus.");

            labels.Add(System.Text.Encoding.ASCII.GetString(data.Slice(current, len)));
            current += len;
        }

        return labels.Count == 0 ? DnsName.Root : DnsName.Parse(string.Join('.', labels));
    }
}
