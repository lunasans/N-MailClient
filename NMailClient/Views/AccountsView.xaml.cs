using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NMailClient.Models;
using NMailClient.Services;
using NMailClient.Services.Dav;
using NMailClient.ViewModels;

namespace NMailClient.Views;

public partial class AccountsView : UserControl
{
    private readonly MainViewModel _vm;
    private Account _current = new();
    private bool _suppressSelection;
    private CancellationTokenSource? _discoverCts;

    private readonly DispatcherTimer _typingTimer;

    /// <summary>Sobald der Nutzer selbst Serverdaten eintippt, nicht mehr automatisch füllen.</summary>
    private bool _serverFieldsTouched;

    /// <summary>True, während Autodiscover die Felder setzt – sonst würde das als
    /// Nutzereingabe gewertet und die Automatik gleich wieder abschalten.</summary>
    private bool _fillingFields;

    public AccountsView(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        AccountList.ItemsSource = _vm.Accounts;

        _typingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _typingTimer.Tick += async (_, _) =>
        {
            _typingTimer.Stop();
            await DiscoverAsync(overwrite: false, allowProbe: false);
        };

        foreach (var box in new[] { TxtImapHost, TxtImapPort, TxtSmtpHost, TxtSmtpPort })
            box.TextChanged += (_, _) => { if (!_fillingFields) _serverFieldsTouched = true; };

        if (_vm.Accounts.Count > 0) AccountList.SelectedIndex = 0;
        else LoadIntoEditor(NewAccount());
    }

    private void AccountList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;
        if (AccountList.SelectedItem is Account a) LoadIntoEditor(Clone(a));
    }

    // Tiefe Kopie über Account.Clone(): der Editor verändert die Objekte direkt,
    // eine flache Kopie schlüge sofort aufs Originalkonto durch – auch ohne „Speichern".
    private static Account Clone(Account a) => a.Clone();

    /// <summary>
    /// Ein neues Konto bekommt eine noch freie Farbe. Ohne das trüge jedes
    /// weitere dieselbe wie das erste — und im gemeinsamen Posteingang wäre
    /// nicht mehr zu erkennen, woher eine Nachricht kommt.
    /// </summary>
    private Account NewAccount()
        => new() { Color = ColorPalette.NextFree(_vm.Accounts.Select(a => a.Color)) };

    private void LoadIntoEditor(Account a)
    {
        _current = a;
        _typingTimer.Stop();

        _fillingFields = true;
        try
        {
            TxtName.Text = a.Name;
            TxtEmail.Text = a.Email;
            TxtUser.Text = a.User;
            TxtPassword.Password = a.Password;
            TxtImapHost.Text = a.ImapHost;
            TxtImapPort.Text = a.ImapPort.ToString();
            TxtSmtpHost.Text = a.SmtpHost;
            TxtSmtpPort.Text = a.SmtpPort.ToString();
            TxtSignature.Text = a.Signature;
            TxtCardDav.Text = a.CardDavUrl;
            TxtCalDav.Text = a.CalDavUrl;
        TxtMailcowUrl.Text = a.MailcowUrl;

        // Der Schluessel liegt im Anmeldeinformationsverwalter, nicht im Konto.
        TxtMailcowKey.Password =
            CredentialStore.Read(CredentialStore.TargetFor(a.Id, "mailcow")) ?? "";

        TxtSieveHost.Text = a.SieveHost;
        TxtSievePort.Text = (a.SievePort <= 0 ? 4190 : a.SievePort).ToString();

        CmbColor.ItemsSource = ColorPalette.Colors;
        CmbColor.SelectedItem = ColorPalette.Nearest(a.Color);
        }
        finally { _fillingFields = false; }

        // Bestehendes Konto: Serverdaten sind gesetzt und dürfen nicht überschrieben
        // werden. Neues Konto: Automatik frei.
        _serverFieldsTouched = !string.IsNullOrWhiteSpace(a.ImapHost)
                              || !string.IsNullOrWhiteSpace(a.SmtpHost);
        LblTest.Text = "";

        LoadAliases();
    }

    // ---- Absender-Aliase ---------------------------------------------------

    /// <summary>Verhindert Rückschreiben, während die Felder gefüllt werden.</summary>
    private bool _fillingAlias;

    private AliasDef? SelectedAlias => AliasList.SelectedItem as AliasDef;

    private void LoadAliases()
    {
        AliasList.ItemsSource = null;
        AliasList.ItemsSource = _current.Aliases;
        AliasList.SelectedIndex = _current.Aliases.Count > 0 ? 0 : -1;

        if (SelectedAlias is null) ClearAliasFields();
        UpdateAliasHint();
    }

    private void ClearAliasFields()
    {
        _fillingAlias = true;
        try
        {
            TxtAliasAddress.Text = "";
            TxtAliasName.Text = "";
            TxtAliasSignature.Text = "";
        }
        finally { _fillingAlias = false; }
    }

    private void UpdateAliasHint()
        => LblNoAliases.Visibility = _current.Aliases.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;

    private void AliasList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedAlias is not { } alias) return;

        _fillingAlias = true;
        try
        {
            TxtAliasAddress.Text = alias.Address;
            TxtAliasName.Text = alias.Name;
            TxtAliasSignature.Text = alias.Signature;
        }
        finally { _fillingAlias = false; }
    }

    private void AliasField_Changed(object sender, TextChangedEventArgs e)
    {
        if (_fillingAlias) return;

        // Ohne Auswahl beim ersten Tippen einen Eintrag anlegen – sonst müsste man
        // erst „Neu" finden, um überhaupt etwas eingeben zu können.
        if (SelectedAlias is null)
        {
            if (TxtAliasAddress.Text.Trim().Length == 0
                && TxtAliasName.Text.Trim().Length == 0
                && TxtAliasSignature.Text.Trim().Length == 0) return;

            AddAlias(select: true);
        }

        if (SelectedAlias is not { } alias) return;

        alias.Address = TxtAliasAddress.Text.Trim();
        alias.Name = TxtAliasName.Text.Trim();
        alias.Signature = TxtAliasSignature.Text;
        UpdateAliasHint();

        // AliasDef meldet keine Änderungen – Anzeige nachziehen, Auswahl behalten.
        var keep = AliasList.SelectedIndex;
        AliasList.Items.Refresh();
        AliasList.SelectedIndex = keep;
    }

    /// <summary>Legt einen leeren Alias an und wählt ihn aus.</summary>
    private AliasDef AddAlias(bool select)
    {
        var alias = new AliasDef();
        _current.Aliases.Add(alias);
        AliasList.Items.Refresh();
        UpdateAliasHint();

        if (select)
        {
            // Auswahl ohne die Felder zu überschreiben – der Nutzer tippt gerade darin.
            _fillingAlias = true;
            try { AliasList.SelectedItem = alias; }
            finally { _fillingAlias = false; }
        }
        return alias;
    }

    private void AliasNew_Click(object sender, RoutedEventArgs e)
    {
        AddAlias(select: false);
        AliasList.SelectedItem = _current.Aliases[^1];
        TxtAliasAddress.Focus();
    }

    private void AliasRemove_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedAlias is not { } alias) return;

        _current.Aliases.Remove(alias);
        AliasList.Items.Refresh();
        AliasList.SelectedIndex = _current.Aliases.Count > 0 ? 0 : -1;

        if (SelectedAlias is null) ClearAliasFields();
        UpdateAliasHint();
    }

    /// <summary>
    /// Während des Tippens suchen, entprellt. Ohne Stufe 3 (kein Anwählen fremder
    /// Ports) und nur solange die Serverfelder unberührt sind, damit manuelle
    /// Eingaben nie überschrieben werden.
    /// </summary>
    private void TxtEmail_TextChanged(object sender, TextChangedEventArgs e)
    {
        _typingTimer.Stop();
        if (!LooksComplete(TxtEmail.Text)) return;
        if (_serverFieldsTouched) return;
        _typingTimer.Start();
    }

    /// <summary>Grobe Vollständigkeitsprüfung – erst dann lohnt eine Abfrage.</summary>
    private static bool LooksComplete(string email)
    {
        var at = email.Trim().LastIndexOf('@');
        if (at <= 0) return false;
        var domain = email.Trim()[(at + 1)..];
        // Mindestens ein Punkt mit etwas dahinter, sonst tippt der Nutzer noch.
        var dot = domain.IndexOf('.');
        return dot > 0 && dot < domain.Length - 1;
    }

    /// <summary>Expliziter Klick: mit Verbindungsprüfung, darf vorhandene Werte ersetzen.</summary>
    private async void Discover_Click(object sender, RoutedEventArgs e)
        => await DiscoverAsync(overwrite: true, allowProbe: true);

    private async Task DiscoverAsync(bool overwrite, bool allowProbe)
    {
        var email = TxtEmail.Text.Trim();
        var at = email.LastIndexOf('@');
        if (at <= 0 || at == email.Length - 1)
        {
            if (overwrite) LblTest.Text = "Bitte zuerst eine vollständige E-Mail-Adresse eingeben.";
            return;
        }

        // Laufende Suche abbrechen, wenn erneut gestartet wird.
        _discoverCts?.Cancel();
        var cts = new CancellationTokenSource();
        _discoverCts = cts;

        BtnDiscover.IsEnabled = false;
        LblTest.Text = "Suche Serverdaten für " + email[(at + 1)..] + " …";
        try
        {
            var r = await Autodiscover.RunAsync(email, allowProbe, cts.Token);
            if (cts.IsCancellationRequested) return;

            _fillingFields = true;
            try
            {
                if (overwrite || string.IsNullOrWhiteSpace(TxtImapHost.Text))
                {
                    TxtImapHost.Text = r.ImapHost;
                    TxtImapPort.Text = r.ImapPort.ToString();
                }
                if (overwrite || string.IsNullOrWhiteSpace(TxtSmtpHost.Text))
                {
                    TxtSmtpHost.Text = r.SmtpHost;
                    TxtSmtpPort.Text = r.SmtpPort.ToString();
                }
            }
            finally { _fillingFields = false; }

            LblTest.Text = r.IsVerified
                ? $"Gefunden via {r.SourceDisplay}."
                : $"Quelle: {r.SourceDisplay}. „Genauer suchen“ prüft die Hosts per Verbindung.";
        }
        catch (OperationCanceledException)
        {
            // Nutzer hat neu gestartet – Ergebnis ist obsolet.
        }
        catch (Exception ex)
        {
            LblTest.Text = $"Suche fehlgeschlagen: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_discoverCts, cts)) BtnDiscover.IsEnabled = true;
        }
    }

    private bool ReadEditor(out Account account)
    {
        account = _current;

        if (string.IsNullOrWhiteSpace(TxtEmail.Text))
        {
            LblTest.Text = "E-Mail-Adresse fehlt.";
            return false;
        }
        if (!int.TryParse(TxtImapPort.Text, out var imapPort) || imapPort is < 1 or > 65535)
        {
            LblTest.Text = "IMAP-Port ist ungültig.";
            return false;
        }
        if (!int.TryParse(TxtSmtpPort.Text, out var smtpPort) || smtpPort is < 1 or > 65535)
        {
            LblTest.Text = "SMTP-Port ist ungültig.";
            return false;
        }

        account.Name = TxtName.Text.Trim();
        account.Email = TxtEmail.Text.Trim();
        account.User = TxtUser.Text.Trim();
        account.Password = TxtPassword.Password;
        account.ImapHost = TxtImapHost.Text.Trim();
        account.ImapPort = imapPort;
        account.SmtpHost = TxtSmtpHost.Text.Trim();
        account.SmtpPort = smtpPort;
        account.Signature = TxtSignature.Text;
        if (CmbColor.SelectedItem is string color) account.Color = color;
        account.CardDavUrl = TxtCardDav.Text.Trim();
        account.CalDavUrl = TxtCalDav.Text.Trim();
        account.MailcowUrl = TxtMailcowUrl.Text.Trim();
        account.SieveHost = TxtSieveHost.Text.Trim();

        // Unlesbare Eingabe faellt auf den Vorgabeport zurueck, statt 0 zu speichern.
        account.SievePort = int.TryParse(TxtSievePort.Text.Trim(), out var sievePort) && sievePort > 0
            ? sievePort : 4190;

        // Der API-Schlüssel geht in den Anmeldeinformationsverwalter, nicht in
        // die Konfigurationsdatei. Ein leeres Feld entfernt ihn.
        var mailcowTarget = CredentialStore.TargetFor(account.Id, "mailcow");
        var mailcowKey = TxtMailcowKey.Password;

        if (string.IsNullOrWhiteSpace(mailcowKey)) CredentialStore.Delete(mailcowTarget);
        else CredentialStore.Save(mailcowTarget, account.Email, mailcowKey);

        // Leere Alias-Einträge stillschweigend verwerfen – sie entstehen durch
        // „Neu" ohne Ausfüllen und wären als Absender unbrauchbar.
        account.Aliases.RemoveAll(x => string.IsNullOrWhiteSpace(x.Address));

        var invalid = account.Aliases.FirstOrDefault(x => !x.Address.Contains('@'));
        if (invalid is not null)
        {
            LblTest.Text = $"Alias '{invalid.Address}' ist keine vollständige Adresse.";
            return false;
        }

        var duplicate = account.Aliases
            .GroupBy(x => x.Address, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            LblTest.Text = $"Alias '{duplicate.Key}' ist doppelt vorhanden.";
            return false;
        }

        return true;
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        _suppressSelection = true;
        AccountList.SelectedItem = null;
        _suppressSelection = false;
        LoadIntoEditor(NewAccount());
        TxtEmail.Focus();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (AccountList.SelectedItem is not Account a) return;
        var res = MessageBox.Show(Window.GetWindow(this), $"Konto {a.Email} entfernen?", "Konto entfernen",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (res != MessageBoxResult.Yes) return;

        _vm.RemoveAccount(a);
        LoadIntoEditor(NewAccount());
    }

    /// <summary>
    /// Adressbücher suchen – zugleich der Test, ob Adresse und Anmeldung stimmen.
    /// </summary>
    private async void CardDav_Click(object sender, RoutedEventArgs e)
    {
        if (!ReadEditor(out var account)) return;

        if (string.IsNullOrWhiteSpace(account.CardDavUrl))
        {
            LblTest.Text = "Bitte zuerst eine CardDAV-Adresse eintragen.";
            return;
        }

        BtnCardDav.IsEnabled = false;
        LblTest.Text = "Suche Adressbücher …";
        try
        {
            var service = new CardDavService(
                () => (account.CardDavUrl, account.LoginUser, account.Password));

            var books = await service.ListAddressBooksAsync();

            LblTest.Text = books.Count == 0
                ? "Verbindung steht, aber es wurde kein Adressbuch gefunden."
                : $"{books.Count} Adressbuch/Adressbücher gefunden: "
                  + string.Join(", ", books.Select(b => b.DisplayName));
        }
        catch (Exception ex)
        {
            LblTest.Text = $"Fehlgeschlagen: {ex.Message}";
        }
        finally { BtnCardDav.IsEnabled = true; }
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (!ReadEditor(out var a)) return;

        LblTest.Text = "Teste Verbindung …";
        IsEnabled = false;
        try
        {
            await using var svc = new ImapService(a);
            await svc.TestAsync();
            LblTest.Text = "IMAP-Verbindung erfolgreich.";
        }
        catch (Exception ex)
        {
            LblTest.Text = $"Fehlgeschlagen: {ex.Message}";
        }
        finally { IsEnabled = true; }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!ReadEditor(out var a)) return;
        _vm.AddOrUpdateAccount(a);
        LblTest.Text = "Gespeichert.";
        AccountList.SelectedItem = _vm.Accounts.FirstOrDefault(x => x.Id == a.Id);
    }
}
