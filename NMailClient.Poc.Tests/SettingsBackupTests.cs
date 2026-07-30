using System.IO;
using NMailClient.Poc.Models;
using NMailClient.Poc.Services;
using Xunit;

namespace NMailClient.Poc.Tests;

public class SettingsBackupTests
{
    private static Account SampleAccount() => new()
    {
        Id = "acc-1", Name = "Rene", Email = "rene@example.org",
        Password = "geheim",
        ImapHost = "imap.example.org", ImapPort = 993,
        SmtpHost = "smtp.example.org", SmtpPort = 465,
        CardDavUrl = "https://dav.example.org/",
        FolderOrder = ["Archiv"],
        Aliases = [new AliasDef { Address = "info@example.org" }],
    };

    [Fact]
    public void BackupNeverContainsPasswords()
    {
        // Der wichtigste Punkt: Passwörter liegen im Anmeldeinformationsverwalter
        // und dürfen nicht in eine Datei geraten.
        var json = SettingsBackup.Serialize(new AppSettings(), [SampleAccount()]);

        Assert.DoesNotContain("geheim", json);
    }

    [Fact]
    public void OriginalAccountKeepsItsPassword()
    {
        // Die Sicherung arbeitet auf Kopien; das laufende Konto bleibt unberührt.
        var account = SampleAccount();
        SettingsBackup.Serialize(new AppSettings(), [account]);

        Assert.Equal("geheim", account.Password);
    }

    [Fact]
    public void RoundTripKeepsAccountsAndSettings()
    {
        var settings = new AppSettings
        {
            Theme = "Dark", PageSize = 25, GroupByDate = false,
            TrustedSenders = ["news@example.org"],
        };

        var backup = SettingsBackup.Parse(SettingsBackup.Serialize(settings, [SampleAccount()]));

        Assert.Equal("Dark", backup.Settings!.Theme);
        Assert.Equal(25, backup.Settings.PageSize);
        Assert.Contains("news@example.org", backup.Settings.TrustedSenders);

        var account = Assert.Single(backup.Accounts);
        Assert.Equal("rene@example.org", account.Email);
        Assert.Equal("https://dav.example.org/", account.CardDavUrl);
        Assert.Single(account.Aliases);
        Assert.Equal("", account.Password);
    }

    [Fact]
    public void ForeignBackupIsRejected()
    {
        const string json = """{"application":"AndereApp","version":1}""";

        var ex = Assert.Throws<InvalidDataException>(() => SettingsBackup.Parse(json));
        Assert.Contains("AndereApp", ex.Message);
    }

    [Fact]
    public void NewerVersionIsRejected()
    {
        // Lieber verweigern als Felder still verlieren.
        const string json = """{"application":"NMailClient.Poc","version":99}""";

        var ex = Assert.Throws<InvalidDataException>(() => SettingsBackup.Parse(json));
        Assert.Contains("99", ex.Message);
    }

    [Fact]
    public void BrokenJsonThrows()
        => Assert.ThrowsAny<Exception>(() => SettingsBackup.Parse("kein json"));

    [Fact]
    public void DescriptionMentionsDateAndCount()
    {
        var backup = SettingsBackup.Parse(
            SettingsBackup.Serialize(new AppSettings(), [SampleAccount(), SampleAccount()]));

        Assert.Contains("2 Konto", backup.Description);
    }

    [Fact]
    public void UmlautsSurviveTheRoundTrip()
    {
        var settings = new AppSettings
        {
            Labels = [new LabelDef { Keyword = "Buero", Display = "Büro", Color = "#123456" }],
        };

        var backup = SettingsBackup.Parse(SettingsBackup.Serialize(settings, []));

        Assert.Equal("Büro", backup.Settings!.Labels[0].Display);
    }
}

public class PdfDetectionTests
{
    [Theory]
    [InlineData("application/pdf", "beleg.pdf", true)]
    [InlineData("application/octet-stream", "beleg.pdf", true)]   // falsch deklariert
    [InlineData("application/pdf", "ohne-endung", true)]
    [InlineData("image/png", "bild.png", false)]
    [InlineData("text/plain", "notiz.txt", false)]
    public void RecognizesPdfByTypeOrExtension(string mime, string name, bool expected)
        => Assert.Equal(expected, new AttachmentInfo { MimeType = mime, FileName = name }.IsPdf);

    [Fact]
    public void ExtensionCheckIgnoresCase()
        => Assert.True(new AttachmentInfo { MimeType = "x", FileName = "BELEG.PDF" }.IsPdf);
}
