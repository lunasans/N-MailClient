using System.IO;
using System.Text;
using NMailClient.Poc.Models;

namespace NMailClient.Poc.Services;

/// <summary>Eine Datei im Archiv, aufbereitet für die Browse-Ansicht.</summary>
public record ArchivedFile(
    string FullPath, string FileName, string Sender, DateTime Modified, long Size)
{
    public string SizeDisplay => Size switch
    {
        < 1024 => $"{Size} B",
        < 1024 * 1024 => $"{Size / 1024.0:0.#} KB",
        _ => $"{Size / 1024.0 / 1024.0:0.#} MB",
    };

    public string ModifiedDisplay => Modified.ToString("dd.MM.yyyy HH:mm");
}

/// <summary>
/// Lokales Anhang-Archiv: legt Anhänge nach Absender, Jahr und Monat ab.
///
/// Die Ablagestruktur ist bewusst im Dateisystem sichtbar und ohne die Anwendung
/// benutzbar – ein Archiv, das nur diese App lesen kann, wäre für die Ablage von
/// Rechnungen und Belegen wertlos.
/// </summary>
public class AttachmentArchive
{
    private readonly Func<string> _rootProvider;

    public AttachmentArchive(Func<string> rootProvider) => _rootProvider = rootProvider;

    public string Root => _rootProvider();

    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "NMailClient-Anhänge");

    /// <summary>
    /// Zielpfad für einen Anhang: &lt;Wurzel&gt;/&lt;Absender&gt;/&lt;Jahr&gt;/&lt;Monat&gt;/&lt;Datei&gt;.
    /// Rein rechnend, ohne Dateizugriff – damit prüfbar.
    /// </summary>
    public static string BuildRelativePath(string sender, DateTimeOffset date, string fileName)
    {
        var folder = SafeSegment(string.IsNullOrWhiteSpace(sender) ? "Unbekannt" : sender);
        var local = date == default ? DateTime.Now : date.LocalDateTime;

        return Path.Combine(
            folder,
            local.ToString("yyyy"),
            local.ToString("MM"),
            SafeFileName(fileName));
    }

    /// <summary>
    /// Ein Pfadsegment aus fremden Daten. Absenderadressen und Dateinamen aus
    /// Mails können alles enthalten – auch Pfadtrenner und "..".
    /// </summary>
    public static string SafeSegment(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value.Trim())
            sb.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);

        var result = sb.ToString().Trim(' ', '.');
        if (result.Length == 0) return "Unbekannt";

        // Windows verbietet einige Gerätenamen als Dateinamen.
        if (IsReservedName(result)) result = "_" + result;

        return result.Length > 100 ? result[..100].TrimEnd(' ', '.') : result;
    }

    public static string SafeFileName(string fileName)
    {
        // Pfadanteile abschneiden: "..\..\evil.exe" darf nicht ausbrechen.
        var name = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(name)) name = "anhang";
        return SafeSegment(name);
    }

    private static bool IsReservedName(string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        string[] reserved =
        [
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        ];
        return reserved.Contains(stem, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Freier Zielpfad – hängt bei Namensgleichheit " (2)", " (3)" an, statt
    /// eine vorhandene Datei zu überschreiben.
    /// </summary>
    public static string Deduplicate(string fullPath)
    {
        if (!File.Exists(fullPath)) return fullPath;

        var dir = Path.GetDirectoryName(fullPath)!;
        var stem = Path.GetFileNameWithoutExtension(fullPath);
        var ext = Path.GetExtension(fullPath);

        for (int i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        // Nach 999 Versuchen eindeutig genug.
        return Path.Combine(dir, $"{stem} ({Guid.NewGuid():N}){ext}");
    }

    /// <summary>Zielpfad ermitteln und Verzeichnisse anlegen.</summary>
    public string PrepareTarget(string sender, DateTimeOffset date, string fileName)
    {
        var full = Path.Combine(Root, BuildRelativePath(sender, date, fileName));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        return Deduplicate(full);
    }

    /// <summary>
    /// Inhalt des Archivs, rekursiv. Der Absender ergibt sich aus dem obersten
    /// Ordner unterhalb der Wurzel.
    /// </summary>
    public List<ArchivedFile> Browse(string? filter = null)
    {
        var root = Root;
        if (!Directory.Exists(root)) return [];

        try
        {
            return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(path =>
                {
                    var info = new FileInfo(path);
                    return new ArchivedFile(
                        path, info.Name, SenderOf(root, path), info.LastWriteTime, info.Length);
                })
                .Where(f => string.IsNullOrWhiteSpace(filter)
                            || f.FileName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                            || f.Sender.Contains(filter, StringComparison.OrdinalIgnoreCase))
                // Nach Absender, darin die neueste Datei zuerst. Die Ansicht
                // gruppiert darauf – deshalb muss die Sortierung hier passen.
                .OrderBy(f => f.Sender, StringComparer.CurrentCultureIgnoreCase)
                .ThenByDescending(f => f.Modified)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Anhang-Archiv nicht lesbar.", ex);
            return [];
        }
    }

    private static string SenderOf(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        var first = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        return first.Length > 1 ? first[0] : "(Wurzel)";
    }

    public bool Delete(string fullPath)
    {
        try
        {
            // Nur innerhalb der Wurzel löschen – nie ausserhalb, egal was übergeben wird.
            var root = Path.GetFullPath(Root);
            var target = Path.GetFullPath(fullPath);
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Warn($"Löschen ausserhalb des Archivs abgelehnt: {target}");
                return false;
            }

            File.Delete(target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error($"Datei nicht löschbar: {fullPath}", ex);
            return false;
        }
    }
}
