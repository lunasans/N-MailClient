using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Threading;
using NMailClient.Poc.Models;
using NMailClient.Poc.Services;

namespace NMailClient.Poc.ViewModels;

/// <summary>
/// Zustand und Ablauf des Hauptfensters. Der Rest der Klasse liegt in den
/// `MainViewModel.*.cs`-Dateien, nach Bereichen getrennt.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly SettingsStore _store;
    private readonly MailServiceRegistry _services;

    /// <summary>Aus den Einstellungen, nicht mehr fest verdrahtet.</summary>
    private int PageSize => _store.Settings.EffectivePageSize;

    public AppSettings Settings => _store.Settings;

    /// <summary>Der lokale Zwischenspeicher – für die Verwaltung in den Einstellungen.</summary>
    public Services.Cache.MailCache Cache => _services.Cache;

    /// <summary>ManageSieve-Zugriff für ein Konto – für die Regelverwaltung.</summary>
    public Services.Sieve.ManageSieveClient SieveFor(Account account) => _services.Sieve(account);

    /// <summary>mailcow-Zugriff für ein Konto – für Quota, Aliase und Quarantäne.</summary>
    public Services.Mailcow.MailcowClient MailcowFor(Account account) => _services.Mailcow(account);

    /// <summary>Nach Aenderung globaler Optionen aufrufen.</summary>
    public void SaveSettings() => _store.Save();
    private readonly DispatcherTimer _searchTimer;
    private CancellationTokenSource? _loadCts;

    public ObservableCollection<Account> Accounts { get; } = new();
    public ObservableCollection<FolderNode> Folders { get; } = new();
    public ObservableCollection<MailSummary> Messages { get; } = new();

    /// <summary>
    /// Flache Ordnerliste mit Einrückung – für das 'Verschieben nach'-Menü.
    /// Ein Baum lässt sich dort nicht sinnvoll darstellen.
    /// </summary>
    public ObservableCollection<FolderNode> FlatFolders { get; } = new();

    /// <summary>
    /// Aktuelle Mehrfachauswahl. Wird vom Code-behind der Liste gespiegelt, weil
    /// ListBox.SelectedItems nicht bindbar ist.
    /// </summary>
    public ObservableCollection<MailSummary> SelectedMessages { get; } = new();

    /// <summary>Auswahl für Aktionen: Mehrfachauswahl, sonst die aktive Nachricht.</summary>
    private List<MailSummary> ActionTargets => SelectedMessages.Count > 0
        ? SelectedMessages.ToList()
        : SelectedMessage is { } m ? [m] : [];

    public void ReplaceSelection(IEnumerable<MailSummary> items)
    {
        SelectedMessages.Clear();
        foreach (var m in items) SelectedMessages.Add(m);
        OnPropertyChanged(nameof(SelectionInfo));
        OnPropertyChanged(nameof(HasMultiSelection));
    }

    public bool HasMultiSelection => SelectedMessages.Count > 1;

    public string SelectionInfo => SelectedMessages.Count > 1
        ? $"{SelectedMessages.Count} Nachrichten ausgewählt"
        : "";


    public MainViewModel(SettingsStore store, MailServiceRegistry services,
        OutboxService outbox, ReminderStore reminders, TranslateService translate,
        AttachmentArchive archive)
    {
        _store = store;
        _services = services;
        _outbox = outbox;
        _reminders = reminders;
        _translate = translate;
        _attachmentArchive = archive;

        // Minütlich genügt – die Erinnerungen sind auf Stunden angelegt.
        _reminderTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _reminderTimer.Tick += (_, _) => CheckReminders();
        _reminderTimer.Start();

        _outbox.Changed += () =>
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(OnOutboxChanged);
        _outbox.Answered += OnAnswered;
        _outbox.SavedToSent += OnSavedToSent;
        OnOutboxChanged();

        _store.Load();
        foreach (var a in _store.Accounts) Accounts.Add(a);

        // Der gemeinsame Eingang hängt an der Anzahl der Konten, nicht am
        // gewählten – deshalb hier und bei jeder Änderung der Kontenliste.
        Accounts.CollectionChanged += (_, _) => RefreshUnifiedInbox();
        RefreshUnifiedInbox();

        foreach (var l in _store.Settings.Labels) Labels.Add(l);

        MessagesView = CollectionViewSource.GetDefaultView(Messages);
        ApplyListSettings();

        // Debounce für die Live-Suche, wie in der Go-Version.
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _searchTimer.Tick += async (_, _) =>
        {
            _searchTimer.Stop();
            await ReloadMessagesAsync();
        };

        if (Accounts.Count > 0) SelectedAccount = Accounts[0];
    }


    // ---- Auswahl -----------------------------------------------------------

    private Account? _selectedAccount;
    public Account? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (!Set(ref _selectedAccount, value)) return;
            _ = LoadFoldersAsync();
        }
    }

    private FolderNode? _selectedFolder;
    public FolderNode? SelectedFolder
    {
        get => _selectedFolder;
        set
        {
            if (!Set(ref _selectedFolder, value)) return;
            _offset = 0;
            OnPropertyChanged(nameof(IsUnifiedView));
            _ = ReloadMessagesAsync();
        }
    }

    /// <summary>Zeigt die Liste gerade mehrere Postfächer nebeneinander?</summary>
    public bool IsUnifiedView => SelectedFolder?.IsUnified == true;

    private MailSummary? _selectedMessage;
    public MailSummary? SelectedMessage
    {
        get => _selectedMessage;
        set
        {
            if (!Set(ref _selectedMessage, value)) return;
            _ = LoadBodyAsync();
        }
    }

    private MailBody? _body;
    public MailBody? Body
    {
        get => _body;
        private set
        {
            Set(ref _body, value);
            OnPropertyChanged(nameof(HasBody));
            OnPropertyChanged(nameof(CanTranslate));
            OnPropertyChanged(nameof(HasInvitation));
            OnPropertyChanged(nameof(InvitationText));
        }
    }
    public bool HasBody => Body != null;

    private string _bodyHtml = "";
    public string BodyHtml { get => _bodyHtml; private set => Set(ref _bodyHtml, value); }

    private bool _imagesBlocked;
    public bool ImagesBlocked { get => _imagesBlocked; private set => Set(ref _imagesBlocked, value); }

    private bool _showRemoteImages;

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!Set(ref _searchText, value)) return;
            _searchTimer.Stop();
            _searchTimer.Start();
        }
    }

    public bool ThreadView
    {
        get => Settings.ThreadView;
        set
        {
            if (Settings.ThreadView == value) return;
            Settings.ThreadView = value;
            _store.Save();
            OnPropertyChanged();
            _ = ReloadMessagesAsync();
        }
    }

    private bool _filterUnanswered;
    public bool FilterUnanswered
    {
        get => _filterUnanswered;
        set
        {
            if (!Set(ref _filterUnanswered, value)) return;
            _ = ReloadMessagesAsync();
        }
    }

    private string _status = "Bereit.";
    public string Status { get => _status; private set => Set(ref _status, value); }

    private bool _busy;
    public bool Busy { get => _busy; private set => Set(ref _busy, value); }

    private int _offset;
    private bool _canLoadMore;
    public bool CanLoadMore
    {
        get => _canLoadMore;
        private set
        {
            if (!Set(ref _canLoadMore, value)) return;
            OnPropertyChanged(nameof(ShowLoadMoreButton));
        }
    }

    // ---- Kontenverwaltung --------------------------------------------------

    public ImapService ImapFor(Account a) => _services.Imap(a);

    public SmtpService SmtpFor(Account a) => _services.Smtp(a);

    public void AddOrUpdateAccount(Account account)
    {
        var existing = _store.Accounts.FirstOrDefault(a => a.Id == account.Id);
        if (existing == null)
        {
            _store.Accounts.Add(account);
            Accounts.Add(account);
        }
        else
        {
            var i = Accounts.IndexOf(existing);
            _store.Accounts[_store.Accounts.IndexOf(existing)] = account;
            if (i >= 0) Accounts[i] = account;
        }

        // Geänderte Zugangsdaten: alte Verbindung und die Auth-Sperre verwerfen.
        _services.Reset(account.Id);
        _watching.Remove(account.Id);

        _store.Save();
        SelectedAccount = account;
    }

    public void RemoveAccount(Account account)
    {
        Accounts.Remove(account);
        _services.Reset(account.Id);
        _watching.Remove(account.Id);

        // Entfernt auch den Keychain-Eintrag – sonst bliebe das Passwort zurück.
        _store.Forget(account);

        // Und die zwischengespeicherte Post: sie bliebe sonst auf der Platte
        // lesbar, obwohl das Konto entfernt wurde.
        _services.Cache.Forget(account.Id);

        if (ReferenceEquals(SelectedAccount, account))
            SelectedAccount = Accounts.FirstOrDefault();
    }


    // ---- Dialog-Hooks (von MainWindow gesetzt) -----------------------------

    /// <summary>Öffnet den Konten-Dialog.</summary>
    public Action? ShowAccountsDialog { get; set; }

    /// <summary>Öffnet den Composer; null = leere Mail, sonst Antwort/Weiterleitung.</summary>
    public Action<ComposeRequest>? ShowComposer { get; set; }

    /// <summary>Liefert einen Zielpfad für "Speichern unter…" oder null bei Abbruch.</summary>
    public Func<string, string?>? AskSavePath { get; set; }


    /// <summary>
    /// Sicherung einspielen: ersetzt Konten und Einstellungen vollständig.
    /// Passwörter sind nicht enthalten und bleiben leer.
    /// </summary>
    public void RestoreBackup(BackupFile backup)
    {
        // Bestehende Verbindungen gehören zu den alten Konten.
        foreach (var account in Accounts.ToList()) _services.Reset(account.Id);
        _watching.Clear();

        _store.Accounts.Clear();
        Accounts.Clear();

        foreach (var account in backup.Accounts)
        {
            _store.Accounts.Add(account);
            Accounts.Add(account);
        }

        if (backup.Settings is { } settings) _store.ReplaceSettings(settings);

        _store.Save();

        Labels.Clear();
        foreach (var label in Settings.Labels) Labels.Add(label);

        ApplyListSettings();
        ThemeManager.Bind(Settings, SaveSettings);
        ThemeManager.Apply(ThemeManager.LoadPreference());

        SelectedAccount = Accounts.FirstOrDefault();
        Status = $"Sicherung eingespielt: {Accounts.Count} Konto/Konten.";
    }

    public async Task ShutdownAsync() => await _services.DisposeAsync();
}
