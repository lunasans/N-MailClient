using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using NMailClient.Services.Dnssec;

namespace NMailClient.Services.Transport;

public enum TransportGrade { Unknown, Bad, Weak, Good }

/// <summary>Was sich über den Transportweg zu einer Empfängerdomain sagen lässt.</summary>
public record DomainSecurity(
    string Domain,
    string? MxHost,
    bool StartTls,
    string? TlsVersion,
    bool CertificateTrusted,
    MtaStsPolicy Sts,
    string? Error,
    DaneResult Dane,
    DnssecStatus MxDnssec = DnssecStatus.Indeterminate)
{
    public static DomainSecurity Unknown(string domain, string? error = null)
        => new(domain, null, false, null, false, MtaStsPolicy.None, error, DaneResult.Unknown);

    /// <summary>
    /// Die Bewertung ist bewusst streng: ein nicht vertrauenswürdiges Zertifikat
    /// zählt nicht als „gut", auch wenn verschlüsselt wird — gegen einen
    /// Zwischenmann hilft die Verschlüsselung dann nämlich nicht.
    /// </summary>
    public TransportGrade Grade
    {
        get
        {
            if (Error is not null && MxHost is null) return TransportGrade.Unknown;

            // Ein DANE-Widerspruch ist kein Schönheitsfehler, sondern der Hinweis
            // auf ein falsches Zertifikat – der wiegt schwerer als alles andere.
            if (Dane.Status is DaneStatus.Mismatch or DaneStatus.Bogus) return TransportGrade.Bad;

            if (!StartTls) return TransportGrade.Bad;

            // Bei bestätigtem DANE braucht es keine Zertifizierungsstelle: der
            // DNS-Eintrag benennt das Zertifikat ja unmittelbar.
            if (Dane.Status == DaneStatus.Valid) return TransportGrade.Good;

            return CertificateTrusted ? TransportGrade.Good : TransportGrade.Weak;
        }
    }

    public bool IsWarning => Grade is TransportGrade.Bad or TransportGrade.Weak;

    public string Summary
    {
        get
        {
            if (Grade == TransportGrade.Unknown)
                return $"{Domain}: nicht prüfbar{(Error is null ? "" : $" ({Error})")}";

            var parts = new List<string>
            {
                StartTls
                    ? $"STARTTLS{(TlsVersion is null ? "" : $" ({TlsVersion})")}"
                    : "kein STARTTLS — unverschlüsselt zustellbar",
            };

            // Bei bestätigtem DANE ist die fehlende CA-Kette unerheblich und
            // würde nur verwirren.
            if (StartTls && !CertificateTrusted && Dane.Status != DaneStatus.Valid)
                parts.Add("Zertifikat nicht vertrauenswürdig");

            if (Dane.Display is { } dane) parts.Add(dane);
            if (Sts.Mode != StsMode.None) parts.Add(Sts.Display);

            return $"{Domain}: {string.Join(", ", parts)}";
        }
    }
}

/// <summary>
/// Prüft, wie gut eine Empfängerdomain per SMTP erreichbar ist: MX nachschlagen,
/// STARTTLS anbieten lassen, Handshake machen, dazu die MTA-STS-Richtlinie holen.
///
/// Bewusst zurückhaltend: <b>eine</b> Verbindung je Domain zum besten MX, kurze
/// Fristen, Ergebnisse zwischengespeichert. Die Sonde meldet sich mit EHLO und
/// legt sofort wieder auf — es wird nie eine Mail übergeben und nie eine
/// Anmeldung versucht.
/// </summary>
public class RecipientSecurity
{
    private static readonly HttpClient Http = HttpFactory.Create(TimeSpan.FromSeconds(4));

    /// <summary>Obergrenze je Domain – der Verfasser soll nicht warten müssen.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(8);

    private readonly ConcurrentDictionary<string, DomainSecurity> _cache = new();
    private readonly DnssecValidator _dnssec = new();
    private readonly Dane _dane;

    public RecipientSecurity() => _dane = new Dane(_dnssec);

    /// <summary>Bereits geprüftes Ergebnis, ohne neue Verbindung. Null heisst: noch nichts.</summary>
    public DomainSecurity? Cached(string domain)
        => _cache.TryGetValue(Normalize(domain), out var v) ? v : null;

    private static string Normalize(string domain) => domain.Trim().TrimEnd('.').ToLowerInvariant();

    /// <summary>Domainteil einer Adresse; leer, wenn keine erkennbar ist.</summary>
    public static string DomainOf(string address)
    {
        var at = address.LastIndexOf('@');
        return at >= 0 && at < address.Length - 1 ? Normalize(address[(at + 1)..]) : "";
    }

    public async Task<DomainSecurity> InspectAsync(string domain, CancellationToken ct = default)
    {
        var key = Normalize(domain);
        if (key.Length == 0) return DomainSecurity.Unknown(domain, "keine Domain");
        if (_cache.TryGetValue(key, out var cached)) return cached;

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(Budget);

        DomainSecurity result;
        try
        {
            // Beides gleichzeitig: der HTTP-Abruf soll die SMTP-Sonde nicht aufhalten.
            var mxTask = LookupMxAsync(key, budget.Token);
            var stsTask = FetchPolicyAsync(key, budget.Token);

            var (mx, mxStatus) = await mxTask;
            if (mx is null)
            {
                result = DomainSecurity.Unknown(key, "kein MX-Eintrag");
            }
            else
            {
                var probe = await ProbeAsync(mx, budget.Token);

                // DANE nur, wenn der MX-Name selbst beglaubigt ist: bei
                // ungesicherter MX-Auflösung könnte uns jemand auf seinen eigenen
                // Server samt passendem TLSA-Eintrag lenken (RFC 7672 §2.2).
                var dane = mxStatus == DnssecStatus.Secure && probe.StartTls
                    ? await _dane.CheckAsync(mx, probe.Certificates, budget.Token)
                    : DaneResult.Unknown;

                result = new DomainSecurity(
                    key, mx, probe.StartTls, probe.TlsVersion, probe.CertificateTrusted,
                    await stsTask, probe.Error, dane, mxStatus);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result = DomainSecurity.Unknown(key, "Zeitüberschreitung");
        }
        catch (Exception ex) when (ex is SocketException or IOException or HttpRequestException)
        {
            result = DomainSecurity.Unknown(key, ex.Message);
        }

        _cache[key] = result;
        return result;
    }

    // ---- MX ----------------------------------------------------------------

    /// <summary>
    /// Den bevorzugten MX holen – über den validierenden Resolver, damit
    /// feststeht, ob der Name selbst beglaubigt ist. Ohne das wäre DANE
    /// wirkungslos: wer die MX-Antwort fälschen kann, benennt einfach seinen
    /// eigenen Server mit passendem TLSA-Eintrag.
    /// </summary>
    private async Task<(string? Host, DnssecStatus Status)> LookupMxAsync(
        string domain, CancellationToken ct)
    {
        try
        {
            var answer = await _dnssec.ResolveAsync(DnsName.Parse(domain), RrType.Mx, ct);

            var best = answer.Records
                .Select(r => ParseMx(r.RData))
                .OfType<(int Preference, string Host)>()
                .OrderBy(m => m.Preference)
                .FirstOrDefault();

            // "." als Ziel heisst laut RFC 7505 ausdrücklich: nimmt keine Mail an.
            return string.IsNullOrEmpty(best.Host) || best.Host == "."
                ? (null, answer.Status)
                : (best.Host, answer.Status);
        }
        catch (Exception ex) when (ex is FormatException or OperationCanceledException)
        {
            return (null, DnssecStatus.Indeterminate);
        }
    }

    /// <summary>MX-RDATA: zwei Bytes Präferenz, dann der Name.</summary>
    private static (int, string)? ParseMx(byte[] rdata)
    {
        if (rdata.Length < 3) return null;

        var name = DnsWire.NameFromWire(rdata.AsSpan(2));
        return name is null ? null : ((rdata[0] << 8) | rdata[1], name.ToDisplay());
    }

    // ---- SMTP-Sonde --------------------------------------------------------

    private record ProbeOutcome(bool StartTls, string? TlsVersion, bool CertificateTrusted,
                                string? Error, IReadOnlyList<X509Certificate2> Certificates)
    {
        public static ProbeOutcome Failed(string? error) => new(false, null, false, error, []);
    }

    private static async Task<ProbeOutcome> ProbeAsync(string host, CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, 25, ct);

            await using var net = tcp.GetStream();
            using var reader = new StreamReader(net, Encoding.ASCII, false, 1024, leaveOpen: true);
            await using var writer = new StreamWriter(net, Encoding.ASCII, 1024, leaveOpen: true)
            {
                AutoFlush = true, NewLine = "\r\n",
            };

            // Begrüssung abwarten; ohne 220 redet dort kein SMTP-Server mit uns.
            var greeting = await reader.ReadLineAsync(ct);
            if (greeting is null || !greeting.StartsWith("220", StringComparison.Ordinal))
                return ProbeOutcome.Failed("keine SMTP-Begrüssung");

            await writer.WriteLineAsync("EHLO localhost");
            var offersStartTls = await ReadEhloAsync(reader, ct);

            if (!offersStartTls)
            {
                await Quit(writer);
                return ProbeOutcome.Failed(null);
            }

            await writer.WriteLineAsync("STARTTLS");
            var ready = await reader.ReadLineAsync(ct);
            if (ready is null || !ready.StartsWith("220", StringComparison.Ordinal))
                return ProbeOutcome.Failed("STARTTLS abgelehnt");

            // Ab hier verschlüsselt. Die Zertifikatsprüfung wird bewusst nicht
            // abgeschaltet — ihr Ergebnis ist ja gerade die Aussage, die wir suchen.
            // Die vorgelegte Kette wird festgehalten: DANE gleicht dagegen ab.
            var trusted = true;
            var certificates = new List<X509Certificate2>();

            await using var ssl = new SslStream(net, leaveInnerStreamOpen: true,
                (_, certificate, chain, errors) =>
                {
                    trusted = errors == SslPolicyErrors.None;

                    if (certificate is not null)
                        certificates.Add(new X509Certificate2(certificate));

                    // Die Zwischenzertifikate braucht DANE für die Nutzungsart 2.
                    if (chain is not null)
                        foreach (var element in chain.ChainElements)
                            if (!certificates.Any(c => c.RawData.SequenceEqual(element.Certificate.RawData)))
                                certificates.Add(new X509Certificate2(element.Certificate));

                    return true;      // Handshake zu Ende führen, um die Version zu sehen
                });

            await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions { TargetHost = host }, ct);

            return new ProbeOutcome(true, Describe(ssl.SslProtocol), trusted, null, certificates);
        }
        catch (OperationCanceledException)
        {
            return ProbeOutcome.Failed("Zeitüberschreitung");
        }
        catch (Exception ex) when (ex is SocketException or IOException
                                      or AuthenticationException or InvalidOperationException)
        {
            return ProbeOutcome.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Die EHLO-Antwort ist mehrzeilig: „250-" setzt fort, „250 " schliesst ab.
    /// </summary>
    private static async Task<bool> ReadEhloAsync(StreamReader reader, CancellationToken ct)
    {
        var startTls = false;

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Contains("STARTTLS", StringComparison.OrdinalIgnoreCase)) startTls = true;
            if (line.Length < 4 || line[3] != '-') break;
        }

        return startTls;
    }

    private static async Task Quit(StreamWriter writer)
    {
        try { await writer.WriteLineAsync("QUIT"); }
        catch (IOException) { /* Verbindung schon zu – für uns folgenlos */ }
    }

    /// <summary>Lesbare Fassung; veraltete Versionen werden benannt, nicht beschönigt.</summary>
    public static string Describe(SslProtocols protocol) => protocol switch
    {
        SslProtocols.Tls13 => "TLS 1.3",
        SslProtocols.Tls12 => "TLS 1.2",
#pragma warning disable SYSLIB0039 // veraltet – wir melden es ja gerade deshalb
        SslProtocols.Tls11 => "TLS 1.1 (veraltet)",
        SslProtocols.Tls => "TLS 1.0 (veraltet)",
#pragma warning restore SYSLIB0039
        _ => protocol.ToString(),
    };

    // ---- MTA-STS -----------------------------------------------------------

    private static async Task<MtaStsPolicy> FetchPolicyAsync(string domain, CancellationToken ct)
    {
        try
        {
            // Der TXT-Eintrag _mta-sts.<domain> sagt nur, dass es eine Richtlinie
            // gibt; verbindlich ist die Datei. Wir holen direkt die Datei — ein
            // fehlender Abruf bedeutet schlicht „kein MTA-STS".
            var url = $"https://mta-sts.{domain}/.well-known/mta-sts.txt";
            using var response = await Http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return MtaStsPolicy.None;

            return MtaStsPolicy.Parse(await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException
                                      or InvalidOperationException)
        {
            return MtaStsPolicy.None;
        }
    }
}
