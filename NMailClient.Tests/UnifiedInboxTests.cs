using NMailClient.Models;
using NMailClient.Services;
using Xunit;

namespace NMailClient.Tests;

/// <summary>
/// Der gemeinsame Posteingang stellt Nachrichten mehrerer Konten nebeneinander.
/// Damit hängt die Zuordnung nicht mehr an der Auswahl, sondern an der Zeile —
/// und ein Fehler darin träfe das falsche Postfach.
/// </summary>
public class UnifiedInboxTests
{
    private static MailSummary Message(
        uint uid, string accountId = "", string folder = "", int daysAgo = 0) => new()
    {
        Uid = uid,
        AccountId = accountId,
        Folder = folder,
        Date = DateTimeOffset.UtcNow.AddDays(-daysAgo),
        Subject = $"Nachricht {uid}",
    };

    // ---- Herkunft einer Zeile ----------------------------------------------

    [Fact]
    public void TheRowDecidesNotTheView()
    {
        // Der Kern: die Ansicht zeigt Konto A, die Zeile gehört zu B. Ohne
        // diese Auflösung liefe ein Löschen gegen A.
        var origin = MessageGrouping.OriginOf(
            Message(1, "B", "Archiv"), fallbackAccountId: "A", fallbackFolder: "INBOX");

        Assert.Equal("B", origin.AccountId);
        Assert.Equal("Archiv", origin.Folder);
    }

    [Fact]
    public void ARowWithoutAnOriginFallsBackToTheView()
    {
        // Zeilen aus einem älteren Zwischenspeicher tragen den Vermerk nicht.
        var origin = MessageGrouping.OriginOf(Message(1), "A", "INBOX");

        Assert.Equal("A", origin.AccountId);
        Assert.Equal("INBOX", origin.Folder);
    }

    [Fact]
    public void OnlyTheMissingHalfFallsBack()
    {
        var origin = MessageGrouping.OriginOf(Message(1, "B"), "A", "INBOX");

        Assert.Equal("B", origin.AccountId);
        Assert.Equal("INBOX", origin.Folder);
    }

    // ---- Aufteilen einer Auswahl -------------------------------------------

    [Fact]
    public void ASelectionIsSplitByMailbox()
    {
        var groups = MessageGrouping.ByOrigin(
        [
            Message(1, "A", "INBOX"),
            Message(2, "B", "INBOX"),
            Message(3, "A", "INBOX"),
        ], "A", "INBOX");

        Assert.Equal(2, groups.Count);
        Assert.Equal(2, groups.Single(g => g.Origin.AccountId == "A").Messages.Count);
        Assert.Single(groups.Single(g => g.Origin.AccountId == "B").Messages);
    }

    [Fact]
    public void TheSameAccountInDifferentFoldersStaysSeparate()
    {
        // Ein Löschen muss den Ordner treffen, in dem die Nachricht liegt.
        var groups = MessageGrouping.ByOrigin(
            [Message(1, "A", "INBOX"), Message(2, "A", "Archiv")], "A", "INBOX");

        Assert.Equal(2, groups.Count);
    }

    [Fact]
    public void EveryMessageEndsUpInExactlyOneGroup()
    {
        var selection = new[]
        {
            Message(1, "A", "INBOX"), Message(2, "B", "INBOX"),
            Message(3, "A", "Archiv"), Message(4), Message(5, "B", "INBOX"),
        };

        var groups = MessageGrouping.ByOrigin(selection, "A", "INBOX");

        Assert.Equal(selection.Length, groups.Sum(g => g.Messages.Count));
        Assert.Equal(
            selection.Select(m => m.Uid).OrderBy(u => u),
            groups.SelectMany(g => g.Messages).Select(m => m.Uid).OrderBy(u => u));
    }

    [Fact]
    public void AnEmptySelectionYieldsNoGroups()
        => Assert.Empty(MessageGrouping.ByOrigin([], "A", "INBOX"));

    [Fact]
    public void OneMailboxYieldsASingleGroup()
    {
        // Wichtig für das Zurücknehmen: nur bei genau einer Gruppe wird es
        // angeboten, weil es über mehrere Konten nur halb umkehrbar wäre.
        var groups = MessageGrouping.ByOrigin(
            [Message(1, "A", "INBOX"), Message(2, "A", "INBOX")], "A", "INBOX");

        Assert.Single(groups);
    }

    // ---- Zusammenführen ----------------------------------------------------

    [Fact]
    public void MessagesOfAllAccountsAreMergedNewestFirst()
    {
        var merged = MessageGrouping.MergeNewest(
        [
            Message(1, "A", daysAgo: 3), Message(2, "A", daysAgo: 1),
            Message(3, "B", daysAgo: 2), Message(4, "B", daysAgo: 0),
        ], 50);

        Assert.Equal([4u, 2u, 3u, 1u], merged.Select(m => m.Uid));
    }

    [Fact]
    public void TheMergedListDoesNotGrowWithTheNumberOfAccounts()
    {
        // Bei drei Konten mit je einer vollen Seite stünden sonst dreimal so
        // viele Zeilen da wie in jedem einzelnen Ordner.
        const int pageSize = 50;
        var all = Enumerable.Range(0, 3)
            .SelectMany(k => Enumerable.Range(0, pageSize)
                .Select(i => Message((uint)(k * 100 + i), $"K{k}", daysAgo: i)));

        Assert.Equal(pageSize, MessageGrouping.MergeNewest(all, pageSize).Count);
    }

    [Fact]
    public void TheNewestMessageWinsRegardlessOfAccount()
    {
        var merged = MessageGrouping.MergeNewest(
            [Message(1, "A", daysAgo: 5), Message(2, "B", daysAgo: 0)], 1);

        Assert.Equal(2u, Assert.Single(merged).Uid);
    }

    // ---- Der virtuelle Ordner ----------------------------------------------

    [Fact]
    public void TheUnifiedFolderIsMarkedAsSuch()
        => Assert.True(new FolderNode { FullName = " unified", IsUnified = true }.IsUnified);

    [Fact]
    public void ARealFolderIsNotUnified()
        => Assert.False(new FolderNode { FullName = "INBOX" }.IsUnified);
}
