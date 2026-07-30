using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using NMailClient.Poc.Models;

namespace NMailClient.Poc.Services;

/// <summary>
/// Inhalt einer Sicherungsdatei.
///
/// <b>Ohne Passwörter.</b> Die liegen im Windows-Anmeldeinformationsverwalter und
/// sind an Benutzer und Rechner gebunden; sie in eine Datei zu schreiben würde die
/// Absicherung aushebeln. Nach dem Einspielen sind sie erneut einzugeben.
/// </summary>
public class BackupFile
{
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    [JsonPropertyName("application")] public string Application { get; set; } = "NMailClient.Poc";

    /// <summary>Format der Sicherung – erlaubt spätere Migrationen.</summary>
    [JsonPropertyName("version")] public int Version { get; set; } = 1;

    [JsonPropertyName("settings")] public AppSettings? Settings { get; set; }
    [JsonPropertyName("accounts")] public List<Account> Accounts { get; set; } = [];

    [JsonIgnore]
    public string Description =>
        $"Sicherung vom {CreatedAt.LocalDateTime:dd.MM.yyyy HH:mm} "
        + $"mit {Accounts.Count} Konto/Konten";
}

public static class SettingsBackup
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(AppSettings settings, IEnumerable<Account> accounts)
    {
        var backup = new BackupFile
        {
            Settings = settings,
            // Über Clone(), damit neue Felder mitkommen – und ohne Passwörter.
            Accounts = accounts.Select(a =>
            {
                var copy = a.Clone();
                copy.Password = "";
                return copy;
            }).ToList(),
        };

        return JsonSerializer.Serialize(backup, Options);
    }

    /// <summary>Liest eine Sicherung; wirft bei fremdem oder kaputtem Inhalt.</summary>
    public static BackupFile Parse(string json)
    {
        var backup = JsonSerializer.Deserialize<BackupFile>(json)
            ?? throw new InvalidDataException("Die Datei enthält keine Sicherung.");

        if (!backup.Application.Equals("NMailClient.Poc", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Die Datei stammt aus '{backup.Application}' und passt nicht zu diesem Programm.");

        if (backup.Version > 1)
            throw new InvalidDataException(
                $"Die Sicherung hat Version {backup.Version} und ist neuer als dieses Programm.");

        return backup;
    }

    public static void Save(string path, AppSettings settings, IEnumerable<Account> accounts)
        => File.WriteAllText(path, Serialize(settings, accounts));

    public static BackupFile Load(string path) => Parse(File.ReadAllText(path));
}
