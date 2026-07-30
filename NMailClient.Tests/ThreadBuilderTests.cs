using NMailClient.Models;
using NMailClient.Services;
using Xunit;

namespace NMailClient.Tests;

public class SubjectNormalizationTests
{
    [Theory]
    [InlineData("Re: Rechnung", "Rechnung")]
    [InlineData("AW: Rechnung", "Rechnung")]
    [InlineData("Fwd: Rechnung", "Rechnung")]
    [InlineData("WG: Rechnung", "Rechnung")]
    [InlineData("Re: AW: Re: Rechnung", "Rechnung")]
    [InlineData("Re[2]: Rechnung", "Rechnung")]
    [InlineData("Rechnung", "Rechnung")]
    public void StripsReplyPrefixes(string subject, string expected)
        => Assert.Equal(expected, ThreadBuilder.NormalizeSubject(subject));

    [Fact]
    public void LeavesInnerOccurrencesAlone()
        => Assert.Equal("Bericht: Quartal", ThreadBuilder.NormalizeSubject("Bericht: Quartal"));

    [Fact]
    public void HandlesEmptySubject()
        => Assert.Equal("", ThreadBuilder.NormalizeSubject(""));
}

public class ThreadBuilderTests
{
    private static MailSummary M(
        uint uid, string id, string subject = "Thema", string? inReplyTo = null,
        int minutes = 0, bool seen = true, bool flagged = false)
        => new()
        {
            Uid = uid,
            MessageId = id,
            Subject = subject,
            Date = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero).AddMinutes(minutes),
            ThreadReferences = inReplyTo is null ? [] : [inReplyTo],
            Seen = seen,
            Flagged = flagged,
        };

    [Fact]
    public void SingleMessageBecomesOneThread()
    {
        var groups = ThreadBuilder.Build([M(1, "a@x")]);

        var g = Assert.Single(groups);
        Assert.Single(g);
    }

    [Fact]
    public void ReplyJoinsItsOriginal()
    {
        var groups = ThreadBuilder.Build([
            M(1, "a@x", minutes: 0),
            M(2, "b@x", inReplyTo: "a@x", minutes: 10),
        ]);

        var g = Assert.Single(groups);
        Assert.Equal(2, g.Count);
        // Innerhalb der Gruppe aufsteigend nach Datum.
        Assert.Equal(1u, g[0].Uid);
        Assert.Equal(2u, g[1].Uid);
    }

    [Fact]
    public void ChainOfRepliesFormsOneThread()
    {
        var groups = ThreadBuilder.Build([
            M(1, "a@x", minutes: 0),
            M(2, "b@x", inReplyTo: "a@x", minutes: 5),
            M(3, "c@x", inReplyTo: "b@x", minutes: 10),
        ]);

        Assert.Single(groups);
        Assert.Equal(3, groups[0].Count);
    }

    [Fact]
    public void RepliesArrivingBeforeTheirParentStillJoin()
    {
        // Reihenfolge der Eingabe darf keine Rolle spielen.
        var groups = ThreadBuilder.Build([
            M(3, "c@x", inReplyTo: "b@x", minutes: 10),
            M(2, "b@x", inReplyTo: "a@x", minutes: 5),
            M(1, "a@x", minutes: 0),
        ]);

        Assert.Single(groups);
        Assert.Equal(3, groups[0].Count);
    }

    [Fact]
    public void DifferentConversationsStaySeparate()
    {
        var groups = ThreadBuilder.Build([
            M(1, "a@x", subject: "Eins"),
            M(2, "b@x", subject: "Zwei"),
        ]);

        Assert.Equal(2, groups.Count);
    }

    [Fact]
    public void SameSubjectFromUnrelatedSendersIsNotMerged()
    {
        // Der entscheidende Vorteil gegenüber Betreff-Gruppierung: zwei Mails
        // „Rechnung" mit eigenen IDs und ohne Bezug sind zwei Konversationen.
        var groups = ThreadBuilder.Build([
            M(1, "a@x", subject: "Rechnung"),
            M(2, "b@x", subject: "Rechnung"),
        ]);

        Assert.Equal(2, groups.Count);
    }

    [Fact]
    public void ChangedSubjectStillBelongsToTheThread()
    {
        var groups = ThreadBuilder.Build([
            M(1, "a@x", subject: "Angebot"),
            M(2, "b@x", subject: "Re: Angebot – neuer Termin", inReplyTo: "a@x"),
        ]);

        Assert.Single(groups);
    }

    [Fact]
    public void FallsBackToSubjectWhenHeadersAreMissing()
    {
        // Absender ohne Message-ID/References: Betreff als Rückfall.
        var a = new MailSummary { Uid = 1, Subject = "Newsletter", Date = DateTimeOffset.UtcNow };
        var b = new MailSummary { Uid = 2, Subject = "Re: Newsletter", Date = DateTimeOffset.UtcNow };

        var groups = ThreadBuilder.Build([a, b]);

        Assert.Single(groups);
    }

    [Fact]
    public void GroupsAreOrderedByNewestMessage()
    {
        var groups = ThreadBuilder.Build([
            M(1, "alt@x", subject: "Alt", minutes: 0),
            M(2, "neu@x", subject: "Neu", minutes: 100),
        ]);

        Assert.Equal("Neu", groups[0][0].Subject);
    }

    [Fact]
    public void ReferencesLinkAcrossMissingMiddleMessage()
    {
        // Die mittlere Nachricht liegt nicht im Ordner; References verbindet trotzdem.
        var a = M(1, "a@x", minutes: 0);
        var c = new MailSummary
        {
            Uid = 3, MessageId = "c@x", Subject = "Re: Thema",
            Date = a.Date.AddMinutes(20),
            ThreadReferences = ["a@x", "b@x"],
        };

        var groups = ThreadBuilder.Build([a, c]);

        Assert.Single(groups);
        Assert.Equal(2, groups[0].Count);
    }

    [Fact]
    public void HandlesEmptyInput()
        => Assert.Empty(ThreadBuilder.Build([]));

    [Fact]
    public void MessagesWithoutIdOrSubjectStayIndividual()
    {
        var a = new MailSummary { Uid = 1, Subject = "", Date = DateTimeOffset.UtcNow };
        var b = new MailSummary { Uid = 2, Subject = "", Date = DateTimeOffset.UtcNow };

        Assert.Equal(2, ThreadBuilder.Build([a, b]).Count);
    }
}
