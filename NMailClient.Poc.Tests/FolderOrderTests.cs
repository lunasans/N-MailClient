using NMailClient.Poc.Models;
using NMailClient.Poc.Services;
using Xunit;

namespace NMailClient.Poc.Tests;

public class FolderOrderTests
{
    private static FolderInfo F(string name) => new(name, name, '/', 0, true);

    [Fact]
    public void CustomOrderIsApplied()
    {
        var tree = ImapService.BuildTree(
            [F("INBOX"), F("Alpha"), F("Beta"), F("Gamma")],
            order: ["Gamma", "Alpha", "Beta"]);

        Assert.Equal(["INBOX", "Gamma", "Alpha", "Beta"], tree.Select(n => n.FullName).ToArray());
    }

    [Fact]
    public void InboxStaysFirstEvenIfListedElsewhere()
    {
        var tree = ImapService.BuildTree(
            [F("INBOX"), F("Alpha")], order: ["Alpha", "INBOX"]);

        Assert.Equal("INBOX", tree[0].FullName);
    }

    [Fact]
    public void UnlistedFoldersFollowAlphabetically()
    {
        // Neue Serverordner sollen hinten anhängen, nicht an zufälliger Stelle
        // zwischen den sortierten auftauchen.
        var tree = ImapService.BuildTree(
            [F("INBOX"), F("Zeta"), F("Alpha"), F("Neu")],
            order: ["Zeta"]);

        Assert.Equal(["INBOX", "Zeta", "Alpha", "Neu"], tree.Select(n => n.FullName).ToArray());
    }

    [Fact]
    public void WithoutOrderEverythingIsAlphabetical()
    {
        var tree = ImapService.BuildTree([F("INBOX"), F("Zeta"), F("Alpha")]);

        Assert.Equal(["INBOX", "Alpha", "Zeta"], tree.Select(n => n.FullName).ToArray());
    }

    [Fact]
    public void StaleEntriesInOrderAreHarmless()
    {
        // Ordner, der serverseitig gelöscht wurde, steht noch in der Reihenfolge.
        var tree = ImapService.BuildTree(
            [F("INBOX"), F("Alpha")], order: ["Weg", "Alpha"]);

        Assert.Equal(["INBOX", "Alpha"], tree.Select(n => n.FullName).ToArray());
    }

    [Fact]
    public void OrderIsCaseInsensitive()
    {
        var tree = ImapService.BuildTree(
            [F("INBOX"), F("Alpha"), F("Beta")], order: ["BETA", "alpha"]);

        Assert.Equal(["INBOX", "Beta", "Alpha"], tree.Select(n => n.FullName).ToArray());
    }
}
