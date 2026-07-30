using System.Globalization;
using System.Windows;
using NMailClient.Models;

namespace NMailClient.Views;

/// <summary>Termin anlegen oder bearbeiten.</summary>
public partial class EventEditWindow : Window
{
    private readonly CalendarItem _item;
    private readonly bool _ready;

    public EventEditWindow(CalendarItem item)
    {
        InitializeComponent();
        _item = item;

        TxtSummary.Text = item.Summary;
        TxtLocation.Text = item.Location;
        TxtDescription.Text = item.Description;
        ChkAllDay.IsChecked = item.AllDay;

        PickStart.SelectedDate = item.Start.Date;
        PickEnd.SelectedDate = (item.End > item.Start ? item.End : item.Start).Date;
        TxtStartTime.Text = item.Start.ToString("HH:mm");
        TxtEndTime.Text = (item.End > item.Start ? item.End : item.Start.AddHours(1)).ToString("HH:mm");

        _ready = true;
        UpdateTimeFields();

        Loaded += (_, _) => TxtSummary.Focus();
    }

    /// <summary>Bei ganztägigen Terminen sind die Uhrzeiten bedeutungslos.</summary>
    private void AllDay_Changed(object sender, RoutedEventArgs e) => UpdateTimeFields();

    private void UpdateTimeFields()
    {
        if (!_ready) return;

        var enabled = ChkAllDay.IsChecked != true;
        TxtStartTime.IsEnabled = enabled;
        TxtEndTime.IsEnabled = enabled;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var summary = TxtSummary.Text.Trim();
        if (summary.Length == 0)
        {
            Fail("Bitte einen Titel angeben.");
            return;
        }

        if (PickStart.SelectedDate is not { } startDate || PickEnd.SelectedDate is not { } endDate)
        {
            Fail("Bitte Beginn und Ende angeben.");
            return;
        }

        var allDay = ChkAllDay.IsChecked == true;
        DateTime start, end;

        if (allDay)
        {
            start = startDate.Date;
            end = endDate.Date < startDate.Date ? startDate.Date : endDate.Date;
        }
        else
        {
            if (!TryTime(TxtStartTime.Text, out var startTime))
            {
                Fail("Beginn-Uhrzeit bitte als HH:MM angeben.");
                return;
            }
            if (!TryTime(TxtEndTime.Text, out var endTime))
            {
                Fail("Ende-Uhrzeit bitte als HH:MM angeben.");
                return;
            }

            start = startDate.Date + startTime;
            end = endDate.Date + endTime;

            if (end <= start)
            {
                Fail("Das Ende muss nach dem Beginn liegen.");
                return;
            }
        }

        _item.Summary = summary;
        _item.Location = TxtLocation.Text.Trim();
        _item.Description = TxtDescription.Text;
        _item.AllDay = allDay;
        _item.Start = start;
        _item.End = end;

        DialogResult = true;
    }

    private static bool TryTime(string text, out TimeSpan time)
        => TimeSpan.TryParseExact(text.Trim(), @"h\:mm", CultureInfo.InvariantCulture, out time)
           || TimeSpan.TryParseExact(text.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out time);

    private void Fail(string message)
    {
        LblError.Text = message;
        LblError.Visibility = Visibility.Visible;
    }
}
