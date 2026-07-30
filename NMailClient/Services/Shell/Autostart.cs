using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace NMailClient.Services.Shell;

/// <summary>
/// Start mit Windows, über den Run-Schlüssel des Benutzers.
///
/// Bewusst <c>HKEY_CURRENT_USER</c> und nicht die Maschinen-Variante: für
/// letztere bräuchte es Administratorrechte, und ein Mailprogramm, das beim
/// Einschalten eine Rechteabfrage auslöst, ist eine Zumutung.
/// </summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "NMailClient";

    /// <summary>Der Pfad, der im Autostart stehen müsste – in Anführungszeichen.</summary>
    public static string? ExecutablePath
    {
        get
        {
            var path = Environment.ProcessPath;
            return string.IsNullOrEmpty(path) || !File.Exists(path) ? null : $"\"{path}\"";
        }
    }

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string value && value.Length > 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                          or System.Security.SecurityException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Ein- oder ausschalten. Gibt zurück, ob es geklappt hat — eine gesperrte
    /// Registry (etwa per Gruppenrichtlinie) ist kein Absturzgrund, aber der
    /// Schalter darf dann auch nicht so tun, als hätte es gewirkt.
    /// </summary>
    public static bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null) return false;

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return true;
            }

            if (ExecutablePath is not { } path) return false;

            // Der Anwender soll beim Anmelden nicht von einem Fenster überrascht
            // werden – deshalb startet der Autostart in den Infobereich.
            key.SetValue(ValueName, $"{path} --minimized");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or System.Security.SecurityException)
        {
            AppLog.Warn("Autostart konnte nicht geändert werden (Registry gesperrt?).");
            return false;
        }
    }

    /// <summary>
    /// Wurde die Anwendung vom Autostart gerufen? Dann bleibt das Fenster zu.
    /// </summary>
    public static bool StartedMinimized(string[] args)
        => args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Zeigt den Eintrag im Registrierungs-Editor — nur für die Fehlersuche,
    /// nicht im normalen Ablauf verwendet.
    /// </summary>
    public static void OpenRunKeyInRegedit()
    {
        try
        {
            Registry.SetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Applets\Regedit",
                "LastKey", $@"Computer\HKEY_CURRENT_USER\{RunKey}");
            Process.Start(new ProcessStartInfo("regedit.exe") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or System.ComponentModel.Win32Exception)
        {
            AppLog.Warn("Registrierungs-Editor konnte nicht geöffnet werden.");
        }
    }
}
