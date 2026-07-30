using System.Text;

namespace NMailClient.Poc.Services.Dnssec;

/// <summary>
/// Ein Domainname als Folge von Labels.
///
/// Eigene Klasse statt <c>string</c>, weil DNSSEC drei Dinge byte-genau braucht,
/// die eine Zeichenkette nicht hergibt: die unkomprimierte Wire-Darstellung, die
/// Kleinschreibung für die kanonische Form (RFC 4034 §6.2) und die kanonische
/// Sortierung nach Labels von rechts nach links (§6.1).
/// </summary>
public sealed class DnsName : IEquatable<DnsName>, IComparable<DnsName>
{
    /// <summary>Labels von links nach rechts, ohne die leere Wurzel.</summary>
    public IReadOnlyList<byte[]> Labels { get; }

    public static readonly DnsName Root = new([]);

    private DnsName(IReadOnlyList<byte[]> labels) => Labels = labels;

    public int LabelCount => Labels.Count;

    public bool IsRoot => Labels.Count == 0;

    /// <summary>
    /// Aus der Textform. Punkte trennen, ein abschliessender Punkt ist optional.
    /// Escapes (<c>\.</c>, <c>\123</c>) werden bewusst nicht unterstützt: in
    /// Mail-Domainnamen kommen sie nicht vor, und eine halbe Umsetzung wäre
    /// schlimmer als gar keine.
    /// </summary>
    public static DnsName Parse(string name)
    {
        if (string.IsNullOrEmpty(name) || name == ".") return Root;

        var text = name.TrimEnd('.');
        var labels = new List<byte[]>();

        foreach (var part in text.Split('.'))
        {
            if (part.Length is 0 or > 63)
                throw new FormatException($"Ungültiges Label in '{name}'.");
            labels.Add(Encoding.ASCII.GetBytes(part));
        }

        return new DnsName(labels);
    }

    /// <summary>Der übergeordnete Name; die Wurzel bleibt die Wurzel.</summary>
    public DnsName Parent() => IsRoot ? Root : new DnsName(Labels.Skip(1).ToList());

    /// <summary>Alle Namen von der Wurzel bis zu diesem, absteigend im Baum.</summary>
    public IEnumerable<DnsName> FromRootDown()
    {
        for (var i = Labels.Count; i >= 0; i--)
            yield return new DnsName(Labels.Skip(i).ToList());
    }

    /// <summary>Ist dieser Name gleich <paramref name="other"/> oder darunter?</summary>
    public bool IsSubdomainOf(DnsName other)
    {
        if (other.IsRoot) return true;
        if (Labels.Count < other.Labels.Count) return false;

        var offset = Labels.Count - other.Labels.Count;
        for (var i = 0; i < other.Labels.Count; i++)
            if (!LabelEquals(Labels[offset + i], other.Labels[i])) return false;

        return true;
    }

    /// <summary>
    /// Wire-Format: je Label ein Längenbyte, abgeschlossen durch eine Null.
    /// Niemals komprimiert – Komprimierung ist in der kanonischen Form verboten.
    /// </summary>
    public byte[] ToWire(bool canonical = false)
    {
        var size = Labels.Sum(l => l.Length + 1) + 1;
        var buffer = new byte[size];
        var pos = 0;

        foreach (var label in Labels)
        {
            buffer[pos++] = (byte)label.Length;
            foreach (var b in label)
                buffer[pos++] = canonical ? ToLower(b) : b;
        }

        buffer[pos] = 0;
        return buffer;
    }

    /// <summary>Nur ASCII-Grossbuchstaben werden verkleinert (RFC 4034 §6.2).</summary>
    private static byte ToLower(byte b) => b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b + 32) : b;

    public override string ToString()
        => IsRoot ? "." : string.Join('.', Labels.Select(l => Encoding.ASCII.GetString(l))) + ".";

    /// <summary>Ohne abschliessenden Punkt – für die Anzeige und für Vergleiche mit Hostnamen.</summary>
    public string ToDisplay()
        => IsRoot ? "." : string.Join('.', Labels.Select(l => Encoding.ASCII.GetString(l)));

    // ---- Vergleich ---------------------------------------------------------

    private static bool LabelEquals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
            if (ToLower(a[i]) != ToLower(b[i])) return false;
        return true;
    }

    public bool Equals(DnsName? other)
    {
        if (other is null || other.Labels.Count != Labels.Count) return false;
        for (var i = 0; i < Labels.Count; i++)
            if (!LabelEquals(Labels[i], other.Labels[i])) return false;
        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as DnsName);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var label in Labels)
            foreach (var b in label)
                hash.Add(ToLower(b));
        return hash.ToHashCode();
    }

    /// <summary>
    /// Kanonische Ordnung nach RFC 4034 §6.1: Labels von <em>rechts</em> nach
    /// links vergleichen, jedes Label als vorzeichenlose Bytes in Kleinschreibung.
    /// Ein Name mit weniger Labels steht vor einem, der ihn als Suffix hat.
    /// </summary>
    public int CompareTo(DnsName? other)
    {
        if (other is null) return 1;

        var a = Labels.Count - 1;
        var b = other.Labels.Count - 1;

        while (a >= 0 && b >= 0)
        {
            var cmp = CompareLabel(Labels[a], other.Labels[b]);
            if (cmp != 0) return cmp;
            a--;
            b--;
        }

        return Labels.Count.CompareTo(other.Labels.Count);
    }

    private static int CompareLabel(byte[] x, byte[] y)
    {
        var len = Math.Min(x.Length, y.Length);
        for (var i = 0; i < len; i++)
        {
            var cmp = ToLower(x[i]).CompareTo(ToLower(y[i]));
            if (cmp != 0) return cmp;
        }
        return x.Length.CompareTo(y.Length);
    }
}
