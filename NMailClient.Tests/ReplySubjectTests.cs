using NMailClient.Models;
using NMailClient.ViewModels;
using Xunit;

namespace NMailClient.Tests;

/// <summary>
/// Betreff beim Antworten und Weiterleiten.
///
/// Die Kürzel sind sprachabhängig: deutsche Programme schicken „AW:" und „WG:",
/// englische „Re:" und „Fwd:". Wer nur auf das eigene prüft, baut bei jeder
/// Antwort ein weiteres davor — nach ein paar Runden steht dann
/// „Re: AW: Re: AW: Rechnung" im Betreff.
/// </summary>
public class ReplySubjectTests
{
    private static MailBody Mail(string subject) => new()
    {
        Uid = 1,
        Subject = subject,
        From = "Rene <rene@example.org>",
        MessageId = "<abc@example.org>",
    };

    [Fact]
    public void ReplyAddsRe()
        => Assert.Equal("Re: Rechnung", ComposeRequest.Reply(Mail("Rechnung"), false).Subject);

    [Fact]
    public void ForwardAddsFwd()
        => Assert.Equal("Fwd: Rechnung", ComposeRequest.Forward(Mail("Rechnung")).Subject);

    [Theory]
    [InlineData("Re: Rechnung")]
    [InlineData("RE: Rechnung")]
    [InlineData("re: Rechnung")]
    public void ExistingReIsNotDoubled(string subject)
        => Assert.Equal(subject, ComposeRequest.Reply(Mail(subject), false).Subject);

    [Theory]
    [InlineData("AW: Rechnung")]
    [InlineData("Aw: Rechnung")]
    [InlineData("Antwort: Rechnung")]
    [InlineData("Re:Rechnung")]
    public void GermanAndSpacelessPrefixesAreRecognisedToo(string subject)
    {
        // Antwortet jemand aus Outlook auf Deutsch, steht dort „AW:". Ein „Re: "
        // davorzusetzen wäre doppelt gemoppelt.
        var result = ComposeRequest.Reply(Mail(subject), false).Subject;

        Assert.Equal(subject, result);
    }

    [Theory]
    [InlineData("WG: Rechnung")]
    [InlineData("Fwd: Rechnung")]
    [InlineData("FW: Rechnung")]
    public void ExistingForwardPrefixIsNotDoubled(string subject)
        => Assert.Equal(subject, ComposeRequest.Forward(Mail(subject)).Subject);

    [Fact]
    public void EmptySubjectStillGetsThePrefix()
    {
        // Sonst hiesse die Antwort auf eine betrefflose Mail auch betrefflos,
        // und der Empfänger sieht nicht, worauf sie sich bezieht.
        Assert.Equal("Re: ", ComposeRequest.Reply(Mail(""), false).Subject);
    }

    [Fact]
    public void ReplyCarriesTheThreadingHeaders()
    {
        var request = ComposeRequest.Reply(Mail("Rechnung"), false);

        Assert.Equal("<abc@example.org>", request.InReplyTo);
        Assert.Equal(1u, request.ReplyToUid);
    }

    [Fact]
    public void ForwardDeliberatelyHasNoRecipientAndNoReplyContext()
    {
        // Eine Weiterleitung geht an jemand Neues, und sie beantwortet nichts.
        var request = ComposeRequest.Forward(Mail("Rechnung"));

        Assert.Equal("", request.To);
        Assert.Null(request.ReplyToUid);
    }
}
