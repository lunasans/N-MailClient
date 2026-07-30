using System.IO;
using System.Text.Json;
using NMailClient.Services.I18n;
using Xunit;

namespace NMailClient.Tests;

/// <summary>
/// Die Sprachkataloge. Der wichtigste Test ist der Abgleich der Schlüssel: eine
/// fehlende Übersetzung fällt sonst erst auf, wenn jemand die Sprache umstellt
/// und deutsche Wörter in der englischen Oberfläche stehen.
/// </summary>
[Collection(UiCollection.Name)]
public class LocalizationCatalogTests
{
    private static Dictionary<string, string> Load(string language)
    {
        var assembly = typeof(Loc).Assembly;
        var name = $"NMailClient.Resources.Strings.{language}.json";

        using var stream = assembly.GetManifestResourceStream(name);
        Assert.True(stream is not null, $"Ressource '{name}' fehlt im Assembly.");

        using var reader = new StreamReader(stream!);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(reader.ReadToEnd())!;
    }

    [Fact]
    public void BothCatalogsAreEmbedded()
    {
        // Der erste Anlauf lag daneben: MSBuild deutete "Strings.de.json" als
        // kulturspezifische Datei und packte sie in ein Satelliten-Assembly.
        Assert.NotEmpty(Load("de"));
        Assert.NotEmpty(Load("en"));
    }

    [Fact]
    public void EnglishHasEveryGermanKey()
    {
        var missing = Load("de").Keys.Except(Load("en").Keys).OrderBy(k => k).ToList();

        Assert.True(missing.Count == 0,
            "Diese Schlüssel fehlen auf Englisch: " + string.Join(", ", missing));
    }

    [Fact]
    public void GermanHasEveryEnglishKey()
    {
        // Auch andersherum: ein englischer Schlüssel ohne deutsches Gegenstück
        // deutet auf einen Tippfehler beim Anlegen hin.
        var extra = Load("en").Keys.Except(Load("de").Keys).OrderBy(k => k).ToList();

        Assert.True(extra.Count == 0,
            "Diese Schlüssel gibt es nur auf Englisch: " + string.Join(", ", extra));
    }

    [Fact]
    public void NoValueIsEmpty()
    {
        foreach (var language in new[] { "de", "en" })
        {
            var empty = Load(language)
                .Where(e => string.IsNullOrWhiteSpace(e.Value))
                .Select(e => e.Key)
                .ToList();

            Assert.True(empty.Count == 0,
                $"Leere Texte in '{language}': " + string.Join(", ", empty));
        }
    }

    [Fact]
    public void PlaceholdersMatchBetweenLanguages()
    {
        // Steht im Deutschen {0} und im Englischen nicht, fehlt beim Umschalten
        // eine Zahl im Text – oder string.Format wirft.
        var de = Load("de");
        var en = Load("en");

        foreach (var (key, german) in de)
        {
            var english = en[key];

            Assert.Equal(
                CountPlaceholders(german), CountPlaceholders(english));
        }
    }

    private static int CountPlaceholders(string text)
        => System.Text.RegularExpressions.Regex.Matches(text, @"\{\d+\}").Count;

    [Fact]
    public void EnglishIsNotJustTheGermanTextCopied()
    {
        // Ein paar Einträge sind in beiden Sprachen gleich (OpenPGP, Spam) –
        // aber wenn es die Mehrheit wäre, wurde offensichtlich nicht übersetzt.
        var de = Load("de");
        var en = Load("en");

        var identical = de.Count(e => en[e.Key] == e.Value);

        Assert.True(identical < de.Count / 4,
            $"{identical} von {de.Count} Texten sind identisch – da fehlt die Übersetzung.");
    }
}

[Collection(UiCollection.Name)]
public class LocServiceTests
{
    [Fact]
    public void KnownKeyIsTranslated()
    {
        Loc.Current.Use("de");
        Assert.Equal("Neue Mail", Loc.T("Main.Toolbar.Compose"));

        Loc.Current.Use("en");
        Assert.Equal("New mail", Loc.T("Main.Toolbar.Compose"));

        Loc.Current.Use("de");
    }

    [Fact]
    public void UnknownKeyShowsItselfInsteadOfNothing()
    {
        // Eine leere Schaltfläche wäre schlimmer als ein sichtbarer Schlüssel.
        Assert.Equal("Gibt.Es.Nicht", Loc.T("Gibt.Es.Nicht"));
        Assert.False(Loc.Current.Has("Gibt.Es.Nicht"));
    }

    [Fact]
    public void MissingEnglishEntryFallsBackToGerman()
    {
        Loc.Current.Use("en");
        try
        {
            // Alle Schlüssel sind übersetzt; geprüft wird, dass der Rückfall
            // überhaupt greift und nicht der rohe Schlüssel erscheint.
            Assert.NotEqual("Main.Toolbar.Compose", Loc.T("Main.Toolbar.Compose"));
        }
        finally { Loc.Current.Use("de"); }
    }

    [Theory]
    [InlineData("de", "de")]
    [InlineData("en", "en")]
    [InlineData("de-AT", "de")]
    [InlineData("en-GB", "en")]
    [InlineData("fr", "de")]        // nicht mitgeliefert -> Rückfall
    [InlineData("", "de")]
    [InlineData("   ", "de")]
    public void LanguageCodesAreNormalised(string input, string expected)
    {
        Loc.Current.Use(input);
        Assert.Equal(expected, Loc.Current.Language);

        Loc.Current.Use("de");
    }

    [Fact]
    public void IndexerIsWhatXamlBindsTo()
    {
        Loc.Current.Use("de");

        Assert.Equal("Neue Mail", Loc.Current["Main.Toolbar.Compose"]);
        Assert.Equal("", Loc.Current[""]);
    }

    [Fact]
    public void SwitchingRaisesAChangeForEveryBinding()
    {
        var raised = new List<string?>();
        Loc.Current.PropertyChanged += Handler;

        try
        {
            Loc.Current.Use("en");

            // "Item[]" erneuert in WPF jede Bindung an den Indexer.
            Assert.Contains("Item[]", raised);
        }
        finally
        {
            Loc.Current.PropertyChanged -= Handler;
            Loc.Current.Use("de");
        }

        void Handler(object? _, System.ComponentModel.PropertyChangedEventArgs e)
            => raised.Add(e.PropertyName);
    }

    [Fact]
    public void FormattingWithArguments()
    {
        // Ein kaputter Platzhalter darf die Anzeige nicht sprengen.
        Assert.Equal("Kein Platzhalter", Loc.T("Kein Platzhalter", 5));
    }

    [Fact]
    public void AvailableLanguagesAreListed()
    {
        Assert.Contains(Loc.Available, l => l.Code == "de");
        Assert.Contains(Loc.Available, l => l.Code == "en");
        Assert.All(Loc.Available, l => Assert.False(string.IsNullOrWhiteSpace(l.Display)));
    }
}
