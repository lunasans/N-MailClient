using System.IO;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using AuthenticationException = MailKit.Security.AuthenticationException;
using MimeKit;
using NMailClient.Models;

namespace NMailClient.Services;

/// <summary>
/// Pendant zu internal/mail/imap.go. Hält pro Konto eine Verbindung offen und
/// serialisiert alle Zugriffe – ein ImapClient ist nicht threadsicher.
/// </summary>
public class ImapService : IAsyncDisposable
{
    private readonly Account _account;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ImapClient? _client;

    /// <summary>
    /// Gesetzt, sobald der Server die Anmeldedaten abgelehnt hat. Verhindert, dass
    /// jede Folgeoperation (Ordnerbaum, STATUS je Ordner, jede Aktion) einen weiteren
    /// Login-Versuch auslöst – bei falschem Passwort wären das dutzende, was
    /// serverseitig zuverlässig eine fail2ban-Sperre nach sich zieht.
    /// Wird erst durch neue Zugangsdaten aufgehoben (MainViewModel legt den Service neu an).
    /// </summary>
    private string? _authFailure;

    /// <summary>
    /// Ohne Schlüsselbund bleibt eine PGP-Mail einfach verschlüsselt stehen —
    /// deshalb optional, damit Tests den Dienst ohne Krypto bauen können.
    /// </summary>
    private readonly Pgp.PgpService? _pgp;

    /// <summary>
    /// Lokaler Zwischenspeicher; ohne ihn arbeitet der Dienst wie bisher, nur
    /// eben ohne Offline-Rückfall.
    /// </summary>
    private readonly Cache.MailCache? _cache;

    /// <summary>
    /// Wahr, wenn zuletzt aus dem Zwischenspeicher statt vom Server geliefert
    /// wurde. Die Oberfläche zeigt das an – stille Altdaten wären schlimmer als
    /// eine Fehlermeldung.
    /// </summary>
    public bool IsOffline { get; private set; }

    public ImapService(Account account, Pgp.PgpService? pgp = null, Cache.MailCache? cache = null)
    {
        _account = account;
        _pgp = pgp;
        _cache = cache;
    }

    /// <summary>
    /// Netzprobleme heissen „offline"; eine abgelehnte Anmeldung nicht — die
    /// wäre mit Altdaten zu überdecken irreführend, sie verlangt eine Handlung.
    ///
    /// <paramref name="ct"/> entscheidet über den Sonderfall Abbruch: hat der
    /// Aufrufer selbst abgebrochen — weil weitergeklickt wurde —, ist das kein
    /// Netzproblem. Ohne diese Unterscheidung meldete die Anwendung „offline"
    /// und zeigte Altdaten, während die Verbindung tadellos stand. Ein Abbruch
    /// ohne gesetztes Merkmal ist dagegen sehr wohl eine Zeitüberschreitung:
    /// MailKit bricht eigene Fristen auf demselben Weg ab.
    /// </summary>
    public static bool IsConnectionProblem(Exception ex, CancellationToken ct)
    {
        if (ex is OperationCanceledException) return !ct.IsCancellationRequested;

        return ex is System.Net.Sockets.SocketException or IOException or TimeoutException
                   or MailKit.Net.Imap.ImapProtocolException;
    }

    private async Task<ImapClient> ConnectAsync(CancellationToken ct)
    {
        if (_client is { IsConnected: true, IsAuthenticated: true }) return _client;

        if (_authFailure is not null)
            throw new AuthenticationException(_authFailure);

        _client?.Dispose();
        _client = null;
        var client = new ImapClient();

        // Port 993 = implizites TLS, alles andere über STARTTLS.
        var options = _account.ImapPort == 993
            ? SecureSocketOptions.SslOnConnect
            : SecureSocketOptions.StartTls;

        // Nicht über den Namen verbinden lassen: .NET arbeitet die aufgelösten
        // Adressen stur der Reihe nach ab. Steht eine unerreichbare vorn — etwa
        // IPv6 hinter einer Sperre, die Pakete verschluckt — kostet das die volle
        // TCP-Frist von gut 20 Sekunden, bevor der nächste Weg versucht wird.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var socket = await Transport.HappyEyeballs.ConnectAsync(
            _account.ImapHost, _account.ImapPort, ct);

        await client.ConnectAsync(socket, _account.ImapHost, _account.ImapPort, options, ct);

        // Bleibt im Protokoll: eine auffällig lange Verbindung ist fast immer
        // ein Netzproblem und kein Fehler der Anwendung – das war hier so.
        AppLog.Info($"Verbindung zu {_account.ImapHost} steht "
                    + $"({socket.RemoteEndPoint}) nach {watch.ElapsedMilliseconds} ms.");
        try
        {
            await client.AuthenticateAsync(_account.LoginUser, _account.Password, ct);
        }
        catch (AuthenticationException ex)
        {
            // Abgelehnte Zugangsdaten ändern sich nicht von selbst – nicht erneut versuchen.
            _authFailure = $"Anmeldung abgelehnt ({ex.Message}). "
                           + "Zugangsdaten in den Kontoeinstellungen prüfen.";
            client.Dispose();
            throw new AuthenticationException(_authFailure, ex);
        }
        _client = client;
        return client;
    }

    /// <summary>Verbindung + Login prüfen ("Test"-Button in den Kontoeinstellungen).</summary>
    public async Task TestAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { await ConnectAsync(ct); }
        finally { _gate.Release(); }
    }

    /// <summary>Ordnerbaum inkl. Ungelesen-Zähler; Inbox steht immer oben.</summary>
    public async Task<List<FolderNode>> GetFoldersAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var client = await ConnectAsync(ct);
            var folderWatch = System.Diagnostics.Stopwatch.StartNew();

            // Der gesamte Baum in einem Rundlauf: GetFolders fragt den Namensraum
            // mit Platzhalter ab (LIST "" "*") und liefert alle Ebenen auf einmal.
            //
            // Vorher stieg der Code Ordner für Ordner selbst ab — bei 179 Ordnern
            // ebenso viele Rundläufe, gemessen knapp vier Sekunden. Dieselbe
            // Rekursion war auch die Ursache dafür, dass Ordner doppelt in der
            // Liste standen: der Posteingang wurde auf zwei Wegen erfasst.
            //
            // LIST-STATUS (RFC 5819) bringt die Ungelesen-Zähler gleich mit,
            // sonst folgt je Ordner ein eigener STATUS.
            var batched = client.Capabilities.HasFlag(ImapCapabilities.ListStatus);

            var listed = await client.GetFoldersAsync(
                client.PersonalNamespaces[0],
                batched ? StatusItems.Unread : StatusItems.None,
                subscribedOnly: false, ct);

            // Der Posteingang steht immer oben und ist bei manchen Servern nicht
            // Teil des Namensraums.
            var flat = new List<IMailFolder> { client.Inbox };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { client.Inbox.FullName };

            foreach (var f in listed)
                if (seen.Add(f.FullName)) flat.Add(f);

            var infos = new List<FolderInfo>(flat.Count);
            foreach (var f in flat)
            {
                var selectable = !f.Attributes.HasFlag(FolderAttributes.NoSelect);
                if (selectable && !batched) await SafeStatusAsync(f, ct);

                infos.Add(new FolderInfo(
                    f.FullName, f.Name, f.DirectorySeparator,
                    selectable ? Math.Max(f.Unread, 0) : 0, selectable));
            }

            AppLog.Info($"Ordnerliste: {infos.Count} Ordner in {folderWatch.ElapsedMilliseconds} ms "
                        + (batched ? "(LIST-STATUS)" : "(STATUS je Ordner)"));

            return BuildTree(infos, _account.FolderOrder);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Baut aus der flachen Serverliste den Ordnerbaum. Bewusst frei von MailKit-Typen,
    /// damit die Hierarchie-Logik ohne Server prüfbar ist.
    /// </summary>
    public static List<FolderNode> BuildTree(
        IReadOnlyList<FolderInfo> items, IReadOnlyList<string>? order = null)
    {
        var byFullName = new Dictionary<string, FolderNode>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<FolderNode>();

        // Erst alle Knoten anlegen, dann einhängen. In einem Durchgang hinge das
        // Ergebnis an der Reihenfolge der Serverantwort: käme ein Unterordner vor
        // seinem Elternordner, stünde er fälschlich auf oberster Ebene. Die frühere
        // Rekursion lieferte Eltern immer zuerst, eine LIST-Antwort mit Platzhalter
        // sichert das nicht zu.
        foreach (var f in items)
        {
            // Doppelte Namen zusammenführen statt zweimal anlegen – sonst steht
            // derselbe Ordner mehrfach im Baum.
            if (byFullName.ContainsKey(f.FullName)) continue;

            byFullName[f.FullName] = new FolderNode
            {
                FullName = f.FullName,
                Name = f.Name,
                Unread = f.Unread,
                IsSelectable = f.IsSelectable,
            };
        }

        var attached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var f in items)
        {
            if (!byFullName.TryGetValue(f.FullName, out var node)) continue;
            if (!attached.Add(f.FullName)) continue;

            var idx = f.Delimiter == '\0' ? -1 : f.FullName.LastIndexOf(f.Delimiter);
            if (idx > 0 && byFullName.TryGetValue(f.FullName[..idx], out var parent)
                && !ReferenceEquals(parent, node))
            {
                parent.Children.Add(node);
            }
            else roots.Add(node);
        }

        SortChildren(roots);

        // Reihenfolge: Inbox immer zuerst, dann die eigene Sortierung des Kontos,
        // danach alles Übrige alphabetisch.
        int RankOf(FolderNode n)
        {
            if (order is null) return int.MaxValue;
            var i = order.ToList().FindIndex(
                x => x.Equals(n.FullName, StringComparison.OrdinalIgnoreCase));
            return i < 0 ? int.MaxValue : i;
        }

        return roots
            .OrderByDescending(n => n.FullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase))
            .ThenBy(RankOf)
            .ThenBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static void SortChildren(IEnumerable<FolderNode> nodes)
    {
        foreach (var n in nodes)
        {
            var sorted = n.Children
                .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            n.Children.Clear();
            foreach (var c in sorted) n.Children.Add(c);

            SortChildren(sorted);
        }
    }

    /// <summary>
    /// Manche Server verweigern STATUS auf einzelnen Ordnern – dann eben ohne
    /// Zähler, statt die ganze Ordnerliste daran scheitern zu lassen.
    /// </summary>
    private static async Task SafeStatusAsync(IMailFolder folder, CancellationToken ct)
    {
        try { await folder.StatusAsync(StatusItems.Unread, ct); }
        catch (ImapCommandException) { }
    }

    /// <summary>
    /// Der zuletzt bekannte Stand, ohne den Server zu fragen. Damit lässt sich
    /// die Liste sofort zeichnen, während der Abgleich noch läuft — ein
    /// Ordnerwechsel muss nicht auf das Netz warten.
    /// Leer, wenn der Ordner noch nie geöffnet wurde.
    /// </summary>
    public IReadOnlyList<MailSummary> CachedMessages(
        string folderName, int offset = 0, int take = 50)
        => _cache is null
            ? []
            : Stamp([.. _cache.LoadSummaries(_account.Id, folderName, take, offset)], folderName);

    /// <summary>Neueste <paramref name="take"/> Mails ab Offset – Basis für "Mehr laden".</summary>
    public async Task<List<MailSummary>> GetMessagesAsync(
        string folderName, int offset = 0, int take = 50, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var client = await ConnectAsync(ct);
            var folder = await client.GetFolderAsync(folderName, ct);
            await folder.OpenAsync(FolderAccess.ReadOnly, ct);

            IsOffline = false;

            int count = folder.Count;
            if (count == 0) return new List<MailSummary>();

            // Von hinten (= neueste) her ein Fenster schneiden.
            int end = count - 1 - offset;
            if (end < 0) return new List<MailSummary>();
            int start = Math.Max(0, end - take + 1);

            var items = await folder.FetchAsync(start, end,
                MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope |
                MessageSummaryItems.Flags | MessageSummaryItems.BodyStructure |
                MessageSummaryItems.References, CategoryHeaders, ct);

            var list = items
                .OrderByDescending(m => m.Envelope?.Date ?? DateTimeOffset.MinValue)
                .Select(ToSummary)
                .ToList();

            _cache?.StoreSummaries(_account.Id, folderName, folder.UidValidity, list);
            return Stamp(list, folderName);
        }
        catch (Exception ex) when (_cache is not null && IsConnectionProblem(ex, ct))
        {
            // Kein Netz: den letzten bekannten Stand zeigen, statt eine leere
            // Liste oder einen Fehler.
            var cached = _cache.LoadSummaries(_account.Id, folderName, take, offset);
            if (cached.Count == 0) throw;

            IsOffline = true;
            AppLog.Info($"Offline ({_account.Email}, {folderName}): {cached.Count} Nachrichten "
                        + $"aus dem Zwischenspeicher. Ursache: {ex.GetType().Name} – {ex.Message}");
            return Stamp([.. cached], folderName);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Nur diese Header mitholen, nicht den kompletten Satz – sie genügen für die
    /// Kategorisierung und kosten kaum Bandbreite.
    /// </summary>
    private static readonly HashSet<HeaderId> CategoryHeaders =
    [
        HeaderId.ListUnsubscribe,
        HeaderId.Precedence,
        HeaderId.AutoSubmitted,
    ];

    /// <summary>
    /// Herkunft auf jede Zeile schreiben. An einer Stelle, damit keine Liste
    /// ohne sie in die Anzeige gelangt — im gemeinsamen Posteingang wären das
    /// Nachrichten, für die keine Aktion mehr zuzuordnen wäre.
    /// </summary>
    private List<MailSummary> Stamp(List<MailSummary> list, string folderName)
    {
        foreach (var m in list)
        {
            m.AccountId = _account.Id;
            m.Folder = folderName;
        }
        return list;
    }

    private static MailSummary ToSummary(IMessageSummary m)
    {
        var from = m.Envelope?.From.Mailboxes.FirstOrDefault();
        return new MailSummary
        {
            Uid = m.UniqueId.Id,
            Subject = string.IsNullOrWhiteSpace(m.Envelope?.Subject) ? "(kein Betreff)" : m.Envelope!.Subject,
            From = from?.Name ?? from?.Address ?? "",
            FromAddress = from?.Address ?? "",
            Date = m.Envelope?.Date ?? DateTimeOffset.MinValue,
            Seen = m.Flags?.HasFlag(MessageFlags.Seen) ?? false,
            Flagged = m.Flags?.HasFlag(MessageFlags.Flagged) ?? false,
            Answered = m.Flags?.HasFlag(MessageFlags.Answered) ?? false,
            HasAttachments = m.Attachments.Any(),
            Keywords = m.Keywords is null
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(m.Keywords, StringComparer.OrdinalIgnoreCase),
            MessageId = m.Envelope?.MessageId,
            ThreadReferences = CollectReferences(m),
            ListUnsubscribe = Header(m, "List-Unsubscribe"),
            Precedence = Header(m, "Precedence"),
            AutoSubmitted = Header(m, "Auto-Submitted"),
        };
    }

    private static string? Header(IMessageSummary m, string name)
        => m.Headers?[name];

    /// <summary>In-Reply-To und References zusammenführen, ohne Duplikate.</summary>
    private static List<string> CollectReferences(IMessageSummary m)
    {
        var result = new List<string>();

        if (!string.IsNullOrWhiteSpace(m.Envelope?.InReplyTo))
            result.Add(m.Envelope.InReplyTo);

        if (m.References is not null)
            foreach (var r in m.References)
                if (!result.Contains(r, StringComparer.OrdinalIgnoreCase)) result.Add(r);

        return result;
    }

    /// <summary>
    /// IMAP SEARCH über Betreff/Absender/Text; <paramref name="unansweredOnly"/>
    /// schränkt zusätzlich auf Nachrichten ohne \Answered ein. Leerer Text mit
    /// gesetztem Filter listet alle unbeantworteten.
    /// </summary>
    public async Task<List<MailSummary>> SearchAsync(
        string folderName, string query, bool unansweredOnly = false,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var client = await ConnectAsync(ct);
            var folder = await client.GetFolderAsync(folderName, ct);
            await folder.OpenAsync(FolderAccess.ReadOnly, ct);

            SearchQuery q = string.IsNullOrWhiteSpace(query)
                ? SearchQuery.All
                : SearchQuery.SubjectContains(query)
                    .Or(SearchQuery.FromContains(query))
                    .Or(SearchQuery.BodyContains(query));

            if (unansweredOnly) q = q.And(SearchQuery.NotAnswered);

            var uids = await folder.SearchAsync(q, ct);
            if (uids.Count == 0) return new List<MailSummary>();

            var items = await folder.FetchAsync(uids.TakeLast(100).ToList(),
                MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope |
                MessageSummaryItems.Flags | MessageSummaryItems.BodyStructure |
                MessageSummaryItems.References, CategoryHeaders, ct);

            return Stamp([.. items
                .OrderByDescending(m => m.Envelope?.Date ?? DateTimeOffset.MinValue)
                .Select(ToSummary)], folderName);
        }
        finally { _gate.Release(); }
    }

    public async Task<MailBody> GetBodyAsync(string folderName, uint uid, CancellationToken ct = default)
    {
        // Eine Nachricht ändert sich nicht mehr. Liegt sie vollständig vor,
        // wäre ein erneutes Holen reine Wartezeit — und beim zweiten Blick auf
        // dieselbe Mail war genau das bisher der Fall.
        if (_cache is not null && _cache.CanServeBody(_account.Id, folderName, uid)
            && _cache.LoadBody(_account.Id, folderName, uid) is { } stored)
        {
            return stored;
        }

        await _gate.WaitAsync(ct);
        try
        {
            var client = await ConnectAsync(ct);
            var folder = await client.GetFolderAsync(folderName, ct);
            await folder.OpenAsync(FolderAccess.ReadOnly, ct);

            var msg = await folder.GetMessageAsync(new UniqueId(uid), ct);

            // PGP zuerst: alles Weitere (Text, Anhänge, Einladung) soll sich auf
            // den entschlüsselten Inhalt beziehen, nicht auf die Hülle.
            var pgp = Pgp.PgpInfo.None;
            if (_pgp is not null && Pgp.PgpService.Classify(msg.Body) != Pgp.PgpKind.None)
            {
                var opened = _pgp.OpenMessage(msg, ct);
                pgp = opened.Info;
                if (opened.Content is not null) msg.Body = opened.Content;
            }

            var attachments = new List<AttachmentInfo>();
            int i = 0;
            foreach (var part in msg.Attachments.OfType<MimePart>())
            {
                attachments.Add(new AttachmentInfo
                {
                    FileName = part.FileName ?? $"anhang-{i + 1}",
                    MimeType = part.ContentType.MimeType,
                    Size = part.Content?.Stream?.Length ?? 0,
                    Index = i,
                });
                i++;
            }

            var invitation = InvitationReader.Find(msg);
            IsOffline = false;

            var body = new MailBody
            {
                Uid = uid,
                Invitation = invitation,
                Pgp = pgp,
                Authentication = MailAuthentication.Analyze(msg),
                FromAddress = msg.From.Mailboxes.FirstOrDefault()?.Address ?? "",
                MessageId = msg.MessageId,
                References = msg.References?.ToList() ?? [],
                Subject = msg.Subject ?? "(kein Betreff)",
                From = msg.From.ToString(),
                To = msg.To.ToString(),
                ReplyTo = msg.ReplyTo.ToString(),
                Date = msg.Date,
                Html = msg.HtmlBody,
                Text = msg.TextBody ?? "",
                Attachments = attachments,
            };

            _cache?.StoreBody(_account.Id, folderName, body);
            return body;
        }
        catch (Exception ex) when (_cache is not null && IsConnectionProblem(ex, ct))
        {
            // Ohne Netz die gespeicherte Fassung zeigen. Anhänge fehlen darin —
            // die liegen bewusst nicht im Zwischenspeicher.
            var cached = _cache.LoadBody(_account.Id, folderName, uid);
            if (cached is null) throw;

            IsOffline = true;
            return cached;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Anhang auf die Platte schreiben ("Speichern unter…").</summary>
    public async Task SaveAttachmentAsync(
        string folderName, uint uid, int index, string targetPath, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var client = await ConnectAsync(ct);
            var folder = await client.GetFolderAsync(folderName, ct);
            await folder.OpenAsync(FolderAccess.ReadOnly, ct);

            var msg = await folder.GetMessageAsync(new UniqueId(uid), ct);
            var part = msg.Attachments.OfType<MimePart>().ElementAtOrDefault(index)
                       ?? throw new InvalidOperationException("Anhang nicht gefunden.");
            var content = part.Content
                          ?? throw new InvalidOperationException("Anhang hat keinen Inhalt.");

            await using var fs = File.Create(targetPath);
            await content.DecodeToAsync(fs, ct);
        }
        finally { _gate.Release(); }
    }

    public Task SetSeenAsync(string folderName, uint uid, bool seen, CancellationToken ct = default)
        => SetSeenAsync(folderName, [uid], seen, ct);

    /// <summary>
    /// Alle UIDs in einem Rutsch: der Ordner wird einmal geöffnet und ein
    /// STORE-Kommando gesendet, nicht eines je Nachricht.
    /// </summary>
    public async Task SetSeenAsync(
        string folderName, IReadOnlyCollection<uint> uids, bool seen, CancellationToken ct = default)
    {
        if (uids.Count == 0) return;

        await _gate.WaitAsync(ct);
        try
        {
            var folder = await OpenAsync(folderName, FolderAccess.ReadWrite, ct);
            var ids = ToUids(uids);
            if (seen) await folder.AddFlagsAsync(ids, MessageFlags.Seen, true, ct);
            else await folder.RemoveFlagsAsync(ids, MessageFlags.Seen, true, ct);
        }
        finally { _gate.Release(); }
    }

    public Task SetFlaggedAsync(string folderName, uint uid, bool flagged, CancellationToken ct = default)
        => SetFlaggedAsync(folderName, [uid], flagged, ct);

    public async Task SetFlaggedAsync(
        string folderName, IReadOnlyCollection<uint> uids, bool flagged, CancellationToken ct = default)
    {
        if (uids.Count == 0) return;

        await _gate.WaitAsync(ct);
        try
        {
            var folder = await OpenAsync(folderName, FolderAccess.ReadWrite, ct);
            var ids = ToUids(uids);
            if (flagged) await folder.AddFlagsAsync(ids, MessageFlags.Flagged, true, ct);
            else await folder.RemoveFlagsAsync(ids, MessageFlags.Flagged, true, ct);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Setzt oder entfernt ein IMAP-Keyword (Etikett) auf allen UIDs in einem Kommando.
    /// Voraussetzung ist, dass der Server eigene Keywords erlaubt (PERMANENTFLAGS \*) –
    /// Dovecot tut das.
    /// </summary>
    public async Task SetKeywordAsync(
        string folderName, IReadOnlyCollection<uint> uids, string keyword, bool set,
        CancellationToken ct = default)
    {
        if (uids.Count == 0) return;

        await _gate.WaitAsync(ct);
        try
        {
            var folder = await OpenAsync(folderName, FolderAccess.ReadWrite, ct);
            var ids = ToUids(uids);
            var kw = new HashSet<string> { keyword };
            if (set) await folder.AddFlagsAsync(ids, MessageFlags.None, kw, true, ct);
            else await folder.RemoveFlagsAsync(ids, MessageFlags.None, kw, true, ct);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Nach dem Versand einer Antwort: \Answered am Original setzen.</summary>
    public async Task SetAnsweredAsync(string folderName, uint uid, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var folder = await OpenAsync(folderName, FolderAccess.ReadWrite, ct);
            await folder.AddFlagsAsync([new UniqueId(uid)], MessageFlags.Answered, true, ct);
        }
        finally { _gate.Release(); }
    }

    // ---- Verschieben / Archivieren -----------------------------------------

    /// <summary>
    /// Verschiebt in einen beliebigen Ordner. Liefert die neuen UIDs im Ziel,
    /// sofern der Server sie meldet (UIDPLUS) – Grundlage für 'Rückgängig'.
    /// </summary>
    public async Task<MoveOutcome> MoveAsync(
        string folderName, IReadOnlyCollection<uint> uids, string targetFolder,
        CancellationToken ct = default)
    {
        if (uids.Count == 0) return MoveOutcome.None;

        await _gate.WaitAsync(ct);
        try
        {
            var client = await ConnectAsync(ct);
            var source = await client.GetFolderAsync(folderName, ct);
            var target = await client.GetFolderAsync(targetFolder, ct);

            if (source.FullName.Equals(target.FullName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Quell- und Zielordner sind identisch.");

            await source.OpenAsync(FolderAccess.ReadWrite, ct);

            // MailKit liefert eine UniqueIdMap (Quell- auf Ziel-UID). Ohne UIDPLUS
            // ist sie leer – dann gibt es für „Rückgängig" schlicht keine Zieladressen.
            var moved = await source.MoveToAsync(ToUids(uids), target, ct);
            return new MoveOutcome(target.FullName, moved.Values.Select(u => u.Id).ToList());
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Archiviert in den Ordner mit dem \Archive-Attribut. Kennt der Server keinen,
    /// wird auf einen Ordner namens "Archive" bzw. "Archiv" zurückgefallen.
    /// </summary>
    public async Task<MoveOutcome> ArchiveAsync(
        string folderName, IReadOnlyCollection<uint> uids, CancellationToken ct = default)
    {
        var target = await ResolveArchiveFolderAsync(ct)
            ?? throw new InvalidOperationException(
                "Kein Archivordner gefunden. Bitte einen Ordner 'Archiv' anlegen.");

        return await MoveAsync(folderName, uids, target, ct);
    }

    private Task<string?> ResolveArchiveFolderAsync(CancellationToken ct)
        => ResolveSpecialAsync(SpecialFolder.Archive, ["Archive", "Archiv"], ct);

    /// <summary>Gesendet-Ordner des Kontos; null, wenn keiner existiert.</summary>
    public Task<string?> ResolveSentFolderAsync(CancellationToken ct = default)
        => ResolveSpecialAsync(
            SpecialFolder.Sent, ["Sent", "Gesendet", "Sent Items", "Gesendete Objekte"], ct);

    /// <summary>
    /// Eine gesendete Nachricht in den Gesendet-Ordner legen.
    ///
    /// Das muss der Client selbst tun: SMTP befördert die Mail nur nach draussen,
    /// eine Kopie im Postfach entsteht dabei nicht. Nur wenige Anbieter legen sie
    /// serverseitig ab — bei Dovecot und mailcow passiert es nicht.
    ///
    /// Als bereits gelesen markiert: man hat sie ja selbst geschrieben, ein
    /// ungelesener Zähler auf dem eigenen Gesendet-Ordner wäre unsinnig.
    /// </summary>
    /// <returns>
    /// Der Ordner, in dem die Kopie liegt – oder null, wenn keiner gefunden
    /// wurde. Der Aufrufer braucht den Namen, um eine gerade geöffnete Ansicht
    /// desselben Ordners nachzuladen.
    /// </returns>
    public async Task<string?> SaveToSentAsync(MimeMessage message, CancellationToken ct = default)
    {
        var target = await ResolveSentFolderAsync(ct);
        if (target is null)
        {
            AppLog.Warn("Kein Gesendet-Ordner gefunden – Kopie wurde nicht abgelegt.");
            return null;
        }

        await _gate.WaitAsync(ct);
        try
        {
            var client = await ConnectAsync(ct);
            var folder = await client.GetFolderAsync(target, ct);
            await folder.OpenAsync(FolderAccess.ReadWrite, ct);

            await folder.AppendAsync(message, MessageFlags.Seen, ct);
        }
        finally { _gate.Release(); }

        AppLog.Info($"Kopie im Ordner '{target}' abgelegt.");
        return target;
    }

    /// <summary>
    /// Spam-Ordner des Kontos; null, wenn keiner existiert. Bei mailcow trainiert
    /// das Verschieben dorthin rspamd (Junk-Ordner wird als Spam gelernt).
    /// </summary>
    public Task<string?> ResolveJunkFolderAsync(CancellationToken ct = default)
        => ResolveSpecialAsync(SpecialFolder.Junk, ["Junk", "Spam"], ct);

    /// <summary>Als Spam einstufen = in den Junk-Ordner verschieben.</summary>
    public async Task<MoveOutcome> MarkSpamAsync(
        string folderName, IReadOnlyCollection<uint> uids, CancellationToken ct = default)
    {
        var junk = await ResolveJunkFolderAsync(ct)
            ?? throw new InvalidOperationException("Kein Spam-Ordner gefunden.");
        return await MoveAsync(folderName, uids, junk, ct);
    }

    /// <summary>'Kein Spam': aus dem Junk-Ordner zurück in den Posteingang.</summary>
    public async Task<MoveOutcome> MarkHamAsync(
        string junkFolder, IReadOnlyCollection<uint> uids, CancellationToken ct = default)
        => await MoveAsync(junkFolder, uids, "INBOX", ct);

    private async Task<string?> ResolveSpecialAsync(
        SpecialFolder kind, string[] fallbacks, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var client = await ConnectAsync(ct);

            var special = client.GetFolder(kind);
            if (special is not null) return special.FullName;

            foreach (var name in fallbacks)
            {
                try
                {
                    var f = await client.GetFolderAsync(name, ct);
                    return f.FullName;
                }
                catch (FolderNotFoundException) { /* nächster Kandidat */ }
            }
            return null;
        }
        finally { _gate.Release(); }
    }

    public Task DeleteAsync(string folderName, uint uid, CancellationToken ct = default)
        => DeleteAsync(folderName, [uid], ct);

    /// <summary>
    /// Löschen = Move in den Papierkorb, mit \Deleted als Fallback.
    /// Liefert den Zielordner, oder null bei endgültigem Löschen (Expunge) –
    /// nur im ersten Fall ist 'Rückgängig' überhaupt möglich.
    /// </summary>
    public async Task<MoveOutcome> DeleteAsync(
        string folderName, IReadOnlyCollection<uint> uids, CancellationToken ct = default)
    {
        if (uids.Count == 0) return MoveOutcome.None;

        await _gate.WaitAsync(ct);
        try
        {
            var client = await ConnectAsync(ct);
            var folder = await client.GetFolderAsync(folderName, ct);
            await folder.OpenAsync(FolderAccess.ReadWrite, ct);
            var ids = ToUids(uids);

            var trash = client.GetFolder(SpecialFolder.Trash);
            if (trash != null && !trash.FullName.Equals(folder.FullName, StringComparison.OrdinalIgnoreCase))
            {
                var moved = await folder.MoveToAsync(ids, trash, ct);
                return new MoveOutcome(trash.FullName, moved.Values.Select(u => u.Id).ToList());
            }

            // Bereits im Papierkorb (oder Server kennt keinen): endgültig entfernen.
            // Danach gibt es nichts mehr zurückzuholen – MoveOutcome.None sagt das aus.
            await folder.AddFlagsAsync(ids, MessageFlags.Deleted, true, ct);
            await folder.ExpungeAsync(ct);
            return MoveOutcome.None;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Rohform der Nachricht (Header + Body) für die Quelltext-Ansicht.</summary>
    public async Task<string> GetRawMessageAsync(
        string folderName, uint uid, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var folder = await OpenAsync(folderName, FolderAccess.ReadOnly, ct);
            var msg = await folder.GetMessageAsync(new UniqueId(uid), ct);

            using var ms = new MemoryStream();
            await msg.WriteToAsync(ms, ct);
            ms.Position = 0;
            using var reader = new StreamReader(ms);
            return await reader.ReadToEndAsync(ct);
        }
        finally { _gate.Release(); }
    }

    // ---- Entwürfe ----------------------------------------------------------

    /// <summary>
    /// Legt einen Entwurf im Drafts-Ordner ab und ersetzt dabei den vorherigen
    /// Stand (<paramref name="replaceUid"/>). Liefert die neue UID – oder null,
    /// wenn der Server sie nicht meldet (kein UIDPLUS); dann kann der nächste
    /// Auto-Save den alten Stand nicht ersetzen und es bleiben Zwischenstände liegen.
    /// </summary>
    public async Task<uint?> SaveDraftAsync(
        MimeMessage message, uint? replaceUid, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var client = await ConnectAsync(ct);
            var drafts = ResolveDraftsFolder(client)
                ?? throw new InvalidOperationException(
                    "Kein Entwürfe-Ordner gefunden. Bitte einen Ordner 'Drafts' anlegen.");

            await drafts.OpenAsync(FolderAccess.ReadWrite, ct);

            // \Seen dazu, sonst zählt jeder Auto-Save als ungelesene Mail hoch.
            var uid = await drafts.AppendAsync(
                message, MessageFlags.Draft | MessageFlags.Seen, ct);

            if (replaceUid is { } old)
            {
                await drafts.AddFlagsAsync([new UniqueId(old)], MessageFlags.Deleted, true, ct);
                await drafts.ExpungeAsync(ct);
            }

            return uid?.Id;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Lädt einen Entwurf zum Weiterschreiben. Anhänge landen in einem temporären
    /// Ordner, weil der Composer mit Dateipfaden arbeitet.
    /// </summary>
    public async Task<DraftContent> GetDraftAsync(
        string folderName, uint uid, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var folder = await OpenAsync(folderName, FolderAccess.ReadOnly, ct);
            var msg = await folder.GetMessageAsync(new UniqueId(uid), ct);

            var paths = new List<string>();
            var parts = msg.Attachments.OfType<MimePart>().ToList();
            if (parts.Count > 0)
            {
                var dir = Path.Combine(
                    Path.GetTempPath(), "NMailClient", "drafts", uid.ToString());
                Directory.CreateDirectory(dir);

                int i = 0;
                foreach (var part in parts)
                {
                    var name = part.FileName ?? $"anhang-{++i}";
                    // Dateinamen aus fremden Mails können Pfadanteile enthalten.
                    var safe = Path.GetFileName(name);
                    if (string.IsNullOrWhiteSpace(safe)) safe = $"anhang-{i}";

                    var target = Path.Combine(dir, safe);
                    await using var fs = File.Create(target);
                    if (part.Content is not null) await part.Content.DecodeToAsync(fs, ct);
                    paths.Add(target);
                }
            }

            return new DraftContent(
                uid,
                string.Join(", ", msg.To.Mailboxes.Select(m => m.ToString())),
                string.Join(", ", msg.Cc.Mailboxes.Select(m => m.ToString())),
                msg.Subject ?? "",
                msg.TextBody ?? "",
                msg.InReplyTo,
                msg.References?.ToList() ?? [],
                paths);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Entwurf nach erfolgreichem Versand entfernen.</summary>
    public async Task DeleteDraftAsync(uint uid, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var client = await ConnectAsync(ct);
            var drafts = ResolveDraftsFolder(client);
            if (drafts is null) return; // nichts zu löschen

            await drafts.OpenAsync(FolderAccess.ReadWrite, ct);
            await drafts.AddFlagsAsync([new UniqueId(uid)], MessageFlags.Deleted, true, ct);
            await drafts.ExpungeAsync(ct);
        }
        finally { _gate.Release(); }
    }

    private static IMailFolder? ResolveDraftsFolder(ImapClient client)
    {
        var special = client.GetFolder(SpecialFolder.Drafts);
        if (special is not null) return special;

        foreach (var name in new[] { "Drafts", "Entwürfe" })
        {
            try { return client.GetFolder(name); }
            catch (FolderNotFoundException) { /* nächster Kandidat */ }
        }
        return null;
    }

    // ---- Ordnerverwaltung --------------------------------------------------

    /// <summary>Legt einen Unterordner an; <paramref name="parentFullName"/> leer = Wurzel.</summary>
    public async Task<string> CreateFolderAsync(
        string parentFullName, string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Ordnername darf nicht leer sein.", nameof(name));

        await _gate.WaitAsync(ct);
        try
        {
            var client = await ConnectAsync(ct);

            var parent = string.IsNullOrEmpty(parentFullName)
                ? client.GetFolder(client.PersonalNamespaces[0])
                : await client.GetFolderAsync(parentFullName, ct);

            if (parent is null)
                throw new InvalidOperationException(
                    "Übergeordneter Ordner nicht gefunden – Ordnerliste neu laden.");

            // Der Delimiter kommt vom Server; ihn im Namen zu dulden würde
            // unbeabsichtigt Unterordner erzeugen.
            var delim = parent.DirectorySeparator;
            if (delim != '\0' && name.Contains(delim))
                throw new ArgumentException(
                    $"Der Ordnername darf das Trennzeichen '{delim}' nicht enthalten.", nameof(name));

            var created = await parent.CreateAsync(name.Trim(), isMessageFolder: true, ct);
            return created?.FullName
                   ?? throw new InvalidOperationException("Der Server hat den Ordner nicht angelegt.");
        }
        finally { _gate.Release(); }
    }

    public async Task<string> RenameFolderAsync(
        string fullName, string newName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(newName))
            throw new ArgumentException("Ordnername darf nicht leer sein.", nameof(newName));

        await _gate.WaitAsync(ct);
        try
        {
            var client = await ConnectAsync(ct);
            var folder = await client.GetFolderAsync(fullName, ct);

            if (folder.Attributes.HasFlag(FolderAttributes.Inbox))
                throw new InvalidOperationException("Der Posteingang kann nicht umbenannt werden.");

            var parent = folder.ParentFolder ?? client.GetFolder(client.PersonalNamespaces[0]);
            await folder.RenameAsync(parent, newName.Trim(), ct);
            return folder.FullName;
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteFolderAsync(string fullName, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var client = await ConnectAsync(ct);
            var folder = await client.GetFolderAsync(fullName, ct);

            if (folder.Attributes.HasFlag(FolderAttributes.Inbox))
                throw new InvalidOperationException("Der Posteingang kann nicht gelöscht werden.");

            // Unterordner würden serverseitig verwaisen – erst prüfen, dann löschen.
            var children = await folder.GetSubfoldersAsync(false, ct);
            if (children.Count > 0)
                throw new InvalidOperationException(
                    $"Der Ordner enthält {children.Count} Unterordner. Diese zuerst entfernen.");

            await folder.DeleteAsync(ct);
        }
        finally { _gate.Release(); }
    }

    // ---- gemeinsame Helfer --------------------------------------------------

    private async Task<IMailFolder> OpenAsync(
        string folderName, FolderAccess access, CancellationToken ct)
    {
        var client = await ConnectAsync(ct);
        var folder = await client.GetFolderAsync(folderName, ct);
        await folder.OpenAsync(access, ct);
        return folder;
    }

    private static IList<UniqueId> ToUids(IReadOnlyCollection<uint> uids)
        => uids.Select(u => new UniqueId(u)).ToList();

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_client is { IsConnected: true })
                await _client.DisconnectAsync(true);
            _client?.Dispose();
            _client = null;
        }
        catch (Exception ex) when (ex is IOException or ImapProtocolException)
        {
            // Beim Beenden ist ein sauberes LOGOUT nice-to-have, kein Muss.
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
