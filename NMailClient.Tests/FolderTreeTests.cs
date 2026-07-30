using NMailClient.Models;
using NMailClient.Services;
using Xunit;

namespace NMailClient.Tests;

/// <summary>
/// Hierarchie-Aufbau des Ordnerbaums. Entstanden aus einem Fehler, bei dem
/// Unterordner des Posteingangs gar nicht erschienen.
/// </summary>
public class FolderTreeTests
{
    private static FolderInfo F(string fullName, char delim = '/', int unread = 0)
        => new(fullName, fullName.Contains(delim) ? fullName[(fullName.LastIndexOf(delim) + 1)..] : fullName,
               delim, unread, true);

    [Fact]
    public void InboxSubfoldersAppearUnderInbox()
    {
        var tree = ImapService.BuildTree([F("INBOX"), F("INBOX/Archiv"), F("INBOX/Projekte")]);

        var inbox = Assert.Single(tree);
        Assert.Equal("INBOX", inbox.FullName);
        Assert.Equal(2, inbox.Children.Count);
        Assert.Contains(inbox.Children, c => c.Name == "Archiv");
        Assert.Contains(inbox.Children, c => c.Name == "Projekte");
    }

    [Fact]
    public void ChildrenFindTheirParentRegardlessOfOrder()
    {
        // Eine LIST-Antwort mit Platzhalter sichert nicht zu, dass Elternordner
        // vor ihren Kindern kommen. Früher stand ein zu früh genanntes Kind
        // fälschlich auf oberster Ebene.
        var tree = ImapService.BuildTree(
            [F("INBOX/Archiv/2024"), F("INBOX/Archiv"), F("INBOX")]);

        var inbox = Assert.Single(tree);
        var archiv = Assert.Single(inbox.Children);
        Assert.Equal("INBOX/Archiv", archiv.FullName);
        Assert.Equal("INBOX/Archiv/2024", Assert.Single(archiv.Children).FullName);
    }

    [Fact]
    public void ADuplicateEntryAppearsOnlyOnce()
    {
        // Genau das gemeldete Bild: derselbe Ordner mehrfach in der Liste.
        var tree = ImapService.BuildTree(
            [F("INBOX"), F("INBOX/Archiv"), F("INBOX"), F("INBOX/Archiv")]);

        var inbox = Assert.Single(tree);
        Assert.Single(inbox.Children);
    }

    [Fact]
    public void EveryFolderAppearsExactlyOnceInTheWholeTree()
    {
        var tree = ImapService.BuildTree(
        [
            F("INBOX"), F("INBOX/Archiv"), F("INBOX/Archiv/2024"),
            F("Entwürfe"), F("Gesendet"), F("INBOX"),
        ]);

        var seen = new List<string>();
        void Walk(IEnumerable<FolderNode> nodes)
        {
            foreach (var n in nodes) { seen.Add(n.FullName); Walk(n.Children); }
        }
        Walk(tree);

        Assert.Equal(seen.Count, seen.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(5, seen.Count);
    }

    [Fact]
    public void SupportsDotDelimiterUsedByDovecot()
    {
        // mailcow/Dovecot mit Maildir++ nutzt "." als Trennzeichen.
        var tree = ImapService.BuildTree(
            [F("INBOX", '.'), F("INBOX.Sent", '.'), F("INBOX.Sent.2024", '.')]);

        var inbox = Assert.Single(tree);
        var sent = Assert.Single(inbox.Children);
        Assert.Equal("Sent", sent.Name);

        var year = Assert.Single(sent.Children);
        Assert.Equal("2024", year.Name);
    }

    [Fact]
    public void NestsSeveralLevelsDeep()
    {
        var tree = ImapService.BuildTree(
            [F("INBOX"), F("INBOX/a"), F("INBOX/a/b"), F("INBOX/a/b/c")]);

        var a = Assert.Single(tree[0].Children);
        var b = Assert.Single(a.Children);
        var c = Assert.Single(b.Children);
        Assert.Equal("c", c.Name);
    }

    [Fact]
    public void InboxComesFirstRegardlessOfServerOrder()
    {
        var tree = ImapService.BuildTree([F("Archiv"), F("INBOX"), F("Entwürfe")]);

        Assert.Equal("INBOX", tree[0].FullName);
    }

    [Fact]
    public void SiblingsAreSortedAlphabetically()
    {
        var tree = ImapService.BuildTree(
            [F("INBOX"), F("INBOX/zeta"), F("INBOX/alpha"), F("INBOX/Mitte")]);

        var names = tree[0].Children.Select(c => c.Name).ToArray();
        Assert.Equal(["alpha", "Mitte", "zeta"], names);
    }

    [Fact]
    public void TopLevelFoldersStayRoots()
    {
        var tree = ImapService.BuildTree([F("INBOX"), F("Archiv"), F("Archiv/2024")]);

        Assert.Equal(2, tree.Count);
        var archiv = tree.Single(n => n.FullName == "Archiv");
        Assert.Single(archiv.Children);
    }

    [Fact]
    public void OrphanWithoutKnownParentBecomesRoot()
    {
        // Server meldet ein Kind, dessen Elternordner nicht gelistet ist –
        // es darf nicht verloren gehen.
        var tree = ImapService.BuildTree([F("INBOX"), F("Fremd/Unterordner")]);

        Assert.Equal(2, tree.Count);
    }

    [Fact]
    public void FlatServerWithoutDelimiterYieldsOnlyRoots()
    {
        var tree = ImapService.BuildTree(
            [new FolderInfo("INBOX", "INBOX", '\0', 0, true),
             new FolderInfo("Archiv", "Archiv", '\0', 0, true)]);

        Assert.Equal(2, tree.Count);
        Assert.All(tree, n => Assert.Empty(n.Children));
    }

    [Fact]
    public void KeepsUnreadCountsAndSelectability()
    {
        var tree = ImapService.BuildTree(
            [F("INBOX", '/', 5), new FolderInfo("Ordner", "Ordner", '/', 0, false)]);

        Assert.Equal(5, tree[0].Unread);
        Assert.True(tree[0].HasUnread);
        Assert.False(tree.Single(n => n.FullName == "Ordner").IsSelectable);
    }
}
