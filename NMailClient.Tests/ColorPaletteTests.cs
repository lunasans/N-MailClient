using NMailClient.Models;
using Xunit;

namespace NMailClient.Tests;

public class ColorPaletteTests
{
    [Fact]
    public void TheFirstAccountGetsTheFirstColour()
        => Assert.Equal(ColorPalette.Colors[0], ColorPalette.NextFree([]));

    [Fact]
    public void TheSecondAccountGetsADifferentColour()
    {
        // Der gemeldete Fehler: jedes weitere Konto trug dieselbe Farbe wie das
        // erste, und im gemeinsamen Posteingang wäre die Herkunft dann nicht
        // mehr zu erkennen.
        var first = ColorPalette.NextFree([]);
        var second = ColorPalette.NextFree([first]);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void EveryColourIsHandedOutBeforeAnyRepeats()
    {
        var given = new List<string>();
        for (int i = 0; i < ColorPalette.Colors.Length; i++)
            given.Add(ColorPalette.NextFree(given));

        Assert.Equal(given.Count, given.Distinct().Count());
        Assert.Equal(ColorPalette.Colors.Length, given.Count);
    }

    [Fact]
    public void MoreAccountsThanColoursStillGetOne()
    {
        // Lieber eine wiederholte Farbe als gar keine Vergabe.
        var all = ColorPalette.Colors.ToList();

        var extra = ColorPalette.NextFree(all);

        Assert.Contains(extra, ColorPalette.Colors);
    }

    [Fact]
    public void ComparisonIgnoresLetterCase()
    {
        // db.json enthält die Farben teils klein geschrieben.
        var lower = ColorPalette.Colors[0].ToLowerInvariant();

        Assert.NotEqual(ColorPalette.Colors[0], ColorPalette.NextFree([lower]));
    }

    [Fact]
    public void BlankEntriesDoNotBlockTheFirstColour()
        => Assert.Equal(ColorPalette.Colors[0], ColorPalette.NextFree([null, "", "   "]));

    [Fact]
    public void AnUnknownColourFallsBackForTheChooser()
    {
        // Die Auswahlliste kennt nur Werte aus der Tafel; ein von Hand
        // eingetragener Wert darf sie nicht leer lassen.
        Assert.Equal(ColorPalette.Default, ColorPalette.Nearest("#123456"));
        Assert.Equal(ColorPalette.Default, ColorPalette.Nearest(null));
    }

    [Fact]
    public void AKnownColourIsKeptByTheChooser()
        => Assert.Equal(ColorPalette.Colors[3], ColorPalette.Nearest(ColorPalette.Colors[3]));
}
