using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using NMailClient.Poc.Services;
using NMailClient.Poc.Services.Update;

namespace NMailClient.Poc.Views;

public partial class AboutWindow : Window
{
    private readonly UpdateService _updates;
    private UpdateInfo _info;

    public AboutWindow(UpdateService? updates = null)
    {
        InitializeComponent();

        _updates = updates ?? new UpdateService();
        _info = UpdateInfo.Unknown(AppVersion.Current);

        LblVersion.Text = $"Version {AppVersion.Current} — C#/WPF-Portierung";
        LblBuild.Text = $"Gebaut am {BuildDate():d} · .NET {Environment.Version}";

        ShowPaths();
        PackageList.ItemsSource = Packages;
    }

    /// <summary>
    /// Das Änderungsdatum der eigenen Datei. Bei einem deterministischen Build
    /// steht im Zeitstempel des PE-Kopfes keine echte Uhrzeit mehr, deshalb
    /// dieser schlichte Weg.
    /// </summary>
    private static DateTime BuildDate()
    {
        try
        {
            var path = Environment.ProcessPath;
            return string.IsNullOrEmpty(path) ? DateTime.MinValue : File.GetLastWriteTime(path);
        }
        catch (IOException) { return DateTime.MinValue; }
    }

    // ---- Ablageorte --------------------------------------------------------

    private void ShowPaths()
    {
        Add("Konfiguration", Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NMailClient.Poc", "db.json"));

        Add("Protokoll", AppLog.LogPath);
        Add("Zwischenspeicher", Services.Cache.MailCache.DefaultPath);
        Add("OpenPGP-Schlüsselbund", Services.Pgp.PgpKeyring.DefaultDirectory);
        Add("Passwörter", "Windows-Anmeldeinformationsverwalter (nicht als Datei)");

        void Add(string label, string value)
        {
            PathList.Children.Add(new TextBlock
            {
                Text = label,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 2),
            });

            var box = new TextBox
            {
                Text = value,
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                Background = System.Windows.Media.Brushes.Transparent,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 11.5,
                Margin = new Thickness(0, 0, 0, 12),
                TextWrapping = TextWrapping.Wrap,
            };

            // Als TextBox statt TextBlock, damit sich der Pfad herauskopieren lässt.
            PathList.Children.Add(box);
        }
    }

    // ---- Pakete ------------------------------------------------------------

    /// <summary>
    /// Ein Eintrag der Liste. Name, Lizenz und Zweck sind gepflegt — die stehen
    /// nirgends maschinenlesbar. Die <b>Version</b> wird dagegen zur Laufzeit aus
    /// dem Assembly gelesen: von Hand eingetragene Versionen driften still ab,
    /// und eine falsche Angabe ist schlechter als keine.
    /// </summary>
    public sealed record PackageInfo(
        string Name, string PackageId, string License, string Purpose)
    {
        /// <summary>Leere Kennung heisst: eigener Code, also die Version der Anwendung.</summary>
        public string Version => PackageId.Length == 0
            ? AppVersion.Current.ToString()
            : PackageVersions.Value.GetValueOrDefault(PackageId, "?");
    }

    /// <summary>
    /// Die Paketversionen aus <c>*.deps.json</c>.
    ///
    /// Weder die Assembly- noch die Informationsversion taugt dafür: die erste
    /// ist häufig grob gepinnt (BouncyCastle meldet 2.0.0 für das Paket 2.6.2),
    /// die zweite ist bei manchen Paketen schlicht falsch gepflegt
    /// (FolkerKinzel.MimeTypes meldet 1.0.0 für 5.6.0). In der deps.json steht
    /// die Fassung, die der Wiederherstellungsvorgang wirklich aufgelöst hat.
    /// </summary>
    private static readonly Lazy<Dictionary<string, string>> PackageVersions = new(ReadPackageVersions);

    private static Dictionary<string, string> ReadPackageVersions()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // Alle deps.json im Ausgabeordner: unter dem Test-Wirt heisst die
            // Datei anders als die der Anwendung.
            foreach (var file in Directory.GetFiles(AppContext.BaseDirectory, "*.deps.json"))
            {
                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));

                if (!document.RootElement.TryGetProperty("libraries", out var libraries)) continue;

                foreach (var entry in libraries.EnumerateObject())
                {
                    // Die Schlüssel lauten "Paket/Version".
                    var slash = entry.Name.LastIndexOf('/');
                    if (slash <= 0) continue;

                    result.TryAdd(entry.Name[..slash], entry.Name[(slash + 1)..]);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException
                                      or UnauthorizedAccessException)
        {
            AppLog.Warn("Paketversionen konnten nicht gelesen werden.");
        }

        return result;
    }

    /// <summary>
    /// Alle Pakete mit eigener Lizenz, auch die mittelbar eingebundenen — für
    /// eine Nennung zählt, was mitgeliefert wird, nicht was in der Projektdatei
    /// steht.
    /// </summary>
    public static readonly PackageInfo[] Packages =
    [
        // Eigene Bausteine zuerst: sie ersetzen Pakete, die es nicht gibt oder
        // die nicht taugen, und gehören derselben Aufstellung an – sonst sähe
        // es so aus, als käme das aus einer Fremdbibliothek.
        new("DNSSEC / DANE", "", "eigener Code",
            "Signaturkette ab dem Wurzelanker, TLSA-Abgleich"),
        new("CalDAV / CardDAV", "", "eigener Code",
            "PROPFIND, REPORT und ETag-Abgleich"),

        new("MimeKit", "MimeKit", "MIT", "MIME, OpenPGP"),
        new("MailKit", "MailKit", "MIT", "IMAP, SMTP, IDLE"),
        new("BouncyCastle.Cryptography", "BouncyCastle.Cryptography", "MIT",
            "Ed25519 für DNSSEC, OpenPGP-Unterbau"),
        new("Microsoft.Web.WebView2", "Microsoft.Web.WebView2", "BSD-3-Clause",
            "Mailanzeige, PDF, Drucken"),
        new("Microsoft.Data.Sqlite", "Microsoft.Data.Sqlite", "MIT", "Zwischenspeicher, Suche"),
        new("SQLitePCLRaw", "SQLitePCLRaw.core", "Apache-2.0", "Anbindung an SQLite"),
        new("SQLite", "SQLite", "Gemeinfrei", "Datenbank und Volltextsuche (FTS5)"),
        new("Dapper", "Dapper", "Apache-2.0", "Datenzugriff"),
        new("DnsClient", "DnsClient", "Apache-2.0", "SRV-Suche für Autodiscover"),
        new("Ical.Net", "Ical.Net", "MIT", "iCalendar lesen und schreiben"),
        new("NodaTime", "NodaTime", "Apache-2.0", "Zeitzonen für iCalendar"),
        new("FolkerKinzel.VCards", "FolkerKinzel.VCards", "MIT", "vCard 2.1 bis 4.0"),
        new("FolkerKinzel.Strings", "FolkerKinzel.Strings", "MIT", "Unterbau von VCards"),
        new("FolkerKinzel.MimeTypes", "FolkerKinzel.MimeTypes", "MIT", "Unterbau von VCards"),
        new("FolkerKinzel.DataUrls", "FolkerKinzel.DataUrls", "MIT", "Unterbau von VCards"),
        new("FolkerKinzel.Helpers", "FolkerKinzel.Helpers", "MIT", "Unterbau von VCards"),
        new("Microsoft.Extensions.DependencyInjection", "Microsoft.Extensions.DependencyInjection",
            "MIT", "Abhängigkeiten"),
    ];

    // ---- Aktualisierung ----------------------------------------------------

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        BtnCheck.IsEnabled = false;
        LblUpdate.Text = "Sehe nach …";

        try
        {
            _info = await _updates.CheckAsync();
            LblUpdate.Text = _info.Summary;

            BtnOpenRelease.Visibility = _info.State == UpdateState.Available
                                        && _info.ReleaseUrl is not null
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        finally { BtnCheck.IsEnabled = true; }
    }

    private void Release_Click(object sender, RoutedEventArgs e)
    {
        // Bewusst nur öffnen, nicht herunterladen und ausführen: eine
        // Selbstaktualisierung im Hintergrund muss genau einmal schiefgehen.
        if (_info.ReleaseUrl is { } url) Open(url);
    }

    private void Project_Click(object sender, RoutedEventArgs e)
        => Open($"https://github.com/{UpdateService.DefaultRepository}");

    private static void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            AppLog.Warn($"Adresse konnte nicht geöffnet werden: {url}");
        }
    }
}
