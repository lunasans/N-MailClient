using System.IO;
using NMailClient.Models;
using NMailClient.Services;
using Xunit;

namespace NMailClient.Tests;

public class MessageBuildTests
{
    private static readonly Account Acc = new()
    {
        Name = "Rene", Email = "rene@example.org",
    };

    [Fact]
    public async Task BuildsFromSubjectAndRecipients()
    {
        var msg = await SmtpService.BuildMessageAsync(
            Acc, "a@b.c, Zwei <zwei@b.c>", "cc@b.c", "Betreff", "Hallo", null, lenient: false, ct: TestContext.Current.CancellationToken);

        Assert.Equal("Betreff", msg.Subject);
        Assert.Equal(2, msg.To.Count);
        Assert.Single(msg.Cc);
        Assert.Contains("rene@example.org", msg.From.ToString());
        Assert.Equal("Hallo", msg.TextBody);
    }

    [Fact]
    public async Task StrictModeRequiresARecipient()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SmtpService.BuildMessageAsync(Acc, "", "", "x", "y", null, lenient: false, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StrictModeRejectsInvalidAddresses()
    {
        await Assert.ThrowsAsync<FormatException>(() =>
            SmtpService.BuildMessageAsync(Acc, "keine-adresse", "", "x", "y", null, lenient: false, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LenientModeAllowsEmptyAndHalfTypedRecipients()
    {
        // Entwurfsfall: der Nutzer tippt noch – Speichern darf nicht scheitern.
        var msg = await SmtpService.BuildMessageAsync(
            Acc, "halb@", "", "Entwurf", "Text", null, lenient: true, ct: TestContext.Current.CancellationToken);

        Assert.Equal("Entwurf", msg.Subject);
        Assert.Equal("Text", msg.TextBody);
    }

    [Fact]
    public async Task SemicolonsWorkAsSeparators()
    {
        var msg = await SmtpService.BuildMessageAsync(
            Acc, "a@b.c; d@e.f", "", "x", "y", null, lenient: false, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, msg.To.Count);
    }

    [Fact]
    public async Task ReplyCarriesThreadHeaders()
    {
        var msg = await SmtpService.BuildMessageAsync(
            Acc, "a@b.c", "", "Re: x", "y", null, lenient: false,
            inReplyTo: "id3@example.org",
            references: ["id1@example.org", "id2@example.org"],
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("id3@example.org", msg.InReplyTo);
        // RFC 5322: Kette des Originals plus dessen eigene ID, ohne Duplikat.
        Assert.Equal(["id1@example.org", "id2@example.org", "id3@example.org"],
            msg.References.ToArray());
    }

    [Fact]
    public async Task ReplyToAlreadyReferencedMessageAddsNoDuplicate()
    {
        var msg = await SmtpService.BuildMessageAsync(
            Acc, "a@b.c", "", "Re: x", "y", null, lenient: false,
            inReplyTo: "id1@example.org", references: ["id1@example.org"],
            ct: TestContext.Current.CancellationToken);

        Assert.Single(msg.References);
    }

    [Fact]
    public async Task FreshMessageHasNoThreadHeaders()
    {
        var msg = await SmtpService.BuildMessageAsync(
            Acc, "a@b.c", "", "x", "y", null, lenient: false,
            ct: TestContext.Current.CancellationToken);

        Assert.Null(msg.InReplyTo);
        Assert.Empty(msg.References);
    }

    [Fact]
    public async Task HtmlBodyProducesMultipartAlternative()
    {
        var msg = await SmtpService.BuildMessageAsync(
            Acc, "a@b.c", "", "x", "Nur Text", null, lenient: false,
            html: "<p>Mit <strong>Auszeichnung</strong></p>",
            ct: TestContext.Current.CancellationToken);

        // Beide Fassungen müssen vorhanden sein.
        Assert.Contains("Auszeichnung", msg.HtmlBody);
        Assert.Equal("Nur Text", msg.TextBody);
    }

    [Fact]
    public async Task WithoutHtmlOnlyTextIsSent()
    {
        var msg = await SmtpService.BuildMessageAsync(
            Acc, "a@b.c", "", "x", "Nur Text", null, lenient: false,
            ct: TestContext.Current.CancellationToken);

        Assert.Null(msg.HtmlBody);
        Assert.Equal("Nur Text", msg.TextBody);
    }

    [Fact]
    public async Task BccRecipientsAreSet()
    {
        var msg = await SmtpService.BuildMessageAsync(
            Acc, "a@b.c", "", "x", "y", null, lenient: false, bcc: "blind@b.c",
            ct: TestContext.Current.CancellationToken);

        Assert.Single(msg.Bcc);
        Assert.Contains("blind@b.c", msg.Bcc.ToString());
    }

    [Fact]
    public async Task BccAloneIsAValidRecipient()
    {
        var msg = await SmtpService.BuildMessageAsync(
            Acc, "", "", "x", "y", null, lenient: false, bcc: "blind@b.c",
            ct: TestContext.Current.CancellationToken);

        Assert.Single(msg.Bcc);
    }

    [Fact]
    public async Task AliasReplacesTheFromAddress()
    {
        var alias = new AliasDef { Address = "info@example.org", Name = "Info" };

        var msg = await SmtpService.BuildMessageAsync(
            Acc, "a@b.c", "", "x", "y", null, lenient: false, sender: alias,
            ct: TestContext.Current.CancellationToken);

        Assert.Contains("info@example.org", msg.From.ToString());
        Assert.DoesNotContain("rene@example.org", msg.From.ToString());
    }

    [Fact]
    public async Task WithoutAliasTheAccountAddressIsUsed()
    {
        var msg = await SmtpService.BuildMessageAsync(
            Acc, "a@b.c", "", "x", "y", null, lenient: false,
            ct: TestContext.Current.CancellationToken);

        Assert.Contains("rene@example.org", msg.From.ToString());
    }

    [Fact]
    public async Task ReadReceiptHeaderPointsAtTheSender()
    {
        var alias = new AliasDef { Address = "info@example.org" };

        var msg = await SmtpService.BuildMessageAsync(
            Acc, "a@b.c", "", "x", "y", null, lenient: false, sender: alias,
            requestReceipt: true, ct: TestContext.Current.CancellationToken);

        // Die Bestätigung muss an den tatsächlichen Absender gehen, nicht ans Konto.
        Assert.Equal("info@example.org", msg.Headers["Disposition-Notification-To"]);
    }

    [Fact]
    public async Task WithoutRequestNoReceiptHeader()
    {
        var msg = await SmtpService.BuildMessageAsync(
            Acc, "a@b.c", "", "x", "y", null, lenient: false,
            ct: TestContext.Current.CancellationToken);

        Assert.Null(msg.Headers["Disposition-Notification-To"]);
    }

    [Fact]
    public async Task AttachmentsBecomeMimeParts()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"nmc-test-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(tmp, "Inhalt", TestContext.Current.CancellationToken);
        try
        {
            var msg = await SmtpService.BuildMessageAsync(
                Acc, "a@b.c", "", "x", "y", [tmp], lenient: false, ct: TestContext.Current.CancellationToken);

            var att = Assert.Single(msg.Attachments);
            Assert.Equal(Path.GetFileName(tmp), ((MimeKit.MimePart)att).FileName);
        }
        finally { File.Delete(tmp); }
    }
}
