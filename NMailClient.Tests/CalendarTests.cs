using NMailClient.Models;
using NMailClient.Services;
using NMailClient.Services.Dav;
using Xunit;

namespace NMailClient.Tests;

public class CalendarRoundTripTests
{
    [Fact]
    public void TimedEventSurvivesSerializationAndParsing()
    {
        var item = new CalendarItem
        {
            Uid = "evt-1",
            Summary = "Besprechung",
            Description = "Mit Team",
            Location = "Büro",
            Start = new DateTime(2026, 8, 3, 10, 0, 0),
            End = new DateTime(2026, 8, 3, 11, 30, 0),
        };

        var text = CalDavService.Serialize(item);
        var back = Assert.Single(CalDavService.ParseEvents(text, DateTime.MinValue, DateTime.MaxValue));

        Assert.Equal("Besprechung", back.Summary);
        Assert.Equal("Büro", back.Location);
        Assert.False(back.AllDay);
        Assert.Equal(new DateTime(2026, 8, 3, 10, 0, 0), back.Start);
        Assert.Equal(new DateTime(2026, 8, 3, 11, 30, 0), back.End);
    }

    [Fact]
    public void AllDayEventStaysAllDay()
    {
        var item = new CalendarItem
        {
            Uid = "evt-2", Summary = "Feiertag", AllDay = true,
            Start = new DateTime(2026, 8, 3), End = new DateTime(2026, 8, 4),
        };

        var back = Assert.Single(CalDavService.ParseEvents(
            CalDavService.Serialize(item), DateTime.MinValue, DateTime.MaxValue));

        Assert.True(back.AllDay);
        Assert.Equal(new DateTime(2026, 8, 3), back.Start);
    }

    [Fact]
    public void EventWithoutEndGetsASensibleDefault()
    {
        // DTEND fehlt – ohne Vorgabe stünde der Termin auf null Länge.
        var text = """
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:ohne-ende
            DTSTART:20260803T100000Z
            SUMMARY:Kurz
            END:VEVENT
            END:VCALENDAR
            """;

        var item = Assert.Single(CalDavService.ParseEvents(text, DateTime.MinValue, DateTime.MaxValue));
        Assert.True(item.End > item.Start);
    }

    [Fact]
    public void BrokenInputYieldsNothingInsteadOfThrowing()
        => Assert.Empty(CalDavService.ParseEvents("kein iCalendar", DateTime.MinValue, DateTime.MaxValue));

    [Fact]
    public void RecurringEventIsExpandedWithinTheRange()
    {
        var text = """
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:woechentlich
            DTSTART:20260803T100000Z
            DTEND:20260803T110000Z
            RRULE:FREQ=WEEKLY;COUNT=5
            SUMMARY:Jour fixe
            END:VEVENT
            END:VCALENDAR
            """;

        var items = CalDavService.ParseEvents(
            text, new DateTime(2026, 8, 1), new DateTime(2026, 8, 31));

        // Vier Montage im August liegen im Zeitraum (3., 10., 17., 24., 31. – der
        // fünfte Termin fällt auf den 31., der noch dazugehört).
        Assert.True(items.Count >= 4, $"Erwartet mindestens 4 Termine, bekam {items.Count}");
        Assert.All(items, i => Assert.Equal("Jour fixe", i.Summary));
    }
}

public class CalendarItemTests
{
    [Fact]
    public void CoversDayIncludesStartAndEndDay()
    {
        var item = new CalendarItem
        {
            Start = new DateTime(2026, 8, 3, 22, 0, 0),
            End = new DateTime(2026, 8, 5, 2, 0, 0),
        };

        Assert.True(item.CoversDay(new DateTime(2026, 8, 3)));
        Assert.True(item.CoversDay(new DateTime(2026, 8, 4)));
        Assert.True(item.CoversDay(new DateTime(2026, 8, 5)));
        Assert.False(item.CoversDay(new DateTime(2026, 8, 6)));
        Assert.False(item.CoversDay(new DateTime(2026, 8, 2)));
    }

    [Fact]
    public void EventEndingAtMidnightDoesNotSpillIntoTheNextDay()
    {
        // 10:00–00:00 gehört zum 3., nicht auch noch zum 4.
        var item = new CalendarItem
        {
            Start = new DateTime(2026, 8, 3, 10, 0, 0),
            End = new DateTime(2026, 8, 4, 0, 0, 0),
        };

        Assert.True(item.CoversDay(new DateTime(2026, 8, 3)));
        Assert.False(item.CoversDay(new DateTime(2026, 8, 4)));
    }

    [Fact]
    public void TimeDisplayDependsOnAllDay()
    {
        var timed = new CalendarItem
        {
            Start = new DateTime(2026, 8, 3, 9, 0, 0),
            End = new DateTime(2026, 8, 3, 10, 30, 0),
        };
        Assert.Equal("09:00–10:30", timed.TimeDisplay);
        Assert.Equal("ganztägig", new CalendarItem { AllDay = true }.TimeDisplay);
    }
}

public class CalendarLayoutTests
{
    [Fact]
    public void WeekStartsOnMonday()
    {
        // 5.8.2026 ist ein Mittwoch.
        var monday = CalendarLayout.StartOfWeek(new DateTime(2026, 8, 5));
        Assert.Equal(new DateTime(2026, 8, 3), monday);
        Assert.Equal(DayOfWeek.Monday, monday.DayOfWeek);
    }

    [Fact]
    public void SundayBelongsToTheWeekThatStartedMonday()
    {
        // Der Klassiker: mit DayOfWeek als Zahl landet der Sonntag in der Folgewoche.
        var monday = CalendarLayout.StartOfWeek(new DateTime(2026, 8, 9)); // Sonntag
        Assert.Equal(new DateTime(2026, 8, 3), monday);
    }

    [Fact]
    public void MonthGridAlwaysHasSixWeeks()
    {
        var grid = CalendarLayout.BuildMonth(new DateTime(2026, 8, 15), []);

        Assert.Equal(42, grid.Count);
        Assert.Equal(DayOfWeek.Monday, grid[0].Date.DayOfWeek);
    }

    [Fact]
    public void MonthGridMarksDaysOutsideTheMonth()
    {
        var grid = CalendarLayout.BuildMonth(new DateTime(2026, 8, 15), []);

        Assert.Contains(grid, d => !d.InCurrentMonth);
        Assert.All(grid.Where(d => d.InCurrentMonth), d => Assert.Equal(8, d.Date.Month));
    }

    [Fact]
    public void WeekGridHasSevenDays()
        => Assert.Equal(7, CalendarLayout.BuildWeek(new DateTime(2026, 8, 5), []).Count);

    [Fact]
    public void AllDayEventsComeFirstThenByTime()
    {
        var day = new DateTime(2026, 8, 3);
        var items = new List<CalendarItem>
        {
            new() { Summary = "Spät", Start = day.AddHours(16), End = day.AddHours(17) },
            new() { Summary = "Ganztägig", AllDay = true, Start = day, End = day.AddDays(1) },
            new() { Summary = "Früh", Start = day.AddHours(8), End = day.AddHours(9) },
        };

        var sorted = CalendarLayout.ItemsOn(items, day);

        Assert.Equal(["Ganztägig", "Früh", "Spät"], sorted.Select(i => i.Summary).ToArray());
    }
}

public class BirthdayTests
{
    [Fact]
    public void BirthdayAppearsInTheGivenYearWithAge()
    {
        var contacts = new[]
        {
            new Contact { Uid = "c1", DisplayName = "Rene", Birthday = new DateTime(1985, 6, 14) },
        };

        var item = Assert.Single(CalendarLayout.BirthdaysFor(contacts, 2026));

        Assert.Equal(new DateTime(2026, 6, 14), item.Start);
        Assert.True(item.AllDay);
        Assert.True(item.IsReadOnly);
        Assert.Contains("41", item.Summary);
    }

    [Fact]
    public void ContactsWithoutBirthdayAreSkipped()
        => Assert.Empty(CalendarLayout.BirthdaysFor([new Contact { Uid = "x" }], 2026));

    [Fact]
    public void LeapDayFallsBackToFebruary28InNonLeapYears()
    {
        var contacts = new[]
        {
            new Contact { Uid = "c", DisplayName = "Schalttag", Birthday = new DateTime(2000, 2, 29) },
        };

        // 2026 ist kein Schaltjahr – ohne Rückfall gäbe es eine Ausnahme.
        var item = Assert.Single(CalendarLayout.BirthdaysFor(contacts, 2026));
        Assert.Equal(new DateTime(2026, 2, 28), item.Start);

        // 2028 ist eines.
        var leap = Assert.Single(CalendarLayout.BirthdaysFor(contacts, 2028));
        Assert.Equal(new DateTime(2028, 2, 29), leap.Start);
    }

    [Fact]
    public void BirthdaysAreReadOnlyBecauseTheyLiveInContacts()
        => Assert.True(CalendarLayout.BirthdaysFor(
            [new Contact { Uid = "c", Birthday = new DateTime(1990, 1, 1) }], 2026)[0].IsReadOnly);
}
