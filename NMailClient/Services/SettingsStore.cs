using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NMailClient.Models;

namespace NMailClient.Services;

/// <summary>
/// Persistenz nach %APPDATA%\NMailClient\db.json — Konten und globale Optionen.
///
/// Passwörter liegen NICHT in dieser Datei, sondern im Windows-Anmeldeinformations-
/// verwalter (<see cref="CredentialStore"/>). Altbestände aus der DPAPI-Phase der frühen Fassung
/// werden beim Laden erkannt und beim nächsten Speichern automatisch umgezogen.
/// </summary>
public class SettingsStore
{
    private readonly string _dir;
    private readonly string _path;
    private readonly string _legacyThemePath;

    public List<Account> Accounts { get; private set; } = new();
    public AppSettings Settings { get; private set; } = new();

    public SettingsStore()
    {
        _dir = AppPaths.Roaming;
        _path = Path.Combine(_dir, "db.json");
        _legacyThemePath = Path.Combine(_dir, "theme.txt");
    }

    private class Db
    {
        [JsonPropertyName("accounts")] public List<Account> Accounts { get; set; } = new();
        [JsonPropertyName("settings")] public AppSettings? Settings { get; set; }
    }

    public void Load()
    {
        Accounts = new List<Account>();
        Settings = new AppSettings();

        if (File.Exists(_path))
        {
            try
            {
                var db = JsonSerializer.Deserialize<Db>(File.ReadAllText(_path));
                if (db is not null)
                {
                    Settings = db.Settings ?? new AppSettings();
                    foreach (var a in db.Accounts)
                    {
                        a.Password = ResolvePassword(a);
                        Accounts.Add(a);
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                AppLog.Error("db.json nicht lesbar – starte mit leerer Konfiguration.", ex);
            }
        }

        MigrateLegacyTheme();
        SeparateAccountColors();
    }

    /// <summary>
    /// Bestandskonten tragen alle die frühere Vorgabefarbe. Doppelte einmalig
    /// auseinanderziehen — das erste Konto behält seine, jedes weitere bekommt
    /// eine freie. Ohne das wäre die Farbe im gemeinsamen Posteingang wertlos.
    /// </summary>
    private void SeparateAccountColors()
    {
        var taken = new List<string>();
        var changed = false;

        foreach (var account in Accounts)
        {
            var known = taken.Any(
                c => string.Equals(c, account.Color, StringComparison.OrdinalIgnoreCase));

            if (known || string.IsNullOrWhiteSpace(account.Color))
            {
                account.Color = ColorPalette.NextFree(taken);
                changed = true;
            }

            taken.Add(account.Color);
        }

        if (changed) Save();
    }

    /// <summary>
    /// Keychain zuerst, danach der DPAPI-Altbestand aus der Datei.
    /// </summary>
    private static string ResolvePassword(Account a)
    {
        var fromKeychain = CredentialStore.Read(CredentialStore.TargetFor(a.Id));
        if (!string.IsNullOrEmpty(fromKeychain)) return fromKeychain;

        // Altbestand: wird beim nächsten Save() in den Keychain geschrieben
        // und aus der Datei entfernt.
        var legacy = UnprotectLegacy(a.Password);
        if (!string.IsNullOrEmpty(legacy))
            AppLog.Info($"Passwort von Konto {a.Email} wird aus DPAPI übernommen.");
        return legacy;
    }

    /// <summary>Früheres theme.txt in die Einstellungen übernehmen und entfernen.</summary>
    private void MigrateLegacyTheme()
    {
        try
        {
            if (!File.Exists(_legacyThemePath)) return;

            var value = File.ReadAllText(_legacyThemePath).Trim();
            if (value.Length > 0) Settings.Theme = value;

            Save();
            File.Delete(_legacyThemePath);
            AppLog.Info($"theme.txt nach db.json übernommen (Theme={value}).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("theme.txt konnte nicht übernommen werden.");
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(_dir);

            // Passwörter in den Keychain, nicht in die Datei.
            foreach (var a in Accounts)
                CredentialStore.Save(CredentialStore.TargetFor(a.Id), a.Email, a.Password);

            // Über Clone(), damit neue Felder nicht vergessen werden; nur das
            // Passwort wird bewusst geleert – es liegt im Keychain.
            var db = new Db
            {
                Settings = Settings,
                Accounts = Accounts.Select(a =>
                {
                    var copy = a.Clone();
                    copy.Password = "";
                    return copy;
                }).ToList()
            };

            var json = JsonSerializer.Serialize(db, new JsonSerializerOptions { WriteIndented = true });

            // Erst temporär schreiben, dann ersetzen: ein Absturz mitten im Schreiben
            // darf die bestehende Konfiguration nicht zerstören.
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Konfiguration konnte nicht gespeichert werden.", ex);
        }
    }

    /// <summary>
    /// Einstellungen vollständig ersetzen (Sicherung einspielen). Die Instanz
    /// wird ausgetauscht, deshalb müssen alle Halter neu gebunden werden.
    /// </summary>
    public void ReplaceSettings(AppSettings settings) => Settings = settings;

    /// <summary>Konto samt Keychain-Eintrag entfernen.</summary>
    public void Forget(Account a)
    {
        CredentialStore.Delete(CredentialStore.TargetFor(a.Id));

        // Auch der mailcow-Schlüssel – sonst bliebe ein Administratorzugang im
        // Anmeldeinformationsverwalter zurück, obwohl das Konto weg ist.
        CredentialStore.Delete(CredentialStore.TargetFor(a.Id, "mailcow"));

        Accounts.RemoveAll(x => x.Id == a.Id);
        Save();
    }

    // ---- DPAPI: nur noch lesen, für die Migration --------------------------

    private const string DpapiPrefix = "dpapi:";

    private static string UnprotectLegacy(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        if (!stored.StartsWith(DpapiPrefix, StringComparison.Ordinal)) return stored;
        try
        {
            var blob = Convert.FromBase64String(stored[DpapiPrefix.Length..]);
            return Encoding.UTF8.GetString(
                ProtectedData.Unprotect(blob, null, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            AppLog.Warn("DPAPI-Altbestand nicht entschlüsselbar – Passwort neu eingeben.");
            return "";
        }
    }
}
