using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Xml.Linq;
using DnsClient;

namespace NMailClient.Services;

/// <summary>
/// Ermittelt die Mailserver einer Domain. Portierung von internal/mail/autodiscover.go.
///
/// Der Port impliziert die erwartete Transportsicherheit:
///   IMAP 993 / SMTP 465 -> implizites TLS
///   IMAP 143 / SMTP 587 -> STARTTLS
/// </summary>
public record ProbeResult
{
    public string ImapHost { get; init; } = "";
    public int ImapPort { get; init; }
    public string SmtpHost { get; init; } = "";
    public int SmtpPort { get; init; }

    /// <summary>"autoconfig", "srv", "mx", "guess", "fallback" oder "" (ungültige Eingabe).</summary>
    public string Source { get; init; } = "";

    public bool IsComplete => ImapHost.Length > 0 && SmtpHost.Length > 0;

    public string SourceDisplay => Source switch
    {
        "autoconfig" => "Anbieter-Autoconfig",
        "srv" => "DNS-SRV-Einträge",
        "mx-autoconfig" => "Autoconfig des Mailservers laut MX",
        "mx" => "MX-Eintrag der Domain",
        "guess" => "geraten (per TLS geprüft)",
        "fallback" => "Standardnamen, ungeprüft",
        _ => "unbekannt",
    };

    /// <summary>True, wenn die Werte belastbar sind und nicht nur geraten wurden.</summary>
    public bool IsVerified
        => Source is "autoconfig" or "srv" or "mx-autoconfig" or "mx" or "guess";
}

public static class Autodiscover
{
    private static readonly HttpClient Http =
        Transport.HttpFactory.Create(TimeSpan.FromSeconds(5));

    /// <summary>Obergrenze für den gesamten Vorgang – der Nutzer wartet vor einem Dialog.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(12);

    /// <summary>
    /// Ergebnisse je Domain zwischenspeichern. Die Rate-Stufe kostet bis zu 4 s und
    /// öffnet TLS-Verbindungen zu imap./smtp./mail.&lt;domain&gt;; ohne Cache würde
    /// jede Aenderung im Adressfeld das komplett wiederholen.
    /// </summary>
    private static readonly Dictionary<string, ProbeResult> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim CacheGate = new(1, 1);

    /// <summary>
    /// Auflösung in dieser Reihenfolge:
    ///   1. Mozilla-Autoconfig (beim Anbieter gehostet + Thunderbird-ISPDB)
    ///   2. DNS-SRV-Einträge (RFC 6186)
    ///   3. Hostnamen raten, per TLS-Verbindung verifiziert
    ///
    /// Innerhalb jeder Stufe laufen die Abfragen parallel: seriell summieren sich
    /// die Timeouts unerreichbarer Hosts sonst auf über eine Minute.
    /// </summary>
    /// <param name="allowProbe">
    /// Gibt Stufe 3 frei. Diese verbindet sich testweise zu Port 993/465 und kostet
    /// bis zu 4 s. Für automatisch beim Tippen ausgelöste Suchen deshalb <c>false</c>:
    /// Autoconfig und SRV antworten in Millisekunden, ein 4-Sekunden-Hänger bei jeder
    /// Tippause wäre unbrauchbar. Der Fallback liefert dann ungeprüfte
    /// Standardnamen, erkennbar an <see cref="ProbeResult.IsVerified"/>.
    /// </param>
    public static async Task<ProbeResult> RunAsync(
        string email, bool allowProbe = true, CancellationToken ct = default)
    {
        var at = email.LastIndexOf('@');
        if (at < 0) return new ProbeResult { ImapPort = 993, SmtpPort = 465 };

        var domain = email[(at + 1)..].ToLowerInvariant();
        if (domain.Length == 0) return new ProbeResult { ImapPort = 993, SmtpPort = 465 };

        // Nur eine Suche gleichzeitig, damit parallele Aufrufe denselben Cache-Eintrag
        // nutzen statt doppelt zu verbinden.
        await CacheGate.WaitAsync(ct);
        try
        {
            // Ein ungeprüftes Ergebnis darf eine später erlaubte Probe nicht blockieren.
            if (Cache.TryGetValue(domain, out var cached)
                && (cached.IsVerified || !allowProbe))
                return cached;

            var result = await ResolveAsync(domain, email, allowProbe, ct);
            Cache[domain] = result;
            return result;
        }
        finally { CacheGate.Release(); }
    }

    private static async Task<ProbeResult> ResolveAsync(
        string domain, string email, bool allowProbe, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(Budget);
        var bct = budget.Token;

        try
        {
            var auto = await AutoconfigAsync(domain, email, bct);
            if (auto is { IsComplete: true }) return auto with { Source = "autoconfig" };

            var srv = await SrvAsync(domain, bct);
            if (srv is { IsComplete: true }) return srv with { Source = "srv" };

            // Der MX-Eintrag ist die verlässlichste Auskunft, die es zu einer
            // Maildomain gibt — er sagt, welcher Rechner die Post annimmt.
            // Genau daran scheiterte die Suche bisher: liegt das Postfach unter
            // einer anderen Domain als die Adresse, kam „mail.<domain>" heraus,
            // ein Name, den es oft gar nicht gibt.
            var mx = await MxAsync(domain, email, bct);
            if (mx is { IsComplete: true }) return mx;

            // Ohne Freigabe hier aufhören – die nächste Stufe wählt fremde Ports an.
            if (!allowProbe) return await FallbackAsync(domain, bct);

            return await GuessAsync(domain, bct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Budget ausgeschöpft: unverifizierte Standardnamen liefern, statt
            // den Nutzer mit leeren Feldern stehen zu lassen.
            return Fallback(domain);
        }
    }

    private static ProbeResult Fallback(string domain) => new()
    {
        ImapHost = "mail." + domain, ImapPort = 993,
        SmtpHost = "mail." + domain, SmtpPort = 465,
        Source = "fallback",
    };

    /// <summary>
    /// Standardnamen — aber nicht blind: existiert „mail.&lt;domain&gt;" gar nicht
    /// und nennt der MX-Eintrag einen Rechner, ist dieser die bessere Auskunft.
    /// Ein Name, den es nicht gibt, hilft niemandem.
    /// </summary>
    private static async Task<ProbeResult> FallbackAsync(string domain, CancellationToken ct)
    {
        var standard = "mail." + domain;
        if (await ResolvesAsync(standard, ct)) return Fallback(domain);

        if (await LookupMxAsync(domain, ct) is { } mx)
        {
            return new ProbeResult
            {
                ImapHost = mx, ImapPort = 993,
                SmtpHost = mx, SmtpPort = 465,
                Source = "mx",
            };
        }

        return Fallback(domain);
    }

    private static async Task<bool> ResolvesAsync(string host, CancellationToken ct)
    {
        try
        {
            var addresses = await System.Net.Dns.GetHostAddressesAsync(host, ct);
            return addresses.Length > 0;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    // --- 1. Mozilla-Autoconfig / ISPDB --------------------------------------

    private static async Task<ProbeResult?> AutoconfigAsync(
        string domain, string email, CancellationToken ct)
    {
        var esc = Uri.EscapeDataString(email);

        // Bewusst nur HTTPS: Serverangaben aus einem unauthentifizierten Kanal
        // könnten manipuliert sein und Mail über einen fremden Host leiten.
        string[] urls =
        [
            $"https://autoconfig.{domain}/mail/config-v1.1.xml?emailaddress={esc}",
            $"https://{domain}/.well-known/autoconfig/mail/config-v1.1.xml?emailaddress={esc}",
            $"https://autoconfig.thunderbird.net/v1.1/{domain}",
        ];

        // Parallel abfragen, aber in der Reihenfolge auswerten: die beim Anbieter
        // gehostete Konfiguration schlägt die zentrale ISPDB.
        var tasks = urls.Select(u => FetchAutoconfigAsync(u, ct)).ToArray();
        var results = await Task.WhenAll(tasks);
        return results.FirstOrDefault(r => r is { IsComplete: true });
    }

    private static async Task<ProbeResult?> FetchAutoconfigAsync(string url, CancellationToken ct)
    {
        try
        {
            using var resp = await Http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;

            // Gegen absurd grosse Antworten deckeln (Go-Version: 1 MiB).
            var xml = await resp.Content.ReadAsStringAsync(ct);
            if (xml.Length > 1 << 20) return null;

            var doc = XDocument.Parse(xml);
            var provider = doc.Root?.Element("emailProvider");
            if (provider is null) return null;

            var imap = provider.Elements("incomingServer")
                .FirstOrDefault(e => Is(e, "imap"));
            var smtp = provider.Elements("outgoingServer")
                .FirstOrDefault(e => Is(e, "smtp"));

            return new ProbeResult
            {
                ImapHost = Host(imap),
                ImapPort = Port(imap),
                SmtpHost = Host(smtp),
                SmtpPort = Port(smtp),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                      or System.Xml.XmlException)
        {
            return null;
        }

        static bool Is(XElement e, string type)
            => string.Equals((string?)e.Attribute("type"), type, StringComparison.OrdinalIgnoreCase)
               && !string.IsNullOrWhiteSpace((string?)e.Element("hostname"))
               && int.TryParse((string?)e.Element("port"), out var p) && p > 0;

        static string Host(XElement? e) => ((string?)e?.Element("hostname") ?? "").Trim();
        static int Port(XElement? e)
            => int.TryParse((string?)e?.Element("port"), out var p) ? p : 0;
    }

    // --- 2. DNS-SRV (RFC 6186) ----------------------------------------------

    private static async Task<ProbeResult?> SrvAsync(string domain, CancellationToken ct)
    {
        try
        {
            var dns = new LookupClient(new LookupClientOptions
            {
                Timeout = TimeSpan.FromSeconds(2),
                Retries = 1,
                UseCache = true,
            });

            // Alle vier Abfragen gleichzeitig; die Rangfolge entscheidet erst danach.
            var imapsT = LookupSrvAsync(dns, "_imaps._tcp." + domain, ct);
            var imapT = LookupSrvAsync(dns, "_imap._tcp." + domain, ct);
            var subsT = LookupSrvAsync(dns, "_submissions._tcp." + domain, ct);
            var subT = LookupSrvAsync(dns, "_submission._tcp." + domain, ct);
            await Task.WhenAll(imapsT, imapT, subsT, subT);

            // IMAP: implizites TLS bevorzugen, sonst STARTTLS.
            var imap = imapsT.Result ?? imapT.Result;
            // Submission: erst 465 (submissions), dann 587 (submission).
            var smtp = subsT.Result ?? subT.Result;

            if (imap is null || smtp is null) return null;

            return new ProbeResult
            {
                ImapHost = imap.Value.Host, ImapPort = imap.Value.Port,
                SmtpHost = smtp.Value.Host, SmtpPort = smtp.Value.Port,
            };
        }
        catch (DnsResponseException)
        {
            return null;
        }
    }

    private static async Task<(string Host, int Port)?> LookupSrvAsync(
        ILookupClient dns, string query, CancellationToken ct)
    {
        try
        {
            var result = await dns.QueryAsync(query, QueryType.SRV, cancellationToken: ct);
            var rec = result.Answers.SrvRecords()
                .OrderBy(r => r.Priority).ThenByDescending(r => r.Weight)
                .FirstOrDefault();
            if (rec is null || rec.Port == 0) return null;

            var target = rec.Target.Value.TrimEnd('.');
            // RFC 6186/2782: Target "." bedeutet "Dienst nicht verfügbar".
            if (target.Length == 0) return null;

            return (target, rec.Port);
        }
        catch (Exception ex) when (ex is DnsResponseException or OperationCanceledException)
        {
            return null;
        }
    }

    // --- 3. MX-Eintrag ------------------------------------------------------

    /// <summary>
    /// Über den MX-Eintrag zum richtigen Server.
    ///
    /// Zwei Wege, in dieser Reihenfolge:
    ///   a) Autoconfig für die Domain des MX-Ziels. Liegt das Postfach eines
    ///      Anbieters unter einer anderen Domain als die Adresse, steht die
    ///      Konfiguration dort und nicht bei der Adressdomain.
    ///   b) Der MX-Rechner selbst. Bei kleineren Servern nimmt derselbe Rechner
    ///      Post an und stellt IMAP bereit.
    ///
    /// Weg b ist bewusst nachrangig: Bei grossen Anbietern zeigt der MX auf
    /// Annahmerechner, die kein IMAP sprechen — dort greift a, oder es bleibt
    /// beim Raten.
    /// </summary>
    private static async Task<ProbeResult?> MxAsync(
        string domain, string email, CancellationToken ct)
    {
        if (await LookupMxAsync(domain, ct) is not { } mx) return null;

        var mxDomain = BaseDomain(mx);
        if (!string.Equals(mxDomain, domain, StringComparison.OrdinalIgnoreCase))
        {
            var auto = await AutoconfigAsync(mxDomain, email, ct);
            if (auto is { IsComplete: true }) return auto with { Source = "mx-autoconfig" };
        }

        return new ProbeResult
        {
            ImapHost = mx, ImapPort = 993,
            SmtpHost = mx, SmtpPort = 465,
            Source = "mx",
        };
    }

    /// <summary>Der MX mit der höchsten Priorität (kleinste Zahl); null, wenn keiner.</summary>
    private static async Task<string?> LookupMxAsync(string domain, CancellationToken ct)
    {
        try
        {
            var dns = new LookupClient(new LookupClientOptions
            {
                Timeout = TimeSpan.FromSeconds(2),
                Retries = 1,
                UseCache = true,
            });

            var result = await dns.QueryAsync(domain, QueryType.MX, cancellationToken: ct);
            var record = result.Answers.MxRecords()
                .OrderBy(r => r.Preference)
                .FirstOrDefault();

            var target = record?.Exchange.Value.TrimEnd('.');

            // RFC 7505: "." als Ziel heisst ausdrücklich „nimmt keine Mail an".
            return string.IsNullOrEmpty(target) ? null : target;
        }
        catch (Exception ex) when (ex is DnsResponseException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// „mail.neuhaus.or.at" -> „neuhaus.or.at". Nur das erste Namensteil fällt
    /// weg; bei zwei Teilen bleibt der Name, wie er ist.
    /// </summary>
    public static string BaseDomain(string host)
    {
        var parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 2 ? host : string.Join('.', parts[1..]);
    }

    // --- 4. Raten, per TLS verifiziert --------------------------------------

    private static async Task<ProbeResult> GuessAsync(string domain, CancellationToken ct)
    {
        // Der MX-Rechner kommt zuletzt: er ist zwar belegt, spricht bei grossen
        // Anbietern aber kein IMAP. Antwortet imap./mail.<domain>, ist das die
        // näherliegende Wahl.
        var mx = await LookupMxAsync(domain, ct);

        string[] imapCandidates = mx is null
            ? ["imap." + domain, "mail." + domain]
            : ["imap." + domain, "mail." + domain, mx];

        string[] smtpCandidates = mx is null
            ? ["smtp." + domain, "mail." + domain]
            : ["smtp." + domain, "mail." + domain, mx];

        // Alle Kandidaten gleichzeitig anwählen – die Reihenfolge entscheidet danach.
        var imapT = imapCandidates.Select(h => DialOkAsync(h, 993, ct)).ToArray();
        var smtpT = smtpCandidates.Select(h => DialOkAsync(h, 465, ct)).ToArray();
        await Task.WhenAll(imapT.Concat(smtpT));

        var imapHost = imapCandidates.Where((_, i) => imapT[i].Result).FirstOrDefault();
        var smtpHost = smtpCandidates.Where((_, i) => smtpT[i].Result).FirstOrDefault();

        // Hat keine Seite geantwortet, ist nichts verifiziert – dann als solches
        // kennzeichnen, statt geratene Namen als geprüft auszugeben.
        if (imapHost is null && smtpHost is null) return await FallbackAsync(domain, ct);

        return new ProbeResult
        {
            ImapHost = imapHost ?? mx ?? "mail." + domain, ImapPort = 993,
            SmtpHost = smtpHost ?? mx ?? "mail." + domain, SmtpPort = 465,
            Source = "guess",
        };
    }

    /// <summary>TLS-Handshake als Lebenszeichen – nur so ist ein geratener Name belastbar.</summary>
    private static async Task<bool> DialOkAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));

            // Erst auflösen: existiert der Name nicht, spart das den Verbindungsversuch,
            // dessen Timeout sonst voll durchläuft.
            var addrs = await System.Net.Dns.GetHostAddressesAsync(host, timeout.Token);
            if (addrs.Length == 0) return false;

            // Alle Adressen parallel: ein Host mit AAAA und A darf nicht daran scheitern,
            // dass eine der beiden Familien nicht durchkommt. Ein Erfolg genügt.
            var attempts = addrs
                .Select(a => TlsHandshakeAsync(a, host, port, timeout.Token))
                .ToArray();
            var results = await Task.WhenAll(attempts);
            return results.Any(ok => ok);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<bool> TlsHandshakeAsync(
        IPAddress address, string host, int port, CancellationToken ct)
    {
        try
        {
            // Socket in der Familie der Zieladresse anlegen – ein Client mit geratener
            // Familie verbindet sich nicht zuverlässig.
            using var tcp = new TcpClient(address.AddressFamily);
            await tcp.ConnectAsync(address, port, ct);

            await using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions { TargetHost = host }, ct);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or AuthenticationException
                                      or OperationCanceledException or IOException)
        {
            return false;
        }
    }
}
