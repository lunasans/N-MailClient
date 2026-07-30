using System.Net.Http;
using System.Net.Sockets;

namespace NMailClient.Poc.Services.Transport;

/// <summary>
/// HTTP-Verbindungen mit demselben Verbindungsaufbau wie IMAP und SMTP.
///
/// Anlass war eine Meldung aus der Oberfläche: „mailcow nicht erreichbar –
/// HttpClient.Timeout of 15 seconds". Dahinter steckte kein Fehler bei mailcow,
/// sondern derselbe Umstand wie beim Postfach: der Rechner hat einen
/// AAAA-Eintrag, ist über IPv6 aber nicht erreichbar. <see cref="HttpClient"/>
/// arbeitet die aufgelösten Adressen der Reihe nach ab und bleibt an der ersten
/// hängen — die Frist läuft ab, bevor IPv4 überhaupt an die Reihe kommt.
///
/// Deshalb wird der Verbindungsaufbau nicht dem Vorgabeweg überlassen, sondern
/// über <see cref="HappyEyeballs"/> geführt: beide Familien nebeneinander, der
/// schnellere gewinnt.
/// </summary>
public static class HttpFactory
{
    /// <summary>
    /// Ein Client, der nicht an einer unerreichbaren Adresse hängen bleibt.
    /// </summary>
    /// <param name="timeout">Gesamtfrist für eine Anfrage.</param>
    /// <param name="credentials">
    /// Anmeldedaten für Server, die sie verlangen (CalDAV/CardDAV).
    /// </param>
    /// <param name="allowAutoRedirect">
    /// Bei DAV bewusst abschaltbar: eine Umleitung kann dort einen anderen
    /// Sinn haben als „hol es woanders".
    /// </param>
    public static HttpClient Create(
        TimeSpan timeout,
        System.Net.ICredentials? credentials = null,
        bool allowAutoRedirect = true)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (context, ct) =>
            {
                var socket = await HappyEyeballs.ConnectAsync(
                    context.DnsEndPoint.Host, context.DnsEndPoint.Port, ct);

                // Der Aufrufer übernimmt den Socket; NetworkStream schliesst ihn
                // mit, wenn die Verbindung endet.
                return new NetworkStream(socket, ownsSocket: true);
            },
            Credentials = credentials,
            PreAuthenticate = credentials is not null,
            AllowAutoRedirect = allowAutoRedirect,

            // Eine ruhende Verbindung ewig offen zu halten bringt nichts und
            // fällt bei Serverwechseln unangenehm auf.
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        };

        return new HttpClient(handler) { Timeout = timeout };
    }
}
