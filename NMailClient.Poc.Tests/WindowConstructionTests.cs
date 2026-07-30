using System.IO;
using System.Windows;
using NMailClient.Poc.Models;
using NMailClient.Poc.Services;
using NMailClient.Poc.Services.Dav;
using NMailClient.Poc.Views;
using Xunit;

namespace NMailClient.Poc.Tests;

/// <summary>
/// Baut Fenster auf und prüft, dass die Konstruktoren durchlaufen.
///
/// Hintergrund: Ereignisse aus dem XAML – etwa <c>IsChecked="True"</c> mit
/// angehängtem Handler – feuern bereits während <c>InitializeComponent</c>,
/// also bevor die Felder der Klasse gesetzt sind. Genau daran ist das
/// Archiv-Fenster mit einer NullReferenceException gescheitert; ohne einen
/// solchen Test fällt das erst beim Klicken auf.
///
/// Alles läuft in <b>einem</b> Testfall auf <b>einem</b> STA-Thread: WPF erlaubt
/// nur eine <see cref="Application"/> je Prozess, und deren Ressourcen sind an
/// den erzeugenden Thread gebunden.
/// </summary>
[Trait("Category", "Integration")]
[Collection(UiCollection.Name)]
public class WindowConstructionTests
{
    [Fact]
    public void WindowsConstructWithoutError()
    {
        var root = Path.Combine(Path.GetTempPath(), $"poc-wintest-{Guid.NewGuid():N}");
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                EnsureApplicationWithResources();

                // Vorhandener Ablageordner …
                Directory.CreateDirectory(root);
                Assert.NotNull(new ArchiveWindow(new AttachmentArchive(() => root)));

                // … und der Fall beim allerersten Start, wenn er noch fehlt.
                var missing = Path.Combine(root, "gibt-es-nicht");
                Assert.NotNull(new ArchiveWindow(new AttachmentArchive(() => missing)));

                // Kontakte, mit und ohne hinterlegte CardDAV-Adresse. Ohne Adresse
                // darf das Fenster nicht scheitern, sondern muss es nur melden.
                var account = new Account { Id = "test", Email = "a@b.c" };
                Assert.NotNull(new ContactsWindow(
                    account, new CardDavService(() => ("", "", ""))));
                Assert.NotNull(new ContactsWindow(
                    account, new CardDavService(() => ("https://dav.example.org/", "u", "p"))));

                // Die „Über"-Ansicht liest im Konstruktor Version, Ablageorte
                // und Paketliste zusammen – ohne Netz und ohne Prüfung auf
                // Aktualisierungen, die passiert erst auf Knopfdruck.
                Assert.NotNull(new AboutWindow());

                // Der Verfasser blendet die PGP-Schalter im Konstruktor ein und
                // fragt dafür den Schlüsselbund – ohne eingerichtetes PGP darf
                // das Fenster trotzdem aufgehen.
                Assert.NotNull(NewComposeWindow(account, Path.Combine(root, "pgp-leer")));

                // Und mit vorhandenem Schlüssel: dann sind die Schalter sichtbar.
                var withKey = new NMailClient.Poc.Services.Pgp.PgpService(
                    Path.Combine(root, "pgp-voll"));
                withKey.GenerateKey(
                    new MimeKit.MailboxAddress("Test", account.Email), "mantra-im-test");
                Assert.NotNull(NewComposeWindow(account, null, withKey));
            }
            catch (Exception ex) { failure = ex; }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        try
        {
            if (failure is not null) throw failure;
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Die Fenster greifen über StaticResource auf Styles aus App.xaml zu.
    /// Ohne geladene Anwendung schlägt schon das Parsen fehl.
    /// </summary>
    /// <summary>
    /// Baut den Verfasser mit Attrappen: keine Verbindung wird aufgebaut, die
    /// Dienste werden nur konstruiert.
    /// </summary>
    private static ComposeWindow NewComposeWindow(
        Account account, string? keyringDir, NMailClient.Poc.Services.Pgp.PgpService? pgp = null)
    {
        var smtp = new SmtpService(account);
        var imap = new ImapService(account);

        return new ComposeWindow(
            account, smtp, imap, NMailClient.Poc.ViewModels.ComposeRequest.Blank(),
            new SettingsStore(),
            new OutboxService(_ => smtp, _ => imap),
            new CardDavService(() => ("", "", "")),
            pgp ?? new NMailClient.Poc.Services.Pgp.PgpService(keyringDir),
            new NMailClient.Poc.Services.Transport.RecipientSecurity());
    }

    private static void EnsureApplicationWithResources()
    {
        if (Application.Current is not null) return;

        var app = new Application();
        foreach (var path in new[] { "Themes/Light.xaml", "Themes/Controls.xaml" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/NMailClient.Poc;component/{path}"),
            });
    }
}
