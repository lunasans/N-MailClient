using System.IO;
using NMailClient.Services;
using NMailClient.Services.Cache;
using Xunit;

namespace NMailClient.Tests;

/// <summary>
/// Waechter fuer den Fehler aus 0.9.1: die Datenuebernahme legte -wal und -shm
/// der alten Datenbank neben eine andere cache.db, woraufhin SQLite
/// 'database disk image is malformed' meldete.
/// </summary>
public class LegacyMigrationTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"nmc-mig-{Guid.NewGuid():N}");

    public LegacyMigrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("cache.db-wal")]
    [InlineData("cache.db-shm")]
    [InlineData("cache.db-journal")]
    public void CompanionOfExistingFileIsNotTakenOver(string companion)
    {
        // Am Ziel steht bereits eine (andere) Datenbank.
        File.WriteAllText(Path.Combine(_dir, "cache.db"), "neu");

        Assert.True(LegacyDataMigration.BelongsToExistingFile(_dir, companion));
    }

    [Fact]
    public void CompanionWithoutMainFileMayBeTakenOver()
    {
        // Ohne cache.db am Ziel gehoert das Beiwerk zu nichts – dann darf es mit.
        Assert.False(LegacyDataMigration.BelongsToExistingFile(_dir, "cache.db-wal"));
    }

    [Fact]
    public void OrdinaryFileIsNeverTreatedAsCompanion()
    {
        File.WriteAllText(Path.Combine(_dir, "db.json"), "{}");

        Assert.False(LegacyDataMigration.BelongsToExistingFile(_dir, "db.json"));
    }

    /// <summary>
    /// Der eigentliche Schutz fuer alle, die 0.9.1 schon installiert haben: eine
    /// unbrauchbare Datei darf die Anwendung nicht lahmlegen, sondern wird
    /// verworfen. Der Zwischenspeicher kommt vom Server zurueck.
    /// </summary>
    [Fact]
    public void UnusableCacheIsDiscardedInsteadOfThrowing()
    {
        var path = Path.Combine(_dir, "cache.db");

        // Keine SQLite-Datei, sondern Unsinn – wie eine beschaedigte Datenbank.
        File.WriteAllBytes(path, "Dies ist ganz sicher keine Datenbank."u8.ToArray());

        using var cache = new MailCache(path);

        // Benutzbar: der Aufruf wuerde auf einer kaputten Datei werfen.
        Assert.Null(cache.UidValidity("konto", "INBOX"));
    }

    [Fact]
    public void HealthyCacheSurvivesReopening()
    {
        var path = Path.Combine(_dir, "heil.db");

        using (var first = new MailCache(path))
            Assert.Null(first.UidValidity("konto", "INBOX"));

        // Wird beim zweiten Oeffnen nicht faelschlich als beschaedigt verworfen.
        using var second = new MailCache(path);
        Assert.Null(second.UidValidity("konto", "INBOX"));
    }
}
