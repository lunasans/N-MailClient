using System.IO;
using NMailClient.Models;
using NMailClient.Services;
using NMailClient.Services.Cache;
using NMailClient.Services.Pgp;
using Xunit;

namespace NMailClient.Tests;

public class MailCacheTests : IDisposable
{
    private const string Account = "konto-1";
    private const string Inbox = "INBOX";

    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"cache-test-{Guid.NewGuid():N}.db");

    private readonly MailCache _cache;

    public MailCacheTests() => _cache = new MailCache(_path);

    public void Dispose()
    {
        _cache.Dispose();
        try { File.Delete(_path); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private static MailSummary Message(uint uid, string subject = "Rechnung",
                                       string from = "Rene Neuhaus",
                                       string address = "rene@example.org",
                                       int daysAgo = 0) => new()
    {
        Uid = uid,
        Subject = subject,
        From = from,
        FromAddress = address,
        Date = DateTimeOffset.UtcNow.AddDays(-daysAgo),
        HasAttachments = uid % 2 == 0,
        MessageId = $"<{uid}@example.org>",
        ThreadReferences = ["<vorher@example.org>"],
        Keywords = new HashSet<string>(["Arbeit"], StringComparer.OrdinalIgnoreCase),
        Seen = false,
        Flagged = false,
    };

    // ---- Grundlagen --------------------------------------------------------

    [Fact]
    public void FreshCacheIsEmpty()
    {
        var stats = _cache.Stats();

        Assert.Equal(0, stats.Messages);
        Assert.Equal(0, stats.Folders);
        Assert.Empty(_cache.LoadSummaries(Account, Inbox, 50));
    }

    [Fact]
    public void OlderDatabaseIsRebuiltWithTheNewColumn()
    {
        // Bestandsdatenbanken kennen 'answered' nicht. Ohne Neuaufbau liefe
        // jedes INSERT auf einen SQL-Fehler – der Cache wäre still kaputt.
        var path = Path.Combine(Path.GetTempPath(), $"cache-alt-{Guid.NewGuid():N}.db");
        try
        {
            using (var old = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                old.Open();
                using var command = old.CreateCommand();
                command.CommandText = """
                    CREATE TABLE messages (uid INTEGER PRIMARY KEY, seen INTEGER);
                    PRAGMA user_version = 1;
                    """;
                command.ExecuteNonQuery();
            }

            using var cache = new MailCache(path);
            cache.StoreSummaries(Account, Inbox, 1, [Message(1)]);

            Assert.Single(cache.LoadSummaries(Account, Inbox, 50));
        }
        finally { try { File.Delete(path); } catch (IOException) { } }
    }

    [Fact]
    public void AnsweredSurvivesTheRoundTrip()
    {
        // Ohne diese Spalte fehlte die Antwortmarke offline – man säße vor einer
        // Nachricht, auf die man längst geantwortet hat, und wüsste es nicht.
        var answered = Message(1);
        answered.Answered = true;

        _cache.StoreSummaries(Account, Inbox, 1, [answered, Message(2)]);

        var loaded = _cache.LoadSummaries(Account, Inbox, 50);

        Assert.True(loaded.Single(m => m.Uid == 1).Answered);
        Assert.False(loaded.Single(m => m.Uid == 2).Answered);
    }

    [Fact]
    public void AnsweredIsUpdatedOnRestore()
    {
        // Die Antwort geht raus, während die Zeile schon gespeichert ist. Beim
        // nächsten Abgleich muss die aktualisierte Marke gewinnen.
        _cache.StoreSummaries(Account, Inbox, 1, [Message(1)]);

        var again = Message(1);
        again.Answered = true;
        _cache.StoreSummaries(Account, Inbox, 1, [again]);

        Assert.True(_cache.LoadSummaries(Account, Inbox, 50).Single().Answered);
    }

    // ---- Nachrichtenkörper -------------------------------------------------

    private static MailBody Body(uint uid = 1) => new()
    {
        Uid = uid,
        Subject = "Rechnung",
        From = "Rene Neuhaus",
        FromAddress = "rene@example.org",
        To = "kunde@example.org",
        ReplyTo = "buchhaltung@example.org",
        Text = "Anbei die Rechnung.",
        Html = "<p>Anbei die Rechnung.</p>",
        Attachments = [new AttachmentInfo
        {
            FileName = "rechnung.pdf", MimeType = "application/pdf", Size = 4096, Index = 0,
        }],
        Authentication = new MailAuthInfo(
            AuthResult.Pass, AuthResult.Pass, AuthResult.Pass, false, null),
    };

    [Fact]
    public void AStoredBodyMayBeServedWithoutTheServer()
    {
        _cache.StoreSummaries(Account, Inbox, 1, [Message(1)]);
        _cache.StoreBody(Account, Inbox, Body());

        Assert.True(_cache.CanServeBody(Account, Inbox, 1));
    }

    [Fact]
    public void AttachmentsAndChecksSurviveTheRoundTrip()
    {
        // Ohne diese Felder wäre eine gespeicherte Nachricht ärmer als eine
        // frisch geholte: keine Anhangsliste, und die Zustellprüfung sähe
        // ungeprüft aus — die Mail wirkte harmloser als sie ist.
        _cache.StoreSummaries(Account, Inbox, 1, [Message(1)]);
        _cache.StoreBody(Account, Inbox, Body());

        var loaded = _cache.LoadBody(Account, Inbox, 1);

        Assert.NotNull(loaded);
        var attachment = Assert.Single(loaded.Attachments);
        Assert.Equal("rechnung.pdf", attachment.FileName);
        Assert.Equal(4096, attachment.Size);
        Assert.Equal(AuthResult.Pass, loaded.Authentication.Dkim);
        Assert.Equal("<p>Anbei die Rechnung.</p>", loaded.Html);
    }

    [Fact]
    public void TheReplyAddressSurvivesTheRoundTrip()
    {
        // Ohne sie ginge beim zweiten Öffnen verloren, wohin der Absender die
        // Antwort haben wollte – und man antwortete dem Falschen.
        _cache.StoreSummaries(Account, Inbox, 1, [Message(1)]);
        _cache.StoreBody(Account, Inbox, Body());

        Assert.Equal("buchhaltung@example.org",
            _cache.LoadBody(Account, Inbox, 1)!.ReplyTo);
    }

    [Fact]
    public void DecryptedMailIsNeverWrittenToDisk()
    {
        // Der Kern: wer PGP benutzt, will gerade nicht, dass der Klartext in
        // einer unverschlüsselten Datei im Benutzerprofil liegt.
        _cache.StoreSummaries(Account, Inbox, 1, [Message(1)]);

        var secret = new MailBody
        {
            Uid = 1,
            Subject = "Vertraulich",
            Text = "Das Kennwort lautet Hirschgeweih.",
            Pgp = new PgpInfo(PgpKind.Encrypted, true, PgpSignatureState.Valid, "rene", null),
        };

        _cache.StoreBody(Account, Inbox, secret);

        Assert.Null(_cache.LoadBody(Account, Inbox, 1));
        Assert.False(_cache.CanServeBody(Account, Inbox, 1));
        Assert.Empty(_cache.Search(Account, Inbox, "Hirschgeweih"));
    }

    [Fact]
    public void AnEarlierPlaintextCopyIsRemoved()
    {
        // Die Regel gab es nicht immer. Eine Nachricht, die vor ihrer Einführung
        // gespeichert wurde, muss beim nächsten Öffnen verschwinden.
        _cache.StoreSummaries(Account, Inbox, 1, [Message(1)]);
        _cache.StoreBody(Account, Inbox, Body());
        Assert.NotNull(_cache.LoadBody(Account, Inbox, 1));

        _cache.StoreBody(Account, Inbox, new MailBody
        {
            Uid = 1,
            Text = "Das Kennwort lautet Hirschgeweih.",
            Pgp = new PgpInfo(PgpKind.Encrypted, true, PgpSignatureState.None, null, null),
        });

        Assert.Null(_cache.LoadBody(Account, Inbox, 1));
    }

    [Fact]
    public void SignedButUnencryptedMailIsKept()
    {
        // Nur die Verschlüsselung ist der Grund. Eine bloss signierte Nachricht
        // stand ohnehin im Klartext auf dem Server.
        _cache.StoreSummaries(Account, Inbox, 1, [Message(1)]);

        _cache.StoreBody(Account, Inbox, new MailBody
        {
            Uid = 1,
            Text = "Offen und unterschrieben.",
            Pgp = new PgpInfo(PgpKind.Signed, false, PgpSignatureState.Valid, "rene", null),
        });

        Assert.NotNull(_cache.LoadBody(Account, Inbox, 1));
    }

    [Fact]
    public void AnInvitationIsStoredButNotServed()
    {
        // Die Einladungsanzeige hängt an Zustandsdaten, die hier nicht liegen –
        // dafür wird weiterhin gefragt.
        _cache.StoreSummaries(Account, Inbox, 1, [Message(1)]);

        _cache.StoreBody(Account, Inbox, new MailBody
        {
            Uid = 1,
            Text = "Besprechung am Montag.",
            Invitation = new CalendarItem { Summary = "Besprechung" },
        });

        Assert.False(_cache.CanServeBody(Account, Inbox, 1));
        Assert.NotNull(_cache.LoadBody(Account, Inbox, 1));
    }

    [Fact]
    public void AnUnknownMessageIsNotServable()
        => Assert.False(_cache.CanServeBody(Account, Inbox, 999));

    [Fact]
    public void StoredMessagesComeBackNewestFirst()
    {
        _cache.StoreSummaries(Account, Inbox, 1,
        [
            Message(1, "Alt", daysAgo: 10),
            Message(2, "Neu", daysAgo: 1),
            Message(3, "Mittel", daysAgo: 5),
        ]);

        var loaded = _cache.LoadSummaries(Account, Inbox, 50);

        Assert.Equal(["Neu", "Mittel", "Alt"], loaded.Select(m => m.Subject));
    }

    [Fact]
    public void EveryFieldSurvivesTheRoundTrip()
    {
        var original = Message(7, "Ümlaute und ẞonderzeichen");
        _cache.StoreSummaries(Account, Inbox, 1, [original]);

        var loaded = Assert.Single(_cache.LoadSummaries(Account, Inbox, 50));

        Assert.Equal(original.Uid, loaded.Uid);
        Assert.Equal(original.Subject, loaded.Subject);
        Assert.Equal(original.From, loaded.From);
        Assert.Equal(original.FromAddress, loaded.FromAddress);
        Assert.Equal(original.HasAttachments, loaded.HasAttachments);
        Assert.Equal(original.MessageId, loaded.MessageId);
        Assert.Equal(original.ThreadReferences, loaded.ThreadReferences);
        Assert.Contains("Arbeit", loaded.Keywords);

        // Sekundengenau: die Datenbank speichert Unix-Sekunden.
        Assert.Equal(original.Date.ToUnixTimeSeconds(), loaded.Date.ToUnixTimeSeconds());
    }

    [Fact]
    public void StoringTheSameUidTwiceUpdatesInsteadOfDuplicating()
    {
        _cache.StoreSummaries(Account, Inbox, 1, [Message(1, "Erst")]);
        _cache.StoreSummaries(Account, Inbox, 1, [Message(1, "Dann")]);

        var loaded = Assert.Single(_cache.LoadSummaries(Account, Inbox, 50));
        Assert.Equal("Dann", loaded.Subject);
    }

    [Fact]
    public void AccountsAndFoldersDoNotBleedIntoEachOther()
    {
        _cache.StoreSummaries(Account, Inbox, 1, [Message(1, "Posteingang")]);
        _cache.StoreSummaries(Account, "Archiv", 1, [Message(1, "Archiv")]);
        _cache.StoreSummaries("konto-2", Inbox, 1, [Message(1, "Anderes Konto")]);

        Assert.Equal("Posteingang", _cache.LoadSummaries(Account, Inbox, 50)[0].Subject);
        Assert.Equal("Archiv", _cache.LoadSummaries(Account, "Archiv", 50)[0].Subject);
        Assert.Equal("Anderes Konto", _cache.LoadSummaries("konto-2", Inbox, 50)[0].Subject);
    }

    [Fact]
    public void PagingWorks()
    {
        _cache.StoreSummaries(Account, Inbox, 1,
            Enumerable.Range(1, 10).Select(i => Message((uint)i, $"Nr {i}", daysAgo: i)));

        var first = _cache.LoadSummaries(Account, Inbox, limit: 3);
        var second = _cache.LoadSummaries(Account, Inbox, limit: 3, offset: 3);

        Assert.Equal(["Nr 1", "Nr 2", "Nr 3"], first.Select(m => m.Subject));
        Assert.Equal(["Nr 4", "Nr 5", "Nr 6"], second.Select(m => m.Subject));
        Assert.Equal(10, _cache.CountMessages(Account, Inbox));
    }

    // ---- UIDVALIDITY -------------------------------------------------------

    [Fact]
    public void ChangedUidValidityDiscardsTheFolder()
    {
        // Der wichtigste Test im ganzen Cache: vergibt der Server UIDs neu, zeigt
        // jede gespeicherte Zeile auf eine andere Nachricht. Behalten wäre
        // schlimmer als gar kein Cache.
        _cache.StoreSummaries(Account, Inbox, uidValidity: 1000, [Message(1), Message(2)]);
        Assert.Equal(2, _cache.CountMessages(Account, Inbox));

        _cache.StoreSummaries(Account, Inbox, uidValidity: 2000, [Message(5, "Nach dem Wechsel")]);

        var loaded = _cache.LoadSummaries(Account, Inbox, 50);
        Assert.Single(loaded);
        Assert.Equal("Nach dem Wechsel", loaded[0].Subject);
        Assert.Equal(2000u, _cache.UidValidity(Account, Inbox));
    }

    [Fact]
    public void UnchangedUidValidityKeepsEverything()
    {
        _cache.StoreSummaries(Account, Inbox, 1000, [Message(1)]);
        _cache.StoreSummaries(Account, Inbox, 1000, [Message(2)]);

        Assert.Equal(2, _cache.CountMessages(Account, Inbox));
    }

    [Fact]
    public void ChangedValidityAlsoDropsBodiesAndSearchIndex()
    {
        _cache.StoreSummaries(Account, Inbox, 1000, [Message(1, "Geheimprojekt")]);
        _cache.StoreBody(Account, Inbox, new MailBody
        {
            Uid = 1, Subject = "Geheimprojekt", Text = "Sehr vertraulicher Inhalt",
        });

        _cache.StoreSummaries(Account, Inbox, 2000, []);

        Assert.Null(_cache.LoadBody(Account, Inbox, 1));
        Assert.Empty(_cache.Search(Account, Inbox, "vertraulicher"));
    }

    [Fact]
    public void UnknownFolderHasNoValidityYet()
        => Assert.Null(_cache.UidValidity(Account, "Gibt-es-nicht"));

    [Fact]
    public void SyncTimeIsRecorded()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-2);
        _cache.StoreSummaries(Account, Inbox, 1, [Message(1)]);

        var synced = _cache.LastSync(Account, Inbox);

        Assert.NotNull(synced);
        Assert.True(synced >= before, $"Zeitstempel {synced} liegt vor dem Test");
    }

    // ---- Nachrichtenkörper -------------------------------------------------

    [Fact]
    public void BodyRoundTripCarriesTextAndHtml()
    {
        _cache.StoreSummaries(Account, Inbox, 1, [Message(3, "Angebot")]);
        _cache.StoreBody(Account, Inbox, new MailBody
        {
            Uid = 3,
            Subject = "Angebot",
            From = "Rene Neuhaus",
            To = "kunde@example.org",
            Text = "Anbei das Angebot.",
            Html = "<p>Anbei das Angebot.</p>",
        });

        var body = _cache.LoadBody(Account, Inbox, 3);

        Assert.NotNull(body);
        Assert.Equal(3u, body.Uid);
        Assert.Equal("Angebot", body.Subject);
        Assert.Equal("kunde@example.org", body.To);
        Assert.Equal("Anbei das Angebot.", body.Text);
        Assert.Equal("<p>Anbei das Angebot.</p>", body.Html);
    }

    [Fact]
    public void MissingBodyReturnsNullInsteadOfThrowing()
    {
        Assert.Null(_cache.LoadBody(Account, Inbox, 999));
        Assert.False(_cache.HasBody(Account, Inbox, 999));
    }

    [Fact]
    public void BodyWithoutItsSummaryIsNotReturned()
    {
        // Der Körper allein ergibt keine anzeigbare Nachricht – ohne Kopf fehlen
        // Betreff, Absender und Datum.
        _cache.StoreBody(Account, Inbox, new MailBody { Uid = 42, Text = "verwaist" });

        Assert.Null(_cache.LoadBody(Account, Inbox, 42));
    }

    // ---- Volltextsuche -----------------------------------------------------

    [Fact]
    public void SearchFindsBySubject()
    {
        _cache.StoreSummaries(Account, Inbox, 1,
            [Message(1, "Rechnung März"), Message(2, "Urlaubsantrag")]);

        var hits = _cache.Search(Account, Inbox, "Rechnung");

        Assert.Single(hits);
        Assert.Equal("Rechnung März", hits[0].Subject);
    }

    [Fact]
    public void SearchFindsBySender()
    {
        _cache.StoreSummaries(Account, Inbox, 1,
        [
            Message(1, "Betreff A", "Anna Beispiel", "anna@example.org"),
            Message(2, "Betreff B", "Bert Muster", "bert@example.org"),
        ]);

        var hits = _cache.Search(Account, Inbox, "anna");

        Assert.Single(hits);
        Assert.Equal(1u, hits[0].Uid);
    }

    [Fact]
    public void SearchFindsInsideTheBody()
    {
        _cache.StoreSummaries(Account, Inbox, 1, [Message(1, "Nichtssagender Betreff")]);
        _cache.StoreBody(Account, Inbox, new MailBody
        {
            Uid = 1, Subject = "Nichtssagender Betreff",
            Text = "Der Termin ist am Donnerstag im Konferenzraum.",
        });

        var hits = _cache.Search(Account, Inbox, "Konferenzraum");

        Assert.Single(hits);
    }

    [Fact]
    public void StoringHeadersAgainDoesNotLoseTheIndexedBody()
    {
        // Beim erneuten Laden der Liste dürfen die Volltexte nicht verschwinden.
        _cache.StoreSummaries(Account, Inbox, 1, [Message(1)]);
        _cache.StoreBody(Account, Inbox, new MailBody { Uid = 1, Text = "Konferenzraum" });

        _cache.StoreSummaries(Account, Inbox, 1, [Message(1)]);

        Assert.Single(_cache.Search(Account, Inbox, "Konferenzraum"));
    }

    [Fact]
    public void SearchIsCaseInsensitiveAndIgnoresDiacritics()
    {
        _cache.StoreSummaries(Account, Inbox, 1, [Message(1, "Über Rechnungsprüfung")]);

        Assert.NotEmpty(_cache.Search(Account, Inbox, "über"));
        Assert.NotEmpty(_cache.Search(Account, Inbox, "UBER"));
    }

    [Fact]
    public void SearchMatchesWordPrefixes()
    {
        _cache.StoreSummaries(Account, Inbox, 1, [Message(1, "Rechnungsprüfung")]);

        Assert.NotEmpty(_cache.Search(Account, Inbox, "Rechnung"));
    }

    [Fact]
    public void SeveralWordsAllHaveToMatch()
    {
        _cache.StoreSummaries(Account, Inbox, 1,
            [Message(1, "Rechnung März"), Message(2, "Rechnung April")]);

        var hits = _cache.Search(Account, Inbox, "Rechnung März");

        Assert.Single(hits);
    }

    [Fact]
    public void SearchAcrossAllFoldersIsPossible()
    {
        _cache.StoreSummaries(Account, Inbox, 1, [Message(1, "Rechnung")]);
        _cache.StoreSummaries(Account, "Archiv", 1, [Message(1, "Rechnung")]);

        Assert.Equal(2, _cache.Search(Account, folder: null, "Rechnung").Count);
        Assert.Single(_cache.Search(Account, Inbox, "Rechnung"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"")]
    public void EmptyOrMeaninglessQueriesReturnNothing(string query)
        => Assert.Empty(_cache.Search(Account, Inbox, query));

    [Theory]
    [InlineData("AND OR NOT")]
    [InlineData("foo* NEAR bar")]
    [InlineData("spalte:wert")]
    [InlineData("\"unbalanciert")]
    [InlineData("(klammer")]
    public void QuerySyntaxFromTheUserIsTreatedAsPlainText(string query)
    {
        // Was jemand ins Suchfeld tippt, ist ein Suchbegriff und keine Syntax.
        // Ohne Entschärfung erzeugte das FTS5-Fehler statt einer leeren Trefferliste.
        _cache.StoreSummaries(Account, Inbox, 1, [Message(1, "Harmloser Betreff")]);

        var hits = _cache.Search(Account, Inbox, query);

        Assert.Empty(hits);   // wirft vor allem nicht
    }

    [Fact]
    public void SearchOnlyReturnsTheOwnAccount()
    {
        _cache.StoreSummaries(Account, Inbox, 1, [Message(1, "Rechnung")]);
        _cache.StoreSummaries("konto-2", Inbox, 1, [Message(1, "Rechnung")]);

        Assert.Single(_cache.Search(Account, null, "Rechnung"));
    }

    // ---- Nachziehen --------------------------------------------------------

    [Fact]
    public void RemovedMessagesDisappearEverywhere()
    {
        _cache.StoreSummaries(Account, Inbox, 1, [Message(1, "Weg damit"), Message(2, "Bleibt")]);
        _cache.StoreBody(Account, Inbox, new MailBody { Uid = 1, Text = "Wegwerftext" });

        _cache.Remove(Account, Inbox, [1u]);

        Assert.Single(_cache.LoadSummaries(Account, Inbox, 50));
        Assert.Null(_cache.LoadBody(Account, Inbox, 1));
        Assert.Empty(_cache.Search(Account, Inbox, "Wegwerftext"));
    }

    [Fact]
    public void FlagsCanBeUpdatedWithoutReloadingEverything()
    {
        _cache.StoreSummaries(Account, Inbox, 1, [Message(1), Message(2)]);

        _cache.UpdateFlags(Account, Inbox, [1u], seen: true, flagged: null);

        var loaded = _cache.LoadSummaries(Account, Inbox, 50).ToDictionary(m => m.Uid);
        Assert.True(loaded[1].Seen);
        Assert.False(loaded[2].Seen);
    }

    [Fact]
    public void UpdatingFlagsWithNothingToDoIsHarmless()
    {
        _cache.StoreSummaries(Account, Inbox, 1, [Message(1)]);

        _cache.UpdateFlags(Account, Inbox, [], seen: true, flagged: null);
        _cache.UpdateFlags(Account, Inbox, [1u], seen: null, flagged: null);

        Assert.False(_cache.LoadSummaries(Account, Inbox, 50)[0].Seen);
    }

    // ---- Wartung -----------------------------------------------------------

    [Fact]
    public void StatsCountWhatIsStored()
    {
        _cache.StoreSummaries(Account, Inbox, 1, [Message(1), Message(2)]);
        _cache.StoreSummaries(Account, "Archiv", 1, [Message(1)]);
        _cache.StoreBody(Account, Inbox, new MailBody { Uid = 1, Text = "x" });

        var stats = _cache.Stats();

        Assert.Equal(2, stats.Folders);
        Assert.Equal(3, stats.Messages);
        Assert.Equal(1, stats.Bodies);
        Assert.True(stats.SizeBytes > 0);
        Assert.Contains("3 Nachrichten", stats.Display);
    }

    [Fact]
    public void ClearEmptiesEverything()
    {
        _cache.StoreSummaries(Account, Inbox, 1, [Message(1)]);
        _cache.StoreBody(Account, Inbox, new MailBody { Uid = 1, Text = "x" });

        _cache.Clear();

        var stats = _cache.Stats();
        Assert.Equal(0, stats.Messages);
        Assert.Equal(0, stats.Folders);
        Assert.Equal(0, stats.Bodies);
    }

    [Fact]
    public void ForgettingOneAccountLeavesTheOthers()
    {
        _cache.StoreSummaries(Account, Inbox, 1, [Message(1)]);
        _cache.StoreSummaries("konto-2", Inbox, 1, [Message(1)]);

        _cache.Forget(Account);

        Assert.Empty(_cache.LoadSummaries(Account, Inbox, 50));
        Assert.Single(_cache.LoadSummaries("konto-2", Inbox, 50));
    }

    [Fact]
    public void CacheSurvivesBeingClosedAndReopened()
    {
        _cache.StoreSummaries(Account, Inbox, 1234, [Message(1, "Bleibt erhalten")]);
        _cache.Dispose();

        using var reopened = new MailCache(_path);

        Assert.Equal("Bleibt erhalten", reopened.LoadSummaries(Account, Inbox, 50)[0].Subject);
        Assert.Equal(1234u, reopened.UidValidity(Account, Inbox));
    }
}

public class FtsQueryTests
{
    [Theory]
    [InlineData("Rechnung", "\"Rechnung\"*")]
    [InlineData("Rechnung März", "\"Rechnung\"* AND \"März\"*")]
    [InlineData("  doppelt   leer  ", "\"doppelt\"* AND \"leer\"*")]
    public void WordsBecomePrefixTerms(string input, string expected)
        => Assert.Equal(expected, MailCache.FtsQuery(input));

    [Fact]
    public void InnerQuotesAreDoubledNotPassedThrough()
    {
        // Ein Anführungszeichen mitten im Wort würde den Term sonst aufbrechen
        // und FTS5 einen Syntaxfehler liefern.
        Assert.Equal("\"a\"\"b\"*", MailCache.FtsQuery("a\"b"));
    }

    [Fact]
    public void SurroundingQuotesAreStripped()
    {
        // Wer in Anführungszeichen sucht, meint das Wort – nicht die Zeichen.
        Assert.Equal("\"Rechnung\"*", MailCache.FtsQuery("\"Rechnung\""));
    }

    [Theory]
    [InlineData("")]
    [InlineData("    ")]
    [InlineData("\"")]
    public void NothingUsableGivesNull(string input)
        => Assert.Null(MailCache.FtsQuery(input));
}
