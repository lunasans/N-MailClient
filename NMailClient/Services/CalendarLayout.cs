using NMailClient.Models;

namespace NMailClient.Services;

/// <summary>Ein Tag im Monatsraster.</summary>
public class CalendarDay
{
    public DateTime Date { get; init; }
    public bool InCurrentMonth { get; init; }
    public bool IsToday => Date.Date == DateTime.Today;

    public List<CalendarItem> Items { get; init; } = [];

    public string DayNumber => Date.Day.ToString();
}

/// <summary>
/// Rechnet Termine in Raster für Monats- und Wochenansicht um.
/// Bewusst frei von WPF, damit die Kalenderlogik ohne Oberfläche prüfbar ist.
/// </summary>
public static class CalendarLayout
{
    /// <summary>Montag der Woche, in der das Datum liegt.</summary>
    public static DateTime StartOfWeek(DateTime date)
    {
        // DayOfWeek zählt Sonntag als 0 – die Woche beginnt hier montags.
        int sinceMonday = ((int)date.DayOfWeek + 6) % 7;
        return date.Date.AddDays(-sinceMonday);
    }

    /// <summary>
    /// Sechs Wochen ab dem Montag vor dem Monatsersten – ein festes Raster,
    /// damit die Ansicht beim Monatswechsel nicht springt.
    /// </summary>
    public static List<CalendarDay> BuildMonth(
        DateTime anyDayInMonth, IEnumerable<CalendarItem> items)
    {
        var first = new DateTime(anyDayInMonth.Year, anyDayInMonth.Month, 1);
        var start = StartOfWeek(first);
        var list = items.ToList();

        return Enumerable.Range(0, 42)
            .Select(i => start.AddDays(i))
            .Select(day => new CalendarDay
            {
                Date = day,
                InCurrentMonth = day.Month == first.Month && day.Year == first.Year,
                Items = ItemsOn(list, day),
            })
            .ToList();
    }

    /// <summary>Die sieben Tage der Woche, in der das Datum liegt.</summary>
    public static List<CalendarDay> BuildWeek(DateTime anyDayInWeek, IEnumerable<CalendarItem> items)
    {
        var start = StartOfWeek(anyDayInWeek);
        var list = items.ToList();

        return Enumerable.Range(0, 7)
            .Select(i => start.AddDays(i))
            .Select(day => new CalendarDay
            {
                Date = day,
                InCurrentMonth = true,
                Items = ItemsOn(list, day),
            })
            .ToList();
    }

    /// <summary>Termine eines Tages, ganztägige zuerst, dann nach Uhrzeit.</summary>
    public static List<CalendarItem> ItemsOn(IEnumerable<CalendarItem> items, DateTime day)
        => items
            .Where(e => e.CoversDay(day))
            .OrderByDescending(e => e.AllDay)
            .ThenBy(e => e.Start.TimeOfDay)
            .ToList();

    /// <summary>Geburtstage aus Kontakten als Termine des angezeigten Jahres.</summary>
    public static List<CalendarItem> BirthdaysFor(
        IEnumerable<Contact> contacts, int year)
    {
        var result = new List<CalendarItem>();

        foreach (var contact in contacts)
        {
            if (contact.Birthday is not { } birthday) continue;

            // 29. Februar in Nicht-Schaltjahren auf den 28. legen, sonst fiele
            // der Geburtstag in drei von vier Jahren aus.
            var day = birthday.Day;
            var month = birthday.Month;
            if (month == 2 && day == 29 && !DateTime.IsLeapYear(year)) day = 28;

            var date = new DateTime(year, month, day);
            var age = year - birthday.Year;

            result.Add(new CalendarItem
            {
                Uid = $"birthday:{contact.Uid}:{year}",
                Summary = age > 0
                    ? $"{contact.Label} wird {age}"
                    : $"Geburtstag {contact.Label}",
                Start = date,
                End = date.AddDays(1),
                AllDay = true,
                IsReadOnly = true,
                Color = "#8E5EA2",
            });
        }

        return result;
    }
}
