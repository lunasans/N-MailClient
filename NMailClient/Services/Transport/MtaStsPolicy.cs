namespace NMailClient.Services.Transport;

public enum StsMode { None, Testing, Enforce }

/// <summary>
/// MTA-STS-Richtlinie einer Domain (RFC 8461).
///
/// Die Domain hinterlegt unter <c>https://mta-sts.&lt;domain&gt;/.well-known/mta-sts.txt</c>,
/// welche MX-Namen gültig sind und ob TLS verpflichtend ist. Für uns als Client ist
/// das eine <em>Aussage der Empfängerdomain über sich selbst</em>: „so muss man mich
/// erreichen". Wir erzwingen damit nichts — der eigene Server versendet ja —, aber es
/// sagt dem Verfasser, wie ernst die Gegenseite Transportverschlüsselung nimmt.
/// </summary>
public record MtaStsPolicy(StsMode Mode, IReadOnlyList<string> Mx, int MaxAgeSeconds)
{
    public static readonly MtaStsPolicy None = new(StsMode.None, [], 0);

    public bool IsEnforced => Mode == StsMode.Enforce;

    public string Display => Mode switch
    {
        StsMode.Enforce => "MTA-STS: verbindlich",
        StsMode.Testing => "MTA-STS: nur Testbetrieb",
        _ => "kein MTA-STS",
    };

    /// <summary>
    /// Richtlinientext auswerten. Liefert <see cref="None"/>, wenn der Text keine
    /// gültige STSv1-Richtlinie ist — eine kaputte Datei ist kein Schutz, und sie
    /// als solchen anzuzeigen wäre die gefährlichere Lüge.
    /// </summary>
    public static MtaStsPolicy Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return None;

        var mode = StsMode.None;
        var mx = new List<string>();
        var maxAge = 0;
        var versionSeen = false;

        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var sep = line.IndexOf(':');
            if (sep <= 0) continue;

            var key = line[..sep].Trim().ToLowerInvariant();
            var value = line[(sep + 1)..].Trim();

            switch (key)
            {
                case "version":
                    versionSeen = value.Equals("STSv1", StringComparison.OrdinalIgnoreCase);
                    break;

                case "mode":
                    mode = value.ToLowerInvariant() switch
                    {
                        "enforce" => StsMode.Enforce,
                        "testing" => StsMode.Testing,
                        _ => StsMode.None,
                    };
                    break;

                // "mx" darf mehrfach vorkommen, je Zeile ein Muster.
                case "mx" when value.Length > 0:
                    mx.Add(value.ToLowerInvariant());
                    break;

                case "max_age":
                    _ = int.TryParse(value, out maxAge);
                    break;
            }
        }

        // Ohne die Versionszeile ist es keine Richtlinie, egal was sonst drinsteht.
        return versionSeen ? new MtaStsPolicy(mode, mx, maxAge) : None;
    }

    /// <summary>
    /// Passt der MX-Name zu einem der Muster? Erlaubt ist genau eine Form von
    /// Platzhalter: <c>*.example.org</c>, und der deckt nur <em>eine</em> Ebene ab
    /// (RFC 8461 §4.1) — <c>*.example.org</c> passt auf <c>mx.example.org</c>,
    /// nicht auf <c>a.b.example.org</c>.
    /// </summary>
    public bool CoversHost(string host)
    {
        if (Mx.Count == 0) return false;

        var name = host.TrimEnd('.').ToLowerInvariant();

        foreach (var pattern in Mx)
        {
            if (!pattern.StartsWith("*."))
            {
                if (name == pattern) return true;
                continue;
            }

            var suffix = pattern[1..];                  // ".example.org"
            if (!name.EndsWith(suffix, StringComparison.Ordinal)) continue;

            var label = name[..^suffix.Length];
            if (label.Length > 0 && !label.Contains('.')) return true;
        }

        return false;
    }
}
