using System.Globalization;
using System.Reflection;

namespace NMailClient.Services.Update;

/// <summary>
/// Eine Programmversion nach dem Muster <c>MAJOR.MINOR.PATCH</c>, wahlweise mit
/// Vorabkennzeichnung (<c>0.7.0-beta.2</c>) und führendem <c>v</c>.
///
/// Eigener Typ statt <see cref="System.Version"/>, weil zwei Dinge zu klären
/// sind, die dort fehlen: Vorabversionen sind <em>kleiner</em> als die fertige
/// Fassung (SemVer §11), und der Zahlenvergleich muss numerisch laufen — als
/// Zeichenkette wäre 0.10.0 kleiner als 0.9.0.
/// </summary>
public readonly record struct AppVersion(int Major, int Minor, int Patch, string? PreRelease)
    : IComparable<AppVersion>
{
    public bool IsPreRelease => !string.IsNullOrEmpty(PreRelease);

    public static AppVersion? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var value = text.Trim();
        if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];

        // Baukennung (+build) spielt für die Rangfolge keine Rolle.
        var plus = value.IndexOf('+');
        if (plus >= 0) value = value[..plus];

        string? pre = null;
        var dash = value.IndexOf('-');
        if (dash >= 0)
        {
            pre = value[(dash + 1)..];
            value = value[..dash];
            if (pre.Length == 0) pre = null;
        }

        var parts = value.Split('.');
        if (parts.Length is 0 or > 4) return null;

        var numbers = new int[3];
        for (var i = 0; i < 3; i++)
        {
            if (i >= parts.Length) { numbers[i] = 0; continue; }

            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                return null;
            numbers[i] = n;
        }

        return new AppVersion(numbers[0], numbers[1], numbers[2], pre);
    }

    /// <summary>
    /// Die Version dieser Anwendung.
    ///
    /// Bewusst über <c>typeof(AppVersion).Assembly</c> und <b>nicht</b> über
    /// <c>Assembly.GetEntryAssembly()</c>: letzteres liefert die Wirt-Anwendung.
    /// Unter dem Test-Host oder einem Diagnosewerkzeug käme so dessen Version
    /// heraus — und ein Test dagegen prüfte nichts.
    /// </summary>
    public static AppVersion Current
    {
        get
        {
            var assembly = typeof(AppVersion).Assembly;

            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            return Parse(informational)
                   ?? Parse(assembly.GetName().Version?.ToString())
                   ?? new AppVersion(0, 0, 0, null);
        }
    }

    public int CompareTo(AppVersion other)
    {
        var cmp = Major.CompareTo(other.Major);
        if (cmp != 0) return cmp;

        cmp = Minor.CompareTo(other.Minor);
        if (cmp != 0) return cmp;

        cmp = Patch.CompareTo(other.Patch);
        if (cmp != 0) return cmp;

        // Gleiche Zahlen: eine Vorabversion steht vor der fertigen Fassung.
        if (IsPreRelease && !other.IsPreRelease) return -1;
        if (!IsPreRelease && other.IsPreRelease) return 1;
        if (!IsPreRelease && !other.IsPreRelease) return 0;

        return ComparePreRelease(PreRelease!, other.PreRelease!);
    }

    /// <summary>
    /// Vorabkennungen punktweise vergleichen: rein numerische Felder numerisch,
    /// alles andere als Text, und numerisch schlägt textuell (SemVer §11.4).
    /// </summary>
    private static int ComparePreRelease(string left, string right)
    {
        var a = left.Split('.');
        var b = right.Split('.');

        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            if (i >= a.Length) return -1;      // kürzere Kennung ist kleiner
            if (i >= b.Length) return 1;

            var aNumeric = int.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out var an);
            var bNumeric = int.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out var bn);

            if (aNumeric && bNumeric)
            {
                var cmp = an.CompareTo(bn);
                if (cmp != 0) return cmp;
                continue;
            }

            if (aNumeric != bNumeric) return aNumeric ? -1 : 1;

            var text = string.CompareOrdinal(a[i], b[i]);
            if (text != 0) return Math.Sign(text);
        }

        return 0;
    }

    public static bool operator <(AppVersion a, AppVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(AppVersion a, AppVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(AppVersion a, AppVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(AppVersion a, AppVersion b) => a.CompareTo(b) >= 0;

    public override string ToString()
        => IsPreRelease ? $"{Major}.{Minor}.{Patch}-{PreRelease}" : $"{Major}.{Minor}.{Patch}";
}
