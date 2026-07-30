using System.Text.Json;
using NMailClient.Poc.Models;
using NMailClient.Poc.Services.Shell;
using Xunit;

namespace NMailClient.Poc.Tests;

public class QuietHoursTests
{
    private static TimeOnly At(int hour, int minute = 0) => new(hour, minute);

    [Fact]
    public void DisabledMeansNeverQuiet()
    {
        var hours = new QuietHours(false, At(22), At(7));

        Assert.False(hours.IsQuiet(At(23)));
        Assert.False(hours.IsQuiet(At(3)));
        Assert.Contains("keine", hours.Display);
    }

    [Theory]
    [InlineData(22, 0, true)]    // genau der Beginn
    [InlineData(23, 30, true)]
    [InlineData(0, 0, true)]     // Mitternacht liegt mitten drin
    [InlineData(3, 15, true)]
    [InlineData(6, 59, true)]
    [InlineData(7, 0, false)]    // das Ende gehört nicht mehr dazu
    [InlineData(12, 0, false)]
    [InlineData(21, 59, false)]
    public void WindowAcrossMidnightIsHandled(int hour, int minute, bool expected)
    {
        // Der Normalfall: 22:00–07:00 läuft über den Tageswechsel.
        var hours = new QuietHours(true, At(22), At(7));

        Assert.Equal(expected, hours.IsQuiet(At(hour, minute)));
    }

    [Theory]
    [InlineData(8, 0, false)]
    [InlineData(9, 0, true)]
    [InlineData(16, 59, true)]
    [InlineData(17, 0, false)]
    [InlineData(2, 0, false)]
    public void WindowWithinOneDayIsHandled(int hour, int minute, bool expected)
    {
        var hours = new QuietHours(true, At(9), At(17));

        Assert.Equal(expected, hours.IsQuiet(At(hour, minute)));
    }

    [Fact]
    public void IdenticalStartAndEndMeansNoQuietTime()
    {
        // 24 Stunden Ruhe ist mit Sicherheit nicht gemeint, wenn jemand die
        // Zeiten versehentlich gleichsetzt.
        var hours = new QuietHours(true, At(22), At(22));

        Assert.False(hours.IsQuiet(At(22)));
        Assert.False(hours.IsQuiet(At(3)));
    }

    [Fact]
    public void DateTimeOverloadUsesOnlyTheTimeOfDay()
    {
        var hours = new QuietHours(true, At(22), At(7));

        Assert.True(hours.IsQuiet(new DateTime(2026, 7, 27, 23, 30, 0)));
        Assert.False(hours.IsQuiet(new DateTime(2026, 7, 27, 12, 0, 0)));
    }

    [Fact]
    public void DisplayNamesTheWindow()
        => Assert.Equal("Ruhe von 22:00 bis 07:00",
                        new QuietHours(true, At(22), At(7)).Display);

    [Fact]
    public void OffIsDisabledButKeepsSensibleDefaults()
    {
        Assert.False(QuietHours.Off.Enabled);
        Assert.Equal(At(22), QuietHours.Off.From);
        Assert.Equal(At(7), QuietHours.Off.To);
    }
}

public class NotificationSettingsTests
{
    [Fact]
    public void DefaultsAreSensible()
    {
        var settings = new AppSettings();

        Assert.True(settings.MinimizeToTray);
        Assert.True(settings.NotifyOnNewMail);
        Assert.False(settings.QuietHoursEnabled);
        Assert.False(settings.QuietHours.IsQuiet(new TimeOnly(23, 0)));
    }

    [Fact]
    public void QuietHoursComeFromTheStoredStrings()
    {
        var settings = new AppSettings
        {
            QuietHoursEnabled = true, QuietFrom = "20:30", QuietTo = "06:15",
        };

        Assert.Equal(new TimeOnly(20, 30), settings.QuietHours.From);
        Assert.Equal(new TimeOnly(6, 15), settings.QuietHours.To);
        Assert.True(settings.QuietHours.IsQuiet(new TimeOnly(2, 0)));
    }

    [Theory]
    [InlineData("Unsinn")]
    [InlineData("")]
    [InlineData("25:99")]
    public void UnreadableTimesFallBackInsteadOfThrowing(string value)
    {
        // Eine von Hand verkorkste db.json darf den Start nicht verhindern.
        var settings = new AppSettings { QuietHoursEnabled = true, QuietFrom = value };

        Assert.Equal(new TimeOnly(22, 0), settings.QuietHours.From);
    }

    [Fact]
    public void ComputedQuietHoursDoNotLandInTheFile()
    {
        // Abgeleitete Werte gehören nicht in die Datei – sonst wandern sie beim
        // nächsten Einlesen als Datenfeld zurück.
        var json = JsonSerializer.Serialize(new AppSettings());

        Assert.DoesNotContain("QuietHours\"", json);
        Assert.Contains("quietFrom", json);
    }

    [Fact]
    public void SettingsSurviveTheRoundTrip()
    {
        var settings = new AppSettings
        {
            MinimizeToTray = false, NotifyOnNewMail = false,
            QuietHoursEnabled = true, QuietFrom = "21:00", QuietTo = "08:00",
        };

        var back = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;

        Assert.False(back.MinimizeToTray);
        Assert.False(back.NotifyOnNewMail);
        Assert.True(back.QuietHours.IsQuiet(new TimeOnly(23, 0)));
    }
}

public class AutostartTests
{
    [Fact]
    public void MinimizedFlagIsRecognised()
    {
        Assert.True(Autostart.StartedMinimized(["--minimized"]));
        Assert.True(Autostart.StartedMinimized(["--MINIMIZED"]));
        Assert.True(Autostart.StartedMinimized(["irgendwas", "--minimized"]));
    }

    [Fact]
    public void WithoutTheFlagTheWindowOpensNormally()
    {
        Assert.False(Autostart.StartedMinimized([]));
        Assert.False(Autostart.StartedMinimized(["--anderes"]));
    }

    [Fact]
    public void ExecutablePathIsQuotedForTheRegistry()
    {
        // Ohne Anführungszeichen bricht der Autostart bei Leerzeichen im Pfad.
        var path = Autostart.ExecutablePath;

        Assert.NotNull(path);
        Assert.StartsWith("\"", path);
        Assert.EndsWith("\"", path);
    }

    [Fact]
    public void ReadingTheStateNeverThrows()
    {
        // Nur lesen – der Registrierungszustand des Rechners wird nicht verändert.
        _ = Autostart.IsEnabled;
    }
}
