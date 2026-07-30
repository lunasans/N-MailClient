using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace NMailClient.Views;

/// <summary>Zeitpunkt für den geplanten Versand wählen.</summary>
public partial class ScheduleWindow : Window
{
    public DateTimeOffset SendAt { get; private set; }

    public ScheduleWindow()
    {
        InitializeComponent();

        // Vorbelegung: in einer Stunde, auf volle 5 Minuten gerundet.
        var start = DateTime.Now.AddHours(1);
        start = start.AddMinutes(-(start.Minute % 5)).AddSeconds(-start.Second);

        PickDate.SelectedDate = start.Date;
        TxtTime.Text = start.ToString("HH:mm");
    }

    private void Quick_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b) return;

        var when = b.Tag switch
        {
            "evening" => DateTime.Today.AddHours(18),
            "morning" => DateTime.Today.AddDays(1).AddHours(8),
            string s when int.TryParse(s, out var minutes) => DateTime.Now.AddMinutes(minutes),
            _ => DateTime.Now.AddHours(1),
        };

        // „Heute Abend" nach 18 Uhr wäre Vergangenheit – dann morgen.
        if (when <= DateTime.Now) when = when.AddDays(1);

        PickDate.SelectedDate = when.Date;
        TxtTime.Text = when.ToString("HH:mm");
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (PickDate.SelectedDate is not { } date)
        {
            Fail("Bitte ein Datum wählen.");
            return;
        }

        if (!TimeSpan.TryParseExact(TxtTime.Text.Trim(), @"h\:mm",
                CultureInfo.InvariantCulture, out var time)
            && !TimeSpan.TryParseExact(TxtTime.Text.Trim(), @"hh\:mm",
                CultureInfo.InvariantCulture, out time))
        {
            Fail("Uhrzeit bitte als HH:MM angeben.");
            return;
        }

        var when = date.Date + time;
        if (when <= DateTime.Now)
        {
            Fail("Der Zeitpunkt liegt in der Vergangenheit.");
            return;
        }

        SendAt = new DateTimeOffset(when);
        DialogResult = true;
    }

    private void Fail(string message)
    {
        LblError.Text = message;
        LblError.Visibility = Visibility.Visible;
    }
}
