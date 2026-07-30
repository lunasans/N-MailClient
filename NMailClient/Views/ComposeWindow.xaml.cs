using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using NMailClient.Services.Dav;
using NMailClient.Services.Transport;
using Microsoft.Win32;
using NMailClient.Models;
using NMailClient.Services;
using NMailClient.ViewModels;

namespace NMailClient.Views;

public partial class ComposeWindow : Window
{
    private readonly Account _account;
    private readonly SmtpService _smtp;
    private readonly ImapService _imap;
    private readonly ComposeRequest _request;
    private readonly SettingsStore _store;
    private readonly OutboxService _outbox;
    private readonly CardDavService _cardDav;
    private readonly Services.Pgp.PgpService _pgp;
    private readonly RecipientSecurity _transport;
    private readonly List<string> _attachments = new();

    /// <summary>Aktuell gewählter Absender – bestimmt auch die Signatur.</summary>
    private AliasDef _currentSender = new();

    // ---- Entwurf -----------------------------------------------------------

    private readonly DispatcherTimer _draftTimer;

    /// <summary>UID des zuletzt gespeicherten Entwurfs – wird beim nächsten Save ersetzt.</summary>
    private uint? _draftUid;

    /// <summary>Änderungen seit dem letzten Speichern.</summary>
    private bool _dirty;

    /// <summary>Läuft gerade ein Speichervorgang? Verhindert überlappende Saves.</summary>
    private bool _savingDraft;

    private static readonly TimeSpan AutoSaveInterval = TimeSpan.FromSeconds(10);

    public ComposeWindow(Account account, SmtpService smtp, ImapService imap,
        ComposeRequest request, SettingsStore store, OutboxService outbox,
        CardDavService cardDav, Services.Pgp.PgpService pgp, RecipientSecurity transport)
    {
        InitializeComponent();
        _pgp = pgp;
        _transport = transport;
        _account = account;
        _smtp = smtp;
        _imap = imap;
        _request = request;
        _store = store;
        _outbox = outbox;
        _cardDav = cardDav;

        CmbFrom.ItemsSource = account.SenderOptions.ToList();
        CmbFrom.SelectedIndex = 0;
        _currentSender = (AliasDef)CmbFrom.SelectedItem;

        SetUpPgp();

        ReloadTemplates();

        TxtTo.Text = request.To;
        TxtCc.Text = request.Cc;
        TxtSubject.Text = request.Subject;

        // Beim Weiterschreiben an einem Entwurf keine zweite Signatur anhängen.
        var signature = request.SuppressSignature ? "" : SignatureBlock(_currentSender);
        SetBodyText(request.Body + signature);

        CmbFontSize.ItemsSource = new[] { 10, 12, 14, 16, 20, 24 };
        CmbFontSize.SelectedItem = 14;

        _draftUid = request.DraftUid;
        if (request.Attachments.Count > 0) AddAttachments(request.Attachments);
        _dirty = false; // Vorbelegung ist keine Änderung

        // Cursor an den Anfang – oberhalb von Zitat und Signatur.
        TxtBody.CaretPosition = TxtBody.Document.ContentStart;
        Loaded += (_, _) => (string.IsNullOrEmpty(request.To)
            ? (Control)TxtTo : TxtBody).Focus();

        // Erst NACH der Vorbelegung anmelden, sonst gilt schon das Öffnen als Änderung.
        foreach (var box in new[] { TxtTo, TxtCc, TxtSubject })
            box.TextChanged += (_, _) => _dirty = true;
        TxtBody.TextChanged += (_, _) => _dirty = true;

        // Vervollständigung an die drei Empfängerfelder hängen.
        foreach (var box in new[] { TxtTo, TxtCc, TxtBcc })
        {
            box.TextChanged += Recipient_TextChanged;
            box.PreviewKeyDown += Recipient_PreviewKeyDown;
            box.LostFocus += (_, _) => { if (!LstSuggest.IsKeyboardFocusWithin) PopSuggest.IsOpen = false; };

            // Transportprüfung erst beim Verlassen des Feldes: bei jedem Tastendruck
            // wären es Verbindungen zu halbfertigen Domainnamen.
            box.LostFocus += async (_, _) => await UpdateTransportAsync();
        }
        TxtBcc.TextChanged += (_, _) => _dirty = true;

        // Kontakte im Hintergrund holen – das Fenster soll sofort benutzbar sein.
        Loaded += async (_, _) => await LoadContactsAsync();

        _draftTimer = new DispatcherTimer { Interval = AutoSaveInterval };
        _draftTimer.Tick += async (_, _) => await SaveDraftAsync();
        _draftTimer.Start();

        Closing += async (_, _) =>
        {
            _draftTimer.Stop();
            // Letzten Stand sichern, wenn seit dem letzten Auto-Save getippt wurde.
            if (_dirty) await SaveDraftAsync();
        };
    }

    // ---- Textkörper --------------------------------------------------------

    /// <summary>Reintext in das Dokument setzen – Zeilen werden zu Absätzen.</summary>
    private void SetBodyText(string text)
    {
        TxtBody.Document.Blocks.Clear();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            TxtBody.Document.Blocks.Add(new Paragraph(new Run(line)));
    }

    private string BodyHtml => FlowDocumentHtml.ToHtml(TxtBody.Document);
    private string BodyText => FlowDocumentHtml.ToPlainText(TxtBody.Document);

    private bool HasDraftContent =>
        TxtTo.Text.Trim().Length > 0 || TxtCc.Text.Trim().Length > 0
        || TxtBcc.Text.Trim().Length > 0
        || TxtSubject.Text.Trim().Length > 0 || BodyText.Trim().Length > 0
        || _attachments.Count > 0;

    // ---- Empfänger-Autovervollständigung ------------------------------------

    private readonly List<Contact> _contacts = [];

    /// <summary>Feld, zu dem die Vorschlagsliste gerade gehört.</summary>
    private TextBox? _suggestTarget;

    /// <summary>
    /// Kontakte im Hintergrund laden. Scheitert es (keine CardDAV-Adresse,
    /// Server nicht erreichbar), bleibt der Composer voll benutzbar.
    /// </summary>
    private async Task LoadContactsAsync()
    {
        if (!_cardDav.IsConfigured) return;

        try
        {
            foreach (var book in await _cardDav.ListAddressBooksAsync())
                _contacts.AddRange(await _cardDav.GetContactsAsync(book.Url));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Kontakte für die Vervollständigung nicht geladen: {ex.Message}");
        }
    }

    private void Recipient_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox box) return;

        var (_, _, term) = RecipientCompletion.CurrentToken(box.Text, box.CaretIndex);
        var matches = RecipientCompletion.Suggest(_contacts, term);

        if (matches.Count == 0)
        {
            PopSuggest.IsOpen = false;
            return;
        }

        _suggestTarget = box;
        LstSuggest.ItemsSource = matches;
        LstSuggest.SelectedIndex = 0;

        PopSuggest.PlacementTarget = box;
        PopSuggest.Width = box.ActualWidth;
        PopSuggest.IsOpen = true;
    }

    /// <summary>
    /// Tastensteuerung im Eingabefeld: Pfeil ab wechselt in die Liste, Eingabe
    /// und Tabulator übernehmen den obersten Vorschlag, Esc schliesst.
    /// </summary>
    private void Recipient_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!PopSuggest.IsOpen) return;

        switch (e.Key)
        {
            case Key.Down:
                LstSuggest.Focus();
                if (LstSuggest.SelectedIndex < 0) LstSuggest.SelectedIndex = 0;
                e.Handled = true;
                break;

            case Key.Enter:
            case Key.Tab:
                if (LstSuggest.SelectedItem is Contact) { AcceptSuggestion(); e.Handled = true; }
                break;

            case Key.Escape:
                PopSuggest.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    private void Suggest_Click(object sender, MouseButtonEventArgs e) => AcceptSuggestion();

    private void Suggest_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter) { AcceptSuggestion(); e.Handled = true; }
        else if (e.Key is Key.Escape)
        {
            PopSuggest.IsOpen = false;
            _suggestTarget?.Focus();
            e.Handled = true;
        }
    }

    private void AcceptSuggestion()
    {
        if (LstSuggest.SelectedItem is not Contact contact || _suggestTarget is not { } box) return;

        var (text, caret) = RecipientCompletion.Replace(
            box.Text, box.CaretIndex, contact.AsRecipient);

        box.Text = text;
        box.CaretIndex = caret;

        PopSuggest.IsOpen = false;
        box.Focus();
    }

    // ---- Absender und Signatur ---------------------------------------------

    /// <summary>Signatur des Absenders; leer heisst: die des Kontos.</summary>
    private string SignatureBlock(AliasDef sender)
    {
        var text = string.IsNullOrWhiteSpace(sender.Signature)
            ? _account.Signature : sender.Signature;
        return string.IsNullOrWhiteSpace(text) ? "" : "\n\n--\n" + text;
    }

    /// <summary>
    /// Beim Wechsel des Absenders die alte Signatur durch die neue ersetzen –
    /// aber nur die automatisch eingefügte, nichts vom Nutzer Geschriebenes.
    /// </summary>
    private void From_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (CmbFrom.SelectedItem is not AliasDef next) return;

        var previous = SignatureBlock(_currentSender);
        _currentSender = next;
        var replacement = SignatureBlock(next);

        if (previous == replacement || previous.Length == 0) return;

        var body = BodyText;
        if (!body.EndsWith(previous.TrimEnd(), StringComparison.Ordinal)) return;

        SetBodyText(body[..^previous.TrimEnd().Length] + replacement);
        _dirty = true;
    }

    private void Bcc_Toggled(object sender, RoutedEventArgs e)
    {
        var visible = TglBcc.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        LblBcc.Visibility = visible;
        TxtBcc.Visibility = visible;
        if (visible == Visibility.Visible) TxtBcc.Focus();
    }

    // ---- Textbausteine -----------------------------------------------------

    private void ReloadTemplates()
    {
        CmbTemplates.ItemsSource = null;
        CmbTemplates.ItemsSource = _store.Settings.Templates;
        CmbTemplates.SelectedIndex = -1;
        CmbTemplates.Text = "Baustein…";
    }

    private void Template_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (CmbTemplates.SelectedItem is not TemplateDef t) return;

        // An der Einfügemarke einsetzen, nicht den Text ersetzen.
        TxtBody.CaretPosition.InsertTextInRun(t.Text);
        _dirty = true;

        CmbTemplates.SelectedIndex = -1;
        TxtBody.Focus();
    }

    private void SaveTemplate_Click(object sender, RoutedEventArgs e)
    {
        var selection = TxtBody.Selection;
        var text = selection.IsEmpty ? BodyText : selection.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            MessageBox.Show(this, "Kein Text zum Speichern vorhanden.",
                "Baustein", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var name = PromptWindow.Ask(this, "Baustein speichern", "Name des Bausteins:");
        if (string.IsNullOrWhiteSpace(name)) return;

        var existing = _store.Settings.Templates
            .FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        if (existing is not null) existing.Text = text;
        else _store.Settings.Templates.Add(new TemplateDef { Name = name.Trim(), Text = text });

        _store.Save();
        ReloadTemplates();
    }

    // ---- Formatierleiste ---------------------------------------------------

    private void Strike_Click(object sender, RoutedEventArgs e)
    {
        // Für Durchstreichen gibt es keinen Editierbefehl – direkt am Bereich setzen.
        var selection = TxtBody.Selection;
        if (selection.IsEmpty) return;

        var current = selection.GetPropertyValue(Inline.TextDecorationsProperty);
        var isSet = current is TextDecorationCollection { Count: > 0 } c
                    && c.Any(d => d.Location == TextDecorationLocation.Strikethrough);

        selection.ApplyPropertyValue(Inline.TextDecorationsProperty,
            isSet ? null : TextDecorations.Strikethrough);
        TxtBody.Focus();
    }

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        var selection = TxtBody.Selection;
        if (selection.IsEmpty)
        {
            MessageBox.Show(this, "Bitte zuerst den Text markieren, der verlinkt werden soll.",
                "Link einfügen", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var url = PromptWindow.Ask(this, "Link einfügen", "Ziel-Adresse:", "https://",
            v => Uri.TryCreate(v, UriKind.Absolute, out var u)
                 && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps
                     || u.Scheme == Uri.UriSchemeMailto)
                ? null
                : "Bitte eine vollständige Adresse angeben (http, https oder mailto).");
        if (url is null) return;

        new Hyperlink(selection.Start, selection.End) { NavigateUri = new Uri(url) };
        _dirty = true;
        TxtBody.Focus();
    }

    private void FontSize_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (CmbFontSize.SelectedItem is not int size) return;
        if (TxtBody.Selection.IsEmpty) return;

        TxtBody.Selection.ApplyPropertyValue(TextElement.FontSizeProperty, (double)size);
        TxtBody.Focus();
    }

    private void ClearFormat_Click(object sender, RoutedEventArgs e)
    {
        var selection = TxtBody.Selection;
        if (selection.IsEmpty) return;

        selection.ClearAllProperties();
        TxtBody.Focus();
    }

    // ---- Transportsicherheit der Empfänger ---------------------------------

    /// <summary>
    /// Prüft je Empfängerdomain, ob der annehmende Server STARTTLS anbietet und
    /// ein gültiges Zertifikat vorweist. Läuft im Hintergrund; das Ergebnis ist
    /// ein Hinweis, kein Hindernis — versendet wird über den eigenen Server, der
    /// die Zustellung ohnehin selbst aushandelt.
    /// </summary>
    private async Task UpdateTransportAsync()
    {
        var domains = new[] { TxtTo.Text, TxtCc.Text, TxtBcc.Text }
            .SelectMany(t => t.Split(',', StringSplitOptions.RemoveEmptyEntries
                                          | StringSplitOptions.TrimEntries))
            .Select(t => MimeKit.MailboxAddress.TryParse(t, out var m)
                ? RecipientSecurity.DomainOf(m.Address) : "")
            .Where(d => d.Length > 0)
            .Distinct()
            .ToList();

        if (domains.Count == 0)
        {
            LblTransport.Visibility = Visibility.Collapsed;
            return;
        }

        // Bekanntes sofort zeigen, damit die Zeile beim Zurückspringen nicht flackert.
        Render(domains.Select(_transport.Cached).OfType<DomainSecurity>().ToList());

        var results = await Task.WhenAll(domains.Select(d => _transport.InspectAsync(d)));
        Render(results);
    }

    private void Render(IReadOnlyList<DomainSecurity> results)
    {
        if (results.Count == 0) return;

        LblTransport.Text = string.Join("   ·   ", results.Select(r => r.Summary));
        LblTransport.Visibility = Visibility.Visible;

        // Die schlechteste Domain bestimmt die Farbe – eine grüne Zeile neben
        // einem unverschlüsselt erreichbaren Empfänger wäre irreführend.
        var worst = results.Min(r => r.Grade);
        LblTransport.SetResourceReference(ForegroundProperty, worst switch
        {
            TransportGrade.Good => "OkText",
            TransportGrade.Bad or TransportGrade.Weak => "BadText",
            _ => "TextMuted",
        });
    }

    // ---- OpenPGP -----------------------------------------------------------

    /// <summary>
    /// Die beiden Schalter erscheinen nur, wenn überhaupt ein Schlüsselbund da
    /// ist — sonst wären es zwei Knöpfe, die nichts können.
    /// </summary>
    private void SetUpPgp()
    {
        if (!_pgp.HasAnyKey()) return;

        ChkEncrypt.Visibility = Visibility.Visible;

        // Signieren geht nur mit eigenem geheimem Schlüssel für diese Adresse.
        var from = new MimeKit.MailboxAddress(_account.Name, SenderAddress());
        if (_pgp.CanSign(from)) ChkSign.Visibility = Visibility.Visible;
    }

    private string SenderAddress() =>
        string.IsNullOrWhiteSpace(_currentSender.Address) ? _account.Email : _currentSender.Address;

    private void Pgp_Toggled(object sender, RoutedEventArgs e) => _dirty = true;

    /// <summary>
    /// Vor dem Senden prüfen, ob für alle Empfänger ein Schlüssel vorliegt.
    /// Lieber abbrechen als die Mail still im Klartext hinausgehen zu lassen —
    /// der Absender glaubt sonst, sie sei geschützt.
    /// </summary>
    private bool PgpRecipientsAreReady()
    {
        if (ChkEncrypt.IsChecked != true) return true;

        var addresses = new[] { TxtTo.Text, TxtCc.Text, TxtBcc.Text }
            .SelectMany(t => t.Split(',', StringSplitOptions.RemoveEmptyEntries
                                          | StringSplitOptions.TrimEntries))
            .Select(t => MimeKit.MailboxAddress.TryParse(t, out var m) ? m : null)
            .OfType<MimeKit.MailboxAddress>()
            .ToList();

        if (addresses.Count == 0) return true;   // die übliche Empfängerprüfung greift

        var missing = _pgp.RecipientsWithoutKey(addresses);
        if (missing.Count == 0) return true;

        MessageBox.Show(this,
            "Für diese Empfänger liegt kein öffentlicher Schlüssel vor:\n\n"
            + string.Join("\n", missing)
            + "\n\nDie Nachricht wurde nicht gesendet. Schlüssel in den Einstellungen "
            + "einlesen oder die Verschlüsselung abschalten.",
            "Verschlüsseln nicht möglich", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private async Task SaveDraftAsync()
    {
        if (!_dirty || _savingDraft || !HasDraftContent) return;

        _savingDraft = true;
        _dirty = false;
        try
        {
            // Entwürfe bleiben bewusst ungeschützt: verschlüsselt wird für die
            // Empfänger, nicht für einen selbst — der eigene Entwurf liesse sich
            // sonst nie wieder öffnen.
            var msg = await SmtpService.BuildMessageAsync(
                _account, TxtTo.Text, TxtCc.Text, TxtSubject.Text, BodyText,
                _attachments, lenient: true, _request.InReplyTo, _request.References,
                BodyHtml, TxtBcc.Text, _currentSender, ChkReceipt.IsChecked == true);

            var uid = await _imap.SaveDraftAsync(msg, _draftUid);
            if (uid is not null) _draftUid = uid;

            LblAttachments.ToolTip = $"Entwurf gespeichert um {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            // Auto-Save darf das Schreiben nie stören – nur merken und weitertippen lassen.
            _dirty = true;
            AppLog.Warn($"Entwurf konnte nicht gespeichert werden: {ex.Message}");
        }
        finally { _savingDraft = false; }
    }

    private async Task MarkOriginalAnsweredAsync()
    {
        if (_request is not { ReplyToFolder: { } folder, ReplyToUid: { } uid }) return;
        try
        {
            await _imap.SetAnsweredAsync(folder, uid);
        }
        catch (Exception ex)
        {
            // Kosmetik – die Antwort ist raus, das Flag darf still scheitern.
            AppLog.Warn($"\\Answered konnte nicht gesetzt werden: {ex.Message}");
        }
    }

    private async Task DeleteDraftAsync()
    {
        if (_draftUid is not { } uid) return;
        try
        {
            await _imap.DeleteDraftAsync(uid);
            _draftUid = null;
        }
        catch (Exception ex)
        {
            // Verwaister Entwurf ist ärgerlich, aber kein Grund den Versand zu verweigern.
            AppLog.Warn($"Entwurf konnte nach dem Senden nicht entfernt werden: {ex.Message}");
        }
    }

    // ---- Anhänge -----------------------------------------------------------

    private void Attach_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Multiselect = true, Filter = "Alle Dateien|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        AddAttachments(dlg.FileNames);
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddAttachments(paths);
    }

    private void AddAttachments(IEnumerable<string> paths)
    {
        foreach (var p in paths)
            if (File.Exists(p) && !_attachments.Contains(p))
            {
                _attachments.Add(p);
                _dirty = true;
            }

        LblAttachments.Text = _attachments.Count == 0
            ? "Keine Anhänge (Dateien hierher ziehen)"
            : $"{_attachments.Count} Anhang/Anhänge: " +
              string.Join(", ", _attachments.Select(Path.GetFileName));
    }

    // ---- Senden ------------------------------------------------------------

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        // Frist aus den Einstellungen; 0 heisst sofort raus.
        var delay = _store.Settings.EffectiveUndoSeconds;
        await QueueAsync(DateTimeOffset.Now.AddSeconds(delay), delay > 0
            ? $"Wird in {delay} s gesendet."
            : "Wird gesendet.");
    }

    private async void SendLater_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ScheduleWindow { Owner = this };
        if (dlg.ShowDialog() != true) return;

        await QueueAsync(dlg.SendAt, $"Geplant für {dlg.SendAt.LocalDateTime:dd.MM.yyyy HH:mm}.");
    }

    /// <summary>
    /// Nachricht in die Ausgangs-Warteschlange stellen. Der eigentliche Versand
    /// passiert dort – auch das Entfernen des Entwurfs und das \Answered-Flag,
    /// damit beides greift, wenn das Fenster längst zu ist.
    /// </summary>
    private async Task QueueAsync(DateTimeOffset sendAt, string confirmation)
    {
        if (!PgpRecipientsAreReady()) return;

        _draftTimer.Stop();
        BtnSend.IsEnabled = false;
        try
        {
            var msg = await SmtpService.BuildMessageAsync(
                _account, TxtTo.Text, TxtCc.Text, TxtSubject.Text, BodyText,
                _attachments, lenient: false, _request.InReplyTo, _request.References,
                BodyHtml, TxtBcc.Text, _currentSender, ChkReceipt.IsChecked == true,
                _pgp, ChkSign.IsChecked == true, ChkEncrypt.IsChecked == true);

            await _outbox.EnqueueAsync(msg, _account, sendAt,
                _draftUid, _request.ReplyToFolder, _request.ReplyToUid);

            _dirty = false; // Closing darf keinen neuen Entwurf mehr anlegen
            _draftUid = null; // die Warteschlange räumt ihn auf
            AppLog.Info(confirmation);

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Senden fehlgeschlagen",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            BtnSend.IsEnabled = true;
            _draftTimer.Start();
        }
    }
}
