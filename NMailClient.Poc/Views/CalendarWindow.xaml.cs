using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using NMailClient.Poc.Models;
using NMailClient.Poc.Services;
using NMailClient.Poc.Services.Dav;
// WPF hat unter System.Windows.Controls.Primitives einen eigenen CalendarItem.
using CalendarItem = NMailClient.Poc.Models.CalendarItem;

namespace NMailClient.Poc.Views;

/// <summary>Kalender mit Monats-, Wochen- und Listenansicht.</summary>
public partial class CalendarWindow : Window
{
    private enum ViewMode { Month, Week, List }

    private readonly CalDavService _calDav;
    private readonly CardDavService _cardDav;

    private readonly List<CalendarItem> _events = [];
    private DateTime _anchor = DateTime.Today;
    private ViewMode _mode = ViewMode.Month;

    private readonly bool _ready;

    private CalendarItem? _selected;

    public CalendarWindow(CalDavService calDav, CardDavService cardDav)
    {
        InitializeComponent();
        _calDav = calDav;
        _cardDav = cardDav;
        _ready = true;

        Loaded += async (_, _) => await LoadCalendarsAsync();
    }

    /// <summary>
    /// Termin aus einer Mail-Einladung vorbereiten. Der Editor öffnet sich, sobald
    /// die Kalender geladen sind – vorher wüsste man nicht, wohin gespeichert wird.
    /// </summary>
    public void PrepareInvitation(CalendarItem invitation)
    {
        _pendingInvitation = new CalendarItem
        {
            // Bewusst neue Identität: der Termin wird im eigenen Kalender angelegt,
            // nicht die Karte des Absenders übernommen.
            Summary = invitation.Summary,
            Description = invitation.Description,
            Location = invitation.Location,
            Start = invitation.Start,
            End = invitation.End,
            AllDay = invitation.AllDay,
        };
        _anchor = invitation.Start.Date;
    }

    private CalendarItem? _pendingInvitation;

    // ---- Laden ---------------------------------------------------------------

    private async Task LoadCalendarsAsync()
    {
        if (!_calDav.IsConfigured)
        {
            LblInfo.Text = "Für dieses Konto ist keine CalDAV-Adresse hinterlegt "
                           + "(Einstellungen → Konten).";
            Render();
            return;
        }

        LblInfo.Text = "Suche Kalender …";
        try
        {
            var calendars = await _calDav.ListCalendarsAsync();
            CmbCalendars.ItemsSource = calendars;

            if (calendars.Count == 0)
            {
                LblInfo.Text = "Kein Kalender gefunden.";
                Render();
                return;
            }

            CmbCalendars.SelectedIndex = 0; // löst das Laden aus
        }
        catch (Exception ex)
        {
            LblInfo.Text = $"Fehlgeschlagen: {ex.Message}";
            AppLog.Error("Kalender konnten nicht geladen werden.", ex);
            Render();
        }
    }

    /// <summary>Zeitraum, den die aktuelle Ansicht braucht – mit Rand.</summary>
    private (DateTime From, DateTime To) VisibleRange() => _mode switch
    {
        ViewMode.Week => (CalendarLayout.StartOfWeek(_anchor), CalendarLayout.StartOfWeek(_anchor).AddDays(7)),
        ViewMode.List => (_anchor.Date, _anchor.Date.AddMonths(3)),
        _ => (CalendarLayout.StartOfWeek(new DateTime(_anchor.Year, _anchor.Month, 1)),
              CalendarLayout.StartOfWeek(new DateTime(_anchor.Year, _anchor.Month, 1)).AddDays(42)),
    };

    private async Task LoadEventsAsync()
    {
        _events.Clear();

        var (from, to) = VisibleRange();

        if (CmbCalendars.SelectedItem is DavCollection calendar)
        {
            LblInfo.Text = "Lade Termine …";
            IsEnabled = false;
            try
            {
                _events.AddRange(await _calDav.GetEventsAsync(calendar.Url, from, to));
                LblInfo.Text = $"{_events.Count} Termin(e) in '{calendar.DisplayName}'";
            }
            catch (Exception ex)
            {
                LblInfo.Text = $"Fehlgeschlagen: {ex.Message}";
                AppLog.Error("Termine konnten nicht geladen werden.", ex);
            }
            finally { IsEnabled = true; }
        }

        await AddBirthdaysAsync(from, to);
        Render();

        // Eine wartende Einladung erst jetzt anbieten – der Zielkalender steht fest.
        if (_pendingInvitation is { } invitation)
        {
            _pendingInvitation = null;
            if (ShowEditor(invitation)) await SaveAsync(invitation);
        }
    }

    /// <summary>
    /// Geburtstage aus den Kontakten ergänzen. Fehlt CardDAV oder scheitert der
    /// Abruf, bleibt der Kalender trotzdem nutzbar.
    /// </summary>
    private async Task AddBirthdaysAsync(DateTime from, DateTime to)
    {
        if (!_cardDav.IsConfigured) return;

        try
        {
            var books = await _cardDav.ListAddressBooksAsync();
            var contacts = new List<Contact>();
            foreach (var book in books) contacts.AddRange(await _cardDav.GetContactsAsync(book.Url));

            // Der sichtbare Zeitraum kann über den Jahreswechsel gehen.
            for (int year = from.Year; year <= to.Year; year++)
                _events.AddRange(CalendarLayout.BirthdaysFor(contacts, year)
                    .Where(b => b.Start >= from && b.Start < to));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Geburtstage konnten nicht geladen werden: {ex.Message}");
        }
    }

    // ---- Darstellung ---------------------------------------------------------

    private void Render()
    {
        if (!_ready) return;

        GridView.Visibility = _mode == ViewMode.List ? Visibility.Collapsed : Visibility.Visible;
        ListView.Visibility = _mode == ViewMode.List ? Visibility.Visible : Visibility.Collapsed;

        switch (_mode)
        {
            case ViewMode.Month:
                LblPeriod.Text = _anchor.ToString("MMMM yyyy");
                DayGrid.ItemsSource = CalendarLayout.BuildMonth(_anchor, _events);
                break;

            case ViewMode.Week:
                var start = CalendarLayout.StartOfWeek(_anchor);
                LblPeriod.Text = $"{start:dd.MM.} – {start.AddDays(6):dd.MM.yyyy}";
                DayGrid.ItemsSource = CalendarLayout.BuildWeek(_anchor, _events);
                break;

            case ViewMode.List:
                LblPeriod.Text = $"ab {_anchor:dd.MM.yyyy}";
                ListView.ItemsSource = _events
                    .Where(e => e.End >= _anchor.Date)
                    .OrderBy(e => e.Start)
                    .ToList();
                break;
        }
    }

    private void View_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton button) return;

        _mode = (string?)button.Tag switch
        {
            "week" => ViewMode.Week,
            "list" => ViewMode.List,
            _ => ViewMode.Month,
        };

        // Umschalter verhalten sich wie Optionsfelder.
        TglMonth.IsChecked = _mode == ViewMode.Month;
        TglWeek.IsChecked = _mode == ViewMode.Week;
        TglList.IsChecked = _mode == ViewMode.List;

        _ = LoadEventsAsync();
    }

    private void Prev_Click(object sender, RoutedEventArgs e) => Shift(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => Shift(+1);

    private void Shift(int direction)
    {
        _anchor = _mode switch
        {
            ViewMode.Week => _anchor.AddDays(7 * direction),
            ViewMode.List => _anchor.AddMonths(direction),
            _ => _anchor.AddMonths(direction),
        };
        _ = LoadEventsAsync();
    }

    private void Today_Click(object sender, RoutedEventArgs e)
    {
        _anchor = DateTime.Today;
        _ = LoadEventsAsync();
    }

    private async void Calendar_Changed(object sender, SelectionChangedEventArgs e)
        => await LoadEventsAsync();

    private void Item_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: CalendarItem item }) _selected = item;
        else if (sender is ListView { SelectedItem: CalendarItem listItem }) _selected = listItem;

        if (_selected is not null) LblInfo.Text = $"{_selected.Summary} – {_selected.RangeDisplay}";
    }

    // ---- Bearbeiten ----------------------------------------------------------

    private void New_Click(object sender, RoutedEventArgs e)
    {
        if (CmbCalendars.SelectedItem is not DavCollection)
        {
            LblInfo.Text = "Bitte zuerst einen Kalender wählen.";
            return;
        }

        var start = _anchor.Date.AddHours(9);
        var item = new CalendarItem { Start = start, End = start.AddHours(1) };

        if (ShowEditor(item)) _ = SaveAsync(item);
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } item) return;

        if (item.IsReadOnly)
        {
            LblInfo.Text = "Geburtstage stammen aus den Kontakten und werden dort gepflegt.";
            return;
        }

        if (ShowEditor(item)) _ = SaveAsync(item);
    }

    private bool ShowEditor(CalendarItem item)
    {
        var dlg = new EventEditWindow(item) { Owner = this };
        return dlg.ShowDialog() == true;
    }

    private async Task SaveAsync(CalendarItem item)
    {
        if (CmbCalendars.SelectedItem is not DavCollection calendar) return;

        IsEnabled = false;
        try
        {
            await _calDav.SaveAsync(calendar.Url, item);
            LblInfo.Text = $"'{item.Summary}' gespeichert.";
            await LoadEventsAsync();
        }
        catch (DavException ex) when (ex.Status == System.Net.HttpStatusCode.PreconditionFailed)
        {
            LblInfo.Text = "Der Termin wurde zwischenzeitlich anderswo geändert. "
                           + "Bitte aktualisieren und erneut versuchen.";
        }
        catch (Exception ex)
        {
            LblInfo.Text = $"Speichern fehlgeschlagen: {ex.Message}";
            AppLog.Error("Termin konnte nicht gespeichert werden.", ex);
        }
        finally { IsEnabled = true; }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } item) return;

        if (item.IsReadOnly)
        {
            LblInfo.Text = "Geburtstage lassen sich hier nicht löschen.";
            return;
        }

        var res = MessageBox.Show(this, $"'{item.Summary}' löschen?", "Kalender",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (res != MessageBoxResult.Yes) return;

        IsEnabled = false;
        try
        {
            await _calDav.DeleteAsync(item);
            _selected = null;
            await LoadEventsAsync();
            LblInfo.Text = "Termin gelöscht.";
        }
        catch (Exception ex)
        {
            LblInfo.Text = $"Löschen fehlgeschlagen: {ex.Message}";
            AppLog.Error("Termin konnte nicht gelöscht werden.", ex);
        }
        finally { IsEnabled = true; }
    }
}
