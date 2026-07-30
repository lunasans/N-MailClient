using Microsoft.Data.Sqlite;

namespace NMailClient.Services.Cache;

/// <summary>
/// Aufbau und Fortschreibung der Cache-Datenbank.
///
/// Getrennt vom Zugriff, damit die Migrationen an einer Stelle stehen und
/// prüfbar sind: eine Datenbank, die beim Versionswechsel still das falsche
/// Schema behält, fällt sonst erst im Betrieb auf.
/// </summary>
public static class CacheSchema
{
    /// <summary>
    /// Version des Schemas. Bei jeder Änderung erhöhen und eine Migration
    /// ergänzen — oder, solange es nur ein Cache ist, verwerfen und neu
    /// aufbauen. Verlorene Daten sind hier folgenlos, sie liegen ja auf dem
    /// Server.
    /// </summary>
    public const int Version = 4;

    public static void Apply(SqliteConnection connection)
    {
        var current = ReadVersion(connection);

        if (current > Version || (current > 0 && current < Version))
        {
            // Ein Cache ist wiederherstellbar; ein Migrationspfad wäre hier
            // Aufwand ohne Gegenwert.
            AppLog.Info($"Cache-Schema {current} passt nicht zu {Version} – wird neu aufgebaut.");
            Drop(connection);
            current = 0;
        }

        if (current == Version) return;

        Create(connection);
        Execute(connection, $"PRAGMA user_version = {Version};");
    }

    private static int ReadVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar() ?? 0);
    }

    private static void Drop(SqliteConnection connection) => Execute(connection, """
        DROP TABLE IF EXISTS messages_fts;
        DROP TABLE IF EXISTS bodies;
        DROP TABLE IF EXISTS messages;
        DROP TABLE IF EXISTS folders;
        """);

    private static void Create(SqliteConnection connection) => Execute(connection, """
        -- Je Konto und Ordner der Stand, auf den sich die UIDs beziehen.
        -- Ohne UIDVALIDITY wäre der ganze Cache wertlos: der Server darf UIDs
        -- neu vergeben, und dann zeigt jede gespeicherte Zeile auf etwas anderes.
        CREATE TABLE folders (
            account_id   TEXT NOT NULL,
            folder       TEXT NOT NULL,
            uid_validity INTEGER NOT NULL,
            synced_at    INTEGER NOT NULL,
            PRIMARY KEY (account_id, folder)
        );

        CREATE TABLE messages (
            account_id   TEXT NOT NULL,
            folder       TEXT NOT NULL,
            uid          INTEGER NOT NULL,
            subject      TEXT NOT NULL DEFAULT '',
            sender       TEXT NOT NULL DEFAULT '',
            sender_addr  TEXT NOT NULL DEFAULT '',
            date_utc     INTEGER NOT NULL,
            seen         INTEGER NOT NULL DEFAULT 0,
            flagged      INTEGER NOT NULL DEFAULT 0,
            answered     INTEGER NOT NULL DEFAULT 0,
            attachments  INTEGER NOT NULL DEFAULT 0,
            message_id   TEXT,
            refs         TEXT NOT NULL DEFAULT '',
            keywords     TEXT NOT NULL DEFAULT '',
            list_unsub   TEXT,
            precedence   TEXT,
            auto_sub     TEXT,
            PRIMARY KEY (account_id, folder, uid)
        );

        -- Für die Listenansicht wird immer nach Datum absteigend gelesen.
        CREATE INDEX ix_messages_date ON messages (account_id, folder, date_utc DESC);

        -- Anhänge, Zustellprüfung und PGP-Befund werden mitgeschrieben. Ohne sie
        -- wäre eine aus dem Zwischenspeicher gezeigte Nachricht ärmer als eine
        -- frisch geholte: die Anhangsliste fehlte, und die Merkmale zu SPF/DKIM
        -- und Signatur verschwänden — eine Nachricht sähe unbedenklicher aus,
        -- als sie ist. Der Inhalt der Anhänge bleibt draussen.
        --
        -- 'reusable' entscheidet, ob die Zeile online verwendet werden darf.
        -- Termin-Einladungen stehen auf 0: ihre Anzeige hängt an Zustandsdaten,
        -- die hier nicht liegen, also wird dafür weiterhin gefragt.
        CREATE TABLE bodies (
            account_id   TEXT NOT NULL,
            folder       TEXT NOT NULL,
            uid          INTEGER NOT NULL,
            body_text    TEXT NOT NULL DEFAULT '',
            body_html    TEXT,
            recipients   TEXT NOT NULL DEFAULT '',
            reply_to     TEXT NOT NULL DEFAULT '',
            attachments  TEXT NOT NULL DEFAULT '[]',
            auth         TEXT,
            pgp          TEXT,
            reusable     INTEGER NOT NULL DEFAULT 1,
            fetched_at   INTEGER NOT NULL,
            PRIMARY KEY (account_id, folder, uid)
        );

        -- Volltextsuche ohne Serverrundlauf. 'content' bleibt leer: der Index
        -- verwaltet seine eigenen Kopien, dafür entfällt die Pflicht, ihn bei
        -- jeder Änderung an 'messages' synchron zu halten.
        CREATE VIRTUAL TABLE messages_fts USING fts5 (
            subject, sender, body,
            account_id UNINDEXED,
            folder UNINDEXED,
            uid UNINDEXED,
            tokenize = 'unicode61 remove_diacritics 2'
        );
        """);

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
