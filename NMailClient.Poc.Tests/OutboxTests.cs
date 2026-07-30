using System.Text.Json;
using NMailClient.Poc.Models;
using Xunit;

namespace NMailClient.Poc.Tests;

public class OutboxItemTests
{
    [Fact]
    public void FutureItemIsPendingAndCancellable()
    {
        var item = new OutboxItem { SendAt = DateTimeOffset.Now.AddSeconds(10) };

        Assert.True(item.IsPending);
        Assert.InRange(item.SecondsLeft, 9, 10);
    }

    [Fact]
    public void DueItemIsNoLongerPending()
    {
        var item = new OutboxItem { SendAt = DateTimeOffset.Now.AddSeconds(-1) };

        Assert.False(item.IsPending);
        Assert.Equal(0, item.SecondsLeft);
    }

    [Fact]
    public void SecondsLeftNeverGoesNegative()
        => Assert.Equal(0, new OutboxItem { SendAt = DateTimeOffset.Now.AddHours(-5) }.SecondsLeft);

    [Fact]
    public void DisplayFallsBackForEmptySubject()
        => Assert.Equal("(kein Betreff)", new OutboxItem().Display);

    [Fact]
    public void SurvivesRoundTrip()
    {
        var item = new OutboxItem
        {
            AccountId = "acc1",
            SendAt = new DateTimeOffset(2026, 3, 1, 8, 0, 0, TimeSpan.Zero),
            Subject = "Test", To = "a@b.c",
            DraftUid = 42, ReplyFolder = "INBOX", ReplyUid = 7,
        };

        var back = JsonSerializer.Deserialize<OutboxItem>(JsonSerializer.Serialize(item))!;

        Assert.Equal("acc1", back.AccountId);
        Assert.Equal(item.SendAt, back.SendAt);
        Assert.Equal(42u, back.DraftUid);
        Assert.Equal("INBOX", back.ReplyFolder);
        Assert.Equal(7u, back.ReplyUid);
    }

    [Fact]
    public void DerivedFieldsAreNotPersisted()
    {
        var json = JsonSerializer.Serialize(new OutboxItem());

        Assert.DoesNotContain("IsPending", json);
        Assert.DoesNotContain("SecondsLeft", json);
        Assert.DoesNotContain("Display", json);
    }
}

public class UndoSendSettingTests
{
    [Theory]
    [InlineData(10, 10)]
    [InlineData(0, 0)]      // sofort senden ist erlaubt
    [InlineData(-5, 0)]
    [InlineData(9999, 120)] // gedeckelt, sonst hängt die Mail ewig fest
    public void ClampsToUsableRange(int stored, int expected)
        => Assert.Equal(expected, new AppSettings { UndoSendSeconds = stored }.EffectiveUndoSeconds);

    [Fact]
    public void DefaultsToTenSeconds()
        => Assert.Equal(10, new AppSettings().UndoSendSeconds);
}
