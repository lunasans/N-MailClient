using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using NMailClient.Poc.Models;
using NMailClient.Poc.Services;
using NMailClient.Poc.ViewModels;
using NMailClient.Poc.Views;

namespace NMailClient.Poc;

public partial class App : Application
{
    private ServiceProvider? _services;

    /// <summary>
    /// Zugriff für Stellen, die (noch) nicht über den Konstruktor versorgt werden –
    /// vor allem Fenster, die WPF selbst erzeugt.
    /// </summary>
    public static IServiceProvider Services =>
        ((App)Current)._services
        ?? throw new InvalidOperationException("Container noch nicht aufgebaut.");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Unbehandelte Fehler sollen die PoC nicht wortlos beenden.
        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Error("Unbehandelte Ausnahme in der Oberfläche.", args.Exception);
            MessageBox.Show(args.Exception.Message, "Unerwarteter Fehler",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        _services = BuildContainer();

        // Reihenfolge ist wichtig: das ViewModel lädt die Einstellungen, daran hängt
        // die Theme-Wahl, und die muss vor dem ersten Fenster stehen – sonst blitzt
        // kurz das helle Schema auf.
        var vm = _services.GetRequiredService<MainViewModel>();

        // Sprache vor dem ersten Fenster setzen, sonst blitzt kurz die
        // Vorgabesprache auf.
        NMailClient.Poc.Services.I18n.Loc.Current.Use(vm.Settings.Language);

        ThemeManager.Bind(vm.Settings, vm.SaveSettings);
        ThemeManager.Apply(ThemeManager.LoadPreference());

        // Das Fenster darf verborgen sein, ohne dass die Anwendung endet – sonst
        // liesse sich weder in den Infobereich legen noch minimiert starten.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var window = _services.GetRequiredService<MainWindow>();

        if (NMailClient.Poc.Services.Shell.Autostart.StartedMinimized(e.Args))
        {
            // Vom Autostart gerufen: nur das Symbol im Infobereich, kein Fenster,
            // das sich beim Anmelden vor alles andere schiebt.
            window.StartHidden();
        }
        else
        {
            window.Show();
        }
    }

    /// <summary>
    /// Öffentlich, damit Tests die Registrierungen prüfen können: fehlende
    /// Abhängigkeiten fallen sonst erst beim Start auf, nicht beim Kompilieren.
    /// </summary>
    public static ServiceProvider BuildContainer()
    {
        var services = new ServiceCollection();

        // Anwendungsweiter Zustand.
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<MailServiceRegistry>();
        services.AddSingleton<ReminderStore>();

        // Der Schlüsselbund hängt an der Registrierung (ein Bestand für alle
        // Konten), soll aber auch einzeln injizierbar sein.
        services.AddSingleton(sp => sp.GetRequiredService<MailServiceRegistry>().Pgp);

        // Ein Zwischenspeicher für die ganze Anwendung: dieselbe Empfängerdomain
        // soll nicht in jedem Verfasserfenster neu angefragt werden.
        services.AddSingleton<Services.Transport.RecipientSecurity>();

        // Archivwurzel bei jedem Zugriff frisch lesen – eine Änderung in den
        // Einstellungen soll ohne Neustart wirken.
        services.AddSingleton(sp =>
        {
            var store = sp.GetRequiredService<SettingsStore>();
            return new AttachmentArchive(() =>
                string.IsNullOrWhiteSpace(store.Settings.ArchiveDir)
                    ? AttachmentArchive.DefaultRoot
                    : store.Settings.ArchiveDir);
        });

        // Übersetzung liest ihre Adresse aus den Einstellungen, den Schlüssel aus
        // dem Anmeldeinformationsverwalter – beides bei jedem Aufruf frisch, damit
        // Änderungen ohne Neustart greifen.
        services.AddSingleton(sp =>
        {
            var store = sp.GetRequiredService<SettingsStore>();
            return new TranslateService(() => (
                store.Settings.TranslateUrl,
                CredentialStore.Read(CredentialStore.GlobalTarget("translate")) ?? ""));
        });

        services.AddSingleton<MainViewModel>();

        // Ausgang: löst Konten über den Store auf, damit die Warteschlange auch
        // nach einem Neustart weiss, zu welchem Konto eine Mail gehört.
        services.AddSingleton(sp =>
        {
            var store = sp.GetRequiredService<SettingsStore>();
            var registry = sp.GetRequiredService<MailServiceRegistry>();

            Account? Lookup(string id) => store.Accounts.FirstOrDefault(a => a.Id == id);

            return new OutboxService(
                id => Lookup(id) is { } a ? registry.Smtp(a) : null,
                id => Lookup(id) is { } a ? registry.Imap(a) : null);
        });

        // Fenster: Hauptfenster einmalig, Dialoge je Aufruf neu.
        services.AddSingleton<MainWindow>();
        services.AddTransient<SettingsWindow>();

        // Dialoge werden pro Aufruf erzeugt, teils mit Laufzeitwerten (Konto,
        // Vorbelegung). Fabriken statt Direktinjektion, damit das Hauptfenster
        // keine Fensterinstanzen festhält.
        services.AddTransient<ArchiveWindow>();

        services.AddSingleton<Func<SettingsWindow>>(sp =>
            sp.GetRequiredService<SettingsWindow>);

        services.AddSingleton<Func<ArchiveWindow>>(sp =>
            sp.GetRequiredService<ArchiveWindow>);

        // Kontakte und Kalender hängen am gewählten Konto, deshalb als Fabriken.
        services.AddSingleton<Func<Account, ContactsWindow>>(sp => account =>
        {
            var registry = sp.GetRequiredService<MailServiceRegistry>();
            return new ContactsWindow(account, registry.CardDav(account));
        });

        services.AddSingleton<Func<Account, CalendarWindow>>(sp => account =>
        {
            var registry = sp.GetRequiredService<MailServiceRegistry>();
            // Der Kalender zieht Geburtstage aus den Kontakten – daher beide Dienste.
            return new CalendarWindow(registry.CalDav(account), registry.CardDav(account));
        });

        services.AddSingleton<Func<Account, ComposeRequest, ComposeWindow>>(sp =>
            (account, request) =>
            {
                var registry = sp.GetRequiredService<MailServiceRegistry>();
                return new ComposeWindow(
                    account, registry.Smtp(account), registry.Imap(account), request,
                    sp.GetRequiredService<SettingsStore>(),
                    sp.GetRequiredService<OutboxService>(),
                    // Für die Empfänger-Autovervollständigung.
                    registry.CardDav(account),
                    registry.Pgp,
                    sp.GetRequiredService<Services.Transport.RecipientSecurity>());
            });

        return services.BuildServiceProvider();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // MailServiceRegistry ist nur IAsyncDisposable (IMAP-Verbindungen werden
        // sauber abgemeldet). Ein synchrones Dispose() wirft hier, deshalb blockierend
        // auf DisposeAsync warten – beim Beenden ist das vertretbar.
        if (_services is not null)
            _services.DisposeAsync().AsTask().GetAwaiter().GetResult();

        base.OnExit(e);
    }
}
