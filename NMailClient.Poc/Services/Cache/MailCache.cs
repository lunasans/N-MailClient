using System.IO;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using NMailClient.Poc.Models;

namespace NMailClient.Poc.Services.Cache;

/// <summary>Zahlen für die Anzeige in den Einstellungen.</summary>
public record CacheStats(int Folders, int Messages, int Bodies, long SizeBytes)
{
    public string SizeDisplay => SizeBytes switch
    {
        < 1024 => $"{SizeBytes} B",
        < 1024 * 1024 => $"{SizeBytes / 1024.0:0.#} KB",
        _ => $"{SizeBytes / 1024.0 / 1024.0:0.#} MB",
    };

    public string Display => $"{Messages} Nachrichten in {Folders} Ordnern, "
                             + $"{Bodies} davon vollständig · {SizeDisplay}";
}

/// <summary>
/// Lokaler Zwischenspeicher für Nachrichtenlisten und -körper.
///
/// Zweck ist doppelt: ohne Verbindung bleibt die Post lesbar, und die Suche
/// läuft über FTS5 im Haus statt als IMAP-Rundlauf.
///
/// Der wichtigste Punkt im ganzen Entwurf ist <c>UIDVALIDITY</c>: ein Server
/// darf UIDs neu vergeben. Passiert das unbemerkt, zeigt jede gespeicherte Zeile
/// auf eine andere Nachricht — schlimmer als gar kein Cache. Deshalb wird der
/// Ordner beim kleinsten Zweifel geleert.
/// </summary>
public sealed class MailCache : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly Lock _gate = new();

    public string Path { get; }

    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NMailClient.Poc", "cache.db");

    public MailCache(string? path = null)
    {
        Path = path ?? DefaultPath;

        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        _db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());

        _db.Open();

        // WAL: Lesen blockiert nicht, während im Hintergrund geschrieben wird.
        Execute("PRAGMA journal_mode = WAL;");
        Execute("PRAGMA synchronous = NORMAL;");

        CacheSchema.Apply(_db);
    }

    private void Execute(string sql)
    {
        using var command = _db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    // ---- Ordnerstand -------------------------------------------------------

    /// <summary>Der Stand, auf den sich die gespeicherten UIDs beziehen.</summary>
    public uint? UidValidity(string accountId, string folder)
    {
        lock (_gate)
        {
            return _db.QueryFirstOrDefault<long?>(
                "SELECT uid_validity FROM folders WHERE account_id = @a AND folder = @f",
                new { a = accountId, f = folder }) is { } value ? (uint)value : null;
        }
    }

    public DateTimeOffset? LastSync(string accountId, string folder)
    {
        lock (_gate)
        {
            var value = _db.QueryFirstOrDefault<long?>(
                "SELECT synced_at FROM folders WHERE account_id = @a AND folder = @f",
                new { a = accountId, f = folder });

            return value is null ? null : DateTimeOffset.FromUnixTimeSeconds(value.Value);
        }
    }

    /// <summary>
    /// Stimmt der Stand nicht mehr, wird der Ordner geleert. Gibt zurück, ob
    /// verworfen wurde — der Aufrufer soll das im Protokoll sehen.
    /// </summary>
    private bool EnsureValidity(string accountId, string folder, uint uidValidity)
    {
        var known = _db.QueryFirstOrDefault<long?>(
            "SELECT uid_validity FROM folders WHERE account_id = @a AND folder = @f",
            new { a = accountId, f = folder });

        var discarded = known is not null && (uint)known.Value != uidValidity;

        if (discarded)
        {
            AppLog.Info($"UIDVALIDITY von {folder} hat sich geändert – Cache wird verworfen.");
            ClearFolderRows(accountId, folder);
        }

        _db.Execute("""
            INSERT INTO folders (account_id, folder, uid_validity, synced_at)
            VALUES (@a, @f, @v, @t)
            ON CONFLICT (account_id, folder)
            DO UPDATE SET uid_validity = @v, synced_at = @t
            """,
            new { a = accountId, f = folder, v = (long)uidValidity, t = Now });

        return discarded;
    }

    private void ClearFolderRows(string accountId, string folder)
    {
        var args = new { a = accountId, f = folder };
        _db.Execute("DELETE FROM messages WHERE account_id = @a AND folder = @f", args);
        _db.Execute("DELETE FROM bodies   WHERE account_id = @a AND folder = @f", args);
        _db.Execute("DELETE FROM messages_fts WHERE account_id = @a AND folder = @f", args);
    }

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // ---- Zusammenfassungen -------------------------------------------------

    /// <summary>
    /// Eine Seite Nachrichtenköpfe ablegen. Vorhandene Zeilen werden ersetzt,
    /// damit sich geänderte Flags niederschlagen.
    /// </summary>
    public void StoreSummaries(
        string accountId, string folder, uint uidValidity, IEnumerable<MailSummary> messages)
    {
        lock (_gate)
        {
            using var transaction = _db.BeginTransaction();
            EnsureValidity(accountId, folder, uidValidity);

            foreach (var m in messages)
            {
                _db.Execute("""
                    INSERT INTO messages
                        (account_id, folder, uid, subject, sender, sender_addr, date_utc,
                         seen, flagged, answered, attachments, message_id, refs, keywords,
                         list_unsub, precedence, auto_sub)
                    VALUES (@a, @f, @uid, @subject, @sender, @addr, @date,
                            @seen, @flagged, @answered, @att, @mid, @refs, @keywords,
                            @unsub, @prec, @auto)
                    ON CONFLICT (account_id, folder, uid) DO UPDATE SET
                        subject = @subject, sender = @sender, sender_addr = @addr,
                        date_utc = @date, seen = @seen, flagged = @flagged, answered = @answered,
                        attachments = @att, message_id = @mid, refs = @refs,
                        keywords = @keywords, list_unsub = @unsub,
                        precedence = @prec, auto_sub = @auto
                    """,
                    new
                    {
                        a = accountId, f = folder, uid = (long)m.Uid,
                        subject = m.Subject, sender = m.From, addr = m.FromAddress,
                        date = m.Date.ToUnixTimeSeconds(),
                        seen = m.Seen ? 1 : 0, flagged = m.Flagged ? 1 : 0,
                        answered = m.Answered ? 1 : 0,
                        att = m.HasAttachments ? 1 : 0,
                        mid = m.MessageId,
                        refs = string.Join('\n', m.ThreadReferences),
                        keywords = string.Join('\n', m.Keywords),
                        unsub = m.ListUnsubscribe, prec = m.Precedence, auto = m.AutoSubmitted,
                    }, transaction);

                IndexHeaders(accountId, folder, m, transaction);
            }

            transaction.Commit();
        }
    }

    /// <summary>Die gespeicherte Seite, neueste zuerst – wie die Liste sie zeigt.</summary>
    public IReadOnlyList<MailSummary> LoadSummaries(
        string accountId, string folder, int limit, int offset = 0)
    {
        lock (_gate)
        {
            return _db.Query<Row>("""
                SELECT * FROM messages
                WHERE account_id = @a AND folder = @f
                ORDER BY date_utc DESC
                LIMIT @limit OFFSET @offset
                """,
                new { a = accountId, f = folder, limit, offset })
                .Select(ToSummary)
                .ToList();
        }
    }

    public int CountMessages(string accountId, string folder)
    {
        lock (_gate)
        {
            return _db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM messages WHERE account_id = @a AND folder = @f",
                new { a = accountId, f = folder });
        }
    }

    /// <summary>Nach dem Löschen oder Verschieben auf dem Server nachziehen.</summary>
    public void Remove(string accountId, string folder, IEnumerable<uint> uids)
    {
        var list = uids.Select(u => (long)u).ToList();
        if (list.Count == 0) return;

        lock (_gate)
        {
            using var transaction = _db.BeginTransaction();
            var args = new { a = accountId, f = folder, uids = list };

            _db.Execute("DELETE FROM messages WHERE account_id = @a AND folder = @f AND uid IN @uids", args, transaction);
            _db.Execute("DELETE FROM bodies WHERE account_id = @a AND folder = @f AND uid IN @uids", args, transaction);
            _db.Execute("DELETE FROM messages_fts WHERE account_id = @a AND folder = @f AND uid IN @uids", args, transaction);

            transaction.Commit();
        }
    }

    /// <summary>Gelesen-/Stern-Zustand nachziehen, ohne alles neu zu laden.</summary>
    public void UpdateFlags(
        string accountId, string folder, IEnumerable<uint> uids, bool? seen, bool? flagged)
    {
        var list = uids.Select(u => (long)u).ToList();
        if (list.Count == 0 || (seen is null && flagged is null)) return;

        lock (_gate)
        {
            var sets = new List<string>();
            if (seen is not null) sets.Add("seen = @seen");
            if (flagged is not null) sets.Add("flagged = @flagged");

            _db.Execute($"""
                UPDATE messages SET {string.Join(", ", sets)}
                WHERE account_id = @a AND folder = @f AND uid IN @uids
                """,
                new
                {
                    a = accountId, f = folder, uids = list,
                    seen = seen == true ? 1 : 0, flagged = flagged == true ? 1 : 0,
                });
        }
    }

    // ---- Nachrichtenkörper -------------------------------------------------

    /// <summary>
    /// Eine entschlüsselte Nachricht gehört nicht auf die Platte. Wer PGP
    /// benutzt, will gerade <b>nicht</b>, dass der Klartext irgendwo liegt —
    /// eine unverschlüsselte Datei im Benutzerprofil hebelte den Schutz aus,
    /// und der Volltextindex machte ihn zusätzlich durchsuchbar.
    /// </summary>
    private static bool MustNotBeStored(MailBody body)
        => body.Pgp.Kind == Services.Pgp.PgpKind.Encrypted;

    public void StoreBody(string accountId, string folder, MailBody body)
    {
        if (MustNotBeStored(body))
        {
            // Auch Reste aus früheren Läufen entfernen: die Nachricht könnte
            // gespeichert worden sein, bevor diese Regel bestand.
            ForgetBody(accountId, folder, body.Uid);
            return;
        }

        lock (_gate)
        {
            using var transaction = _db.BeginTransaction();

            _db.Execute("""
                INSERT INTO bodies
                    (account_id, folder, uid, body_text, body_html, recipients,
                     reply_to, attachments, auth, pgp, reusable, fetched_at)
                VALUES (@a, @f, @uid, @text, @html, @to,
                        @replyTo, @att, @auth, @pgp, @reusable, @t)
                ON CONFLICT (account_id, folder, uid) DO UPDATE SET
                    body_text = @text, body_html = @html, recipients = @to,
                    reply_to = @replyTo, attachments = @att, auth = @auth, pgp = @pgp,
                    reusable = @reusable, fetched_at = @t
                """,
                new
                {
                    a = accountId, f = folder, uid = (long)body.Uid,
                    text = body.Text, html = body.Html, to = body.To, t = Now,
                    replyTo = body.ReplyTo,
                    att = JsonSerializer.Serialize(body.Attachments),
                    auth = JsonSerializer.Serialize(body.Authentication),
                    pgp = JsonSerializer.Serialize(body.Pgp),
                    reusable = body.HasInvitation ? 0 : 1,
                }, transaction);

            // Den Volltext in den Index nachtragen, damit die Suche auch den
            // Nachrichtentext erfasst und nicht nur Betreff und Absender.
            _db.Execute(
                "DELETE FROM messages_fts WHERE account_id = @a AND folder = @f AND uid = @uid",
                new { a = accountId, f = folder, uid = (long)body.Uid }, transaction);

            _db.Execute("""
                INSERT INTO messages_fts (subject, sender, body, account_id, folder, uid)
                VALUES (@subject, @sender, @body, @a, @f, @uid)
                """,
                new
                {
                    subject = body.Subject, sender = body.From, body = body.Text,
                    a = accountId, f = folder, uid = (long)body.Uid,
                }, transaction);

            transaction.Commit();
        }
    }

    /// <summary>
    /// Eine Nachricht aus dem Zwischenspeicher entfernen – samt Volltexteintrag.
    /// </summary>
    public void ForgetBody(string accountId, string folder, uint uid)
    {
        lock (_gate)
        {
            var args = new { a = accountId, f = folder, uid = (long)uid };
            _db.Execute(
                "DELETE FROM bodies WHERE account_id = @a AND folder = @f AND uid = @uid", args);
            _db.Execute(
                "DELETE FROM messages_fts WHERE account_id = @a AND folder = @f AND uid = @uid", args);
        }
    }

    /// <summary>
    /// Darf diese Nachricht ohne Rückfrage beim Server angezeigt werden? Wahr,
    /// sobald sie vollständig gespeichert ist. Eine Nachricht ist unveränderlich,
    /// deshalb ist die gespeicherte Fassung so gut wie eine frisch geholte.
    /// </summary>
    public bool CanServeBody(string accountId, string folder, uint uid)
    {
        lock (_gate)
        {
            return _db.ExecuteScalar<int>("""
                SELECT COUNT(*) FROM bodies
                WHERE account_id = @a AND folder = @f AND uid = @uid AND reusable = 1
                """,
                new { a = accountId, f = folder, uid = (long)uid }) > 0;
        }
    }

    /// <summary>
    /// Der gespeicherte Körper samt Anhangsliste, Zustellprüfung und
    /// PGP-Befund. Der <b>Inhalt</b> der Anhänge ist nicht dabei: er
    /// vervielfachte die Datenbankgrösse und wird bei Bedarf geholt.
    /// </summary>
    public MailBody? LoadBody(string accountId, string folder, uint uid)
    {
        lock (_gate)
        {
            var row = _db.QueryFirstOrDefault<BodyRow>("""
                SELECT b.body_text, b.body_html, b.recipients, b.reply_to,
                       b.attachments, b.auth, b.pgp,
                       m.subject, m.sender, m.sender_addr, m.date_utc, m.message_id, m.refs
                FROM bodies b
                JOIN messages m ON m.account_id = b.account_id
                               AND m.folder = b.folder AND m.uid = b.uid
                WHERE b.account_id = @a AND b.folder = @f AND b.uid = @uid
                """,
                new { a = accountId, f = folder, uid = (long)uid });

            if (row is null) return null;

            return new MailBody
            {
                Uid = uid,
                Subject = row.subject ?? "",
                From = row.sender ?? "",
                FromAddress = row.sender_addr ?? "",
                To = row.recipients ?? "",
                ReplyTo = row.reply_to ?? "",
                Date = DateTimeOffset.FromUnixTimeSeconds(row.date_utc),
                Text = row.body_text ?? "",
                Html = row.body_html,
                MessageId = row.message_id,
                References = Split(row.refs),
                Attachments = Read<List<AttachmentInfo>>(row.attachments) ?? [],
                Authentication = Read<Services.MailAuthInfo>(row.auth)
                                 ?? Services.MailAuthInfo.None,
                Pgp = Read<Services.Pgp.PgpInfo>(row.pgp) ?? Services.Pgp.PgpInfo.None,
            };
        }
    }

    public bool HasBody(string accountId, string folder, uint uid)
    {
        lock (_gate)
        {
            return _db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM bodies WHERE account_id = @a AND folder = @f AND uid = @uid",
                new { a = accountId, f = folder, uid = (long)uid }) > 0;
        }
    }

    // ---- Suche -------------------------------------------------------------

    /// <summary>
    /// Volltextsuche über den Cache. <paramref name="folder"/> null durchsucht
    /// alle Ordner des Kontos.
    /// </summary>
    public IReadOnlyList<MailSummary> Search(
        string accountId, string? folder, string query, int limit = 100)
    {
        var term = FtsQuery(query);
        if (term is null) return [];

        lock (_gate)
        {
            var sql = """
                SELECT m.* FROM messages_fts f
                JOIN messages m ON m.account_id = f.account_id
                               AND m.folder = f.folder AND m.uid = f.uid
                WHERE messages_fts MATCH @term AND f.account_id = @a
                """
                + (folder is null ? "" : " AND f.folder = @f")
                + " ORDER BY m.date_utc DESC LIMIT @limit";

            return _db.Query<Row>(sql, new { term, a = accountId, f = folder, limit })
                      .Select(ToSummary)
                      .ToList();
        }
    }

    /// <summary>
    /// Eingabe in eine FTS5-Abfrage übersetzen. Die Sonderzeichen der
    /// Abfragesprache werden entschärft: was der Anwender tippt, ist ein
    /// Suchbegriff und keine Syntax — ein einzelnes Anführungszeichen soll keine
    /// Fehlermeldung erzeugen.
    /// </summary>
    public static string? FtsQuery(string input)
    {
        var words = input.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                         .Select(w => w.Trim('"'))
                         .Where(w => w.Length > 0)
                         .Select(w => "\"" + w.Replace("\"", "\"\"") + "\"*")
                         .ToList();

        return words.Count == 0 ? null : string.Join(" AND ", words);
    }

    // ---- Wartung -----------------------------------------------------------

    public CacheStats Stats()
    {
        lock (_gate)
        {
            var size = File.Exists(Path) ? new FileInfo(Path).Length : 0;

            return new CacheStats(
                _db.ExecuteScalar<int>("SELECT COUNT(*) FROM folders"),
                _db.ExecuteScalar<int>("SELECT COUNT(*) FROM messages"),
                _db.ExecuteScalar<int>("SELECT COUNT(*) FROM bodies"),
                size);
        }
    }

    /// <summary>Alles verwerfen. Verlust ist folgenlos – die Post liegt am Server.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            using var transaction = _db.BeginTransaction();
            foreach (var table in new[] { "messages_fts", "bodies", "messages", "folders" })
                _db.Execute($"DELETE FROM {table}", transaction: transaction);
            transaction.Commit();

            Execute("VACUUM;");
        }
    }

    /// <summary>Ein einzelnes Konto vergessen – beim Entfernen des Kontos.</summary>
    public void Forget(string accountId)
    {
        lock (_gate)
        {
            using var transaction = _db.BeginTransaction();
            foreach (var table in new[] { "messages_fts", "bodies", "messages", "folders" })
                _db.Execute($"DELETE FROM {table} WHERE account_id = @a",
                            new { a = accountId }, transaction);
            transaction.Commit();
        }
    }

    public void Dispose()
    {
        _db.Close();
        _db.Dispose();
        SqliteConnection.ClearPool(_db);
    }

    // ---- Abbildung ---------------------------------------------------------

    private void IndexHeaders(
        string accountId, string folder, MailSummary m, SqliteTransaction transaction)
    {
        // Ist der Körper schon indiziert, bleibt er stehen – sonst ginge der
        // Volltext beim nächsten Listenabruf wieder verloren.
        var hasBody = _db.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM bodies WHERE account_id = @a AND folder = @f AND uid = @uid",
            new { a = accountId, f = folder, uid = (long)m.Uid }, transaction) > 0;
        if (hasBody) return;

        _db.Execute("DELETE FROM messages_fts WHERE account_id = @a AND folder = @f AND uid = @uid",
            new { a = accountId, f = folder, uid = (long)m.Uid }, transaction);

        _db.Execute("""
            INSERT INTO messages_fts (subject, sender, body, account_id, folder, uid)
            VALUES (@subject, @sender, '', @a, @f, @uid)
            """,
            new
            {
                subject = m.Subject, sender = $"{m.From} {m.FromAddress}",
                a = accountId, f = folder, uid = (long)m.Uid,
            }, transaction);
    }

    private static IReadOnlyList<string> Split(string? value)
        => string.IsNullOrEmpty(value) ? [] : value.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static MailSummary ToSummary(Row r) => new()
    {
        Uid = (uint)r.uid,
        Subject = r.subject ?? "",
        From = r.sender ?? "",
        FromAddress = r.sender_addr ?? "",
        Date = DateTimeOffset.FromUnixTimeSeconds(r.date_utc),
        HasAttachments = r.attachments != 0,
        MessageId = r.message_id,
        ThreadReferences = Split(r.refs),
        ListUnsubscribe = r.list_unsub,
        Precedence = r.precedence,
        AutoSubmitted = r.auto_sub,
        Keywords = new HashSet<string>(Split(r.keywords), StringComparer.OrdinalIgnoreCase),
        Seen = r.seen != 0,
        Flagged = r.flagged != 0,
        Answered = r.answered != 0,
    };

    /// <summary>
    /// Beschädigtes JSON darf die Anzeige nicht verhindern – lieber die
    /// Nachricht ohne Beiwerk zeigen als gar nicht.
    /// </summary>
    private static T? Read<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json); }
        catch (JsonException) { return null; }
    }

    /// <summary>Rohzeile; die Feldnamen entsprechen den Spalten.</summary>
    private sealed class Row
    {
        public long uid { get; set; }
        public string? subject { get; set; }
        public string? sender { get; set; }
        public string? sender_addr { get; set; }
        public long date_utc { get; set; }
        public int seen { get; set; }
        public int flagged { get; set; }
        public int answered { get; set; }
        public int attachments { get; set; }
        public string? message_id { get; set; }
        public string? refs { get; set; }
        public string? keywords { get; set; }
        public string? list_unsub { get; set; }
        public string? precedence { get; set; }
        public string? auto_sub { get; set; }
    }

    private sealed class BodyRow
    {
        public string? body_text { get; set; }
        public string? body_html { get; set; }
        public string? recipients { get; set; }
        public string? reply_to { get; set; }
        public string? subject { get; set; }
        public string? sender { get; set; }
        public string? sender_addr { get; set; }
        public long date_utc { get; set; }
        public string? message_id { get; set; }
        public string? refs { get; set; }
        public string? attachments { get; set; }
        public string? auth { get; set; }
        public string? pgp { get; set; }
    }
}
