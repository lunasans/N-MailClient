using System.Text;
using NMailClient.Services.Sieve;
using Xunit;

namespace NMailClient.Tests;

public class SieveStringTests
{
    [Theory]
    [InlineData("einfach", "\"einfach\"")]
    [InlineData("mit \"Zitat\"", "\"mit \\\"Zitat\\\"\"")]
    [InlineData("Rück\\wärts", "\"Rück\\\\wärts\"")]
    [InlineData("", "\"\"")]
    public void QuotingEscapesBackslashAndQuote(string value, string expected)
        => Assert.Equal(expected, SieveString.Quote(value));

    [Theory]
    [InlineData("\"einfach\"", "einfach")]
    [InlineData("\"mit \\\"Zitat\\\"\"", "mit \"Zitat\"")]
    [InlineData("\"Rück\\\\wärts\"", "Rück\\wärts")]
    [InlineData("ohne Anführung", "ohne Anführung")]
    [InlineData("\"\"", "")]
    public void UnquotingReversesIt(string value, string expected)
        => Assert.Equal(expected, SieveString.Unquote(value));

    [Fact]
    public void QuoteAndUnquoteRoundTrip()
    {
        foreach (var value in new[] { "abc", "a\"b", "a\\b", "a\\\"b", "" })
            Assert.Equal(value, SieveString.Unquote(SieveString.Quote(value)));
    }

    [Fact]
    public void LiteralLengthCountsBytesNotCharacters()
    {
        // Der entscheidende Punkt: "ä" ist ein Zeichen, aber zwei Bytes. Wer
        // Zeichen zählt, gibt eine zu kleine Länge an – der Server liest dann zu
        // wenig und der Rest des Skripts landet als Befehl im Nirgendwo.
        Assert.Equal("{2+}", SieveString.LiteralHeader("ä"));
        Assert.Equal("{3+}", SieveString.LiteralHeader("abc"));
        Assert.Equal("{0+}", SieveString.LiteralHeader(""));

        var script = "if header :contains \"subject\" \"Grüße\" { fileinto \"Post\"; }";
        Assert.Equal($"{{{Encoding.UTF8.GetByteCount(script)}+}}",
                     SieveString.LiteralHeader(script));
    }

    [Theory]
    [InlineData("{42+}", 42)]
    [InlineData("{42}", 42)]
    [InlineData("{0+}", 0)]
    [InlineData("{7+}\r\n", 7)]
    public void LiteralAnnouncementIsRecognised(string line, int expected)
        => Assert.Equal(expected, SieveString.LiteralLength(line));

    [Theory]
    [InlineData("\"kein Literal\"")]
    [InlineData("OK")]
    [InlineData("{abc+}")]
    [InlineData("{-1}")]
    [InlineData("")]
    public void NonLiteralsAreRejected(string line)
        => Assert.Null(SieveString.LiteralLength(line));

    [Fact]
    public void ClosingQuoteSkipsEscapedOnes()
    {
        Assert.Equal(6, SieveString.FindClosingQuote("\"abcde\" Rest"));
        Assert.Equal(8, SieveString.FindClosingQuote("\"a\\\"bcde\""));
        Assert.Equal(-1, SieveString.FindClosingQuote("\"ohne Ende"));
        Assert.Equal(-1, SieveString.FindClosingQuote("kein Anfang\""));
    }

    [Fact]
    public void PlainCredentialsFollowTheSaslFormat()
    {
        // NUL, Benutzer, NUL, Passwort – die führende NUL ist die leere
        // Autorisierungskennung und wird gern vergessen.
        var encoded = SieveString.PlainCredentials("rene", "geheim");
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));

        Assert.Equal("\0rene\0geheim", decoded);
    }

    [Fact]
    public void PlainCredentialsSurviveUmlauts()
    {
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(
            SieveString.PlainCredentials("rené", "paßwort")));

        Assert.Equal("\0rené\0paßwort", decoded);
    }
}

public class SieveResponseTests
{
    [Fact]
    public void PlainOkIsUnderstood()
    {
        var response = SieveResponse.Parse("OK");

        Assert.NotNull(response);
        Assert.Equal(SieveStatus.Ok, response.Status);
        Assert.True(response.IsOk);
        Assert.Null(response.Code);
    }

    [Fact]
    public void OkWithMessage()
    {
        var response = SieveResponse.Parse("OK \"Logged in.\"");

        Assert.Equal(SieveStatus.Ok, response!.Status);
        Assert.Equal("Logged in.", response.Message);
    }

    [Fact]
    public void NoWithCodeAndMessage()
    {
        var response = SieveResponse.Parse("NO (QUOTA/MAXSCRIPTS) \"Too many scripts\"");

        Assert.Equal(SieveStatus.No, response!.Status);
        Assert.Equal("QUOTA/MAXSCRIPTS", response.Code);
        Assert.Equal("Too many scripts", response.Message);
        Assert.False(response.IsOk);
    }

    [Fact]
    public void NestedParenthesesInTheCodeAreHandled()
    {
        // Beim ersten schliessenden Zeichen abzubrechen wäre falsch.
        var response = SieveResponse.Parse("NO (WARNINGS (line 3)) \"siehe oben\"");

        Assert.Equal("WARNINGS (line 3)", response!.Code);
        Assert.Equal("siehe oben", response.Message);
    }

    [Fact]
    public void ByeIsRecognised()
    {
        var response = SieveResponse.Parse("BYE \"Too many failed attempts\"");

        Assert.Equal(SieveStatus.Bye, response!.Status);
        Assert.Contains("failed", response.Message);
    }

    [Fact]
    public void TryLaterIsNotAPermanentRejection()
    {
        // Sonst würde eine Anmeldesperre greifen, obwohl der Server nur bittet,
        // es später erneut zu versuchen.
        Assert.True(SieveResponse.Parse("NO (TRYLATER) \"Server busy\"")!.IsTryLater);
        Assert.False(SieveResponse.Parse("NO \"Authentication failed\"")!.IsTryLater);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"nur eine Zeichenkette\"")]
    [InlineData("VIELLEICHT")]
    public void NonResponsesYieldNull(string line)
        => Assert.Null(SieveResponse.Parse(line));

    [Fact]
    public void UnterminatedCodeDoesNotThrow()
    {
        var response = SieveResponse.Parse("NO (OFFEN \"Text\"");

        Assert.NotNull(response);
        Assert.Equal(SieveStatus.No, response.Status);
    }

    [Fact]
    public void TrailingLineBreakIsIgnored()
        => Assert.Equal(SieveStatus.Ok, SieveResponse.Parse("OK\r\n")!.Status);

    [Fact]
    public void DisplayFallsBackToCodeThenStatus()
    {
        Assert.Equal("Meldung", SieveResponse.Parse("NO (CODE) \"Meldung\"")!.Display);
        Assert.Equal("CODE", SieveResponse.Parse("NO (CODE)")!.Display);
        Assert.Equal("No", SieveResponse.Parse("NO")!.Display);
    }
}

public class SieveCapabilitiesTests
{
    /// <summary>Die Begrüssung eines Dovecot-Servers, wie sie wirklich kommt.</summary>
    private static readonly string[] Greeting =
    [
        "\"IMPLEMENTATION\" \"Dovecot Pigeonhole\"",
        "\"SIEVE\" \"fileinto reject envelope encoded-character vacation subaddress comparator-i;ascii-numeric relational regex imap4flags copy include variables body enotify environment mailbox date index ihave duplicate mime foreverypart extracttext\"",
        "\"NOTIFY\" \"mailto\"",
        "\"SASL\" \"PLAIN LOGIN\"",
        "\"STARTTLS\"",
        "\"VERSION\" \"1.0\"",
    ];

    [Fact]
    public void GreetingIsParsed()
    {
        var caps = SieveCapabilities.Parse(Greeting);

        Assert.Equal("Dovecot Pigeonhole", caps.Implementation);
        Assert.Equal("1.0", caps.Version);
        Assert.True(caps.StartTls);
    }

    [Fact]
    public void SaslMechanismsAreSplit()
    {
        var caps = SieveCapabilities.Parse(Greeting);

        Assert.Equal(["PLAIN", "LOGIN"], caps.Sasl);
        Assert.True(caps.SupportsSasl("plain"));
        Assert.False(caps.SupportsSasl("GSSAPI"));
    }

    [Fact]
    public void ExtensionsAreSplitAndSearchable()
    {
        var caps = SieveCapabilities.Parse(Greeting);

        Assert.True(caps.Supports("fileinto"));
        Assert.True(caps.Supports("vacation"));
        Assert.True(caps.Supports("IMAP4FLAGS"));      // schreibungsunabhängig
        Assert.False(caps.Supports("gibtesnicht"));
        Assert.Contains("regex", caps.Extensions);
    }

    /// <summary>
    /// So antwortet Dovecot <b>vor</b> STARTTLS: die SASL-Liste ist leer, weil
    /// im Klartext keine Anmeldung erlaubt ist. Erst nach der Umstellung kommen
    /// PLAIN und LOGIN dazu. Wer die erste Auskunft weiterverwendet, kommt nie
    /// zur Anmeldung — gegen mail.neuhaus.or.at nachgemessen.
    /// </summary>
    [Fact]
    public void EmptySaslListBeforeTlsIsParsedAsEmptyNotAsOneEntry()
    {
        var before = SieveCapabilities.Parse(
        [
            "\"IMPLEMENTATION\" \"Dovecot Pigeonhole\"",
            "\"SASL\" \"\"",
            "\"STARTTLS\"",
        ]);

        Assert.Empty(before.Sasl);
        Assert.False(before.SupportsSasl("PLAIN"));
        Assert.True(before.StartTls);

        var after = SieveCapabilities.Parse(
        [
            "\"IMPLEMENTATION\" \"Dovecot Pigeonhole\"",
            "\"SASL\" \"PLAIN LOGIN\"",
        ]);

        Assert.True(after.SupportsSasl("PLAIN"));
    }

    [Fact]
    public void ServerWithoutStartTlsIsRecognised()
    {
        var caps = SieveCapabilities.Parse(
            ["\"IMPLEMENTATION\" \"Testserver\"", "\"SASL\" \"PLAIN\""]);

        Assert.False(caps.StartTls);
        Assert.Empty(caps.Extensions);
    }

    [Fact]
    public void EmptyInputGivesEmptyCapabilities()
    {
        var caps = SieveCapabilities.Parse([]);

        Assert.Null(caps.Implementation);
        Assert.Empty(caps.Sasl);
        Assert.False(caps.StartTls);
    }

    [Fact]
    public void UnknownLinesAreIgnoredNotFatal()
    {
        var caps = SieveCapabilities.Parse(
            ["\"UNBEKANNT\" \"wert\"", "kaputte Zeile", "", "\"SASL\" \"PLAIN\""]);

        Assert.Single(caps.Sasl);
    }
}

public class SieveScriptTests
{
    [Fact]
    public void ActiveScriptIsMarked()
    {
        var script = SieveScript.Parse("\"meine-regeln\" ACTIVE");

        Assert.Equal("meine-regeln", script!.Name);
        Assert.True(script.IsActive);
        Assert.Contains("aktiv", script.Display);
    }

    [Fact]
    public void InactiveScript()
    {
        var script = SieveScript.Parse("\"alt\"");

        Assert.Equal("alt", script!.Name);
        Assert.False(script.IsActive);
        Assert.Equal("alt", script.Display);
    }

    [Fact]
    public void NamesWithSpacesAndQuotesSurvive()
    {
        var script = SieveScript.Parse("\"Regeln \\\"Arbeit\\\" 2026\" ACTIVE");

        Assert.Equal("Regeln \"Arbeit\" 2026", script!.Name);
        Assert.True(script.IsActive);
    }

    [Theory]
    [InlineData("OK")]
    [InlineData("")]
    [InlineData("ohne Anführungszeichen")]
    [InlineData("\"ohne Ende")]
    public void NonScriptLinesYieldNull(string line)
        => Assert.Null(SieveScript.Parse(line));
}
