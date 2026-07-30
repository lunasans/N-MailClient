using System.Text.Json;
using NMailClient.Models;
using Xunit;

namespace NMailClient.Tests;

public class LabelKeywordTests
{
    [Theory]
    [InlineData("Wichtig", "Wichtig")]
    [InlineData("Zu erledigen", "Zu_erledigen")]
    [InlineData("Büro", "Buero")]
    [InlineData("Größe", "Groesse")]
    [InlineData("Über-Wichtig", "Ueber-Wichtig")]
    public void DerivesAsciiKeywordFromDisplayName(string display, string expected)
        => Assert.Equal(expected, LabelDef.MakeKeyword(display));

    [Theory]
    [InlineData("Rechnung!")]
    [InlineData("A/B")]
    [InlineData("50 %")]
    [InlineData("\"Zitat\"")]
    public void StripsCharactersThatBreakImapAtoms(string display)
    {
        var kw = LabelDef.MakeKeyword(display);

        // IMAP-Keywords sind Atome: keine Sonderzeichen, keine Leerzeichen.
        Assert.DoesNotContain(' ', kw);
        Assert.All(kw, c => Assert.True(
            char.IsAsciiLetterOrDigit(c) || c is '_' or '-', $"Unerlaubtes Zeichen: {c}"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    [InlineData("äöü!")]   // wird zu "aeoeue" – nicht leer
    public void NeverProducesAnEmptyKeyword(string display)
        => Assert.NotEmpty(LabelDef.MakeKeyword(display));

    [Fact]
    public void FallsBackWhenNothingUsableRemains()
        => Assert.Equal("Etikett", LabelDef.MakeKeyword("!!!"));

    [Fact]
    public void KeywordIsStableAcrossCalls()
    {
        // Stabilität ist wesentlich: das Keyword liegt am Server und darf sich
        // beim Umbenennen des Etiketts nicht ändern.
        Assert.Equal(LabelDef.MakeKeyword("Zu erledigen"), LabelDef.MakeKeyword("Zu erledigen"));
    }
}

public class LabelSettingsTests
{
    [Fact]
    public void ShipsWithDefaultLabels()
    {
        var s = new AppSettings();

        Assert.Equal(3, s.Labels.Count);
        Assert.Contains(s.Labels, l => l.Keyword == "Wichtig");
    }

    [Fact]
    public void LabelsSurviveRoundTrip()
    {
        var s = new AppSettings
        {
            Labels = [new LabelDef { Keyword = "Test", Display = "Test", Color = "#123456" }],
        };

        var back = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s))!;

        var l = Assert.Single(back.Labels);
        Assert.Equal("Test", l.Keyword);
        Assert.Equal("#123456", l.Color);
    }

    [Fact]
    public void EmptyLabelListIsPreserved()
    {
        // Wer alle Etiketten löscht, soll nicht beim nächsten Start die Vorgaben
        // zurückbekommen.
        var s = new AppSettings { Labels = [] };
        var back = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s))!;

        Assert.Empty(back.Labels);
    }
}

public class MailSummaryKeywordTests
{
    [Fact]
    public void KeywordMatchingIgnoresCase()
    {
        var m = new MailSummary { Keywords = { "wichtig" } };
        Assert.Contains("Wichtig", m.Keywords);
    }

    [Fact]
    public void SetKeywordAddsAndRemoves()
    {
        var m = new MailSummary();

        m.SetKeyword("Arbeit", true);
        Assert.Contains("Arbeit", m.Keywords);

        m.SetKeyword("Arbeit", false);
        Assert.DoesNotContain("Arbeit", m.Keywords);
    }

    [Fact]
    public void LabelsRaisePropertyChanged()
    {
        var m = new MailSummary();
        var raised = new List<string?>();
        m.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        m.Labels = [new LabelDef { Keyword = "X", Display = "X" }];

        Assert.Contains("Labels", raised);
    }
}
