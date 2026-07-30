using System.IO;
using NMailClient.Poc.Services.I18n;
using NMailClient.Poc.ViewModels;
using Xunit;

namespace NMailClient.Poc.Tests;

/// <summary>
/// Die Schlummer-Vorgaben werden über eine Kennung angesprochen, nicht über ihre
/// Beschriftung.
///
/// Vorher war die deutsche Beschriftung zugleich der Schlüssel, und der
/// Befehlsparameter im XAML musste wörtlich dazu passen. Beim Übersetzen wäre
/// das lautlos auseinandergefallen: jede Auswahl hätte dann „in einer Stunde"
/// bedeutet, ohne Fehlermeldung. Diese Tests halten die Trennung fest.
/// </summary>
[Collection(UiCollection.Name)]
public class ReminderPresetTests
{
    private static string MainWindowXaml()
    {
        // Vom Testverzeichnis aus zum Quelltext hoch.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "NMailClient.Poc")))
            dir = dir.Parent;

        Assert.True(dir is not null, "Projektverzeichnis nicht gefunden.");
        return File.ReadAllText(
            Path.Combine(dir!.FullName, "NMailClient.Poc", "Views", "MainWindow.xaml"));
    }

    [Fact]
    public void TokensAreLanguageIndependent()
    {
        foreach (var (token, _, _) in MainViewModel.ReminderPresets)
        {
            Assert.Matches("^[A-Za-z]+$", token);
        }
    }

    [Fact]
    public void EveryPresetHasATranslationInBothLanguages()
    {
        foreach (var (_, key, _) in MainViewModel.ReminderPresets)
        {
            foreach (var language in new[] { "de", "en" })
            {
                Loc.Current.Use(language);
                Assert.True(Loc.Current.Has(key), $"'{key}' fehlt in '{language}'.");
                Assert.NotEqual(key, Loc.T(key));
            }
        }

        Loc.Current.Use("de");
    }

    [Fact]
    public void XamlPassesTheTokenNotTheLabel()
    {
        // Der eigentliche Wächter: stünde im XAML wieder eine deutsche
        // Beschriftung als Parameter, wäre die Zuordnung erneut sprachabhängig.
        var xaml = MainWindowXaml();

        foreach (var (token, _, _) in MainViewModel.ReminderPresets)
        {
            Assert.Contains($"CommandParameter=\"{token}\"", xaml);
        }

        Assert.DoesNotContain("CommandParameter=\"In 1 Stunde\"", xaml);
        Assert.DoesNotContain("CommandParameter=\"Morgen früh\"", xaml);
    }

    [Fact]
    public void EveryTokenIsUsedInTheXaml()
    {
        // Und andersherum: eine Vorgabe ohne Menüeintrag wäre tot.
        var xaml = MainWindowXaml();

        Assert.Equal(
            MainViewModel.ReminderPresets.Count,
            MainViewModel.ReminderPresets.Count(p => xaml.Contains($"CommandParameter=\"{p.Token}\"")));
    }

    [Fact]
    public void TokensAreUnique()
    {
        var tokens = MainViewModel.ReminderPresets.Select(p => p.Token).ToList();

        Assert.Equal(tokens.Count, tokens.Distinct().Count());
    }
}
