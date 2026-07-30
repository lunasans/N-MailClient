using System.Text.Json;
using NMailClient.Services.Mailcow;
using Xunit;

namespace NMailClient.Tests;

public class MailcowResultTests
{
    [Fact]
    public void SuccessWithPlainMessage()
    {
        var result = MailcowResult.Parse("""[{"type":"success","msg":"alias_added"}]""");

        Assert.True(result.Success);
        Assert.Equal("alias_added", result.Message);
    }

    [Fact]
    public void MessageAsArrayIsFlattened()
    {
        // mailcow schickt oft ein Feld aus Meldungsschlüssel und Platzhaltern.
        var result = MailcowResult.Parse(
            """[{"type":"success","msg":["alias_added","info@example.org"]}]""");

        Assert.True(result.Success);
        Assert.Contains("alias_added", result.Message);
        Assert.Contains("info@example.org", result.Message);
    }

    [Fact]
    public void DangerIsAFailure()
    {
        var result = MailcowResult.Parse(
            """[{"type":"danger","msg":"alias_exists"}]""");

        Assert.False(result.Success);
        Assert.Equal("alias_exists", result.Message);
    }

    [Fact]
    public void OneFailureAmongSeveralDecides()
    {
        // Bei einer Teilablehnung von Erfolg zu sprechen wäre falsch.
        var result = MailcowResult.Parse("""
            [{"type":"success","msg":"eins"},{"type":"danger","msg":"zwei"}]
            """);

        Assert.False(result.Success);
        Assert.Contains("eins", result.Message);
        Assert.Contains("zwei", result.Message);
    }

    [Fact]
    public void SingleObjectWithoutArrayWorks()
    {
        var result = MailcowResult.Parse("""{"type":"success","msg":"fertig"}""");

        Assert.True(result.Success);
        Assert.Equal("fertig", result.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("kein json")]
    [InlineData("[")]
    [InlineData("[]")]
    public void BrokenAnswersBecomeAFailureNotAnException(string json)
    {
        var result = MailcowResult.Parse(json);

        Assert.False(result.Success);
        Assert.NotEmpty(result.Message);
    }

    [Fact]
    public void MissingTypeCountsAsFailure()
    {
        // Ohne ausdrückliches „success" wird nichts angenommen.
        Assert.False(MailcowResult.Parse("""[{"msg":"unklar"}]""").Success);
    }
}

public class MailcowMailboxTests
{
    private static JsonElement Json(string text)
        => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void MailboxIsParsed()
    {
        var mailbox = MailcowMailbox.Parse(Json("""
            {
              "username": "rene@example.org",
              "name": "Rene Neuhaus",
              "active": 1,
              "quota": 3221225472,
              "quota_used": 1610612736,
              "messages": 4711
            }
            """));

        Assert.NotNull(mailbox);
        Assert.Equal("rene@example.org", mailbox.Address);
        Assert.Equal("Rene Neuhaus", mailbox.Name);
        Assert.True(mailbox.Active);
        Assert.Equal(4711, mailbox.MessageCount);
        Assert.Equal(50, mailbox.UsedPercent, 1);
    }

    [Fact]
    public void NumbersAsStringsAreAlsoRead()
    {
        // mailcow liefert Zahlen je nach Feld mal so, mal so. Wer nur Zahlen
        // liest, zeigt dauerhaft eine Belegung von null an.
        var mailbox = MailcowMailbox.Parse(Json("""
            {"username":"x@y.z","quota":"1000","quota_used":"250","active":"1"}
            """));

        Assert.NotNull(mailbox);
        Assert.Equal(25, mailbox.UsedPercent, 1);
        Assert.True(mailbox.Active);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("true", true)]
    public void FlagsComeInSeveralShapes(string value, bool expected)
    {
        var mailbox = MailcowMailbox.Parse(Json($$"""{"username":"x@y.z","active":"{{value}}"}"""));

        Assert.Equal(expected, mailbox!.Active);
    }

    [Fact]
    public void BooleanFlagsWork()
    {
        Assert.True(MailcowMailbox.Parse(Json("""{"username":"x@y.z","active":true}"""))!.Active);
        Assert.False(MailcowMailbox.Parse(Json("""{"username":"x@y.z","active":false}"""))!.Active);
    }

    [Fact]
    public void UnlimitedQuotaIsNotAPercentage()
    {
        var mailbox = MailcowMailbox.Parse(Json("""
            {"username":"x@y.z","quota":0,"quota_used":12345}
            """));

        Assert.Equal(0, mailbox!.UsedPercent);
        Assert.False(mailbox.IsNearlyFull);
        Assert.Contains("unbegrenzt", mailbox.QuotaDisplay);
    }

    [Fact]
    public void NearlyFullIsFlaggedFromNinetyPercent()
    {
        MailcowMailbox At(int percent) => MailcowMailbox.Parse(Json($$"""
            {"username":"x@y.z","quota":1000,"quota_used":{{percent * 10}}}
            """))!;

        Assert.False(At(89).IsNearlyFull);
        Assert.True(At(90).IsNearlyFull);
        Assert.True(At(100).IsNearlyFull);
    }

    [Fact]
    public void OverfullDoesNotExceedHundredPercent()
    {
        var mailbox = MailcowMailbox.Parse(Json("""
            {"username":"x@y.z","quota":1000,"quota_used":5000}
            """));

        Assert.Equal(100, mailbox!.UsedPercent);
    }

    [Fact]
    public void EntryWithoutUsernameIsRejected()
        => Assert.Null(MailcowMailbox.Parse(Json("""{"name":"ohne Adresse"}""")));

    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(2048, "2 KB")]
    [InlineData(5242880, "5 MB")]
    [InlineData(3221225472, "3 GB")]
    public void SizesAreReadable(long bytes, string expected)
        => Assert.Equal(expected, MailcowMailbox.Size(bytes));
}

public class MailcowAliasTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void AliasIsParsed()
    {
        var alias = MailcowAlias.Parse(Json("""
            {"id":7,"address":"info@example.org","goto":"rene@example.org","active":1}
            """));

        Assert.NotNull(alias);
        Assert.Equal(7, alias.Id);
        Assert.Equal("info@example.org", alias.Address);
        Assert.Equal(["rene@example.org"], alias.Targets);
        Assert.Contains("→", alias.Display);
    }

    [Fact]
    public void SeveralTargetsAreSplit()
    {
        var alias = MailcowAlias.Parse(Json("""
            {"id":1,"address":"team@example.org","goto":"a@x.de,b@x.de , c@x.de","active":1}
            """));

        Assert.NotNull(alias);
        Assert.Equal(["a@x.de", "b@x.de", "c@x.de"], alias.Targets);
    }

    [Fact]
    public void InactiveAliasIsMarked()
    {
        var alias = MailcowAlias.Parse(Json("""
            {"id":1,"address":"alt@example.org","goto":"x@y.de","active":0}
            """));

        Assert.False(alias!.Active);
        Assert.Contains("(aus)", alias.Display);
    }

    [Fact]
    public void EntryWithoutAddressIsRejected()
        => Assert.Null(MailcowAlias.Parse(Json("""{"id":1,"goto":"x@y.de"}""")));
}

public class MailcowAppPasswordTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void ProtocolsAreListed()
    {
        var password = MailcowAppPassword.Parse(Json("""
            {"id":3,"name":"Handy","active":1,
             "imap_access":1,"smtp_access":1,"dav_access":0,"eas_access":0,"pop3_access":0}
            """));

        Assert.NotNull(password);
        Assert.Equal("Handy", password.Name);
        Assert.Equal("IMAP, SMTP", password.Protocols);
    }

    [Fact]
    public void WithoutAnyProtocolADashIsShown()
    {
        var password = MailcowAppPassword.Parse(Json("""{"id":1,"name":"Leer","active":1}"""));

        Assert.Equal("—", password!.Protocols);
    }

    [Fact]
    public void EntryWithoutNameIsRejected()
        => Assert.Null(MailcowAppPassword.Parse(Json("""{"id":1,"active":1}""")));
}

public class MailcowQuarantineTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void ItemIsParsed()
    {
        var item = MailcowQuarantineItem.Parse(Json("""
            {"id":42,"sender":"spam@bad.example","rcpt":"rene@example.org",
             "subject":"Sie haben gewonnen","created":1782000000,"score":14.5}
            """));

        Assert.NotNull(item);
        Assert.Equal(42, item.Id);
        Assert.Equal("spam@bad.example", item.Sender);
        Assert.Equal("Sie haben gewonnen", item.Subject);
        Assert.Equal(14.5, item.Score, 2);

        // Die Anzeige folgt der Kultur des Rechners – auf Deutsch mit Komma.
        Assert.Equal(14.5.ToString("0.0"), item.ScoreDisplay);
    }

    [Fact]
    public void ScoreWithADotIsReadRegardlessOfTheMachineCulture()
    {
        // mailcow liefert Zahlen immer mit Punkt. Wer sie mit der Kultur des
        // Rechners liest, macht auf einem deutschen System aus 9.25 die Zahl 925.
        var item = MailcowQuarantineItem.Parse(Json("""
            {"id":1,"rcpt":"x@y.de","score":"9.25"}
            """));

        Assert.Equal(9.25, item!.Score, 2);
    }

    [Fact]
    public void ScoreAsStringIsRead()
    {
        var item = MailcowQuarantineItem.Parse(Json("""
            {"id":1,"rcpt":"x@y.de","score":"9.25"}
            """));

        Assert.Equal(9.25, item!.Score, 2);
    }

    [Fact]
    public void MissingSubjectGetsAPlaceholder()
    {
        var item = MailcowQuarantineItem.Parse(Json("""{"id":1,"rcpt":"x@y.de"}"""));

        Assert.Equal("(kein Betreff)", item!.Subject);
    }

    [Fact]
    public void EntryWithoutIdIsRejected()
        => Assert.Null(MailcowQuarantineItem.Parse(Json("""{"sender":"x@y.de"}""")));
}

public class MailcowPasswordGeneratorTests
{
    [Fact]
    public void GeneratedPasswordHasTheRequestedLength()
        => Assert.Equal(24, MailcowClient.GeneratePassword().Length);

    [Fact]
    public void AmbiguousCharactersAreAvoided()
    {
        // Das Passwort wird abgetippt, nicht kopiert: l/I/1 und O/0 sind dort
        // eine Fehlerquelle.
        var password = MailcowClient.GeneratePassword(500);

        Assert.DoesNotContain('l', password);
        Assert.DoesNotContain('I', password);
        Assert.DoesNotContain('O', password);
        Assert.DoesNotContain('0', password);
        Assert.DoesNotContain('1', password);
    }

    [Fact]
    public void TwoPasswordsDiffer()
        => Assert.NotEqual(MailcowClient.GeneratePassword(), MailcowClient.GeneratePassword());

    [Fact]
    public async Task UnconfiguredAccessIsRefusedBeforeAnyRequest()
    {
        // Ohne Adresse und Schlüssel darf gar keine Anfrage hinausgehen.
        var client = new MailcowClient(() => new MailcowSettings("", "", "x@y.de"));

        var ex = await Assert.ThrowsAsync<MailcowException>(
            async () => await client.GetMailboxAsync(TestContext.Current.CancellationToken));

        Assert.Contains("eingerichtet", ex.Message);
    }
}
