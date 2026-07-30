using System.Net;
using System.Net.Sockets;

namespace NMailClient.Poc.Services.Transport;

/// <summary>
/// Verbindungsaufbau nach RFC 8305 („Happy Eyeballs").
///
/// Der Anlass war messbar: mail.neuhaus.or.at hat einen AAAA-Eintrag, ist über
/// IPv6 aber nicht erreichbar. Die Namensauflösung nennt IPv6 zuerst, .NET
/// arbeitet die Liste stur von oben ab und wartet die volle TCP-Frist ab —
/// 21 Sekunden, bevor überhaupt der erste IPv4-Versuch startet. Danach steht
/// die Verbindung in 23 Millisekunden.
///
/// Ein reines Umsortieren auf „IPv4 zuerst" wäre die falsche Lehre: es
/// bestrafte alle, bei denen IPv6 der bessere Weg ist. Stattdessen werden
/// beide Familien nebeneinander versucht; wer zuerst antwortet, gewinnt, die
/// übrigen Versuche werden verworfen.
/// </summary>
public static class HappyEyeballs
{
    /// <summary>
    /// Vorsprung für IPv6, wie in RFC 8305 §5 empfohlen. Lang genug, dass ein
    /// funktionierendes IPv6 fast immer gewinnt, kurz genug, dass ein totes
    /// nicht spürbar aufhält.
    /// </summary>
    public static readonly TimeSpan ResolutionDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Verbundener Socket zum ersten erreichbaren Ziel. Der Aufrufer übernimmt
    /// ihn — MailKit schließt ihn mit der Sitzung.
    /// </summary>
    /// <exception cref="SocketException">Kein Ziel war erreichbar.</exception>
    public static async Task<Socket> ConnectAsync(
        string host, int port, CancellationToken ct = default)
        => await ConnectAsync(await Dns.GetHostAddressesAsync(host, ct), port, ct);

    /// <summary>
    /// Dieselbe Wahl bei bereits bekannten Adressen. Getrennt, damit sich der
    /// Ablauf ohne Namensauflösung prüfen lässt — ein totes Ziel liesse sich
    /// sonst nicht verlässlich nachstellen.
    /// </summary>
    public static async Task<Socket> ConnectAsync(
        IReadOnlyList<IPAddress> addresses, int port, CancellationToken ct = default)
    {
        if (addresses.Count == 0)
            throw new SocketException((int)SocketError.HostNotFound);

        // Ein einziges Ziel: der Umweg über die Nebenläufigkeit lohnt nicht.
        if (addresses.Count == 1)
            return await ConnectOneAsync(addresses[0], port, ct);

        // Abwechselnd IPv6 und IPv4, damit keine Familie die Warteschlange
        // besetzt, wenn eine Seite viele Adressen hat.
        var ordered = Interleave(
            addresses.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6).ToList(),
            addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToList());

        using var loser = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var attempts = new List<Task<Socket>>();
        var pending = new List<Task>();

        foreach (var address in ordered)
        {
            var attempt = ConnectOneAsync(address, port, loser.Token);
            attempts.Add(attempt);
            pending.Add(attempt);

            // Den nächsten Versuch nicht sofort hinterherwerfen: gewinnt der
            // laufende innerhalb der Frist, bleibt es bei einer Verbindung.
            var delay = Task.Delay(ResolutionDelay, loser.Token);
            var first = await Task.WhenAny([.. pending, delay]);

            if (first == delay) continue;

            pending.Remove(first);
            if (first.IsCompletedSuccessfully) return Finish(loser, attempts, (Task<Socket>)first);
        }

        // Alle sind gestartet – jetzt auf den ersten Erfolg warten.
        while (pending.Count > 0)
        {
            var first = await Task.WhenAny(pending);
            pending.Remove(first);
            if (first.IsCompletedSuccessfully) return Finish(loser, attempts, (Task<Socket>)first);
        }

        // Keiner kam durch: den ersten echten Fehler weiterreichen, nicht einen
        // nichtssagenden Sammelfehler.
        foreach (var attempt in attempts)
            if (attempt.Exception?.InnerException is SocketException ex) throw ex;

        throw new SocketException((int)SocketError.HostUnreachable);
    }

    /// <summary>
    /// Gewinner behalten, alle übrigen abbrechen und ihre Sockets schließen —
    /// sonst bliebe je Verbindungsaufbau ein halboffener Versuch zurück.
    /// </summary>
    private static Socket Finish(
        CancellationTokenSource loser, List<Task<Socket>> attempts, Task<Socket> winner)
    {
        loser.Cancel();

        foreach (var attempt in attempts)
        {
            if (attempt == winner) continue;
            _ = attempt.ContinueWith(
                t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); },
                TaskScheduler.Default);
        }

        return winner.Result;
    }

    private static async Task<Socket> ConnectOneAsync(
        IPAddress address, int port, CancellationToken ct)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            // Kleine Pakete sofort schicken; IMAP ist ein Frage-Antwort-Spiel.
            socket.NoDelay = true;
            await socket.ConnectAsync(new IPEndPoint(address, port), ct);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Erst je eine aus beiden Listen, dann der Rest.</summary>
    public static List<IPAddress> Interleave(
        IReadOnlyList<IPAddress> first, IReadOnlyList<IPAddress> second)
    {
        var result = new List<IPAddress>(first.Count + second.Count);
        for (int i = 0; i < Math.Max(first.Count, second.Count); i++)
        {
            if (i < first.Count) result.Add(first[i]);
            if (i < second.Count) result.Add(second[i]);
        }
        return result;
    }
}
