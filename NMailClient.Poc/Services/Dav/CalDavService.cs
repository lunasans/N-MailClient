using System.Xml.Linq;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using NMailClient.Poc.Models;

namespace NMailClient.Poc.Services.Dav;

/// <summary>
/// CalDAV-Zugriff: Kalender finden, Termine eines Zeitraums lesen, anlegen,
/// ändern, löschen. Das iCalendar-Format übernimmt iCal.Net; selbst geschrieben
/// ist nur die Protokollschicht.
/// </summary>
public class CalDavService
{
    private readonly Func<(string Url, string User, string Password)> _config;

    public CalDavService(Func<(string, string, string)> config) => _config = config;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_config().Url);

    private DavHttp CreateClient()
    {
        var (_, user, password) = _config();
        return new DavHttp(user, password);
    }

    public async Task<List<DavCollection>> ListCalendarsAsync(CancellationToken ct = default)
    {
        var (url, _, _) = _config();
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("Keine CalDAV-Adresse hinterlegt.");

        using var dav = CreateClient();
        return await DavDiscovery.FindCollectionsAsync(dav, url, DavDiscovery.Kind.CalDav, ct);
    }

    /// <summary>
    /// Termine eines Zeitraums. Die Einschränkung übernimmt der Server
    /// (<c>time-range</c>) – einen ganzen Kalender zu laden wäre bei Jahren
    /// voller Termine unbrauchbar.
    /// </summary>
    public async Task<List<CalendarItem>> GetEventsAsync(
        string calendarUrl, DateTime from, DateTime to, CancellationToken ct = default)
    {
        using var dav = CreateClient();

        var body = new XElement(DavHttp.CalDav + "calendar-query",
            new XAttribute(XNamespace.Xmlns + "d", DavHttp.D),
            new XAttribute(XNamespace.Xmlns + "c", DavHttp.CalDav),
            new XElement(DavHttp.D + "prop",
                new XElement(DavHttp.D + "getetag"),
                new XElement(DavHttp.CalDav + "calendar-data")),
            new XElement(DavHttp.CalDav + "filter",
                new XElement(DavHttp.CalDav + "comp-filter",
                    new XAttribute("name", "VCALENDAR"),
                    new XElement(DavHttp.CalDav + "comp-filter",
                        new XAttribute("name", "VEVENT"),
                        new XElement(DavHttp.CalDav + "time-range",
                            new XAttribute("start", Utc(from)),
                            new XAttribute("end", Utc(to)))))));

        var doc = await dav.ReportAsync(calendarUrl, body, 1, ct);
        var result = new List<CalendarItem>();

        foreach (var response in doc.Descendants(DavHttp.D + "response"))
        {
            var href = response.Element(DavHttp.D + "href")?.Value;
            var data = response.Descendants(DavHttp.CalDav + "calendar-data").FirstOrDefault()?.Value;
            if (string.IsNullOrWhiteSpace(href) || string.IsNullOrWhiteSpace(data)) continue;

            var etag = response.Descendants(DavHttp.D + "getetag").FirstOrDefault()?.Value;

            foreach (var item in ParseEvents(data, from, to))
            {
                item.Url = DavDiscovery.Absolute(calendarUrl, href);
                item.ETag = etag;
                item.CalendarUrl = calendarUrl;
                result.Add(item);
            }
        }

        return result.OrderBy(e => e.Start).ToList();
    }

    public async Task<CalendarItem> SaveAsync(
        string calendarUrl, CalendarItem item, CancellationToken ct = default)
    {
        using var dav = CreateClient();

        if (string.IsNullOrWhiteSpace(item.Uid)) item.Uid = Guid.NewGuid().ToString();

        var url = item.IsNew
            ? calendarUrl.TrimEnd('/') + "/" + item.Uid + ".ics"
            : item.Url;

        var etag = await dav.PutAsync(
            url, Serialize(item), "text/calendar; charset=utf-8", item.ETag, ct);

        item.Url = url;
        item.ETag = etag;
        item.CalendarUrl = calendarUrl;
        return item;
    }

    public async Task DeleteAsync(CalendarItem item, CancellationToken ct = default)
    {
        if (item.IsNew) return;

        using var dav = CreateClient();
        await dav.DeleteAsync(item.Url, item.ETag, ct);
    }

    // ---- iCalendar -----------------------------------------------------------

    /// <summary>
    /// Termine aus iCalendar-Text. Wiederholungen werden über
    /// <c>GetOccurrences</c> im Zeitraum aufgelöst, damit ein wöchentlicher
    /// Termin auch an jedem Tag erscheint.
    /// </summary>
    public static List<CalendarItem> ParseEvents(string icalText, DateTime from, DateTime to)
    {
        var result = new List<CalendarItem>();
        try
        {
            var calendar = Calendar.Load(icalText);
            if (calendar is null) return result;

            foreach (var evt in calendar.Events)
            {
                if (evt.RecurrenceRule is not null)
                {
                    var occurrences = evt.GetOccurrences(new CalDateTime(from))
                        .TakeWhile(o => o.Period.StartTime.Value < to);

                    foreach (var occurrence in occurrences)
                    {
                        var item = FromEvent(evt);
                        var start = occurrence.Period.StartTime.Value;
                        var length = item.End - item.Start;

                        item.Start = start;
                        item.End = start + (length > TimeSpan.Zero ? length : TimeSpan.FromHours(1));
                        result.Add(item);
                    }
                }
                else result.Add(FromEvent(evt));
            }
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException
                                      or InvalidOperationException
                                      or System.Runtime.Serialization.SerializationException)
        {
            // iCal.Net wirft bei unbrauchbarem Text eine SerializationException.
            // Eine kaputte Einladung darf die Mailanzeige nicht zerlegen.
            AppLog.Warn($"iCalendar nicht lesbar: {ex.Message}");
        }
        return result;
    }

    private static CalendarItem FromEvent(CalendarEvent evt)
    {
        var allDay = evt.Start is { HasTime: false };
        var start = evt.Start?.Value ?? DateTime.Now;

        // Ohne DTEND gilt bei ganztägigen Terminen ein Tag, sonst eine Stunde.
        var end = evt.End?.Value ?? (allDay ? start.AddDays(1) : start.AddHours(1));

        return new CalendarItem
        {
            Uid = evt.Uid ?? "",
            Summary = evt.Summary ?? "(ohne Titel)",
            Description = evt.Description ?? "",
            Location = evt.Location ?? "",
            Start = start,
            End = end,
            AllDay = allDay,
        };
    }

    public static string Serialize(CalendarItem item)
    {
        var evt = new CalendarEvent
        {
            Uid = item.Uid,
            Summary = item.Summary,
            Description = item.Description,
            Location = item.Location,
        };

        if (item.AllDay)
        {
            evt.Start = new CalDateTime(DateOnly.FromDateTime(item.Start));
            // DTEND ist bei ganztägigen Terminen exklusiv – ein Tag heisst +1.
            evt.End = new CalDateTime(DateOnly.FromDateTime(item.End.Date > item.Start.Date
                ? item.End.Date
                : item.Start.Date.AddDays(1)));
        }
        else
        {
            var zone = TimeZoneInfo.Local.Id;
            evt.Start = new CalDateTime(item.Start, zone);
            evt.End = new CalDateTime(item.End > item.Start ? item.End : item.Start.AddHours(1), zone);
        }

        var calendar = new Calendar();
        calendar.Events.Add(evt);
        return new CalendarSerializer().SerializeToString(calendar) ?? "";
    }

    private static string Utc(DateTime value)
        => value.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'");
}
