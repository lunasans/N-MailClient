using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using NMailClient.Models;
using NMailClient.Services;
using NMailClient.ViewModels;

namespace NMailClient.Views;

public partial class MainWindow : Window
{
    private MainViewModel Vm => (MainViewModel)DataContext;

    private bool _webViewReady;
    private string? _pendingHtml;

    private const string EmptyPage = "<html><body></body></html>";

    private readonly Func<SettingsWindow> _settingsFactory;
    private readonly Func<Account, ComposeRequest, ComposeWindow> _composerFactory;
    private readonly Func<ArchiveWindow> _archiveFactory;
    private readonly Func<Account, ContactsWindow> _contactsFactory;
    private readonly Func<Account, CalendarWindow> _calendarFactory;

    public MainWindow(
        MainViewModel vm,
        Func<SettingsWindow> settingsFactory,
        Func<Account, ComposeRequest, ComposeWindow> composerFactory,
        Func<ArchiveWindow> archiveFactory,
        Func<Account, ContactsWindow> contactsFactory,
        Func<Account, CalendarWindow> calendarFactory)
    {
        InitializeComponent();
        DataContext = vm;
        _settingsFactory = settingsFactory;
        _composerFactory = composerFactory;
        _archiveFactory = archiveFactory;
        _contactsFactory = contactsFactory;
        _calendarFactory = calendarFactory;

        Loaded += async (_, _) =>
        {
            Vm.ShowAccountsDialog = OpenAccounts;
            Vm.ShowComposer = OpenComposer;
            Vm.AskSavePath = AskSavePath;
            Vm.ConfirmDelete = AskYesNo;
            Vm.ConfirmAction = AskYesNo;
            Vm.AskFolderName = AskFolderName;
            Vm.ShowSource = (subject, raw) =>
                new SourceWindow(subject, raw) { Owner = this }.Show();
            Vm.ShowPdf = (path, name) =>
                new PdfWindow(path, name) { Owner = this }.Show();
            Vm.PropertyChanged += Vm_PropertyChanged;

            ThemeManager.ThemeChanged += OnThemeChanged;
            UpdateThemeButton();

            SetUpTray();

            await InitWebViewAsync();

            if (Vm.Accounts.Count == 0) OpenAccounts();
        };
    }

    private void About_Click(object sender, RoutedEventArgs e)
        => new AboutWindow { Owner = this }.ShowDialog();

    // ---- Infobereich -------------------------------------------------------

    private readonly Services.Shell.TrayIcon _tray = new();

    /// <summary>Gesetzt, wenn wirklich beendet werden soll – nicht nur zugeklappt.</summary>
    private bool _reallyExit;

    private void SetUpTray()
    {
        _tray.Activated += (_, _) => RestoreFromTray();
        _tray.MenuRequested += (_, _) =>
        {
            if (TryFindResource("TrayMenuResource") is ContextMenu menu)
            {
                // Ohne Platzierungsziel bleibt das Menü unsichtbar.
                menu.PlacementTarget = this;
                menu.IsOpen = true;
            }
        };

        Vm.Notify += ShowTrayBalloon;
        _tray.Show("N-MailClient");
    }

    /// <summary>
    /// Der Aufrufer hat die Ruhezeiten bereits geprüft; hier wird nur noch
    /// angezeigt.
    /// </summary>
    private void ShowTrayBalloon(string title, string text)
    {
        if (!_tray.IsVisible) _tray.Show("N-MailClient");
        _tray.ShowBalloon(title, text);
    }

    /// <summary>
    /// Start aus dem Autostart: das Fenster wird aufgebaut (sonst liefe der
    /// Loaded-Teil nie und es gäbe kein Symbol im Infobereich), aber sofort
    /// wieder verborgen.
    /// </summary>
    public void StartHidden()
    {
        WindowState = WindowState.Minimized;
        ShowInTaskbar = false;
        Show();
        Hide();
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void TrayOpen_Click(object sender, RoutedEventArgs e) => RestoreFromTray();

    private void TrayCompose_Click(object sender, RoutedEventArgs e)
    {
        RestoreFromTray();
        Vm.ComposeCommand.Execute(null);
    }

    private void TrayExit_Click(object sender, RoutedEventArgs e)
    {
        _reallyExit = true;
        Close();
    }

    /// <summary>
    /// Schliessen legt die Anwendung in den Infobereich, statt sie zu beenden —
    /// sonst käme keine Meldung mehr an. Über das Menü im Infobereich lässt sie
    /// sich wirklich beenden.
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_reallyExit && Vm.Settings.MinimizeToTray && _tray.IsVisible)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    // ---- WebView2 ----------------------------------------------------------

    private async Task InitWebViewAsync()
    {
        try
        {
            // Eigener Benutzerdatenordner - sonst landet er neben der EXE.
            var dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NMailClient", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
            await BodyView.EnsureCoreWebView2Async(env);

            var core = BodyView.CoreWebView2;
            var s = core.Settings;
            s.AreDefaultContextMenusEnabled = false;
            s.AreDevToolsEnabled = false;
            s.IsStatusBarEnabled = false;
            s.IsZoomControlEnabled = true;
            s.AreHostObjectsAllowed = false;

            // Kein Skript aus fremden Mails. Bis hierhin war der regex-basierte
            // HtmlSanitizer die einzige Instanz, die Skript aufhielt — und er setzt
            // Markup voraus, das ein Angreifer nicht schreiben muss: ein
            // ungequotetes onerror= oder ein <script> ohne schliessendes Tag geht
            // durch jedes seiner Muster hindurch. Das genuegte, um beim blossen
            // Oeffnen einer Mail deren Inhalt nach draussen zu schicken.
            //
            // Die Mailansicht braucht kein Skript: es gibt im ganzen Projekt keinen
            // ExecuteScriptAsync-Aufruf, und Mails sind Dokumente, keine Anwendungen.
            s.IsScriptEnabled = false;

            // Links aus Mails im Systembrowser öffnen, nicht in der Mailansicht
            // (Feature der Go-Version, dort über Wails BrowserOpenURL).
            core.NavigationStarting += (_, e) =>
            {
                if (e.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return;
                if (e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return;

                e.Cancel = true;
                OpenExternally(e.Uri);
            };
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                OpenExternally(e.Uri);
            };

            _webViewReady = true;
            RenderHtml(_pendingHtml ?? EmptyPage);
            _pendingHtml = null;
        }
        catch (Exception ex)
        {
            // Fehlende WebView2-Runtime soll die App nicht unbrauchbar machen.
            Vm.SetStatus($"Mailansicht nicht verfügbar (WebView2): {ex.Message}");
        }
    }

    private static void OpenExternally(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps
            && parsed.Scheme != Uri.UriSchemeMailto) return;

        try
        {
            Process.Start(new ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            // Kein Standardbrowser gesetzt - kein Grund zu scheitern.
        }
    }

    private void RenderHtml(string html)
    {
        if (!_webViewReady)
        {
            _pendingHtml = html;
            return;
        }
        BodyView.NavigateToString(string.IsNullOrEmpty(html) ? EmptyPage : html);
    }

    // ---- Theme -------------------------------------------------------------

    private void Theme_Click(object sender, RoutedEventArgs e) => ThemeManager.Toggle();

    private void Archive_Click(object sender, RoutedEventArgs e)
    {
        var dlg = _archiveFactory();
        dlg.Owner = this;
        dlg.ShowDialog();
    }

    private void Contacts_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.SelectedAccount is not { } account)
        {
            MessageBox.Show(this, "Bitte zuerst ein Konto auswählen.", "Kontakte",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = _contactsFactory(account);
        dlg.Owner = this;
        dlg.ShowDialog();
    }

    /// <summary>
    /// Einladung übernehmen: Kalender öffnen, damit der Nutzer Kalender wählen
    /// und den Termin vor dem Speichern prüfen kann.
    /// </summary>
    private void AcceptInvitation_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.SelectedAccount is not { } account || Vm.Body?.Invitation is not { } invitation)
            return;

        var dlg = _calendarFactory(account);
        dlg.Owner = this;
        dlg.PrepareInvitation(invitation);
        dlg.ShowDialog();
    }

    private void Calendar_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.SelectedAccount is not { } account)
        {
            MessageBox.Show(this, "Bitte zuerst ein Konto auswählen.", "Kalender",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = _calendarFactory(account);
        dlg.Owner = this;
        dlg.ShowDialog();
    }

    /// <summary>
    /// Druckt über den Chromium-Druckdialog von WebView2 – derselbe Renderer,
    /// der die Mail anzeigt, erzeugt auch die Druckausgabe.
    /// </summary>
    private void Print_Click(object sender, RoutedEventArgs e)
    {
        if (_webViewReady) BodyView.CoreWebView2.ShowPrintUI(CoreWebView2PrintDialogKind.System);
    }

    private void OnThemeChanged()
    {
        UpdateThemeButton();
        // Die Mailanzeige ist HTML - sie muss mit den neuen Farben neu gerendert werden.
        Vm.RerenderBody();
    }

    /// <summary>
    /// Die Aufschrift wechselt mit dem Schema und lässt sich deshalb nicht im
    /// XAML binden — sie wird hier gesetzt, aber übersetzt.
    /// </summary>
    private void UpdateThemeButton()
        => BtnTheme.Content = Services.I18n.Loc.T(
            ThemeManager.IsDark ? "Main.Toolbar.ThemeLight" : "Main.Toolbar.Theme");

    // ---- Rest --------------------------------------------------------------

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // WebView2 lässt sich nicht binden - Inhalt hier nachziehen.
        if (e.PropertyName == nameof(MainViewModel.BodyHtml)) RenderHtml(Vm.BodyHtml);
    }

    /// <summary>
    /// Verhindert, dass sich die beiden Auswahlen gegenseitig anstossen: das
    /// Abwählen der einen löst dort erneut ein Ereignis aus.
    /// </summary>
    private bool _switchingFolder;

    private void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_switchingFolder) return;
        if (e.NewValue is not FolderNode node) return;

        // Der gemeinsame Eingang steht in einer eigenen Liste. Ohne das
        // Abwählen blieben beide hervorgehoben, und es wäre nicht zu erkennen,
        // was die Nachrichtenliste gerade zeigt.
        _switchingFolder = true;
        try { UnifiedList.SelectedItem = null; }
        finally { _switchingFolder = false; }

        Vm.SelectedFolder = node;
    }

    private void UnifiedList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_switchingFolder) return;
        if (UnifiedList.SelectedItem is not FolderNode node) return;

        _switchingFolder = true;
        try { ClearTreeSelection(FolderTree); }
        finally { _switchingFolder = false; }

        Vm.SelectedFolder = node;
    }

    /// <summary>
    /// TreeView.SelectedItem lässt sich nicht setzen – die Auswahl wird über den
    /// Container aufgehoben, der sie trägt. Der kann beliebig tief liegen.
    /// </summary>
    private static void ClearTreeSelection(ItemsControl parent)
    {
        foreach (var item in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(item)
                is not TreeViewItem container) continue;

            if (container.IsSelected) container.IsSelected = false;
            ClearTreeSelection(container);
        }
    }

    /// <summary>
    /// Nachladen, sobald das Ende der Liste in Sicht kommt.
    ///
    /// Der Abstand ist bewusst grosszügig: würde erst am letzten Bildpunkt
    /// geladen, sähe man beim schnellen Scrollen jedes Mal das Ende der Liste,
    /// bevor der Nachschub da ist. Zwei Bildschirmhöhen im Voraus reichen, damit
    /// der Übergang nicht auffällt.
    ///
    /// Die Sperre gegen mehrfaches Auslösen liegt im ViewModel: das Ereignis
    /// feuert bei jedem Rad-Schritt, lange bevor die erste Anforderung
    /// zurück ist.
    /// </summary>
    private void MessageList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ViewportHeight <= 0) return;              // noch nicht dargestellt
        if (e.VerticalChange < 0) return;               // nach oben gescrollt
        if (!Vm.CanLoadMore || Vm.LoadingMore) return;

        var remaining = e.ExtentHeight - e.VerticalOffset - e.ViewportHeight;
        if (remaining > e.ViewportHeight * 2) return;

        _ = Vm.LoadMoreAsync();
    }

    /// <summary>
    /// ListBox.SelectedItems ist nicht bindbar – die Auswahl wird deshalb hier
    /// ins ViewModel gespiegelt.
    /// </summary>
    private void MessageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => Vm.ReplaceSelection(MessageList.SelectedItems.Cast<MailSummary>());

    /// <summary>
    /// Doppelklick im Entwürfe-Ordner öffnet den Composer. In anderen Ordnern
    /// bewusst ohne Wirkung – die Vorschau zeigt die Mail bereits.
    /// </summary>
    private async void MessageList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Nur reagieren, wenn wirklich eine Zeile getroffen wurde (nicht die Leerfläche).
        if ((e.OriginalSource as DependencyObject)?.FindAncestor<ListBoxItem>() is null) return;
        if (Vm.InDraftsFolder) await Vm.OpenDraftAsync();
    }

    private bool AskYesNo(string question)
        => MessageBox.Show(this, question, "Bestätigen",
               MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private string? AskFolderName(string title, string prompt, string initial)
        => PromptWindow.Ask(this, title, prompt, initial, value =>
            value.Length switch
            {
                0 => "Bitte einen Namen eingeben.",
                > 255 => "Der Name ist zu lang.",
                _ => null,
            });

    private void OpenAccounts()
    {
        var dlg = _settingsFactory();
        dlg.Owner = this;
        dlg.ShowAccountsTab();
        dlg.ShowDialog();
    }

    /// <summary>Einstellungen auf dem allgemeinen Reiter öffnen.</summary>
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = _settingsFactory();
        dlg.Owner = this;
        dlg.ShowDialog();
    }

    private void OpenComposer(ComposeRequest request)
    {
        if (Vm.SelectedAccount is not { } account) return;
        var dlg = _composerFactory(account, request);
        dlg.Owner = this;
        dlg.ShowDialog();
    }

    private string? AskSavePath(string suggestedName)
    {
        var dlg = new SaveFileDialog
        {
            FileName = suggestedName,
            Filter = "Alle Dateien|*.*",
            OverwritePrompt = true,
        };
        return dlg.ShowDialog(this) == true ? dlg.FileName : null;
    }

    protected override async void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _tray.Dispose();
        ThemeManager.ThemeChanged -= OnThemeChanged;

        // Die Anwendung endet nicht mehr von selbst mit dem letzten Fenster
        // (siehe App.OnStartup), also hier ausdrücklich.
        Application.Current?.Shutdown();
        await Vm.ShutdownAsync();
    }
}
