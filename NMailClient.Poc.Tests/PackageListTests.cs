using NMailClient.Poc.Views;
using Xunit;

namespace NMailClient.Poc.Tests;

/// <summary>
/// Die Paketliste der „Über"-Ansicht. Ohne diese Prüfung wäre sie eine
/// gepflegte Behauptung: ein Tippfehler im Assembly-Namen oder ein entferntes
/// Paket fielen niemandem auf, und die Anzeige zeigte still „?".
/// </summary>
public class PackageListTests
{
    [Fact]
    public void EveryEntryResolvesARealVersion()
    {
        var broken = AboutWindow.Packages
            .Where(p => p.Version is "?" or "")
            .Select(p => $"{p.Name} ({p.PackageId})")
            .ToList();

        Assert.True(broken.Count == 0,
            "Diese Einträge liefern keine Version: " + string.Join(", ", broken));
    }

    [Fact]
    public void VersionsAreNotHardcoded()
    {
        // Gegenprobe zur vorigen Prüfung: die Version muss wirklich aus dem
        // Assembly stammen. MailKit steht auf 4.x – bliebe hier etwas anderes
        // stehen, käme der Wert aus einer Konstanten.
        var mailkit = AboutWindow.Packages.Single(p => p.Name == "MailKit");

        Assert.StartsWith("4.", mailkit.Version);
    }

    [Fact]
    public void SqliteEngineIsListedWithItsOwnVersion()
    {
        var sqlite = AboutWindow.Packages.Single(p => p.Name == "SQLite");

        Assert.Equal("SQLite", sqlite.PackageId);
        Assert.StartsWith("3.", sqlite.Version);
    }

    [Fact]
    public void PinnedAssemblyVersionsAreNotUsed()
    {
        // BouncyCastle hat die Assembly-Version fest auf 2.0.0 gesetzt, das
        // Paket steht auf 2.6.x. Wer die Assembly-Version anzeigt, zeigt hier
        // etwas Falsches an — genau das war der erste Anlauf.
        var bc = AboutWindow.Packages.Single(p => p.Name == "BouncyCastle.Cryptography");

        Assert.StartsWith("2.6.", bc.Version);
        Assert.NotEqual("2.0.0", bc.Version);
    }

    [Fact]
    public void MisreportedInformationalVersionsAreNotUsedEither()
    {
        // FolkerKinzel.MimeTypes traegt als Informationsversion 1.0.0, ist aber
        // Paket 5.6.x. Damit scheidet auch diese Quelle aus; verlaesslich ist
        // nur die deps.json.
        var mime = AboutWindow.Packages.Single(p => p.Name == "FolkerKinzel.MimeTypes");

        Assert.StartsWith("5.", mime.Version);
        Assert.NotEqual("1.0.0", mime.Version);
    }

    [Fact]
    public void EveryEntryNamesALicense()
    {
        Assert.All(AboutWindow.Packages, p => Assert.False(string.IsNullOrWhiteSpace(p.License)));
        Assert.All(AboutWindow.Packages, p => Assert.False(string.IsNullOrWhiteSpace(p.Purpose)));
    }

    [Fact]
    public void NoDuplicates()
    {
        var names = AboutWindow.Packages.Select(p => p.Name).ToList();

        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void OwnComponentsAreListedAsSuch()
    {
        // DNSSEC/DANE und die DAV-Schicht sind kein Fremdpaket. Sie stehen
        // trotzdem in der Aufstellung — sonst sähe es so aus, als käme das aus
        // einer Fremdbibliothek. Die Lizenzspalte sagt deshalb ausdrücklich,
        // woher es stammt.
        var own = AboutWindow.Packages.Where(p => p.License == "eigener Code").ToList();

        Assert.Contains(own, p => p.Name == "DNSSEC / DANE");
        Assert.Contains(own, p => p.Name == "CalDAV / CardDAV");

        // Und sie tragen die Version der Anwendung, nicht die eines Pakets.
        var expected = NMailClient.Poc.Services.Update.AppVersion.Current;
        Assert.All(own, p => Assert.StartsWith($"{expected.Major}.{expected.Minor}.", p.Version));
    }

    [Fact]
    public void TransitiveDependenciesAreListedToo()
    {
        // Für eine Nennung zählt, was mitgeliefert wird – nicht, was in der
        // Projektdatei steht. Diese drei kommen nur mittelbar herein.
        var names = AboutWindow.Packages.Select(p => p.Name).ToList();

        Assert.Contains("NodaTime", names);                    // über Ical.Net
        Assert.Contains("BouncyCastle.Cryptography", names);   // über MimeKit
        Assert.Contains("FolkerKinzel.Strings", names);        // über VCards
    }
}
