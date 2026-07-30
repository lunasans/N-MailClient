using System.Net;
using System.Net.Http;
using NMailClient.Poc.Services.Transport;
using Xunit;

namespace NMailClient.Poc.Tests;

/// <summary>
/// Der eigene Verbindungsaufbau für HTTP.
///
/// Anlass war „mailcow nicht erreichbar – Timeout of 15 seconds": nicht mailcow
/// war das Problem, sondern ein AAAA-Eintrag ohne erreichbares IPv6. HttpClient
/// arbeitet die Adressen der Reihe nach ab und blieb an der ersten hängen.
///
/// Diese Tests laufen gegen einen Zuhörer im eigenen Prozess — geprüft wird der
/// Weg durch <c>ConnectCallback</c>, nicht das Netz. Ein Fehler darin legte
/// jede HTTP-Verbindung der Anwendung lahm, also auch Kalender, Adressbuch und
/// die Aktualisierungsprüfung.
/// </summary>
public class HttpFactoryTests
{
    /// <summary>Ein Zuhörer auf einem freien Port; gibt seine Adresse zurück.</summary>
    private static HttpListener Listen(out string url)
    {
        // Freien Port über einen kurzlebigen Socket ermitteln.
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        url = $"http://127.0.0.1:{port}/";
        var listener = new HttpListener();
        listener.Prefixes.Add(url);
        listener.Start();
        return listener;
    }

    private static async Task RespondOnceAsync(HttpListener listener, string body)
    {
        var context = await listener.GetContextAsync();
        var bytes = System.Text.Encoding.UTF8.GetBytes(body);
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    [Fact]
    public async Task ARequestGoesThroughOurConnector()
    {
        using var listener = Listen(out var url);
        var serving = RespondOnceAsync(listener, "guten tag");

        using var http = HttpFactory.Create(TimeSpan.FromSeconds(10));
        var answer = await http.GetStringAsync(url);

        await serving;
        Assert.Equal("guten tag", answer);
    }

    [Fact]
    public async Task SeveralRequestsShareTheClient()
    {
        // Der Verbindungsvorrat muss weiter funktionieren – ein eigener
        // ConnectCallback darf ihn nicht aushebeln.
        using var listener = Listen(out var url);

        using var http = HttpFactory.Create(TimeSpan.FromSeconds(10));

        for (int i = 0; i < 3; i++)
        {
            var serving = RespondOnceAsync(listener, $"antwort {i}");
            Assert.Equal($"antwort {i}", await http.GetStringAsync(url));
            await serving;
        }
    }

    [Fact]
    public void TheTimeoutIsTakenOver()
    {
        using var http = HttpFactory.Create(TimeSpan.FromSeconds(7));

        Assert.Equal(TimeSpan.FromSeconds(7), http.Timeout);
    }

    [Fact]
    public async Task AClosedPortFailsQuicklyInsteadOfHanging()
    {
        // Ein geschlossener Port muss sofort abgewiesen werden; hinge er, wäre
        // die Wirkung dieselbe wie vor der Korrektur.
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        using var http = HttpFactory.Create(TimeSpan.FromSeconds(10));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<HttpRequestException>(
            () => http.GetStringAsync($"http://127.0.0.1:{port}/"));

        Assert.True(watch.ElapsedMilliseconds < 5000,
            $"Abweisung dauerte {watch.ElapsedMilliseconds} ms.");
    }
}
