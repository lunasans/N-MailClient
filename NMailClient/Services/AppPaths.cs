using System.IO;

namespace NMailClient.Services;

/// <summary>
/// Die eine Stelle, an der steht, wie die Anwendung ihre Daten auf der Platte
/// und im Anmeldeinformationsverwalter benennt.
///
/// Solange der Name an vier Stellen ausgeschrieben stand, war eine Umbenennung
/// nicht nur Fleissarbeit, sondern ein Datenverlust mit Ansage: wer eine der
/// Stellen uebersieht, hinterlaesst Konten, Schluessel oder Passwoerter in einem
/// Ordner, den niemand mehr liest.
/// </summary>
public static class AppPaths
{
    /// <summary>Ordnername unter %APPDATA% bzw. %LOCALAPPDATA%.</summary>
    public const string Name = "NMailClient";

    /// <summary>
    /// Voriger Name aus der Zeit als Proof of Concept. Bleibt hier stehen,
    /// solange es Installationen gibt, die noch darunter abgelegt haben —
    /// <see cref="LegacyDataMigration"/> raeumt sie beim Start um.
    /// </summary>
    public const string LegacyName = "NMailClient.Poc";

    /// <summary>Wandert mit dem Benutzerprofil: Einstellungen, Schluesselbund.</summary>
    public static string Roaming { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Name);

    /// <summary>Bleibt am Geraet: Zwischenspeicher, Protokoll.</summary>
    public static string Local { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name);

    public static string LegacyRoaming { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), LegacyName);

    public static string LegacyLocal { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LegacyName);
}
