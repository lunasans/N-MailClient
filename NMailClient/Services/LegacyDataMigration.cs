using System.IO;

namespace NMailClient.Services;

/// <summary>
/// Holt Daten ab, die noch unter dem alten Namen "NMailClient.Poc" liegen:
/// Einstellungen samt Konten, PGP-Schluesselbund, Ausgangswarteschlange und die
/// Eintraege im Anmeldeinformationsverwalter.
///
/// <para><b>Nur %APPDATA%.</b> Unter %LOCALAPPDATA% liegen ausschliesslich
/// entbehrliche Dinge: der Zwischenspeicher (jederzeit vom Server wieder-
/// herstellbar), das Protokoll und das WebView2-Profil. Die zu uebernehmen
/// bringt nichts und ging in 0.9.1 gruendlich schief — dazu unten mehr.</para>
///
/// Laeuft vor dem Aufbau des Containers — jeder Dienst legt seinen Pfad im
/// Feldinitialisierer fest, ein Umzug danach kaeme zu spaet.
/// </summary>
public static class LegacyDataMigration
{
    /// <summary>
    /// Fuehrt den Umzug aus, falls noetig. Fehler brechen den Start nicht ab:
    /// eine Anwendung, die wegen eines Altbestands gar nicht mehr startet,
    /// waere schlimmer als eine, die mit leeren Einstellungen hochkommt.
    /// </summary>
    public static void Run()
    {
        try
        {
            var notiz = MoveFolder(AppPaths.LegacyRoaming, AppPaths.Roaming);
            if (notiz.Length > 0) AppLog.Info(notiz);

            CredentialStore.MigrateLegacyPrefix(AppPaths.LegacyName);
        }
        catch (Exception ex)
        {
            AppLog.Error("Uebernahme der Daten aus der fruehen Fassung fehlgeschlagen.", ex);
        }
    }

    /// <returns>Was geschehen ist, oder leer, wenn nichts zu tun war.</returns>
    private static string MoveFolder(string from, string to)
    {
        if (!Directory.Exists(from) || from.Equals(to, StringComparison.OrdinalIgnoreCase))
            return "";

        if (!Directory.Exists(to))
        {
            // Der einfache Fall: schlichtes Umhaengen, kein Kopieren. Alles oder
            // nichts — es kann kein halb uebernommener Stand entstehen.
            Directory.Move(from, to);
            return $"Datenordner uebernommen: '{from}' -> '{to}'.";
        }

        // Beide Ordner existieren — etwa weil die neue Fassung schon einmal lief,
        // bevor der Altbestand auffiel. Nur ergaenzen, was am Ziel fehlt.
        var (added, failed) = MergeInto(from, to);

        return $"{added} Datei(en) aus '{from}' ergaenzt"
               + (failed > 0 ? $", {failed} nicht lesbar (uebersprungen)" : "")
               + "; der uebrige Altbestand bleibt liegen.";
    }

    /// <summary>
    /// Ergaenzt am Ziel, was dort fehlt. Zwei Lehren aus 0.9.1 stecken hier drin:
    ///
    /// <para><b>Je Datei abfangen.</b> Damals riss eine einzige gesperrte Datei —
    /// ein WebView2-Cookie einer noch laufenden alten Instanz — die gesamte
    /// Uebernahme ab und hinterliess sie halbfertig. Ein unlesbarer Einzelfall
    /// darf den Rest nicht verhindern.</para>
    ///
    /// <para><b>Kein Vermischen zusammengehoeriger Dateien.</b> Die Regel
    /// "ueberspringen, was am Ziel schon da ist" ist fuer eine SQLite-Datenbank
    /// falsch: cache.db, -wal und -shm sind <em>ein</em> Gebilde. Damals blieb
    /// die neue cache.db stehen, waehrend -wal und -shm der alten danebengelegt
    /// wurden — SQLite fand ein Write-Ahead-Log mit fremden Pruefsummen und
    /// meldete 'database disk image is malformed'. Hier wird %LOCALAPPDATA% gar
    /// nicht mehr angefasst, aber die Regel gilt weiter: Begleitdateien einer
    /// uebersprungenen Datei werden mit uebersprungen.
    /// </para>
    /// </summary>
    private static (int Added, int Failed) MergeInto(string from, string to)
    {
        var added = 0;
        var failed = 0;

        foreach (var quelle in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var relativ = Path.GetRelativePath(from, quelle);
            var ziel = Path.Combine(to, relativ);

            if (File.Exists(ziel)) continue;
            if (BelongsToExistingFile(to, relativ)) continue;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ziel)!);
                File.Copy(quelle, ziel);
                added++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Meist eine noch laufende alte Instanz. Kein Grund aufzugeben.
                AppLog.Warn($"'{relativ}' nicht uebernommen: {ex.Message}");
                failed++;
            }
        }

        return (added, failed);
    }

    /// <summary>
    /// Wahr, wenn die Datei nur Beiwerk einer anderen ist, die am Ziel schon
    /// steht — dann gehoert sie zu <em>deren</em> Bestand und darf nicht aus dem
    /// Altbestand danebengelegt werden.
    ///
    /// Oeffentlich, damit sich die Regel ohne Dateiumzug pruefen laesst: sie ist
    /// der Kern des Fehlers aus 0.9.1 und soll nicht stillschweigend verrutschen.
    /// </summary>
    public static bool BelongsToExistingFile(string to, string relativ)
    {
        foreach (var suffix in Companions)
        {
            if (!relativ.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;

            var haupt = relativ[..^suffix.Length];
            if (File.Exists(Path.Combine(to, haupt))) return true;
        }

        return false;
    }

    /// <summary>Endungen, die eine Datei zum Beiwerk einer anderen machen.</summary>
    private static readonly string[] Companions = ["-wal", "-shm", "-journal", ".tmp"];
}
