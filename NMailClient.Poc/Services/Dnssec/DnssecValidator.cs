namespace NMailClient.Poc.Services.Dnssec;

/// <summary>Die vier Zustände aus RFC 4035 §4.3.</summary>
public enum DnssecStatus
{
    /// <summary>Lückenlose Signaturkette von der Wurzel bis zu diesen Daten.</summary>
    Secure,

    /// <summary>Nachweislich unsignierter Bereich – kein Fehler, nur kein Schutz.</summary>
    Insecure,

    /// <summary>Signaturen vorhanden, aber falsch: hier stimmt etwas nicht.</summary>
    Bogus,

    /// <summary>Nicht feststellbar (kein Resolver erreichbar, unbekanntes Verfahren).</summary>
    Indeterminate,
}

public sealed record SecureAnswer(
    DnssecStatus Status, IReadOnlyList<DnsRecord> Records, string? Reason = null)
{
    public bool IsSecure => Status == DnssecStatus.Secure;
}

/// <summary>
/// Ein validierender Stub-Resolver: prüft die Signaturkette selbst, ab dem
/// Vertrauensanker der Wurzelzone.
///
/// Bewusst <b>nicht</b> auf das AD-Bit eines fremden Resolvers gestützt. Das Bit
/// sagt nur „irgendjemand hat geprüft" und ist auf dem Weg zu uns beliebig
/// setzbar; hier wird jede Signatur selbst nachgerechnet.
/// </summary>
public class DnssecValidator(DnsResolver? resolver = null)
{
    private readonly DnsResolver _dns = resolver ?? new DnsResolver();

    /// <summary>
    /// Die Vertrauensanker der Wurzelzone, wie die IANA sie veröffentlicht.
    /// Beide aktuellen Schlüssel sind hinterlegt, damit ein Rollenwechsel die
    /// Prüfung nicht von einem Tag auf den anderen scheitern lässt.
    /// </summary>
    private static readonly DsRecord[] RootAnchors =
    [
        new(20326, DnssecAlgorithm.RsaSha256, 2,
            Convert.FromHexString("E06D44B80B8F1D39A95C0B0D7C65D08458E880409BBC683457104237C7F8EC8D")),
        new(38696, DnssecAlgorithm.RsaSha256, 2,
            Convert.FromHexString("683D2D0ACB8C9B712A1948B27F741219298D0A450D612C483AF444A4C0FB2B16")),
    ];

    private readonly Dictionary<string, IReadOnlyList<DnsKey>?> _keyCache = [];

    /// <summary>Der Stand der Kette an der tiefsten erreichten Zone.</summary>
    private sealed record Chain(
        DnsName Zone, IReadOnlyList<DnsKey> Keys, DnssecStatus Status, string? Reason);

    // ---- Öffentlich --------------------------------------------------------

    /// <summary>
    /// Einträge holen und die Signaturkette bis zur Wurzel prüfen. Bei
    /// <see cref="DnssecStatus.Insecure"/> werden die Daten mitgegeben — sie sind
    /// nicht falsch, nur ungeschützt; der Aufrufer entscheidet, was er damit tut.
    /// </summary>
    public async Task<SecureAnswer> ResolveAsync(DnsName name, ushort type, CancellationToken ct)
    {
        // DS-Einträge stehen in der übergeordneten Zone und werden von dieser
        // signiert – die Kette darf also nicht bis zum Namen selbst laufen,
        // sonst prüfte man mit den Schlüsseln der falschen Zone.
        var signingZone = type == RrType.Ds ? name.Parent() : name;

        var chain = await WalkAsync(signingZone, ct);
        if (chain.Status != DnssecStatus.Secure)
            return new SecureAnswer(chain.Status, await UnvalidatedAsync(name, type, ct), chain.Reason);

        var response = await _dns.QueryAsync(name, type, ct);
        if (response is null)
            return new SecureAnswer(DnssecStatus.Indeterminate, [], "kein Resolver erreichbar");

        var records = response.Answers
            .Where(r => r.Type == type && r.Name.Equals(name))
            .ToList();

        if (records.Count == 0)
        {
            // Nichts da — das muss belegt sein, sonst hat womöglich jemand die
            // Einträge unterschlagen.
            var denial = NsecProof.Check(response, name, type, chain.Keys, chain.Zone);

            return denial switch
            {
                DenialResult.ProvenNoData or DenialResult.ProvenNxDomain =>
                    new SecureAnswer(DnssecStatus.Secure, []),
                DenialResult.ProvenInsecureDelegation =>
                    new SecureAnswer(DnssecStatus.Insecure, [], "unsignierte Delegation"),
                _ => new SecureAnswer(DnssecStatus.Bogus, [],
                        "Abwesenheit der Einträge ist nicht belegt"),
            };
        }

        return VerifyRrset(response.Answers, name, type, chain.Keys)
            ? new SecureAnswer(DnssecStatus.Secure, records)
            : new SecureAnswer(DnssecStatus.Bogus, records, "keine gültige Signatur für die Daten");
    }

    private async Task<IReadOnlyList<DnsRecord>> UnvalidatedAsync(
        DnsName name, ushort type, CancellationToken ct)
    {
        var response = await _dns.QueryAsync(name, type, ct);
        return response?.Answers.Where(r => r.Type == type && r.Name.Equals(name)).ToList() ?? [];
    }

    // ---- Kette -------------------------------------------------------------

    /// <summary>
    /// Von der Wurzel abwärts Zonenschnitt für Zonenschnitt. An jedem Label wird
    /// nach einem DS gefragt:
    /// <list type="bullet">
    /// <item>DS vorhanden → geprüfter Zonenschnitt, weiter mit den Kindschlüsseln.</item>
    /// <item>DS belegt abwesend <em>und</em> der Name ist eine Delegation → ab hier unsigniert.</item>
    /// <item>DS belegt abwesend, aber keine Delegation → gar kein Zonenschnitt, weiter wie bisher.</item>
    /// <item>Abwesenheit unbelegt → jemand hat etwas entfernt.</item>
    /// </list>
    /// </summary>
    private async Task<Chain> WalkAsync(DnsName name, CancellationToken ct)
    {
        var keys = await KeysForZoneAsync(DnsName.Root, RootAnchors, ct);
        if (keys is null)
            return new Chain(DnsName.Root, [], DnssecStatus.Indeterminate,
                             "Wurzelschlüssel nicht überprüfbar");

        var zone = DnsName.Root;

        foreach (var candidate in name.FromRootDown().Skip(1))
        {
            var response = await _dns.QueryAsync(candidate, RrType.Ds, ct);
            if (response is null)
                return new Chain(zone, keys, DnssecStatus.Indeterminate, "kein Resolver erreichbar");

            var dsRecords = response.Answers
                .Where(r => r.Type == RrType.Ds && r.Name.Equals(candidate))
                .ToList();

            if (dsRecords.Count == 0)
            {
                var denial = NsecProof.Check(response, candidate, RrType.Ds, keys, zone);

                if (denial == DenialResult.ProvenInsecureDelegation)
                    return new Chain(zone, keys, DnssecStatus.Insecure,
                                     $"unsignierte Delegation ab {candidate.ToDisplay()}");

                if (denial is DenialResult.ProvenNoData or DenialResult.ProvenNxDomain)
                    continue;   // hier ist schlicht kein Zonenschnitt

                return new Chain(zone, keys, DnssecStatus.Bogus,
                                 $"fehlender DS für {candidate.ToDisplay()} ist nicht belegt");
            }

            if (!VerifyRrset(response.Answers, candidate, RrType.Ds, keys))
                return new Chain(zone, keys, DnssecStatus.Bogus,
                                 $"DS für {candidate.ToDisplay()} nicht gültig signiert");

            var anchors = dsRecords.Select(r => DsRecord.Parse(r.RData)).OfType<DsRecord>().ToList();

            // Signiert die Zone ausschliesslich mit Verfahren, die wir nicht
            // anerkennen (in der Praxis: SHA-1), dann ist das kein Angriff,
            // sondern schlicht nicht nachrechenbar. „Bogus" wäre hier ein
            // Fehlalarm.
            if (anchors.Count > 0 && !anchors.Any(a => DnssecAlgorithm.IsSupported(a.Algorithm)))
            {
                var names = string.Join(", ",
                    anchors.Select(a => DnssecAlgorithm.Name(a.Algorithm)).Distinct());

                return new Chain(zone, keys, DnssecStatus.Indeterminate,
                                 $"{candidate.ToDisplay()} signiert mit {names} – nicht anerkannt");
            }

            var childKeys = await KeysForZoneAsync(candidate, anchors, ct);

            if (childKeys is null)
                return new Chain(zone, keys, DnssecStatus.Bogus,
                                 $"Schlüssel von {candidate.ToDisplay()} passen nicht zum DS");

            keys = childKeys;
            zone = candidate;
        }

        return new Chain(zone, keys, DnssecStatus.Secure, null);
    }

    /// <summary>
    /// Die Schlüsselmenge einer Zone holen und gegen die übergebenen DS-Anker
    /// prüfen. Erst wenn ein Schlüssel, auf den ein Anker zeigt, die ganze Menge
    /// signiert hat, gelten alle Schlüssel darin.
    /// </summary>
    private async Task<IReadOnlyList<DnsKey>?> KeysForZoneAsync(
        DnsName zone, IReadOnlyList<DsRecord> anchors, CancellationToken ct)
    {
        var cacheKey = zone.ToString();
        if (_keyCache.TryGetValue(cacheKey, out var cached)) return cached;

        var keys = await LoadKeysAsync(zone, anchors, ct);
        _keyCache[cacheKey] = keys;
        return keys;
    }

    private async Task<IReadOnlyList<DnsKey>?> LoadKeysAsync(
        DnsName zone, IReadOnlyList<DsRecord> anchors, CancellationToken ct)
    {
        var response = await _dns.QueryAsync(zone, RrType.DnsKey, ct);
        if (response is null) return null;

        var keyRecords = response.Answers
            .Where(r => r.Type == RrType.DnsKey && r.Name.Equals(zone))
            .ToList();
        if (keyRecords.Count == 0) return null;

        var keys = keyRecords.Select(r => DnsKey.Parse(r.RData)).OfType<DnsKey>().ToList();

        foreach (var sig in SignaturesFor(response.Answers, zone, RrType.DnsKey))
        {
            var data = DnsCanonical.SignedData(sig, zone, keyRecords);

            foreach (var key in keys.Where(k => k.IsZoneKey && k.KeyTag() == sig.KeyTag))
            {
                if (!anchors.Any(a => a.Matches(zone, key))) continue;
                if (DnssecCrypto.Verify(key, sig, data)) return keys;
            }
        }

        return null;
    }

    // ---- Signaturprüfung einer Menge ---------------------------------------

    private static IEnumerable<RrSig> SignaturesFor(
        IReadOnlyList<DnsRecord> section, DnsName owner, ushort type)
        => section
            .Where(r => r.Type == RrType.RrSig && r.Name.Equals(owner))
            .Select(r => RrSig.Parse(r.RData))
            .OfType<RrSig>()
            .Where(s => s.TypeCovered == type
                        && s.IsCurrent(DateTimeOffset.UtcNow)
                        && DnssecAlgorithm.IsSupported(s.Algorithm)
                        && owner.IsSubdomainOf(s.Signer));

    private static bool VerifyRrset(
        IReadOnlyList<DnsRecord> section, DnsName owner, ushort type, IReadOnlyList<DnsKey> keys)
    {
        var rrset = section.Where(r => r.Type == type && r.Name.Equals(owner)).ToList();
        if (rrset.Count == 0) return false;

        foreach (var sig in SignaturesFor(section, owner, type))
        {
            var data = DnsCanonical.SignedData(sig, owner, rrset);
            if (keys.Any(k => k.KeyTag() == sig.KeyTag && DnssecCrypto.Verify(k, sig, data)))
                return true;
        }

        return false;
    }
}
