using System.Text.Json;
using NMailClient.Poc.Models;
using Xunit;

namespace NMailClient.Poc.Tests;

public class ReminderTests
{
    private static Reminder R(ReminderKind kind, int minutesFromNow, uint uid = 1,
        string folder = "INBOX", string account = "acc1")
        => new()
        {
            AccountId = account, Folder = folder, Uid = uid, Kind = kind,
            DueAt = DateTimeOffset.Now.AddMinutes(minutesFromNow),
        };

    [Fact]
    public void PendingSnoozeHidesTheMessage()
    {
        var r = R(ReminderKind.Snooze, 60);

        Assert.False(r.IsDue);
        Assert.True(r.HidesMessage);
    }

    [Fact]
    public void DueSnoozeNoLongerHides()
    {
        var r = R(ReminderKind.Snooze, -1);

        Assert.True(r.IsDue);
        Assert.False(r.HidesMessage);
    }

    [Fact]
    public void FollowUpNeverHidesTheMessage()
    {
        // Wiedervorlage soll sichtbar bleiben – nur Snooze blendet aus.
        Assert.False(R(ReminderKind.FollowUp, 60).HidesMessage);
        Assert.False(R(ReminderKind.FollowUp, -60).HidesMessage);
    }

    [Fact]
    public void MatchesRequiresAccountFolderAndUid()
    {
        var r = R(ReminderKind.Snooze, 10, uid: 5, folder: "INBOX", account: "acc1");

        Assert.True(r.Matches("acc1", "INBOX", 5));
        Assert.True(r.Matches("acc1", "inbox", 5));      // Ordner ohne Gross/Klein
        Assert.False(r.Matches("acc2", "INBOX", 5));     // anderes Konto
        Assert.False(r.Matches("acc1", "Archiv", 5));    // anderer Ordner
        Assert.False(r.Matches("acc1", "INBOX", 6));     // andere UID
    }

    [Fact]
    public void UidAloneIsNotEnoughToIdentifyAMessage()
    {
        // UIDs sind nur je Ordner eindeutig – ohne Ordnervergleich würde eine
        // Erinnerung auf eine fremde Nachricht zeigen.
        var r = R(ReminderKind.Snooze, 10, uid: 1, folder: "INBOX");
        Assert.False(r.Matches("acc1", "Gesendet", 1));
    }

    [Fact]
    public void SurvivesRoundTrip()
    {
        var r = new Reminder
        {
            AccountId = "acc1", Folder = "INBOX", Uid = 9,
            Kind = ReminderKind.FollowUp,
            DueAt = new DateTimeOffset(2026, 5, 1, 9, 0, 0, TimeSpan.Zero),
            Subject = "Angebot",
        };

        var back = JsonSerializer.Deserialize<Reminder>(JsonSerializer.Serialize(r))!;

        Assert.Equal(ReminderKind.FollowUp, back.Kind);
        Assert.Equal(r.DueAt, back.DueAt);
        Assert.Equal("Angebot", back.Subject);
    }

    [Fact]
    public void DerivedFieldsAreNotPersisted()
    {
        var json = JsonSerializer.Serialize(R(ReminderKind.Snooze, 5));

        Assert.DoesNotContain("IsDue", json);
        Assert.DoesNotContain("HidesMessage", json);
    }
}

public class MailSummaryFollowUpTests
{
    [Fact]
    public void SettingFollowUpRaisesAllDependentProperties()
    {
        var m = new MailSummary();
        var raised = new List<string?>();
        m.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        m.FollowUpAt = DateTimeOffset.Now.AddHours(1);

        Assert.Contains("FollowUpAt", raised);
        Assert.Contains("HasFollowUp", raised);
        Assert.Contains("FollowUpDisplay", raised);
        Assert.True(m.HasFollowUp);
    }

    [Fact]
    public void ClearingFollowUpEmptiesTheLabel()
    {
        var m = new MailSummary { FollowUpAt = DateTimeOffset.Now };
        m.FollowUpAt = null;

        Assert.False(m.HasFollowUp);
        Assert.Equal("", m.FollowUpDisplay);
    }

    [Fact]
    public void UnchangedValueRaisesNothing()
    {
        var due = DateTimeOffset.Now.AddHours(2);
        var m = new MailSummary { FollowUpAt = due };

        var raised = 0;
        m.PropertyChanged += (_, _) => raised++;
        m.FollowUpAt = due;

        Assert.Equal(0, raised);
    }
}
