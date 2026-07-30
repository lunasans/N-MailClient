using System.Windows;
using System.Windows.Controls;
using NMailClient.Models;
using NMailClient.Services;
using NMailClient.ViewModels;

namespace NMailClient.Views;

/// <summary>
/// Ein Dialog für alle Einstellungen – Ersatz für den früheren, eigenständigen
/// Konten-Dialog. Weitere Bereiche (Sieve, Kalender) kommen als zusätzliche Reiter
/// dazu, statt als je eigenes Fenster.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly MainViewModel _vm;

    /// <summary>Unterdrückt Speichern, während die Steuerelemente initial gefüllt werden.</summary>
    private bool _loading = true;

    private readonly Services.Pgp.PgpService _pgp;

    public SettingsWindow(MainViewModel vm, Services.Pgp.PgpService pgp)
    {
        InitializeComponent();
        _vm = vm;
        _pgp = pgp;

        AccountsHost.Child = new AccountsView(vm);

        LoadSettings();
        InitLabels();
        RefreshPgpKeys();
        _loading = false;

        LblPaths.Text = $"Konfiguration: %APPDATA%\\NMailClient\\db.json\n"
                        + $"Protokoll: {AppLog.LogPath}";
    }

    /// <summary>Öffnet den Dialog direkt auf dem Konten-Reiter.</summary>
    public void ShowAccountsTab() => Tabs.SelectedItem = TabAccounts;

    // ---- OpenPGP-Schlüssel -------------------------------------------------

    private void RefreshPgpKeys()
    {
        PgpList.ItemsSource = _pgp.ListKeys();
        LblPgpDir.Text = $"Schlüsselbund: {_pgp.Directory}";
    }

    private Services.Pgp.PgpKeyInfo? SelectedKey =>
        PgpList.SelectedItem as Services.Pgp.PgpKeyInfo;

    /// <summary>Ohne Auswahl passiert nichts – mit Hinweis statt stillem Nichtstun.</summary>
    private Services.Pgp.PgpKeyInfo? RequireSelection()
    {
        if (SelectedKey is { } key) return key;

        MessageBox.Show(this, "Bitte zuerst einen Schlüssel in der Liste auswählen.",
            "Kein Schlüssel gewählt", MessageBoxButton.OK, MessageBoxImage.Information);
        return null;
    }

    private async void PgpGenerate_Click(object sender, RoutedEventArgs e)
    {
        var account = _vm.SelectedAccount;
        if (account is null)
        {
            MessageBox.Show(this, "Es ist noch kein Konto eingerichtet.",
                "Kein Konto", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var address = PromptWindow.Ask(this, "Schlüssel erzeugen",
            "Für welche Adresse?", account.Email,
            v => MimeKit.MailboxAddress.TryParse(v, out _) ? null : "Keine gültige Adresse.");
        if (address is null) return;

        var passphrase = PromptWindow.Ask(this, "Mantra",
            "Mantra zum Schutz des geheimen Schlüssels:", "",
            v => v.Length >= 8 ? null : "Mindestens 8 Zeichen.");
        if (passphrase is null) return;

        // Das Erzeugen dauert je nach Rechner mehrere Sekunden – nicht im
        // Oberflächen-Thread, sonst friert der Dialog ein.
        IsEnabled = false;
        try
        {
            var owner = new MimeKit.MailboxAddress(account.Name, address);
            var key = await Task.Run(() => _pgp.GenerateKey(owner, passphrase));

            _pgp.RememberPassword(key.KeyId, passphrase);
            RefreshPgpKeys();

            MessageBox.Show(this,
                $"Schlüssel {key.KeyId} für {address} erzeugt.\n\n"
                + "Das Mantra liegt im Windows-Anmeldeinformationsverwalter.",
                "Fertig", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppLog.Error("PGP: Schlüssel konnte nicht erzeugt werden.", ex);
            MessageBox.Show(this, ex.Message, "Fehler",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { IsEnabled = true; }
    }

    private void PgpImport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Schlüsseldatei einlesen",
            Filter = "OpenPGP-Schlüssel (*.asc;*.gpg;*.pgp;*.key)|*.asc;*.gpg;*.pgp;*.key"
                     + "|Alle Dateien (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var count = _pgp.ImportKeys(dlg.FileName);
            RefreshPgpKeys();

            MessageBox.Show(this,
                count > 0 ? $"{count} Schlüssel eingelesen."
                          : "Die Datei enthielt nur bereits bekannte Schlüssel.",
                "Einlesen", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppLog.Error("PGP: Einlesen fehlgeschlagen.", ex);
            MessageBox.Show(this, ex.Message, "Einlesen fehlgeschlagen",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void PgpExport_Click(object sender, RoutedEventArgs e)
    {
        if (RequireSelection() is not { } key) return;

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Öffentlichen Schlüssel exportieren",
            FileName = $"{key.Address}.asc",
            Filter = "OpenPGP-Schlüssel (*.asc)|*.asc",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            // Bewusst nur der öffentliche Teil: der geheime Schlüssel hat in einer
            // Datei nichts verloren, die man anschliessend verschickt.
            _pgp.ExportPublicKey(key.KeyId, dlg.FileName);
            MessageBox.Show(this, $"Gespeichert: {dlg.FileName}",
                "Exportiert", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppLog.Error("PGP: Export fehlgeschlagen.", ex);
            MessageBox.Show(this, ex.Message, "Export fehlgeschlagen",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void PgpDelete_Click(object sender, RoutedEventArgs e)
    {
        if (RequireSelection() is not { } key) return;

        var warning = key.IsSecret
            ? "\n\nAchtung: Das ist ein geheimer Schlüssel. Damit verschlüsselte "
              + "Nachrichten lassen sich danach nicht mehr öffnen."
            : "";

        if (MessageBox.Show(this, $"Schlüssel {key.UserId} entfernen?{warning}",
                "Entfernen", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes) return;

        try
        {
            _pgp.DeleteKey(key.KeyId);
            RefreshPgpKeys();
        }
        catch (Exception ex)
        {
            AppLog.Error("PGP: Entfernen fehlgeschlagen.", ex);
            MessageBox.Show(this, ex.Message, "Fehler",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---- Etiketten ---------------------------------------------------------

    private static string[] Palette => ColorPalette.Colors;

    private LabelDef? SelectedLabel => LabelList.SelectedItem as LabelDef;

    private void InitLabels()
    {
        LabelList.ItemsSource = _vm.Labels;
        CmbLabelColor.ItemsSource = Palette;
        if (_vm.Labels.Count > 0) LabelList.SelectedIndex = 0;
    }

    private void LabelList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        LabelEditor.IsEnabled = SelectedLabel is not null;
        if (SelectedLabel is not { } l) return;

        TxtLabelName.Text = l.Display;
        CmbLabelColor.SelectedItem = Palette.FirstOrDefault(
            c => string.Equals(c, l.Color, StringComparison.OrdinalIgnoreCase)) ?? Palette[0];
        LblKeyword.Text = l.Keyword;
    }

    private void LabelNew_Click(object sender, RoutedEventArgs e)
    {
        var name = PromptWindow.Ask(this, "Neues Etikett", "Anzeigename:");
        if (string.IsNullOrWhiteSpace(name)) return;

        var keyword = LabelDef.MakeKeyword(name);
        if (_vm.Labels.Any(l => string.Equals(l.Keyword, keyword, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, $"Ein Etikett mit dem Keyword '{keyword}' existiert bereits.",
                "Etikett anlegen", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var label = new LabelDef
        {
            Keyword = keyword,
            Display = name.Trim(),
            Color = Palette[_vm.Labels.Count % Palette.Length],
        };
        _vm.Labels.Add(label);
        _vm.SaveLabels();
        LabelList.SelectedItem = label;
    }

    private void LabelRemove_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedLabel is not { } l) return;

        var res = MessageBox.Show(this,
            $"Etikett '{l.Display}' entfernen?\n\nDas Keyword bleibt auf den Nachrichten "
            + "am Server bestehen und wird nur nicht mehr angezeigt.",
            "Etikett entfernen", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (res != MessageBoxResult.Yes) return;

        _vm.Labels.Remove(l);
        _vm.SaveLabels();
        LabelEditor.IsEnabled = _vm.Labels.Count > 0;
    }

    private void LabelApply_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedLabel is not { } l) return;

        var name = TxtLabelName.Text.Trim();
        if (name.Length == 0) return;

        // Keyword bleibt unverändert – siehe Hinweis im Dialog.
        l.Display = name;
        if (CmbLabelColor.SelectedItem is string color) l.Color = color;

        _vm.SaveLabels();

        // Liste neu binden, damit Name und Farbe im Eintrag aktualisiert werden
        // (LabelDef meldet keine Änderungen).
        var keep = LabelList.SelectedIndex;
        LabelList.Items.Refresh();
        LabelList.SelectedIndex = keep;
    }

    private void LoadSettings()
    {
        var theme = _vm.Settings.Theme;
        foreach (ComboBoxItem item in CmbTheme.Items)
        {
            if ((string)item.Tag == theme) { CmbTheme.SelectedItem = item; break; }
        }
        if (CmbTheme.SelectedItem is null) CmbTheme.SelectedIndex = 0;

        var language = _vm.Settings.Language;
        foreach (ComboBoxItem item in CmbLanguage.Items)
        {
            if ((string)item.Tag == language) { CmbLanguage.SelectedItem = item; break; }
        }
        if (CmbLanguage.SelectedItem is null) CmbLanguage.SelectedIndex = 0;

        TxtPageSize.Text = _vm.Settings.PageSize.ToString();
        TxtUndoSeconds.Text = _vm.Settings.UndoSendSeconds.ToString();
        ChkConfirmDelete.IsChecked = _vm.Settings.ConfirmBeforeDelete;
        ChkGroupByDate.IsChecked = _vm.Settings.GroupByDate;
        ChkCategories.IsChecked = _vm.Settings.ShowCategories;

        ChkTray.IsChecked = _vm.Settings.MinimizeToTray;
        ChkNotify.IsChecked = _vm.Settings.NotifyOnNewMail;
        ChkQuiet.IsChecked = _vm.Settings.QuietHoursEnabled;
        TxtQuietFrom.Text = _vm.Settings.QuietFrom;
        TxtQuietTo.Text = _vm.Settings.QuietTo;
        ChkAutostart.IsChecked = Services.Shell.Autostart.IsEnabled;
        ShowQuietSummary();
        ShowCacheStats();
        ShowSieveServer();
        InitRuleEditor();
        ShowVacation();

        TxtArchiveDir.Text = string.IsNullOrWhiteSpace(_vm.Settings.ArchiveDir)
            ? AttachmentArchive.DefaultRoot
            : _vm.Settings.ArchiveDir;

        TxtTranslateUrl.Text = _vm.Settings.TranslateUrl;
        TxtTranslateTarget.Text = _vm.Settings.TranslateTarget;
        TxtTranslateKey.Password =
            CredentialStore.Read(CredentialStore.GlobalTarget("translate")) ?? "";

        foreach (ComboBoxItem item in CmbDensity.Items)
        {
            if ((string)item.Tag == _vm.Settings.ListDensity) { CmbDensity.SelectedItem = item; break; }
        }
        if (CmbDensity.SelectedItem is null) CmbDensity.SelectedIndex = 1; // Normal
    }

    private void Density_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (CmbDensity.SelectedItem is not ComboBoxItem item) return;

        _vm.Settings.ListDensity = (string)item.Tag;
        _vm.SaveSettings();
        _vm.ApplyListSettings();
    }

    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (CmbLanguage.SelectedItem is not ComboBoxItem item) return;

        _vm.Settings.Language = (string)item.Tag;
        _vm.SaveSettings();

        // Wirkt sofort: die Bindungen hängen am Indexer von Loc.
        Services.I18n.Loc.Current.Use(_vm.Settings.Language);
    }

    // ---- mailcow -----------------------------------------------------------

    private Services.Mailcow.MailcowClient? _mailcow;

    private async void McLoad_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedAccount is not { } account) return;

        _mailcow = _vm.MailcowFor(account);
        SetMcStatus("Verbinde …", warn: false);

        try
        {
            var mailbox = await _mailcow.GetMailboxAsync();

            LblMcQuota.Text = $"{mailbox.Address} · {mailbox.QuotaDisplay} · "
                              + $"{mailbox.MessageCount} Nachrichten";
            BarMcQuota.Value = mailbox.UsedPercent;
            McQuotaBox.Visibility = Visibility.Visible;

            // Ein volles Postfach nimmt keine Post mehr an – das gehört auffällig.
            if (mailbox.IsNearlyFull)
                SetMcStatus($"Achtung: Das Postfach ist zu {mailbox.UsedPercent:0} % belegt.",
                            warn: true);
            else
                SetMcStatus("", warn: false);

            await McRefreshListsAsync();
        }
        catch (Exception ex)
        {
            McQuotaBox.Visibility = Visibility.Collapsed;
            ShowMcError(ex);
        }
    }

    private async Task McRefreshListsAsync()
    {
        if (_mailcow is null) return;

        McAliasList.ItemsSource = await _mailcow.GetAliasesAsync();
        McAppList.ItemsSource = await _mailcow.GetAppPasswordsAsync();
        McQuarantineList.ItemsSource = await _mailcow.GetQuarantineAsync();
    }

    // ---- Aliase ------------------------------------------------------------

    private async void McAliasAdd_Click(object sender, RoutedEventArgs e)
    {
        if (_mailcow is null || _vm.SelectedAccount is not { } account) return;

        var address = TxtMcAliasAddress.Text.Trim();
        if (!address.Contains('@'))
        {
            SetMcStatus("Bitte eine vollständige Adresse angeben.", warn: true);
            return;
        }

        await RunMcAsync(async () =>
        {
            var result = await _mailcow.AddAliasAsync(address, account.Email);
            TxtMcAliasAddress.Clear();
            return result;
        });
    }

    private async void McAliasDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_mailcow is null) return;
        if (McAliasList.SelectedItem is not Services.Mailcow.MailcowAlias alias) return;

        if (MessageBox.Show(this, $"Alias {alias.Address} entfernen?", "Entfernen",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        await RunMcAsync(() => _mailcow.DeleteAliasAsync(alias.Id));
    }

    // ---- App-Passwörter ----------------------------------------------------

    private async void McAppAdd_Click(object sender, RoutedEventArgs e)
    {
        if (_mailcow is null) return;

        var name = TxtMcAppName.Text.Trim();
        if (name.Length == 0)
        {
            SetMcStatus("Bitte einen Namen für das Gerät angeben.", warn: true);
            return;
        }

        try
        {
            var (result, password) = await _mailcow.AddAppPasswordAsync(name);
            TxtMcAppName.Clear();

            await McRefreshListsAsync();
            SetMcStatus(result.Message, warn: !result.Success);

            if (result.Success)
            {
                // mailcow zeigt das Passwort später nicht mehr an – jetzt oder nie.
                MessageBox.Show(this,
                    $"Das App-Passwort für '{name}' lautet:\n\n{password}\n\n"
                    + "Es lässt sich später nicht mehr anzeigen. Jetzt notieren oder "
                    + "gleich im Gerät eintragen.",
                    "App-Passwort", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            ShowMcError(ex);
        }
    }

    private async void McAppDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_mailcow is null) return;
        if (McAppList.SelectedItem is not Services.Mailcow.MailcowAppPassword password) return;

        if (MessageBox.Show(this,
                $"App-Passwort '{password.Name}' entfernen?\n\n"
                + "Geräte, die es verwenden, können sich danach nicht mehr anmelden.",
                "Entfernen", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes) return;

        await RunMcAsync(() => _mailcow.DeleteAppPasswordAsync(password.Id));
    }

    // ---- Quarantäne --------------------------------------------------------

    private async void McQuarantineRelease_Click(object sender, RoutedEventArgs e)
        => await HandleQuarantineAsync(Services.Mailcow.QuarantineAction.Release);

    private async void McQuarantineSpam_Click(object sender, RoutedEventArgs e)
        => await HandleQuarantineAsync(Services.Mailcow.QuarantineAction.LearnSpam);

    private async void McQuarantineDelete_Click(object sender, RoutedEventArgs e)
        => await HandleQuarantineAsync(Services.Mailcow.QuarantineAction.Delete);

    private async Task HandleQuarantineAsync(Services.Mailcow.QuarantineAction action)
    {
        if (_mailcow is null) return;
        if (McQuarantineList.SelectedItem is not Services.Mailcow.MailcowQuarantineItem item) return;

        await RunMcAsync(() => _mailcow.HandleQuarantineAsync(item.Id, action));
    }

    // ---- Gemeinsames -------------------------------------------------------

    /// <summary>
    /// Eine ändernde Anfrage ausführen, das Ergebnis melden und die Listen
    /// nachziehen. mailcow meldet Ablehnungen als reguläre Antwort, nicht als
    /// Fehler — deshalb wird beides ausgewertet.
    /// </summary>
    private async Task RunMcAsync(Func<Task<Services.Mailcow.MailcowResult>> operation)
    {
        try
        {
            var result = await operation();
            await McRefreshListsAsync();

            SetMcStatus(result.Message, warn: !result.Success);
        }
        catch (Exception ex)
        {
            ShowMcError(ex);
        }
    }

    private void ShowMcError(Exception ex)
    {
        AppLog.Warn($"mailcow: {ex.Message}");
        SetMcStatus(ex.Message, warn: true);
    }

    private void SetMcStatus(string text, bool warn)
    {
        LblMcStatus.Text = text;
        LblMcStatus.SetResourceReference(ForegroundProperty, warn ? "BadText" : "TextMuted");
    }

    // ---- Regelassistent ----------------------------------------------------

    private readonly List<Services.Sieve.SieveRule> _rules = [];
    private Services.Sieve.VacationSettings _vacation = new();

    /// <summary>Das Skript, wie es zuletzt vom Server kam – für den Fremdanteil.</summary>
    private string _rulesBaseScript = "";

    private Services.Sieve.SieveRule? CurrentRule
        => RuleList.SelectedItem as Services.Sieve.SieveRule;

    private void InitRuleEditor()
    {
        CmbCondField.ItemsSource = new[]
        {
            "Absender", "Empfänger", "Kopie", "Betreff",
            "Beliebiger Empfänger", "Kopfzeile", "Grösse (KB)", "Nachrichtentext",
        };
        CmbCondField.SelectedIndex = 0;

        CmbCondTest.ItemsSource = new[]
        {
            "enthält", "ist genau", "passt auf", "enthält nicht", "grösser als", "kleiner als",
        };
        CmbCondTest.SelectedIndex = 0;

        CmbActionKind.ItemsSource = new[]
        {
            "Ablegen in Ordner", "Weiterleiten an", "Verwerfen", "Zurückweisen mit",
            "Im Posteingang behalten", "Etikett setzen", "Als gelesen markieren",
            "Weitere Regeln überspringen",
        };
        CmbActionKind.SelectedIndex = 0;
    }

    private static Services.Sieve.RuleField FieldFromIndex(int index) => index switch
    {
        0 => Services.Sieve.RuleField.From,
        1 => Services.Sieve.RuleField.To,
        2 => Services.Sieve.RuleField.Cc,
        3 => Services.Sieve.RuleField.Subject,
        4 => Services.Sieve.RuleField.AnyRecipient,
        5 => Services.Sieve.RuleField.Header,
        6 => Services.Sieve.RuleField.Size,
        _ => Services.Sieve.RuleField.Body,
    };

    private static Services.Sieve.RuleTest TestFromIndex(int index) => index switch
    {
        0 => Services.Sieve.RuleTest.Contains,
        1 => Services.Sieve.RuleTest.Is,
        2 => Services.Sieve.RuleTest.Matches,
        3 => Services.Sieve.RuleTest.NotContains,
        4 => Services.Sieve.RuleTest.Over,
        _ => Services.Sieve.RuleTest.Under,
    };

    private static Services.Sieve.RuleAction ActionFromIndex(int index) => index switch
    {
        0 => Services.Sieve.RuleAction.FileInto,
        1 => Services.Sieve.RuleAction.Redirect,
        2 => Services.Sieve.RuleAction.Discard,
        3 => Services.Sieve.RuleAction.Reject,
        4 => Services.Sieve.RuleAction.Keep,
        5 => Services.Sieve.RuleAction.AddFlag,
        6 => Services.Sieve.RuleAction.MarkRead,
        _ => Services.Sieve.RuleAction.Stop,
    };

    private void ShowRules()
    {
        var selected = CurrentRule;

        RuleList.ItemsSource = null;
        RuleList.ItemsSource = _rules;
        RuleList.SelectedItem = selected is not null && _rules.Contains(selected) ? selected : null;

        RuleEditor.IsEnabled = _rules.Count > 0;
    }

    private void ShowRule(Services.Sieve.SieveRule? rule)
    {
        _loading = true;
        try
        {
            RuleEditor.IsEnabled = rule is not null;
            if (rule is null) return;

            TxtRuleName.Text = rule.Name;
            ChkRuleEnabled.IsChecked = rule.Enabled;
            CmbRuleMatch.SelectedIndex = rule.MatchAll ? 0 : 1;

            ConditionList.ItemsSource = null;
            ConditionList.ItemsSource = rule.Conditions;
            ActionList.ItemsSource = null;
            ActionList.ItemsSource = rule.Actions;
        }
        finally { _loading = false; }
    }

    private void ShowVacation()
    {
        _loading = true;
        try
        {
            ChkVacation.IsChecked = _vacation.Enabled;
            TxtVacationDays.Text = _vacation.Days.ToString();
            TxtVacationSubject.Text = _vacation.Subject;
            TxtVacationMessage.Text = _vacation.Message;
        }
        finally { _loading = false; }
    }

    private void RuleList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => ShowRule(CurrentRule);

    private void RuleName_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading || CurrentRule is not { } rule) return;
        rule.Name = TxtRuleName.Text;
        ShowRules();
    }

    private void RuleEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || CurrentRule is not { } rule) return;
        rule.Enabled = ChkRuleEnabled.IsChecked == true;
        ShowRules();
    }

    private void RuleMatch_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CurrentRule is not { } rule) return;
        rule.MatchAll = CmbRuleMatch.SelectedIndex == 0;
    }

    private void RuleNew_Click(object sender, RoutedEventArgs e)
    {
        var rule = new Services.Sieve.SieveRule { Name = $"Regel {_rules.Count + 1}" };
        _rules.Add(rule);

        ShowRules();
        RuleList.SelectedItem = rule;
    }

    private void RuleRemove_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentRule is not { } rule) return;

        _rules.Remove(rule);
        ShowRules();
        ShowRule(CurrentRule);
    }

    private void ConditionAdd_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentRule is not { } rule) return;

        var field = FieldFromIndex(CmbCondField.SelectedIndex);
        var value = TxtCondValue.Text.Trim();
        string? header = null;

        // Bei einer freien Kopfzeile steht der Name vor dem Gleichheitszeichen.
        if (field == Services.Sieve.RuleField.Header)
        {
            var split = value.IndexOf('=');
            if (split <= 0)
            {
                SetRuleStatus("Bei einer Kopfzeile bitte Name=Wert angeben.", warn: true);
                return;
            }

            header = value[..split].Trim();
            value = value[(split + 1)..].Trim();
        }

        rule.Conditions.Add(new Services.Sieve.RuleCondition(
            field, TestFromIndex(CmbCondTest.SelectedIndex), value, header));

        TxtCondValue.Clear();
        ShowRule(rule);
    }

    private void ConditionRemove_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentRule is not { } rule) return;
        if ((sender as FrameworkElement)?.Tag is not Services.Sieve.RuleCondition condition) return;

        rule.Conditions.Remove(condition);
        ShowRule(rule);
    }

    private void ActionAdd_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentRule is not { } rule) return;

        rule.Actions.Add(new Services.Sieve.RuleStep(
            ActionFromIndex(CmbActionKind.SelectedIndex), TxtActionValue.Text.Trim()));

        TxtActionValue.Clear();
        ShowRule(rule);
    }

    private void ActionRemove_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentRule is not { } rule) return;
        if ((sender as FrameworkElement)?.Tag is not Services.Sieve.RuleStep step) return;

        rule.Actions.Remove(step);
        ShowRule(rule);
    }

    private async void RulesLoad_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedAccount is not { } account) return;

        _sieve = _vm.SieveFor(account);
        SetRuleStatus("Hole Skript …", warn: false);

        try
        {
            var scripts = await _sieve.ListAsync();
            var active = scripts.FirstOrDefault(s => s.IsActive) ?? scripts.FirstOrDefault();

            _rulesBaseScript = active is null ? "" : await _sieve.GetAsync(active.Name);
            _activeScriptName = active?.Name ?? "regeln";

            var parsed = Services.Sieve.SieveRuleParser.Parse(_rulesBaseScript);

            _rules.Clear();
            _rules.AddRange(parsed.Rules);
            _vacation = parsed.Vacation;

            ShowRules();
            ShowRule(CurrentRule);
            ShowVacation();

            SetRuleStatus(parsed.HasForeignContent
                ? $"{parsed.Rules.Count} Regel(n) aus '{_activeScriptName}'. "
                  + "Das Skript enthält ausserdem von Hand geschriebene Teile – die bleiben unangetastet."
                : $"{parsed.Rules.Count} Regel(n) aus '{_activeScriptName}'.", warn: false);
        }
        catch (Exception ex)
        {
            ShowRuleError(ex);
        }
    }

    private string _activeScriptName = "regeln";

    private async void RulesSave_Click(object sender, RoutedEventArgs e)
    {
        if (_sieve is null) { SetRuleStatus("Erst vom Server holen.", warn: true); return; }

        // Erst prüfen, was der Anwender gebaut hat – eine halbfertige Regel
        // erzeugt sonst ein Skript, das der Server ablehnt.
        foreach (var rule in _rules.Where(r => r.Enabled))
        {
            if (rule.Problem is { } problem)
            {
                SetRuleStatus($"'{rule.Name}': {problem}", warn: true);
                RuleList.SelectedItem = rule;
                return;
            }
        }

        ReadVacationFromForm();
        if (_vacation.Problem is { } vacationProblem)
        {
            SetRuleStatus($"Abwesenheitsnotiz: {vacationProblem}", warn: true);
            return;
        }

        if (_vm.SelectedAccount is { } account && _vacation.Addresses.Count == 0)
            _vacation.Addresses = [.. account.SenderOptions.Select(a => a.Address)];

        var script = Services.Sieve.SieveGenerator.Build(_rules, _vacation, _rulesBaseScript);

        try
        {
            // Vor dem Speichern vom Server prüfen lassen.
            var check = await _sieve.CheckAsync(script);
            if (!check.IsOk)
            {
                SetRuleStatus($"Der Server lehnt das Skript ab: {check.Display}", warn: true);
                return;
            }

            await _sieve.PutAsync(_activeScriptName, script);
            await _sieve.SetActiveAsync(_activeScriptName);

            _rulesBaseScript = script;
            SetRuleStatus($"Gespeichert und aktiviert: '{_activeScriptName}'.", warn: false);
        }
        catch (Exception ex)
        {
            ShowRuleError(ex);
        }
    }

    private void ReadVacationFromForm()
    {
        _vacation.Enabled = ChkVacation.IsChecked == true;
        _vacation.Subject = TxtVacationSubject.Text.Trim();
        _vacation.Message = TxtVacationMessage.Text;

        if (int.TryParse(TxtVacationDays.Text.Trim(), out var days)) _vacation.Days = days;
    }

    private void ShowRuleError(Exception ex)
    {
        AppLog.Warn($"Sieve-Regeln: {ex.Message}");
        SetRuleStatus(ex.Message, warn: true);
    }

    private void SetRuleStatus(string text, bool warn)
    {
        LblRuleStatus.Text = text;
        LblRuleStatus.SetResourceReference(ForegroundProperty, warn ? "BadText" : "TextMuted");
    }

    // ---- Filterregeln (Sieve) ----------------------------------------------

    private Services.Sieve.ManageSieveClient? _sieve;

    /// <summary>Der Name, unter dem das gezeigte Skript auf dem Server liegt.</summary>
    private string? _openScript;

    private void ShowSieveServer()
    {
        var account = _vm.SelectedAccount;

        LblSieveServer.Text = account is null
            ? "Kein Konto ausgewählt."
            : string.IsNullOrWhiteSpace(account.SieveHost)
                ? "Für dieses Konto ist kein Sieve-Server eingetragen (Reiter 'Konten')."
                : $"{account.SieveHost}:{account.SievePort} als {account.LoginUser}";
    }

    /// <summary>
    /// Verbindet und lädt die Skriptliste. Wird nicht beim Öffnen des Dialogs
    /// getan: eine Anmeldung soll der Anwender auslösen, nicht ein Reiterwechsel.
    /// </summary>
    private async void SieveLoad_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedAccount is not { } account) return;

        _sieve = _vm.SieveFor(account);
        LblSieveStatus.Text = "Verbinde …";

        try
        {
            var scripts = await _sieve.ListAsync();
            SieveList.ItemsSource = scripts;

            LblSieveStatus.Text = scripts.Count == 0
                ? "Keine Skripte auf dem Server."
                : $"{_sieve.Capabilities.Implementation}: {scripts.Count} Skript(e)";
        }
        catch (Exception ex)
        {
            ShowSieveError(ex);
        }
    }

    private async void SieveList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_sieve is null || SieveList.SelectedItem is not Services.Sieve.SieveScript script) return;

        try
        {
            TxtSieveScript.Text = await _sieve.GetAsync(script.Name);
            _openScript = script.Name;
            LblSieveStatus.Text = $"'{script.Name}' geladen.";
        }
        catch (Exception ex)
        {
            ShowSieveError(ex);
        }
    }

    private void SieveNew_Click(object sender, RoutedEventArgs e)
    {
        var name = PromptWindow.Ask(this, "Neues Skript", "Name des Skripts:", "regeln",
            v => v.Contains('"') || v.Contains('\\') ? "Anführungszeichen sind nicht erlaubt." : null);
        if (name is null) return;

        _openScript = name;
        SieveList.SelectedItem = null;
        TxtSieveScript.Text = "require [\"fileinto\"];\r\n\r\n";
        LblSieveStatus.Text = $"Neues Skript '{name}' – noch nicht gespeichert.";
    }

    private async void SieveCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_sieve is null) { LblSieveStatus.Text = "Erst verbinden."; return; }

        try
        {
            var response = await _sieve.CheckAsync(TxtSieveScript.Text);

            // Auch ein OK kann Warnungen tragen – die gehören angezeigt.
            SetSieveStatus(response.IsOk
                ? $"Syntax in Ordnung. {response.Message}".TrimEnd()
                : $"Fehler: {response.Display}", warn: !response.IsOk);
        }
        catch (Exception ex)
        {
            ShowSieveError(ex);
        }
    }

    private async void SieveSave_Click(object sender, RoutedEventArgs e)
    {
        if (_sieve is null) { LblSieveStatus.Text = "Erst verbinden."; return; }
        if (_openScript is not { } name) { LblSieveStatus.Text = "Kein Skript gewählt."; return; }

        try
        {
            await _sieve.PutAsync(name, TxtSieveScript.Text);
            SetSieveStatus($"'{name}' gespeichert.", warn: false);

            SieveList.ItemsSource = await _sieve.ListAsync();
        }
        catch (Exception ex)
        {
            ShowSieveError(ex);
        }
    }

    private async void SieveActivate_Click(object sender, RoutedEventArgs e)
    {
        if (_sieve is null || SieveList.SelectedItem is not Services.Sieve.SieveScript script) return;

        try
        {
            await _sieve.SetActiveAsync(script.Name);
            SieveList.ItemsSource = await _sieve.ListAsync();
            SetSieveStatus($"'{script.Name}' ist jetzt aktiv.", warn: false);
        }
        catch (Exception ex)
        {
            ShowSieveError(ex);
        }
    }

    private async void SieveDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_sieve is null || SieveList.SelectedItem is not Services.Sieve.SieveScript script) return;

        var warning = script.IsActive
            ? "\n\nAchtung: Das Skript ist aktiv. Nach dem Löschen filtert nichts mehr."
            : "";

        if (MessageBox.Show(this, $"Skript '{script.Name}' löschen?{warning}",
                "Löschen", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes) return;

        try
        {
            await _sieve.DeleteAsync(script.Name);
            SieveList.ItemsSource = await _sieve.ListAsync();

            TxtSieveScript.Clear();
            _openScript = null;
            SetSieveStatus("Gelöscht.", warn: false);
        }
        catch (Exception ex)
        {
            ShowSieveError(ex);
        }
    }

    private void ShowSieveError(Exception ex)
    {
        AppLog.Warn($"Sieve: {ex.Message}");
        SetSieveStatus(ex.Message, warn: true);
    }

    private void SetSieveStatus(string text, bool warn)
    {
        LblSieveStatus.Text = text;
        LblSieveStatus.SetResourceReference(ForegroundProperty, warn ? "BadText" : "TextMuted");
    }

    // ---- Zwischenspeicher --------------------------------------------------

    private void ShowCacheStats()
        => LblCache.Text = _vm.Cache?.Stats().Display ?? "nicht verfügbar";

    private void CacheClear_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Cache is not { } cache) return;

        if (MessageBox.Show(this,
                "Den Zwischenspeicher leeren?\n\n"
                + "Ohne Verbindung ist danach nichts mehr lesbar, bis wieder geladen "
                + "wurde. Auf dem Server ändert sich nichts.",
                "Zwischenspeicher", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes) return;

        cache.Clear();
        ShowCacheStats();
    }

    // ---- Infobereich und Meldungen -----------------------------------------

    private void Tray_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        _vm.Settings.MinimizeToTray = ChkTray.IsChecked == true;
        _vm.SaveSettings();
    }

    private void Notify_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        _vm.Settings.NotifyOnNewMail = ChkNotify.IsChecked == true;
        _vm.SaveSettings();
    }

    private void Quiet_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        _vm.Settings.QuietHoursEnabled = ChkQuiet.IsChecked == true;
        _vm.Settings.QuietFrom = TxtQuietFrom.Text.Trim();
        _vm.Settings.QuietTo = TxtQuietTo.Text.Trim();
        _vm.SaveSettings();

        // Die geprüften Werte zurückschreiben: hat jemand Unsinn eingetippt,
        // soll er sehen, womit tatsächlich gerechnet wird.
        var hours = _vm.Settings.QuietHours;
        TxtQuietFrom.Text = hours.From.ToString("HH\\:mm");
        TxtQuietTo.Text = hours.To.ToString("HH\\:mm");
        ShowQuietSummary();
    }

    private void ShowQuietSummary()
    {
        var hours = _vm.Settings.QuietHours;

        LblQuiet.Text = !hours.Enabled
            ? ""
            : hours.IsQuiet(DateTime.Now) ? "gilt gerade" : "gilt gerade nicht";
    }

    private void Autostart_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var wanted = ChkAutostart.IsChecked == true;

        if (!Services.Shell.Autostart.Set(wanted))
        {
            MessageBox.Show(this,
                "Der Autostart-Eintrag konnte nicht geändert werden. "
                + "Möglicherweise ist die Registrierung durch eine Richtlinie gesperrt.",
                "Autostart", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // Nicht das annehmen, was gewünscht war, sondern anzeigen, was gilt.
        ChkAutostart.IsChecked = Services.Shell.Autostart.IsEnabled;
    }

    // ---- Sicherung ----------------------------------------------------------

    private void BackupExport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Sicherung (*.json)|*.json",
            FileName = $"nmailclient-sicherung-{DateTime.Now:yyyy-MM-dd}.json",
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            SettingsBackup.Save(dlg.FileName, _vm.Settings, _vm.Accounts);
            MessageBox.Show(this,
                $"Gesichert nach:\n{dlg.FileName}\n\nPasswörter sind nicht enthalten.",
                "Sicherung", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Sichern fehlgeschlagen: {ex.Message}",
                "Sicherung", MessageBoxButton.OK, MessageBoxImage.Warning);
            AppLog.Error("Sicherung fehlgeschlagen.", ex);
        }
    }

    private void BackupImport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Sicherung (*.json)|*.json|Alle Dateien|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;

        BackupFile backup;
        try
        {
            backup = SettingsBackup.Load(dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Die Datei ist nicht lesbar:\n{ex.Message}",
                "Sicherung", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Einspielen ersetzt den gesamten Bestand – das muss ausdrücklich bestätigt werden.
        var res = MessageBox.Show(this,
            $"{backup.Description}\n\n"
            + "Die vorhandenen Konten und Einstellungen werden dadurch ersetzt. "
            + "Passwörter müssen anschließend neu eingegeben werden.\n\nFortfahren?",
            "Sicherung einspielen", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (res != MessageBoxResult.Yes) return;

        try
        {
            _vm.RestoreBackup(backup);

            MessageBox.Show(this,
                "Eingespielt. Bitte die Passwörter in den Kontoeinstellungen ergänzen.",
                "Sicherung", MessageBoxButton.OK, MessageBoxImage.Information);

            // Anzeige auf den neuen Stand bringen.
            _loading = true;
            LoadSettings();
            InitLabels();
            _loading = false;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Einspielen fehlgeschlagen: {ex.Message}",
                "Sicherung", MessageBoxButton.OK, MessageBoxImage.Warning);
            AppLog.Error("Einspielen der Sicherung fehlgeschlagen.", ex);
        }
    }

    // ---- Anhang-Archiv -----------------------------------------------------

    private void ArchiveDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Ablageordner für Anhänge wählen",
            InitialDirectory = TxtArchiveDir.Text,
        };
        if (dlg.ShowDialog(this) != true) return;

        _vm.Settings.ArchiveDir = dlg.FolderName;
        TxtArchiveDir.Text = dlg.FolderName;
        _vm.SaveSettings();
    }

    private void ArchiveDirReset_Click(object sender, RoutedEventArgs e)
    {
        // Leer heisst: Vorgabe unter „Dokumente".
        _vm.Settings.ArchiveDir = "";
        TxtArchiveDir.Text = AttachmentArchive.DefaultRoot;
        _vm.SaveSettings();
    }

    // ---- Übersetzung -------------------------------------------------------

    private void TranslateUrl_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var url = TxtTranslateUrl.Text.Trim();
        if (url.Length > 0
            && !(Uri.TryCreate(url, UriKind.Absolute, out var parsed)
                 && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)))
        {
            MessageBox.Show(this, "Bitte eine vollständige Adresse angeben, z.B. https://translate.example.org",
                "Übersetzung", MessageBoxButton.OK, MessageBoxImage.Information);
            TxtTranslateUrl.Text = _vm.Settings.TranslateUrl;
            return;
        }

        _vm.Settings.TranslateUrl = url;
        _vm.SaveSettings();
    }

    private void TranslateKey_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        // Leerer Wert löscht den Eintrag – so wird man den Schlüssel wieder los.
        CredentialStore.Save(
            CredentialStore.GlobalTarget("translate"), "translate", TxtTranslateKey.Password);
    }

    private void TranslateTarget_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var target = TxtTranslateTarget.Text.Trim().ToLowerInvariant();
        _vm.Settings.TranslateTarget = target.Length == 0 ? "de" : target;
        TxtTranslateTarget.Text = _vm.Settings.TranslateTarget;
        _vm.SaveSettings();
    }

    private void Categories_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _vm.Settings.ShowCategories = ChkCategories.IsChecked == true;
        _vm.SaveSettings();
        _vm.ApplyListSettings();
    }

    private void GroupByDate_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _vm.Settings.GroupByDate = ChkGroupByDate.IsChecked == true;
        _vm.SaveSettings();
        _vm.ApplyListSettings();
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (CmbTheme.SelectedItem is not ComboBoxItem item) return;

        if (Enum.TryParse<AppTheme>((string)item.Tag, out var theme))
            ThemeManager.Apply(theme); // speichert selbst über die gebundenen Einstellungen
    }

    private void PageSize_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        if (!int.TryParse(TxtPageSize.Text, out var value))
        {
            // Ungültige Eingabe nicht speichern, sondern sichtbar zurücksetzen.
            TxtPageSize.Text = _vm.Settings.PageSize.ToString();
            return;
        }

        _vm.Settings.PageSize = value;
        TxtPageSize.Text = _vm.Settings.EffectivePageSize.ToString();
        _vm.Settings.PageSize = _vm.Settings.EffectivePageSize;
        _vm.SaveSettings();
    }

    private void UndoSeconds_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        if (!int.TryParse(TxtUndoSeconds.Text, out var value))
        {
            TxtUndoSeconds.Text = _vm.Settings.UndoSendSeconds.ToString();
            return;
        }

        _vm.Settings.UndoSendSeconds = value;
        TxtUndoSeconds.Text = _vm.Settings.EffectiveUndoSeconds.ToString();
        _vm.Settings.UndoSendSeconds = _vm.Settings.EffectiveUndoSeconds;
        _vm.SaveSettings();
    }

    private void ConfirmDelete_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _vm.Settings.ConfirmBeforeDelete = ChkConfirmDelete.IsChecked == true;
        _vm.SaveSettings();
    }
}
