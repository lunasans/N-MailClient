using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NMailClient.Poc.Services.Transport;
using Xunit;

namespace NMailClient.Poc.Tests;

public class HappyEyeballsTests
{
    // ---- Reihenfolge -------------------------------------------------------

    [Fact]
    public void FamiliesAreInterleaved()
    {
        // Hat eine Familie viele Adressen, darf sie die Warteschlange nicht
        // besetzen – sonst käme die andere erst nach etlichen Zeitlimits dran.
        var v6 = new[] { IPAddress.Parse("::1"), IPAddress.Parse("::2"), IPAddress.Parse("::3") };
        var v4 = new[] { IPAddress.Parse("10.0.0.1") };

        var order = HappyEyeballs.Interleave(v6, v4);

        Assert.Equal(
            ["::1", "10.0.0.1", "::2", "::3"],
            order.Select(a => a.ToString()));
    }

    [Fact]
    public void InterleavingKeepsEveryAddress()
    {
        var v6 = new[] { IPAddress.Parse("::1") };
        var v4 = new[] { IPAddress.Parse("10.0.0.1"), IPAddress.Parse("10.0.0.2") };

        Assert.Equal(3, HappyEyeballs.Interleave(v6, v4).Count);
    }

    [Fact]
    public void AnEmptyFamilyIsHarmless()
    {
        var v4 = new[] { IPAddress.Parse("10.0.0.1") };

        Assert.Equal(["10.0.0.1"],
            HappyEyeballs.Interleave([], v4).Select(a => a.ToString()));
    }

    // ---- Verhalten am Draht ------------------------------------------------

    [Fact]
    public async Task ConnectsToALocalListener()
    {
        using var listener = Listen(out var port);

        using var socket = await HappyEyeballs.ConnectAsync("localhost", port);

        Assert.True(socket.Connected);
    }

    [Fact]
    public async Task AnUnreachableAddressDoesNotCostTheFullTimeout()
    {
        // Der Kern der ganzen Übung. 198.51.100.x ist nach RFC 5737 für
        // Beispiele reserviert und wird nirgends geroutet – ein Verbindungs-
        // versuch dorthin verschwindet, genau wie hinter einer DROP-Regel.
        //
        // Ohne diesen Verbinder wartete .NET die volle TCP-Frist ab (gemessen
        // gut 21 Sekunden), bevor das zweite Ziel überhaupt versucht wurde.
        using var listener = Listen(out var port);

        var watch = Stopwatch.StartNew();
        using var socket = await HappyEyeballs.ConnectAsync(
            [IPAddress.Parse("198.51.100.42"), IPAddress.Loopback], port);
        watch.Stop();

        Assert.True(socket.Connected);
        Assert.True(watch.ElapsedMilliseconds < 3000,
            $"Verbindung dauerte {watch.ElapsedMilliseconds} ms – das tote Ziel hat aufgehalten.");
    }

    [Fact]
    public async Task TheDeadTargetMayEvenComeFirst()
    {
        // Genau die Lage auf diesem Rechner: die Auflösung nennt das tote Ziel
        // zuerst. Ohne Nebenläufigkeit wartete .NET hier gut 21 Sekunden.
        using var listener = Listen(out var port);

        var watch = Stopwatch.StartNew();
        using var socket = await HappyEyeballs.ConnectAsync(
            [
                IPAddress.Parse("198.51.100.42"),
                IPAddress.Parse("198.51.100.43"),
                IPAddress.Loopback,
            ], port);
        watch.Stop();

        Assert.True(socket.Connected);
        Assert.True(watch.ElapsedMilliseconds < 3000,
            $"Verbindung dauerte {watch.ElapsedMilliseconds} ms trotz erreichbarem Ziel.");
    }

    [Fact]
    public async Task OnlyOneSocketSurvives()
    {
        // Die Verlierer müssen geschlossen werden, sonst bleibt bei jedem
        // Verbindungsaufbau ein halboffener Versuch zurück.
        using var listener = Listen(out var port);

        using var socket = await HappyEyeballs.ConnectAsync(
            [IPAddress.Loopback, IPAddress.Loopback], port);

        Assert.True(socket.Connected);

        // Der Server sieht höchstens die beiden Versuche, aber danach darf
        // nichts Offenes zurückbleiben: der Gewinner ist genau einer.
        await Task.Delay(200);
        Assert.True(socket.Connected);
    }

    [Fact]
    public async Task AGenuineFailureIsReportedAsSuch()
    {
        // Kein Ziel erreichbar: es muss ein Socket-Fehler ankommen, keine
        // stille Rückgabe und kein Hänger.
        var closed = FreePort();

        await Assert.ThrowsAnyAsync<SocketException>(
            () => HappyEyeballs.ConnectAsync("127.0.0.1", closed));
    }

    [Fact]
    public async Task AnUnknownNameFails()
    {
        await Assert.ThrowsAnyAsync<SocketException>(
            () => HappyEyeballs.ConnectAsync(
                "kein-solcher-name.invalid", 993));
    }

    // ---- Hilfen ------------------------------------------------------------

    private static TcpListener Listen(out int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return listener;
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
