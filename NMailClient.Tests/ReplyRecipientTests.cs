using NMailClient.Models;
using NMailClient.ViewModels;
using Xunit;

namespace NMailClient.Tests;

/// <summary>
/// Empfängerfelder beim Antworten.
///
/// Ins Feld gehört die Adresse, nicht der Anzeigename: nur sie sagt, wohin die
/// Antwort tatsächlich geht. Der Name ist frei wählbar — bei Phishing ist er
/// gerade das, was täuschen soll.
/// </summary>
public class ReplyRecipientTests
{
    private static MailBody Mail(string from, string to = "", string replyTo = "") => new()
    {
        Uid = 1,
        Subject = "Rechnung",
        From = from,
        To = to,
        ReplyTo = replyTo,
        MessageId = "<abc@example.org>",
    };

    // ---- Der gemeldete Fehler ----------------------------------------------

    [Fact]
    public void TheAddressLandsInTheFieldNotTheName()
    {
        var reply = ComposeRequest.Reply(Mail("Rene Neuhaus <rene@example.org>"), all: false);

        Assert.Equal("rene@example.org", reply.To);
    }

    [Fact]
    public void TheDisplayNameIsGoneEntirely()
    {
        var reply = ComposeRequest.Reply(Mail("\"Neuhaus, Rene\" <rene@example.org>"), all: false);

        Assert.DoesNotContain("Neuhaus", reply.To);
        Assert.DoesNotContain("<", reply.To);
    }

    [Fact]
    public void ABareAddressIsKept()
        => Assert.Equal("rene@example.org",
            ComposeRequest.Reply(Mail("rene@example.org"), all: false).To);

    // ---- Mehrere Empfänger -------------------------------------------------

    [Fact]
    public void ReplyAllKeepsEveryAddress()
    {
        var reply = ComposeRequest.Reply(
            Mail("Rene <rene@example.org>", "Anna <anna@example.org>, bert@example.org"),
            all: true);

        Assert.Equal("rene@example.org", reply.To);
        Assert.Equal("anna@example.org, bert@example.org", reply.Cc);
    }

    [Fact]
    public void ReplyWithoutAllLeavesTheCopyFieldEmpty()
    {
        var reply = ComposeRequest.Reply(
            Mail("rene@example.org", "anna@example.org"), all: false);

        Assert.Equal("", reply.Cc);
    }

    [Fact]
    public void ARecipientNamedTwiceAppearsOnce()
    {
        // Sonst stünde derselbe Empfänger doppelt im Feld.
        var reply = ComposeRequest.Reply(
            Mail("rene@example.org", "Anna <anna@example.org>, anna@example.org"),
            all: true);

        Assert.Equal("anna@example.org", reply.Cc);
    }

    [Fact]
    public void CaseDoesNotMakeARecipientDistinct()
    {
        var reply = ComposeRequest.Reply(
            Mail("rene@example.org", "Anna@Example.org, anna@example.org"), all: true);

        Assert.Equal("Anna@Example.org", reply.Cc);
    }

    [Fact]
    public void AGroupIsResolvedIntoItsMembers()
    {
        // "Team: a@b, c@d;" ist gültige Syntax und käme sonst unverändert
        // ins Feld – samt Doppelpunkt und Semikolon.
        var reply = ComposeRequest.Reply(
            Mail("rene@example.org", "Team: anna@example.org, bert@example.org;"),
            all: true);

        Assert.Equal("anna@example.org, bert@example.org", reply.Cc);
    }

    // ---- Antwortadresse des Absenders --------------------------------------

    [Fact]
    public void ReplyToBeatsFrom()
    {
        // Der Absender bestimmt ausdrücklich, wohin die Antwort gehen soll.
        var reply = ComposeRequest.Reply(
            Mail("noreply@example.org", replyTo: "support@example.org"), all: false);

        Assert.Equal("support@example.org", reply.To);
    }

    [Fact]
    public void AListAddressWins()
    {
        // Der klassische Verteilerfall: geantwortet wird der Runde, nicht der
        // Person, die zuletzt geschrieben hat.
        var reply = ComposeRequest.Reply(
            Mail("Anna <anna@example.org>", replyTo: "liste@example.org"), all: false);

        Assert.Equal("liste@example.org", reply.To);
        Assert.DoesNotContain("anna", reply.To);
    }

    [Fact]
    public void WithoutReplyToTheSenderIsUsed()
        => Assert.Equal("anna@example.org",
            ComposeRequest.Reply(Mail("Anna <anna@example.org>"), all: false).To);

    // ---- Die eigene Adresse ------------------------------------------------

    [Fact]
    public void MyOwnAddressIsNotCopiedToMyself()
    {
        // Sonst schickt man sich jede Antwort selbst mit.
        var reply = ComposeRequest.Reply(
            Mail("anna@example.org", "rene@example.org, bert@example.org"),
            all: true,
            own: ["rene@example.org"]);

        Assert.Equal("bert@example.org", reply.Cc);
    }

    [Fact]
    public void AnAliasCountsAsMyOwnAddress()
    {
        var reply = ComposeRequest.Reply(
            Mail("anna@example.org", "post@example.org, bert@example.org"),
            all: true,
            own: ["rene@example.org", "post@example.org"]);

        Assert.Equal("bert@example.org", reply.Cc);
    }

    [Fact]
    public void TheRecipientOfTheReplyIsNotAlsoCopied()
    {
        // Anna steht im An der Antwort – in der Kopie hätte sie nichts verloren.
        var reply = ComposeRequest.Reply(
            Mail("Anna <anna@example.org>", "anna@example.org, bert@example.org"),
            all: true,
            own: ["rene@example.org"]);

        Assert.Equal("anna@example.org", reply.To);
        Assert.Equal("bert@example.org", reply.Cc);
    }

    [Fact]
    public void ACopyFieldWithOnlyMyselfEndsUpEmpty()
    {
        var reply = ComposeRequest.Reply(
            Mail("anna@example.org", "rene@example.org"),
            all: true,
            own: ["rene@example.org"]);

        Assert.Equal("", reply.Cc);
    }

    [Fact]
    public void WithoutOwnAddressesNothingIsRemoved()
    {
        // Ohne Angabe darf nichts wegfallen – lieber ein Empfänger zu viel als
        // einer zu wenig.
        var reply = ComposeRequest.Reply(
            Mail("anna@example.org", "rene@example.org, bert@example.org"), all: true);

        Assert.Contains("rene@example.org", reply.Cc);
        Assert.Contains("bert@example.org", reply.Cc);
    }

    // ---- Randfälle ---------------------------------------------------------

    [Fact]
    public void AnEmptySenderYieldsAnEmptyField()
        => Assert.Equal("", ComposeRequest.Reply(Mail(""), all: false).To);

    [Fact]
    public void UnreadableInputIsKeptForCorrection()
    {
        // Lieber etwas Unschönes zum Nachbessern als ein leeres Feld: sonst
        // wäre nicht mehr erkennbar, an wen die Antwort gehen sollte.
        const string broken = "@@@";

        var reply = ComposeRequest.Reply(Mail(broken), all: false);

        Assert.Equal(broken, reply.To);
    }

    [Fact]
    public void ForwardingLeavesTheRecipientOpen()
    {
        // Eine Weiterleitung geht an jemand Neues – das Feld muss leer bleiben.
        var forward = ComposeRequest.Forward(Mail("Rene <rene@example.org>"));

        Assert.Equal("", forward.To);
    }
}
