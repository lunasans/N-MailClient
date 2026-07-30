using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NMailClient.Poc.Models;

/// <summary>
/// Ein Termin. Bewusst nicht die iCal.Net-Klasse selbst: die Oberfläche soll
/// nicht am Format kleben, und Geburtstage aus Kontakten sind ebenfalls Termine
/// ohne CalDAV-Herkunft.
/// </summary>
public class CalendarItem : INotifyPropertyChanged
{
    /// <summary>Serveradresse; leer bei neuen und bei abgeleiteten Terminen.</summary>
    public string Url { get; set; } = "";
    public string? ETag { get; set; }

    public string Uid { get; set; } = "";

    /// <summary>Adresse des Kalenders, in dem der Termin liegt.</summary>
    public string CalendarUrl { get; set; } = "";

    private string _summary = "";
    public string Summary { get => _summary; set => Set(ref _summary, value); }

    private string _description = "";
    public string Description { get => _description; set => Set(ref _description, value); }

    private string _location = "";
    public string Location { get => _location; set => Set(ref _location, value); }

    private DateTime _start;
    public DateTime Start { get => _start; set => Set(ref _start, value); }

    private DateTime _end;
    public DateTime End { get => _end; set => Set(ref _end, value); }

    private bool _allDay;
    public bool AllDay { get => _allDay; set => Set(ref _allDay, value); }

    /// <summary>
    /// Abgeleitete Termine (Geburtstage aus Kontakten) sind nur Anzeige und
    /// lassen sich nicht bearbeiten.
    /// </summary>
    public bool IsReadOnly { get; set; }

    public bool IsNew => string.IsNullOrEmpty(Url);

    /// <summary>Farbe der Markierung in der Ansicht.</summary>
    public string Color { get; set; } = "#4A7DBD";

    public string TimeDisplay => AllDay
        ? "ganztägig"
        : $"{Start:HH:mm}–{End:HH:mm}";

    public string RangeDisplay => AllDay
        ? Start.ToString("dd.MM.yyyy")
        : $"{Start:dd.MM.yyyy HH:mm} – {End:HH:mm}";

    /// <summary>Überdeckt der Termin diesen Tag?</summary>
    public bool CoversDay(DateTime day)
    {
        var from = Start.Date;
        // Ein Termin, der um 00:00 des Folgetags endet, gehört nicht mehr dorthin.
        var to = End <= Start ? from : End.AddTicks(-1).Date;
        return day.Date >= from && day.Date <= to;
    }

    public override string ToString() => Summary;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TimeDisplay)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RangeDisplay)));
    }
}
