using NMailClient.Poc.Models;
using Xunit;

namespace NMailClient.Poc.Tests;

public class DateGroupTests
{
    private static string GroupFor(DateTime local)
        => new MailSummary { Date = new DateTimeOffset(local) }.DateGroup;

    [Fact]
    public void TodayAndYesterdayAreNamed()
    {
        Assert.Equal("Heute", GroupFor(DateTime.Now));
        Assert.Equal("Gestern", GroupFor(DateTime.Today.AddDays(-1).AddHours(9)));
    }

    [Fact]
    public void TodayIsIndependentOfTimeOfDay()
    {
        Assert.Equal("Heute", GroupFor(DateTime.Today));                    // 00:00
        Assert.Equal("Heute", GroupFor(DateTime.Today.AddHours(23.9)));     // kurz vor Mitternacht
    }

    [Fact]
    public void WeekStartsOnMonday()
    {
        // DayOfWeek zaehlt Sonntag als 0 – die Woche beginnt hier aber montags.
        var today = DateTime.Today;
        int sinceMonday = ((int)today.DayOfWeek + 6) % 7;
        var monday = today.AddDays(-sinceMonday);

        // Der Montag dieser Woche gehoert nie zu "Letzte Woche".
        Assert.Contains(GroupFor(monday), new[] { "Heute", "Gestern", "Diese Woche" });

        // Der Sonntag davor liegt in der Vorwoche – ausser heute ist Montag, dann
        // ist er zugleich "Gestern". Die taggenaue Angabe hat bewusst Vorrang,
        // sie ist fuer den Leser die nuetzlichere.
        var sunday = monday.AddDays(-1);
        var expected = today.DayOfWeek == DayOfWeek.Monday ? "Gestern" : "Letzte Woche";
        Assert.Equal(expected, GroupFor(sunday));
    }

    [Fact]
    public void DayLabelsTakePrecedenceOverWeekGrouping()
    {
        // Gestern bleibt "Gestern", auch wenn es kalendarisch in die Vorwoche faellt.
        Assert.Equal("Gestern", GroupFor(DateTime.Today.AddDays(-1)));
    }

    [Fact]
    public void OlderMessagesFallBackToMonthNames()
    {
        var longAgo = DateTime.Today.AddYears(-2);
        var group = GroupFor(longAgo);

        // Mit Jahresangabe, sonst waeren zwei Februare nicht unterscheidbar.
        Assert.Contains(longAgo.Year.ToString(), group);
    }

    [Fact]
    public void GroupsAreStableForTheSameDay()
    {
        var day = DateTime.Today.AddDays(-40);
        Assert.Equal(GroupFor(day.AddHours(1)), GroupFor(day.AddHours(20)));
    }
}

public class ListDensityTests
{
    [Theory]
    [InlineData("Compact", 3, 24)]
    [InlineData("Normal", 7, 32)]
    [InlineData("Comfortable", 12, 38)]
    [InlineData("quatsch", 7, 32)]  // unbekannter Wert faellt auf Normal zurueck
    public void DerivesSpacingAndAvatarSize(string density, double spacing, double avatar)
    {
        var s = new AppSettings { ListDensity = density };

        Assert.Equal(spacing, s.RowSpacing);
        Assert.Equal(avatar, s.AvatarSize);
    }

    [Fact]
    public void DerivedValuesAreNotPersisted()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new AppSettings());

        Assert.Contains("listDensity", json);
        Assert.DoesNotContain("RowSpacing", json);
        Assert.DoesNotContain("AvatarSize", json);
    }
}

public class MoveOutcomeTests
{
    [Fact]
    public void NoneCannotBeUndone()
    {
        Assert.False(MoveOutcome.None.CanUndo);
        Assert.Null(MoveOutcome.None.TargetFolder);
    }

    [Fact]
    public void WithoutTargetUidsUndoIsNotOffered()
    {
        // Server ohne UIDPLUS: Zielordner bekannt, aber keine UIDs – Rueckgaengig
        // waere nicht durchfuehrbar und darf deshalb nicht angeboten werden.
        var outcome = new MoveOutcome("Trash", []);
        Assert.False(outcome.CanUndo);
    }

    [Fact]
    public void WithTargetAndUidsUndoIsPossible()
        => Assert.True(new MoveOutcome("Trash", [1u, 2u]).CanUndo);
}
