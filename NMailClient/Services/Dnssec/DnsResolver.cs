using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace NMailClient.Services.Dnssec;

/// <summary>
/// Stellt DNS-Anfragen an rekursive Resolver und liefert die Antwort roh zurück.
///
/// Der Klartext-Transport ist hier ausdrücklich in Ordnung: Vertraulichkeit ist
/// nicht das Ziel, und gegen Verfälschung schützt genau die DNSSEC-Prüfung, die
/// oben darauf aufsetzt. Ein Resolver, der uns anlügt, fliegt bei der
/// Signaturprüfung auf.
///
/// Der Systemresolver wird bewusst nicht genommen: viele Heimrouter reichen
/// RRSIG- und DNSKEY-Einträge gar nicht durch, und dann sähe jede Domain
/// unsigniert aus.
/// </summary>
public class DnsResolver
{
    /// <summary>Öffentliche Resolver, die EDNS0 und DNSSEC zuverlässig durchreichen.</summary>
    private static readonly IPAddress[] DefaultServers =
    [
        IPAddress.Parse("1.1.1.1"),
        IPAddress.Parse("9.9.9.9"),
        IPAddress.Parse("8.8.8.8"),
    ];

    private readonly IPAddress[] _servers;
    private readonly ConcurrentDictionary<(string, ushort), DnsMessage> _cache = new();

    public DnsResolver(IPAddress[]? servers = null)
        => _servers = servers is { Length: > 0 } ? servers : DefaultServers;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Anfrage stellen. Antworten werden für die Dauer des Vorgangs gemerkt — bei
    /// einer Kettenprüfung wird derselbe DNSKEY sonst mehrfach geholt.
    /// </summary>
    public async Task<DnsMessage?> QueryAsync(DnsName name, ushort type, CancellationToken ct)
    {
        var key = (name.ToString(), type);
        if (_cache.TryGetValue(key, out var cached)) return cached;

        foreach (var server in _servers)
        {
            var response = await TryServerAsync(server, name, type, ct);
            if (response is null) continue;

            // Ein SERVFAIL kann am einzelnen Resolver liegen; dann der nächste.
            if (response.Rcode is DnsRcode.ServFail or DnsRcode.Refused) continue;

            _cache[key] = response;
            return response;
        }

        return null;
    }

    private async Task<DnsMessage?> TryServerAsync(
        IPAddress server, DnsName name, ushort type, CancellationToken ct)
    {
        var id = (ushort)RandomNumberGenerator.GetInt32(1, ushort.MaxValue);
        var query = DnsMessage.BuildQuery(name, type, id);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);

            var response = await OverUdpAsync(server, query, id, timeout.Token);

            // Abgeschnitten? Dann noch einmal über TCP – DNSKEY-Mengen sprengen
            // regelmässig auch grosszügige UDP-Puffer.
            if (response is null or { Truncated: true })
                response = await OverTcpAsync(server, query, id, timeout.Token);

            return response;
        }
        catch (Exception ex) when (ex is SocketException or IOException
                                      or OperationCanceledException or FormatException)
        {
            return null;
        }
    }

    private static async Task<DnsMessage?> OverUdpAsync(
        IPAddress server, byte[] query, ushort id, CancellationToken ct)
    {
        using var udp = new UdpClient(server.AddressFamily);
        udp.Connect(server, 53);

        await udp.SendAsync(query, ct);

        // Fremde Antworten auf demselben Socket verwerfen, bis die eigene kommt.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var result = await udp.ReceiveAsync(ct);
            var message = ParseOrNull(result.Buffer);
            if (message?.Id == id) return message;
        }

        return null;
    }

    private static async Task<DnsMessage?> OverTcpAsync(
        IPAddress server, byte[] query, ushort id, CancellationToken ct)
    {
        using var tcp = new TcpClient(server.AddressFamily);
        await tcp.ConnectAsync(server, 53, ct);

        await using var stream = tcp.GetStream();

        // Über TCP steht die Länge als zwei Bytes davor.
        var prefix = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(prefix, (ushort)query.Length);
        await stream.WriteAsync(prefix, ct);
        await stream.WriteAsync(query, ct);

        await stream.ReadExactlyAsync(prefix, ct);
        var length = BinaryPrimitives.ReadUInt16BigEndian(prefix);
        if (length == 0) return null;

        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer, ct);

        var message = ParseOrNull(buffer);
        return message?.Id == id ? message : null;
    }

    private static DnsMessage? ParseOrNull(byte[] buffer)
    {
        try { return DnsMessage.Parse(buffer); }
        catch (FormatException) { return null; }
    }
}
