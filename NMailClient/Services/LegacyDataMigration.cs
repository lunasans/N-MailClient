using System.IO;

namespace NMailClient.Services;

/// <summary>
/// Holt Daten ab, die noch unter dem alten Namen "NMailClient.Poc" liegen:
/// Einstellungen samt Konten, PGP-Schluesselbund, Zwischenspeicher, Protokoll
/// und die Eintraege im Anmeldeinformationsverwalter.
///
/// Laeuft vor dem Aufbau des Containers — jeder Dienst legt seinen Pfad im
/// Feldinitialisierer fest, ein Umzug danach kaeme zu spaet.
///
/// Grundregel bei jedem Schritt: was am Ziel schon steht, bleibt. Ein
/// vorhandener neuer Bestand ist der juengere; ihn mit Altdaten zu ueberschreiben
/// waere genau der Datenverlust, den diese Klasse verhindern soll.
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
        // Erst beide Ordner umziehen, dann protokollieren. AppLog legt beim
        // ersten Schreiben den Ordner unter %LOCALAPPDATA% an — dazwischen
        // protokolliert faende der zweite Umzug sein Ziel bereits vor und
        // muesste kopieren statt umzuhaengen.
        var notizen = new List<string>();

        try
        {
            notizen.Add(MoveFolder(AppPaths.LegacyRoaming, AppPaths.Roaming));
            notizen.Add(MoveFolder(AppPaths.LegacyLocal, AppPaths.Local));

            foreach (var n in notizen.Where(n => n.Length > 0)) AppLog.Info(n);

            CredentialStore.MigrateLegacyPrefix(AppPaths.LegacyName);
        }
        catch (Exception ex)
        {
            AppLog.Error("Uebernahme der Daten aus der PoC-Fassung fehlgeschlagen.", ex);
        }
    }

    /// <returns>Was geschehen ist, oder leer, wenn nichts zu tun war.</returns>
    private static string MoveFolder(string from, string to)
    {
        if (!Directory.Exists(from) || from.Equals(to, StringComparison.OrdinalIgnoreCase))
            return "";

        if (!Directory.Exists(to))
        {
            // Der einfache Fall: schlichtes Umhaengen, kein Kopieren.
            Directory.Move(from, to);
            return $"Datenordner uebernommen: '{from}' -> '{to}'.";
        }

        // Beide Ordner existieren — etwa weil die neue Fassung schon einmal lief,
        // bevor der Altbestand auffiel. Nur ergaenzen, was am Ziel fehlt.
        var added = MergeInto(from, to);

        return added > 0
            ? $"{added} Datei(en) aus '{from}' ergaenzt; der uebrige Altbestand bleibt liegen."
            : $"Altbestand in '{from}' vollstaendig durch neuere Daten gedeckt.";
    }

    private static int MergeInto(string from, string to)
    {
        var added = 0;

        foreach (var quelle in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var ziel = Path.Combine(to, Path.GetRelativePath(from, quelle));
            if (File.Exists(ziel)) continue;

            Directory.CreateDirectory(Path.GetDirectoryName(ziel)!);
            File.Copy(quelle, ziel);
            added++;
        }

        return added;
    }
}
