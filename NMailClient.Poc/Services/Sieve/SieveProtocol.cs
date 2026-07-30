using System.Text;

namespace NMailClient.Poc.Services.Sieve;

public enum SieveStatus { Ok, No, Bye, Unknown }

/// <summary>
/// Eine Serverantwort: <c>OK</c>, <c>NO</c> oder <c>BYE</c>, wahlweise mit
/// Antwortcode in Klammern und einer Meldung (RFC 5804 §1.2).
/// </summary>
public sealed record SieveResponse(SieveStatus Status, string? Code, string? Message)
{
    public bool IsOk => Status == SieveStatus.Ok;

    /// <summary>
    /// Bittet der Server ausdrücklich, es später erneut zu versuchen? Dann ist
    /// es keine dauerhafte Ablehnung, und die Anmeldesperre darf nicht greifen.
    /// </summary>
    public bool IsTryLater
        => Code is not null && Code.StartsWith("TRYLATER", StringComparison.OrdinalIgnoreCase);

    /// <summary>Die Meldung für den Anwender; fällt auf den Code zurück.</summary>
    public string Display => Message ?? Code ?? Status.ToString();

    /// <summary>
    /// Antwortzeile zerlegen. Der Code steht in runden Klammern und kann selbst
    /// Klammern enthalten (etwa <c>(QUOTA/MAXSCRIPTS)</c>), deshalb wird bis zur
    /// passenden schliessenden Klammer gezählt statt bis zur ersten.
    /// </summary>
    public static SieveResponse? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        var text = line.TrimEnd('\r', '\n');
        var status = FirstWord(text) switch
        {
            "OK" => SieveStatus.Ok,
            "NO" => SieveStatus.No,
            "BYE" => SieveStatus.Bye,
            _ => SieveStatus.Unknown,
        };

        if (status == SieveStatus.Unknown) return null;

        var rest = text[FirstWord(text).Length..].TrimStart();
        string? code = null;

        if (rest.StartsWith('('))
        {
            var depth = 0;
            var end = -1;

            for (var i = 0; i < rest.Length; i++)
            {
                if (rest[i] == '(') depth++;
                else if (rest[i] == ')' && --depth == 0) { end = i; break; }
            }

            if (end < 0) return new SieveResponse(status, null, null);

            code = rest[1..end];
            rest = rest[(end + 1)..].TrimStart();
        }

        var message = rest.Length == 0 ? null : SieveString.Unquote(rest);
        return new SieveResponse(status, code, message);
    }

    private static string FirstWord(string text)
    {
        var space = text.IndexOf(' ');
        return space < 0 ? text : text[..space];
    }
}

/// <summary>Ein Skript auf dem Server.</summary>
public sealed record SieveScript(string Name, bool IsActive)
{
    public string Display => IsActive ? $"{Name}  (aktiv)" : Name;

    /// <summary>
    /// Eine Zeile aus <c>LISTSCRIPTS</c>: der Name in Anführungszeichen, bei
    /// genau einem Skript zusätzlich <c>ACTIVE</c>.
    /// </summary>
    public static SieveScript? Parse(string line)
    {
        var text = line.TrimEnd('\r', '\n').Trim();
        if (text.Length == 0 || !text.StartsWith('"')) return null;

        var end = SieveString.FindClosingQuote(text);
        if (end < 0) return null;

        var name = SieveString.Unquote(text[..(end + 1)]);
        var active = text[(end + 1)..].Trim()
                         .Equals("ACTIVE", StringComparison.OrdinalIgnoreCase);

        return new SieveScript(name, active);
    }
}

/// <summary>Was der Server kann – aus der Begrüssung beziehungsweise CAPABILITY.</summary>
public sealed record SieveCapabilities(
    string? Implementation,
    IReadOnlyList<string> Sasl,
    IReadOnlyList<string> Extensions,
    bool StartTls,
    string? Version)
{
    public static readonly SieveCapabilities Empty =
        new(null, [], [], false, null);

    public bool Supports(string extension)
        => Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase);

    public bool SupportsSasl(string mechanism)
        => Sasl.Contains(mechanism, StringComparer.OrdinalIgnoreCase);

    /// <summary>Die Fähigkeitszeilen auswerten (RFC 5804 §1.7).</summary>
    public static SieveCapabilities Parse(IEnumerable<string> lines)
    {
        string? implementation = null, version = null;
        var sasl = new List<string>();
        var extensions = new List<string>();
        var startTls = false;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r', '\n').Trim();
            if (line.Length == 0) continue;

            var (name, value) = SplitCapability(line);

            switch (name.ToUpperInvariant())
            {
                case "IMPLEMENTATION": implementation = value; break;
                case "VERSION": version = value; break;
                case "STARTTLS": startTls = true; break;

                // Beide Listen sind mit Leerzeichen getrennt.
                case "SASL":
                    sasl.AddRange(Split(value));
                    break;
                case "SIEVE":
                    extensions.AddRange(Split(value));
                    break;
            }
        }

        return new SieveCapabilities(implementation, sasl, extensions, startTls, version);
    }

    private static IEnumerable<string> Split(string? value)
        => (value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Aus <c>"SASL" "PLAIN LOGIN"</c> wird (SASL, "PLAIN LOGIN").</summary>
    private static (string Name, string? Value) SplitCapability(string line)
    {
        if (!line.StartsWith('"')) return (line, null);

        var end = SieveString.FindClosingQuote(line);
        if (end < 0) return (line, null);

        var name = SieveString.Unquote(line[..(end + 1)]);
        var rest = line[(end + 1)..].Trim();

        return (name, rest.Length == 0 ? null : SieveString.Unquote(rest));
    }
}

/// <summary>
/// Zeichenketten im ManageSieve-Format.
///
/// Zwei Formen: in Anführungszeichen (mit <c>\"</c> und <c>\\</c> maskiert) oder
/// als Literal <c>{n+}</c> mit anschliessenden Rohbytes. Skripte gehen immer als
/// Literal — sie enthalten Zeilenumbrüche, und die sind in der Anführungsform
/// nicht zulässig.
/// </summary>
public static class SieveString
{
    /// <summary>Für kurze Werte ohne Zeilenumbruch: die Anführungsform.</summary>
    public static string Quote(string value)
        => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>
    /// Ein Literal samt Längenangabe. Gezählt werden <b>Bytes in UTF-8</b>, nicht
    /// Zeichen — bei Umlauten im Skript wäre die Zeichenzahl zu klein und der
    /// Server verlöre den Anschluss.
    /// </summary>
    public static string LiteralHeader(string value)
        => "{" + Encoding.UTF8.GetByteCount(value) + "+}";

    /// <summary>
    /// Maskierung wieder auflösen. Nicht in Anführungszeichen stehende Eingaben
    /// werden unverändert zurückgegeben.
    /// </summary>
    public static string Unquote(string value)
    {
        var text = value.Trim();
        if (text.Length < 2 || !text.StartsWith('"')) return text;

        var end = FindClosingQuote(text);
        if (end < 0) return text;

        var inner = text[1..end];
        var result = new StringBuilder(inner.Length);

        for (var i = 0; i < inner.Length; i++)
        {
            if (inner[i] == '\\' && i + 1 < inner.Length) i++;
            result.Append(inner[i]);
        }

        return result.ToString();
    }

    /// <summary>
    /// Das schliessende Anführungszeichen zum ersten Zeichen finden, maskierte
    /// überspringend. -1, wenn keines folgt.
    /// </summary>
    public static int FindClosingQuote(string text)
    {
        if (text.Length == 0 || text[0] != '"') return -1;

        for (var i = 1; i < text.Length; i++)
        {
            if (text[i] == '\\') { i++; continue; }
            if (text[i] == '"') return i;
        }

        return -1;
    }

    /// <summary>
    /// Beginnt die Zeile mit einer Literal-Ankündigung? Dann steht darin, wie
    /// viele Bytes danach folgen.
    /// </summary>
    public static int? LiteralLength(string line)
    {
        var text = line.TrimEnd('\r', '\n').Trim();
        if (!text.StartsWith('{') || !text.EndsWith('}')) return null;

        var inner = text[1..^1].TrimEnd('+');
        return int.TryParse(inner, out var length) && length >= 0 ? length : null;
    }

    /// <summary>Anmeldedaten für SASL PLAIN: NUL, Benutzer, NUL, Passwort.</summary>
    public static string PlainCredentials(string user, string password)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes($"\0{user}\0{password}"));
}
