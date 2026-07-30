using System.IO;

namespace NMailClient.Services;

/// <summary>
/// Schlanker Datei-Logger. Ersetzt die MessageBox als Ort für Diagnosen –
/// Fehler in Hintergrundpfaden (Keychain, Cache, IMAP-Reconnect) dürfen den
/// Nutzer nicht mit Dialogen behelligen, aber auch nicht verschwinden.
///
/// Absichtlich ohne Fremdpaket: bei DI-Einführung (Roadmap 0.2.0) wird das
/// hinter <c>ILogger</c> gezogen, die Aufrufstellen bleiben dieselben.
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static readonly string Path = System.IO.Path.Combine(
        AppPaths.Local, "app.log");

    /// <summary>Aeltere Einträge abschneiden, damit die Datei nicht unbegrenzt wächst.</summary>
    private const long MaxBytes = 1 << 20; // 1 MiB

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);

    /// <summary>
    /// Fehler samt Stapelüberwachung. Ohne sie ist eine NullReferenceException
    /// im Log wertlos – die Meldung allein sagt nicht, wo sie entstand.
    /// </summary>
    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex is null
            ? message
            : $"{message}{Environment.NewLine}{ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

                if (File.Exists(Path) && new FileInfo(Path).Length > MaxBytes)
                    File.Delete(Path);

                File.AppendAllText(Path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nicht schreiben zu können darf niemals die Ursache eines Absturzes sein.
        }
    }

    /// <summary>Für die „Über"-Ansicht bzw. Supportfälle.</summary>
    public static string LogPath => Path;
}
