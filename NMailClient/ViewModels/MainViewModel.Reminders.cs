using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Threading;
using NMailClient.Models;
using NMailClient.Services;

namespace NMailClient.ViewModels;

/// <summary>Snooze und Wiedervorlage.</summary>
public partial class MainViewModel
{
    // ---- Erinnerungen (Snooze / Wiedervorlage) -----------------------------

    private readonly ReminderStore _reminders;
    private readonly DispatcherTimer _reminderTimer;

    /// <summary>
    /// Vorgaben für die Kontextmenüs.
    ///
    /// Die Kennungen sind bewusst sprachunabhängig: früher diente die deutsche
    /// Beschriftung zugleich als Schlüssel, und der Befehlsparameter im XAML
    /// musste wörtlich dazu passen. Beim Übersetzen wäre das lautlos
    /// auseinandergefallen — die Erinnerung hätte dann immer „in einer Stunde"
    /// bedeutet, egal was angeklickt wurde.
    /// </summary>
    public static IReadOnlyList<(string Token, string LabelKey, TimeSpan Delay)> ReminderPresets { get; } =
    [
        ("OneHour", "Main.Snooze.OneHour", TimeSpan.FromHours(1)),
        ("Tonight", "Main.Snooze.Tonight", TimeSpan.Zero),   // gesondert berechnet
        ("Tomorrow", "Main.Snooze.Tomorrow", TimeSpan.Zero),
        ("NextWeek", "Main.Snooze.NextWeek", TimeSpan.FromDays(7)),
    ];

    private static DateTimeOffset ResolvePreset(string token) => token switch
    {
        "Tonight" => NextOccurrence(18),
        "Tomorrow" => new DateTimeOffset(DateTime.Today.AddDays(1).AddHours(8)),
        "NextWeek" => DateTimeOffset.Now.AddDays(7),
        _ => DateTimeOffset.Now.AddHours(1),
    };

    /// <summary>Heute zur vollen Stunde – ist die vorbei, dann morgen.</summary>
    private static DateTimeOffset NextOccurrence(int hour)
    {
        var when = DateTime.Today.AddHours(hour);
        if (when <= DateTime.Now) when = when.AddDays(1);
        return new DateTimeOffset(when);
    }

    public async Task SetReminderAsync(ReminderKind kind, string presetLabel)
    {
        var targets = ActionTargets;
        if (SelectedAccount is not { } a || SelectedFolder is not { } f || targets.Count == 0) return;

        var due = ResolvePreset(presetLabel);
        foreach (var m in targets)
            _reminders.Set(new Reminder
            {
                AccountId = a.Id, Folder = f.FullName, Uid = m.Uid,
                Kind = kind, DueAt = due, Subject = m.Subject,
            });

        Status = kind == ReminderKind.Snooze
            ? $"Ausgeblendet bis {due.LocalDateTime:dd.MM. HH:mm}."
            : $"Wiedervorlage am {due.LocalDateTime:dd.MM. HH:mm}.";

        // Ausgeblendete verschwinden sofort aus der Liste.
        if (kind == ReminderKind.Snooze) await ReloadMessagesAsync();
        else RefreshReminderMarks();
    }

    private void CheckReminders()
    {
        var due = _reminders.TakeDue();
        if (due.Count == 0) return;

        var first = due[0];
        Status = due.Count == 1
            ? $"Erinnerung: '{first.Subject}'"
            : $"{due.Count} Erinnerungen fällig, u.a. '{first.Subject}'";

        // Ausgeblendete Nachrichten müssen wieder auftauchen.
        if (SelectedAccount is { } a && SelectedFolder is { } f
            && due.Any(r => r.Kind == ReminderKind.Snooze
                            && r.AccountId == a.Id
                            && r.Folder.Equals(f.FullName, StringComparison.OrdinalIgnoreCase)))
        {
            _ = ReloadMessagesAsync();
        }
        else RefreshReminderMarks();
    }

    /// <summary>Wiedervorlage-Markierung auf den Zeilen nachziehen.</summary>
    private void RefreshReminderMarks()
    {
        if (SelectedAccount is not { } a || SelectedFolder is not { } f) return;

        foreach (var m in Messages)
            m.FollowUpAt = _reminders
                .For(a.Id, f.FullName, m.Uid, ReminderKind.FollowUp)?.DueAt;
    }

}
