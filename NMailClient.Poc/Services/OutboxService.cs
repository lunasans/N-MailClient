using System.IO;
using System.Text.Json;
using MimeKit;
using NMailClient.Poc.Models;
using AuthenticationException = MailKit.Security.AuthenticationException;

namespace NMailClient.Poc.Services;

/// <summary>
/// Warteschlange für ausgehende Mail. Trägt zwei Funktionen mit derselben Mechanik:
///
///  * **Versand rückgängig** – jede Mail wartet eine kurze Frist, in der sie
///    zurückgeholt werden kann.
///  * **Geplanter Versand** – dieselbe Warteschlange, nur mit späterem Zeitpunkt.
///
/// Die Warteschlange ist persistent: ein Absturz oder Neustart darf keine Mail
/// verlieren. Überfällige Einträge werden beim Start nachgeholt.
///
/// Einschränkung wie in der Go-Version: gesendet wird nur, **während die Anwendung
/// läuft**. Ein für 3 Uhr nachts geplanter Versand geht erst raus, wenn die App
/// dann läuft – sonst beim nächsten Start.
/// </summary>
public sealed class OutboxService : IDisposable
{
    private readonly string _dir;
    private readonly string _indexPath;
    private readonly Func<string, SmtpService?> _smtpFor;
    private readonly Func<string, ImapService?> _imapFor;

    private readonly List<OutboxItem> _items = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly System.Timers.Timer _timer;

    /// <summary>Nach so vielen Fehlversuchen bleibt die Mail liegen.</summary>
    private const int MaxAttempts = 3;

    public event Action? Changed;

    /// <summary>
    /// Eine Antwort ist hinausgegangen: Konto, Ordner und UID des Originals.
    /// Die Liste setzt daraufhin die Antwortmarke, ohne neu zu laden.
    /// </summary>
    public event Action<string, string, uint>? Answered;

    /// <summary>
    /// Eine Kopie liegt im Gesendet-Ordner: Konto und Ordnername. Steht dieser
    /// gerade offen, lädt die Liste nach – sonst sähe man dort weiter den Stand
    /// von vor dem Senden und hielte die Mail für verloren.
    /// </summary>
    public event Action<string, string>? SavedToSent;

    public OutboxService(
        Func<string, SmtpService?> smtpFor, Func<string, ImapService?> imapFor)
    {
        _smtpFor = smtpFor;
        _imapFor = imapFor;

        _dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NMailClient.Poc", "outbox");
        _indexPath = Path.Combine(_dir, "queue.json");

        Load();

        // Sekündlich, damit die Restzeit im Rückgängig-Streifen mitläuft.
        _timer = new System.Timers.Timer(1000) { AutoReset = true };
        _timer.Elapsed += async (_, _) => await ProcessAsync();
        _timer.Start();
    }

    public IReadOnlyList<OutboxItem> Items
    {
        get { lock (_items) return _items.ToList(); }
    }

    /// <summary>Nachricht in die Warteschlange stellen.</summary>
    public async Task<OutboxItem> EnqueueAsync(
        MimeMessage message, Account account, DateTimeOffset sendAt,
        uint? draftUid = null, string? replyFolder = null, uint? replyUid = null)
    {
        Directory.CreateDirectory(_dir);

        var item = new OutboxItem
        {
            AccountId = account.Id,
            SendAt = sendAt,
            Subject = message.Subject ?? "",
            To = message.To.ToString(),
            DraftUid = draftUid,
            ReplyFolder = replyFolder,
            ReplyUid = replyUid,
        };

        await message.WriteToAsync(PathOf(item));

        lock (_items) _items.Add(item);
        Save();
        Changed?.Invoke();
        return item;
    }

    /// <summary>
    /// Zurückholen. Nur möglich, solange die Frist läuft – danach ist die Mail
    /// unterwegs und lässt sich nicht mehr einfangen.
    /// </summary>
    public bool Cancel(string id)
    {
        lock (_items)
        {
            var item = _items.FirstOrDefault(x => x.Id == id);
            if (item is null || !item.IsPending) return false;

            _items.Remove(item);
            TryDelete(PathOf(item));
        }
        Save();
        Changed?.Invoke();
        return true;
    }

    private async Task ProcessAsync()
    {
        // Nicht überlappen: ein langsamer SMTP-Server darf keinen zweiten Lauf starten.
        if (!await _gate.WaitAsync(0)) return;
        try
        {
            List<OutboxItem> due;
            lock (_items)
                due = _items
                    .Where(x => !x.IsPending && x.Attempts < MaxAttempts)
                    .ToList();

            if (due.Count == 0)
            {
                // Trotzdem melden, damit die Restzeit-Anzeige mitläuft.
                lock (_items) if (_items.Any(x => x.IsPending)) Changed?.Invoke();
                return;
            }

            foreach (var item in due) await SendAsync(item);
        }
        finally { _gate.Release(); }
    }

    private async Task SendAsync(OutboxItem item)
    {
        var smtp = _smtpFor(item.AccountId);
        if (smtp is null)
        {
            // Konto existiert nicht mehr – Eintrag verwerfen, sonst bleibt er ewig liegen.
            AppLog.Warn($"Ausgang: Konto zu '{item.Subject}' fehlt, Eintrag verworfen.");
            Remove(item);
            return;
        }

        try
        {
            var message = await MimeMessage.LoadAsync(PathOf(item));
            await smtp.SendAsync(message);

            var imap = _imapFor(item.AccountId);
            if (imap is not null)
            {
                // Kopie in den Gesendet-Ordner. SMTP befördert die Mail nur nach
                // draussen; ohne diesen Schritt fehlt sie im eigenen Postfach.
                // Ein Fehlschlag darf den Versand nicht rückgängig erscheinen
                // lassen – die Mail ist ja bereits unterwegs.
                try
                {
                    if (await imap.SaveToSentAsync(message) is { } sentFolder)
                    {
                        // Steht dieser Ordner gerade offen, muss er nachladen –
                        // sonst wartet man vor einer Liste, in der die eben
                        // gesendete Mail fehlt, und hält sie für verloren.
                        SavedToSent?.Invoke(item.AccountId, sentFolder);
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"Kopie im Gesendet-Ordner fehlgeschlagen: {ex.Message}");
                }

                if (item.DraftUid is { } draft)
                {
                    try { await imap.DeleteDraftAsync(draft); }
                    catch (Exception ex) { AppLog.Warn($"Entwurf nicht entfernt: {ex.Message}"); }
                }
                if (item is { ReplyFolder: { } folder, ReplyUid: { } uid })
                {
                    try
                    {
                        await imap.SetAnsweredAsync(folder, uid);

                        // Der Liste sagen, welche Nachricht es betrifft – sonst
                        // erschiene die Antwortmarke erst nach dem nächsten Laden.
                        Answered?.Invoke(item.AccountId, folder, uid);
                    }
                    catch (Exception ex) { AppLog.Warn($"\\Answered nicht gesetzt: {ex.Message}"); }
                }
            }

            AppLog.Info($"Ausgang: '{item.Subject}' gesendet.");
            Remove(item);
        }
        catch (AuthenticationException ex)
        {
            // Zugangsdaten ändern sich nicht von selbst – nicht weiter versuchen.
            item.Attempts = MaxAttempts;
            item.LastError = ex.Message;
            AppLog.Warn($"Ausgang: '{item.Subject}' – Anmeldung abgelehnt, bleibt liegen.");
            Save();
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            item.Attempts++;
            item.LastError = ex.Message;
            // Nächster Versuch mit Abstand, nicht im Sekundentakt.
            item.SendAt = DateTimeOffset.Now.AddSeconds(30 * item.Attempts);

            AppLog.Warn($"Ausgang: '{item.Subject}' fehlgeschlagen "
                        + $"(Versuch {item.Attempts}/{MaxAttempts}): {ex.Message}");
            Save();
            Changed?.Invoke();
        }
    }

    private void Remove(OutboxItem item)
    {
        lock (_items) _items.Remove(item);
        TryDelete(PathOf(item));
        Save();
        Changed?.Invoke();
    }

    // ---- Persistenz ---------------------------------------------------------

    private string PathOf(OutboxItem item) => Path.Combine(_dir, item.Id + ".eml");

    private void Load()
    {
        try
        {
            if (!File.Exists(_indexPath)) return;

            var list = JsonSerializer.Deserialize<List<OutboxItem>>(File.ReadAllText(_indexPath));
            if (list is null) return;

            foreach (var item in list)
            {
                // Ohne Nachrichtendatei ist der Eintrag wertlos.
                if (!File.Exists(PathOf(item))) continue;
                lock (_items) _items.Add(item);
            }

            var overdue = _items.Count(x => !x.IsPending);
            if (overdue > 0) AppLog.Info($"Ausgang: {overdue} nachzuholende Sendung(en) gefunden.");
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            AppLog.Error("Ausgangs-Warteschlange nicht lesbar.", ex);
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(_dir);
            List<OutboxItem> snapshot;
            lock (_items) snapshot = _items.ToList();

            var tmp = _indexPath + ".tmp";
            File.WriteAllText(tmp,
                JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _indexPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Ausgangs-Warteschlange nicht speicherbar.", ex);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* beim nächsten Start weg */ }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
        _gate.Dispose();
    }
}
