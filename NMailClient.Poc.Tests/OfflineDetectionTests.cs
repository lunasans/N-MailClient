using System.IO;
using System.Net.Sockets;
using MailKit.Net.Imap;
using NMailClient.Poc.Services;
using Xunit;
using AuthenticationException = MailKit.Security.AuthenticationException;

namespace NMailClient.Poc.Tests;

/// <summary>
/// Wann gilt die Anwendung als „offline"?
///
/// Die Frage entscheidet, ob Altdaten aus dem Zwischenspeicher gezeigt werden
/// und der Hinweisstreifen erscheint. Eine falsche Antwort ist in beide
/// Richtungen unangenehm: als offline gemeldet zu werden, obwohl die Verbindung
/// steht, verwirrt — und ein echtes Netzproblem zu verschweigen ebenso.
/// </summary>
public class OfflineDetectionTests
{
    private static CancellationToken Cancelled()
    {
        var source = new CancellationTokenSource();
        source.Cancel();
        return source.Token;
    }

    [Fact]
    public void ASocketErrorMeansOffline()
        => Assert.True(ImapService.IsConnectionProblem(
            new SocketException(10060), CancellationToken.None));

    [Fact]
    public void ABrokenStreamMeansOffline()
        => Assert.True(ImapService.IsConnectionProblem(
            new IOException("Verbindung unterbrochen"), CancellationToken.None));

    [Fact]
    public void ATimeoutMeansOffline()
        => Assert.True(ImapService.IsConnectionProblem(
            new TimeoutException(), CancellationToken.None));

    [Fact]
    public void AProtocolErrorMeansOffline()
        => Assert.True(ImapService.IsConnectionProblem(
            new ImapProtocolException("unerwartete Antwort"), CancellationToken.None));

    [Fact]
    public void ARejectedLoginDoesNotMeanOffline()
        => Assert.False(ImapService.IsConnectionProblem(
            new AuthenticationException("Anmeldung abgelehnt"), CancellationToken.None));

    // ---- Der Sonderfall Abbruch --------------------------------------------

    [Fact]
    public void OurOwnCancellationIsNotOffline()
    {
        // Der gemeldete Fehler: beim Weiterklicken wird die laufende Anforderung
        // abgebrochen. Das ist kein Netzproblem — die Anwendung meldete aber
        // „offline" und zeigte Altdaten, während die Verbindung tadellos stand.
        Assert.False(ImapService.IsConnectionProblem(
            new OperationCanceledException(), Cancelled()));
    }

    [Fact]
    public void ACancellationWithoutOurRequestIsATimeout()
    {
        // MailKit bricht eigene Fristen auf demselben Weg ab. Ist unser Merkmal
        // nicht gesetzt, war es also sehr wohl ein Netzproblem.
        Assert.True(ImapService.IsConnectionProblem(
            new OperationCanceledException(), CancellationToken.None));
    }

    [Fact]
    public void ARealErrorStaysOfflineEvenAfterCancellation()
    {
        // Ein Netzfehler bleibt einer, auch wenn zwischenzeitlich abgebrochen
        // wurde – sonst verschwiege die Anwendung ein echtes Problem.
        Assert.True(ImapService.IsConnectionProblem(new SocketException(10060), Cancelled()));
    }
}
