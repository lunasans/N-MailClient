using NMailClient.Models;
using Xunit;

namespace NMailClient.Tests;

public class FolderNodeTests
{
    [Fact]
    public void UnreadRaisesBothPropertyChanges()
    {
        var f = new FolderNode { Name = "Inbox" };
        var raised = new List<string?>();
        f.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        f.Unread = 3;

        // HasUnread steuert die Sichtbarkeit des Badges und muss mitgemeldet werden.
        Assert.Contains("Unread", raised);
        Assert.Contains("HasUnread", raised);
        Assert.True(f.HasUnread);
    }

    [Fact]
    public void UnchangedUnreadRaisesNothing()
    {
        var f = new FolderNode { Unread = 2 };
        var raised = 0;
        f.PropertyChanged += (_, _) => raised++;

        f.Unread = 2;

        Assert.Equal(0, raised);
    }

    [Theory]
    [InlineData(0, "Posteingang")]
    [InlineData(1, "    Posteingang")]
    [InlineData(2, "        Posteingang")]
    public void IndentsByDepthForFlatMenu(int depth, string expected)
    {
        var f = new FolderNode { Name = "Posteingang", Depth = depth };
        Assert.Equal(expected, f.IndentedName);
    }
}

/// <summary>
/// Die Entscheidung „setzen statt umschalten" bei gemischter Auswahl ist die
/// fehleranfällige Stelle der Batch-Aktionen. Hier als reine Logik geprüft,
/// unabhängig von IMAP.
/// </summary>
public class BatchDecisionTests
{
    // Spiegelt MainViewModel: value = !targets.All(...)
    private static bool TargetValue(params bool[] current) => !current.All(x => x);

    [Fact]
    public void AllSetMeansUnset()
        => Assert.False(TargetValue(true, true, true));

    [Fact]
    public void AllUnsetMeansSet()
        => Assert.True(TargetValue(false, false, false));

    [Fact]
    public void MixedSelectionSetsRatherThanToggles()
    {
        // Entscheidend: bei gemischter Auswahl wird einheitlich gesetzt – sonst
        // hänge das Ergebnis von der Reihenfolge ab und wäre nicht vorhersehbar.
        Assert.True(TargetValue(true, false));
        Assert.True(TargetValue(false, true));
        Assert.True(TargetValue(true, false, true));
    }

    [Fact]
    public void SingleMessageBehavesLikeToggle()
    {
        Assert.True(TargetValue(false));
        Assert.False(TargetValue(true));
    }
}
