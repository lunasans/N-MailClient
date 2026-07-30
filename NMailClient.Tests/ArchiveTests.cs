using System.IO;
using NMailClient.Services;
using Xunit;

namespace NMailClient.Tests;

public class ArchivePathTests
{
    private static readonly DateTimeOffset March = new(2026, 3, 7, 14, 30, 0, TimeSpan.Zero);

    [Fact]
    public void BuildsSenderYearMonthStructure()
    {
        var path = AttachmentArchive.BuildRelativePath("rene@example.org", March, "Rechnung.pdf");

        Assert.Equal(
            Path.Combine("rene@example.org", "2026", "03", "Rechnung.pdf"),
            path);
    }

    [Fact]
    public void MonthIsZeroPaddedSoFoldersSortCorrectly()
    {
        var path = AttachmentArchive.BuildRelativePath(
            "a@b.c", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), "x.txt");

        Assert.Contains(Path.Combine("2026", "01"), path);
    }

    [Fact]
    public void MissingSenderGetsItsOwnFolder()
    {
        var path = AttachmentArchive.BuildRelativePath("", March, "x.txt");
        Assert.StartsWith("Unbekannt", path);
    }

    // ---- Absicherung gegen bösartige Namen ---------------------------------

    [Theory]
    [InlineData("..\\..\\evil.exe")]
    [InlineData("../../evil.exe")]
    [InlineData("C:\\Windows\\System32\\evil.exe")]
    public void FileNamesCannotEscapeTheArchive(string fileName)
    {
        var safe = AttachmentArchive.SafeFileName(fileName);

        Assert.DoesNotContain("..", safe);
        Assert.DoesNotContain('\\', safe);
        Assert.DoesNotContain('/', safe);
        Assert.DoesNotContain(':', safe);
    }

    [Fact]
    public void SenderWithPathSeparatorsIsNeutralized()
    {
        // Absenderadressen kommen aus fremden Mails und sind nicht vertrauenswürdig.
        var segment = AttachmentArchive.SafeSegment("../../etc/passwd");

        Assert.DoesNotContain('/', segment);
        Assert.DoesNotContain('\\', segment);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("COM1.txt")]
    public void ReservedWindowsNamesArePrefixed(string name)
    {
        var safe = AttachmentArchive.SafeSegment(name);
        Assert.StartsWith("_", safe);
    }

    [Fact]
    public void TrailingDotsAndSpacesAreRemoved()
    {
        // Windows kann Dateien mit Punkt oder Leerzeichen am Ende nicht anlegen.
        var safe = AttachmentArchive.SafeSegment("Rechnung. . ");

        Assert.False(safe.EndsWith('.'));
        Assert.False(safe.EndsWith(' '));
    }

    [Fact]
    public void OverlongSegmentsAreShortened()
    {
        var safe = AttachmentArchive.SafeSegment(new string('a', 400));
        Assert.True(safe.Length <= 100);
    }

    [Fact]
    public void EmptyNameFallsBack()
    {
        Assert.NotEmpty(AttachmentArchive.SafeSegment(""));
        Assert.NotEmpty(AttachmentArchive.SafeFileName("   "));
    }

    // ---- Namenskollisionen --------------------------------------------------

    [Fact]
    public void FreePathIsUsedUnchanged()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nmc-{Guid.NewGuid():N}.txt");
        Assert.Equal(path, AttachmentArchive.Deduplicate(path));
    }

    [Fact]
    public void ExistingFileIsNotOverwritten()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"nmc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "Rechnung.pdf");
            File.WriteAllText(path, "erste");

            var second = AttachmentArchive.Deduplicate(path);
            Assert.NotEqual(path, second);
            Assert.Equal("Rechnung (2).pdf", Path.GetFileName(second));

            File.WriteAllText(second, "zweite");
            var third = AttachmentArchive.Deduplicate(path);
            Assert.Equal("Rechnung (3).pdf", Path.GetFileName(third));

            // Die erste Datei muss unangetastet sein.
            Assert.Equal("erste", File.ReadAllText(path));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}

[Trait("Category", "Integration")]
public class ArchiveBrowseTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"nmc-archive-{Guid.NewGuid():N}");

    private AttachmentArchive Archive => new(() => _root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void MissingArchiveYieldsEmptyList()
        => Assert.Empty(Archive.Browse());

    [Fact]
    public void FindsFilesAndDerivesSenderFromFolder()
    {
        var target = Archive.PrepareTarget("rene@example.org",
            new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), "Beleg.pdf");
        File.WriteAllText(target, "inhalt");

        var file = Assert.Single(Archive.Browse());

        Assert.Equal("Beleg.pdf", file.FileName);
        Assert.Equal("rene@example.org", file.Sender);
        Assert.Equal(6, file.Size);
    }

    [Fact]
    public void FilterMatchesFileNameAndSender()
    {
        var date = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        File.WriteAllText(Archive.PrepareTarget("anna@example.org", date, "Vertrag.pdf"), "x");
        File.WriteAllText(Archive.PrepareTarget("bernd@example.org", date, "Foto.jpg"), "x");

        Assert.Single(Archive.Browse("Vertrag"));
        Assert.Single(Archive.Browse("bernd"));
        Assert.Equal(2, Archive.Browse("").Count);
    }

    [Fact]
    public void ResultsAreGroupedBySenderThenNewestFirst()
    {
        var date = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

        // Absichtlich verschachtelt angelegt, damit die Reihenfolge nicht zufällig stimmt.
        var zeta = Archive.PrepareTarget("zeta@example.org", date, "z1.txt");
        File.WriteAllText(zeta, "x");
        var annaAlt = Archive.PrepareTarget("anna@example.org", date, "alt.txt");
        File.WriteAllText(annaAlt, "x");
        File.SetLastWriteTime(annaAlt, DateTime.Now.AddHours(-2));
        var annaNeu = Archive.PrepareTarget("anna@example.org", date, "neu.txt");
        File.WriteAllText(annaNeu, "x");

        var files = Archive.Browse();

        // Absender alphabetisch – anna vor zeta.
        Assert.Equal(["anna@example.org", "anna@example.org", "zeta@example.org"],
            files.Select(f => f.Sender).ToArray());

        // Innerhalb eines Absenders die neueste Datei zuerst.
        Assert.Equal("neu.txt", files[0].FileName);
        Assert.Equal("alt.txt", files[1].FileName);
    }

    [Fact]
    public void SenderSortingIgnoresCase()
    {
        var date = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        File.WriteAllText(Archive.PrepareTarget("Berta@example.org", date, "b.txt"), "x");
        File.WriteAllText(Archive.PrepareTarget("anna@example.org", date, "a.txt"), "x");

        var senders = Archive.Browse().Select(f => f.Sender).ToArray();

        Assert.Equal(["anna@example.org", "Berta@example.org"], senders);
    }

    [Fact]
    public void DeleteOutsideTheArchiveIsRefused()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"nmc-outside-{Guid.NewGuid():N}.txt");
        File.WriteAllText(outside, "wichtig");
        try
        {
            Assert.False(Archive.Delete(outside));
            Assert.True(File.Exists(outside));  // unangetastet
        }
        finally { File.Delete(outside); }
    }

    [Fact]
    public void DeleteInsideTheArchiveWorks()
    {
        var target = Archive.PrepareTarget("a@b.c", DateTimeOffset.Now, "weg.txt");
        File.WriteAllText(target, "x");

        Assert.True(Archive.Delete(target));
        Assert.False(File.Exists(target));
    }
}
