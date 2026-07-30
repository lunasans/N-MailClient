using System.Buffers.Binary;
using NMailClient.Poc.Services.Dnssec;
using Xunit;

namespace NMailClient.Poc.Tests;

public class DnsNameTests
{
    [Fact]
    public void RootIsEmpty()
    {
        Assert.True(DnsName.Parse(".").IsRoot);
        Assert.True(DnsName.Parse("").IsRoot);
        Assert.Equal(".", DnsName.Root.ToString());
        Assert.Equal([0], DnsName.Root.ToWire());
    }

    [Fact]
    public void TrailingDotIsOptional()
        => Assert.Equal(DnsName.Parse("example.org"), DnsName.Parse("example.org."));

    [Fact]
    public void WireFormatIsLengthPrefixedAndNullTerminated()
    {
        // 3 'o' 'r' 'g' 0
        Assert.Equal([3, (byte)'o', (byte)'r', (byte)'g', 0], DnsName.Parse("org").ToWire());
    }

    [Fact]
    public void CanonicalWireFormatIsLowercased()
    {
        Assert.Equal(DnsName.Parse("example.org").ToWire(canonical: true),
                     DnsName.Parse("EXAMPLE.ORG").ToWire(canonical: true));

        // Ohne kanonische Form bleibt die Schreibung erhalten.
        Assert.NotEqual(DnsName.Parse("example.org").ToWire(),
                        DnsName.Parse("EXAMPLE.ORG").ToWire());
    }

    [Fact]
    public void ComparisonIgnoresCase()
    {
        Assert.Equal(DnsName.Parse("Example.ORG"), DnsName.Parse("example.org"));
        Assert.Equal(DnsName.Parse("Example.ORG").GetHashCode(),
                     DnsName.Parse("example.org").GetHashCode());
    }

    [Fact]
    public void ParentWalksUpAndStopsAtTheRoot()
    {
        var name = DnsName.Parse("mx.example.org");

        Assert.Equal(DnsName.Parse("example.org"), name.Parent());
        Assert.Equal(DnsName.Parse("org"), name.Parent().Parent());
        Assert.True(name.Parent().Parent().Parent().IsRoot);
        Assert.True(DnsName.Root.Parent().IsRoot);
    }

    [Fact]
    public void FromRootDownYieldsTheWholeChain()
    {
        var chain = DnsName.Parse("mx.example.org").FromRootDown()
                           .Select(n => n.ToString()).ToList();

        Assert.Equal([".", "org.", "example.org.", "mx.example.org."], chain);
    }

    [Theory]
    [InlineData("mx.example.org", "example.org", true)]
    [InlineData("example.org", "example.org", true)]
    [InlineData("example.org", "org", true)]
    [InlineData("example.org", ".", true)]
    [InlineData("org", "example.org", false)]
    [InlineData("notexample.org", "example.org", false)]
    public void SubdomainCheck(string child, string parent, bool expected)
        => Assert.Equal(expected, DnsName.Parse(child).IsSubdomainOf(DnsName.Parse(parent)));

    [Fact]
    public void CanonicalOrderFollowsRfc4034()
    {
        // Beispiel aus RFC 4034 §6.1, ohne die Escape-Formen (die wir bewusst
        // nicht unterstützen). Sortiert wird Label für Label von rechts.
        string[] expected =
        [
            "example",
            "a.example",
            "yljkjljk.a.example",
            "Z.a.example",
            "zABC.a.EXAMPLE",
            "z.example",
            "*.z.example",
        ];

        var shuffled = expected.Reverse().Select(DnsName.Parse).ToList();
        shuffled.Sort();

        Assert.Equal(expected.Select(e => DnsName.Parse(e).ToString()),
                     shuffled.Select(n => n.ToString()));
    }

    [Fact]
    public void ShorterNameSortsBeforeItsSubdomain()
        => Assert.True(DnsName.Parse("example.org").CompareTo(DnsName.Parse("a.example.org")) < 0);

    [Theory]
    [InlineData("a..b")]
    [InlineData("dieses-label-ist-mit-abstand-viel-zu-lang-und-sprengt-die-dreiundsechzig-zeichen.org")]
    public void MalformedNamesAreRejected(string name)
        => Assert.Throws<FormatException>(() => DnsName.Parse(name));
}

public class DnsMessageTests
{
    [Fact]
    public void QueryCarriesTheDoAndCdBits()
    {
        var query = DnsMessage.BuildQuery(DnsName.Parse("example.org"), RrType.Tlsa, 0x1234);

        Assert.Equal(0x1234, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(2 - 2)));

        var flags = BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(2));
        Assert.NotEqual(0, flags & 0x0100);   // RD: Rekursion erwünscht
        Assert.NotEqual(0, flags & 0x0010);   // CD: der Resolver soll nicht filtern

        // Das DO-Bit steht im TTL-Feld des OPT-Eintrags, ganz am Ende.
        var doBits = BinaryPrimitives.ReadUInt32BigEndian(query.AsSpan(query.Length - 6));
        Assert.NotEqual(0u, doBits & 0x8000);
    }

    [Fact]
    public void QueryAsksExactlyOneQuestionAndCarriesOneOptRecord()
    {
        var query = DnsMessage.BuildQuery(DnsName.Parse("org"), RrType.Ds, 1);

        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(4)));    // QDCOUNT
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(6)));    // ANCOUNT
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(10)));   // ARCOUNT
    }

    [Fact]
    public void OwnQueryParsesBackWithoutRecords()
    {
        // Der OPT-Eintrag ist ein Pseudo-Record und darf nicht als Datensatz auftauchen.
        var parsed = DnsMessage.Parse(DnsMessage.BuildQuery(DnsName.Parse("example.org"), RrType.A, 7));

        Assert.Equal(7, parsed.Id);
        Assert.Empty(parsed.Answers);
        Assert.Empty(parsed.Additional);
    }

    /// <summary>
    /// Baut eine Antwort mit einem komprimierten Namen im MX-RDATA und prüft,
    /// dass er ausgeschrieben zurückkommt – komprimierte Zeiger sind in der
    /// kanonischen Form verboten und würden jede Signaturprüfung zerstören.
    /// </summary>
    [Fact]
    public void CompressedNamesInRdataAreExpanded()
    {
        var message = new List<byte>();

        void U16(int v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); message.AddRange(b); }

        U16(0x0042);              // Id
        U16(0x8180);              // Antwort, keine Fehler
        U16(1); U16(1); U16(0); U16(0);

        // Frage: example.org. MX IN — der Name beginnt an Offset 12.
        message.AddRange(DnsName.Parse("example.org").ToWire());
        U16(RrType.Mx); U16(1);

        // Antwort: Name als Zeiger auf Offset 12
        message.Add(0xC0); message.Add(12);
        U16(RrType.Mx); U16(1);
        message.AddRange([0, 0, 0x0E, 0x10]);   // TTL 3600

        // RDATA: Präferenz 10, dann "mx" + Zeiger auf example.org (Offset 12)
        U16(2 + 3 + 2);
        U16(10);
        message.Add(2); message.Add((byte)'m'); message.Add((byte)'x');
        message.Add(0xC0); message.Add(12);

        var parsed = DnsMessage.Parse(message.ToArray());

        var mx = Assert.Single(parsed.Answers);
        Assert.Equal(DnsName.Parse("example.org"), mx.Name);
        Assert.Equal(3600u, mx.Ttl);

        // Erwartet: 2 Byte Präferenz + der vollständig ausgeschriebene Name.
        var expected = new List<byte> { 0, 10 };
        expected.AddRange(DnsName.Parse("mx.example.org").ToWire());
        Assert.Equal(expected, mx.RData);
    }

    [Fact]
    public void PointerLoopsAreRejectedInsteadOfHanging()
    {
        var message = new List<byte>();
        void U16(int v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); message.AddRange(b); }

        U16(1); U16(0x8180); U16(1); U16(0); U16(0); U16(0);

        // Ein Zeiger, der auf sich selbst zeigt.
        message.Add(0xC0); message.Add(12);

        Assert.Throws<FormatException>(() => DnsMessage.Parse(message.ToArray()));
    }

    [Fact]
    public void TooShortMessageIsRejected()
        => Assert.Throws<FormatException>(() => DnsMessage.Parse(new byte[5]));

    [Fact]
    public void RcodeIsReadFromTheFlags()
    {
        var message = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2), 0x8183);   // NXDomain

        Assert.Equal(DnsRcode.NxDomain, DnsMessage.Parse(message).Rcode);
    }

    [Fact]
    public void TruncationFlagIsRead()
    {
        var message = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2), 0x8380);

        Assert.True(DnsMessage.Parse(message).Truncated);
    }
}
