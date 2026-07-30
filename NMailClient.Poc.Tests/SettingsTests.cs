using System.Text.Json;
using NMailClient.Poc.Models;
using Xunit;

namespace NMailClient.Poc.Tests;

public class AppSettingsTests
{
    [Fact]
    public void SerializesOnlyPersistedFields()
    {
        var json = JsonSerializer.Serialize(new AppSettings());

        Assert.Contains("\"theme\"", json);
        Assert.Contains("\"pageSize\"", json);
        Assert.Contains("\"schema\"", json);
        // Abgeleitet – darf nicht in die Datei geraten.
        Assert.DoesNotContain("EffectivePageSize", json);
    }

    [Fact]
    public void ReadsFilesContainingLegacyComputedField()
    {
        // So sahen Dateien aus, bevor EffectivePageSize [JsonIgnore] bekam.
        var json = """
            { "theme": "Dark", "pageSize": 25, "schema": 1, "EffectivePageSize": 999 }
            """;

        var s = JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.Equal("Dark", s.Theme);
        Assert.Equal(25, s.PageSize);
        Assert.Equal(25, s.EffectivePageSize);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(-5, 10)]
    [InlineData(50, 50)]
    [InlineData(5000, 200)]
    public void ClampsPageSizeToUsableRange(int stored, int expected)
        => Assert.Equal(expected, new AppSettings { PageSize = stored }.EffectivePageSize);

    [Fact]
    public void DefaultsToSystemTheme()
        => Assert.Equal("System", new AppSettings().Theme);
}

public class AccountTests
{
    [Fact]
    public void LoginUserFallsBackToEmail()
    {
        var a = new Account { Email = "rene@example.org", User = "" };
        Assert.Equal("rene@example.org", a.LoginUser);
    }

    [Fact]
    public void LoginUserPrefersExplicitUser()
    {
        var a = new Account { Email = "rene@example.org", User = "rene" };
        Assert.Equal("rene", a.LoginUser);
    }

    [Fact]
    public void DisplayFallsBackToEmailWithoutName()
        => Assert.Equal("a@b.c", new Account { Email = "a@b.c" }.Display);

    [Fact]
    public void DisplayCombinesNameAndEmail()
        => Assert.Equal("Rene <a@b.c>", new Account { Name = "Rene", Email = "a@b.c" }.Display);

    [Fact]
    public void JsonFieldNamesMatchGoVersion()
    {
        // Feldnamen müssen zu internal/store/store.go passen, damit ein db.json
        // der Go-Version gelesen werden kann.
        var json = JsonSerializer.Serialize(new Account());

        foreach (var field in new[]
                 {
                     "\"id\"", "\"name\"", "\"email\"", "\"user\"", "\"password\"",
                     "\"imapHost\"", "\"imapPort\"", "\"smtpHost\"", "\"smtpPort\"",
                     "\"signature\"", "\"color\"",
                 })
            Assert.Contains(field, json);
    }
}

public class MailSummaryTests
{
    [Theory]
    [InlineData("Rene Neuhaus", "", "RN")]
    [InlineData("", "rene.neuhaus@example.org", "RN")]
    [InlineData("Rene", "", "RE")]
    [InlineData("", "", "?")]
    public void BuildsInitials(string from, string address, string expected)
    {
        var m = new MailSummary { From = from, FromAddress = address };
        Assert.Equal(expected, m.Initials);
    }

    [Fact]
    public void AvatarColorIsStableForSameAddress()
    {
        var a = new MailSummary { FromAddress = "x@y.z" }.AvatarColor;
        var b = new MailSummary { FromAddress = "x@y.z" }.AvatarColor;
        Assert.Equal(a, b);
    }

    [Fact]
    public void SeenRaisesPropertyChanged()
    {
        var m = new MailSummary();
        var raised = new List<string?>();
        m.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        m.Seen = true;
        m.Seen = true; // unverändert -> kein weiteres Ereignis

        Assert.Equal(["Seen"], raised);
    }
}
