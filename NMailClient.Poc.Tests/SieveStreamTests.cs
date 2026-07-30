using System.IO;
using System.Text;
using NMailClient.Poc.Services.Sieve;
using Xunit;

namespace NMailClient.Poc.Tests;

/// <summary>
/// Der Leser mischt Zeilen und Rohbytes: nach einer Literal-Ankündigung folgen
/// genau n Bytes. Genau dort geht so ein Protokoll kaputt, deshalb wird es hier
/// gegen einen Speicherstrom geprüft, ohne Server.
/// </summary>
public class SieveStreamTests
{
    private static SieveStream Reading(string content)
        => new(new MemoryStream(Encoding.UTF8.GetBytes(content)));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LinesAreSplitOnCrLf()
    {
        await using var stream = Reading("\"IMPLEMENTATION\" \"Dovecot\"\r\nOK\r\n");

        Assert.Equal("\"IMPLEMENTATION\" \"Dovecot\"", await stream.ReadLineAsync(Ct));
        Assert.Equal("OK", await stream.ReadLineAsync(Ct));
        Assert.Null(await stream.ReadLineAsync(Ct));
    }

    [Fact]
    public async Task LoneLineFeedIsAlsoAccepted()
    {
        // Nicht jeder Server hält sich streng an CRLF; daran soll es nicht scheitern.
        await using var stream = Reading("erste\nzweite\n");

        Assert.Equal("erste", await stream.ReadLineAsync(Ct));
        Assert.Equal("zweite", await stream.ReadLineAsync(Ct));
    }

    [Fact]
    public async Task LastLineWithoutBreakIsStillReturned()
    {
        await using var stream = Reading("ohne Abschluss");

        Assert.Equal("ohne Abschluss", await stream.ReadLineAsync(Ct));
        Assert.Null(await stream.ReadLineAsync(Ct));
    }

    [Fact]
    public async Task LiteralIsReadByteExactAndTheLineAfterItSurvives()
    {
        // Der Kernfall: nach {11+} folgen genau elf Bytes, danach geht es
        // zeilenweise weiter. Wer hier zu viel oder zu wenig liest, verliert den
        // Anschluss für den Rest der Sitzung.
        await using var stream = Reading("{11+}\r\nHallo Welt!\r\nOK\r\n");

        var announcement = await stream.ReadLineAsync(Ct);
        Assert.Equal(11, SieveString.LiteralLength(announcement!));

        Assert.Equal("Hallo Welt!", await stream.ReadExactlyAsync(11, Ct));
        Assert.Equal("", await stream.ReadLineAsync(Ct));       // der Abschluss des Literals
        Assert.Equal("OK", await stream.ReadLineAsync(Ct));
    }

    [Fact]
    public async Task LiteralWithUmlautsIsCountedInBytes()
    {
        // "Grüße" sind fünf Zeichen, aber sieben Bytes. Zeichen zu zählen wäre
        // der klassische Fehler.
        const string text = "Grüße";
        var length = Encoding.UTF8.GetByteCount(text);
        Assert.Equal(7, length);

        await using var stream = Reading($"{{{length}+}}\r\n{text}\r\nOK\r\n");

        await stream.ReadLineAsync(Ct);
        Assert.Equal(text, await stream.ReadExactlyAsync(length, Ct));
        await stream.ReadLineAsync(Ct);
        Assert.Equal("OK", await stream.ReadLineAsync(Ct));
    }

    [Fact]
    public async Task LiteralContainingLineBreaksStaysIntact()
    {
        // Skripte enthalten Zeilenumbrüche – die dürfen nicht als Zeilenende des
        // Protokolls gedeutet werden.
        const string script = "require [\"fileinto\"];\r\nif true {\r\n  fileinto \"X\";\r\n}\r\n";
        var length = Encoding.UTF8.GetByteCount(script);

        await using var stream = Reading($"{{{length}+}}\r\n{script}\r\nOK\r\n");

        await stream.ReadLineAsync(Ct);
        Assert.Equal(script, await stream.ReadExactlyAsync(length, Ct));
        await stream.ReadLineAsync(Ct);
        Assert.Equal("OK", await stream.ReadLineAsync(Ct));
    }

    [Fact]
    public async Task EmptyLiteralIsAllowed()
    {
        await using var stream = Reading("{0+}\r\n\r\nOK\r\n");

        await stream.ReadLineAsync(Ct);
        Assert.Equal("", await stream.ReadExactlyAsync(0, Ct));
    }

    [Fact]
    public async Task TruncatedLiteralIsReportedNotSilentlyShortened()
    {
        // Eine abgebrochene Verbindung mitten im Literal darf kein halbes Skript
        // liefern – das würde beim Speichern stillschweigend Regeln löschen.
        await using var stream = Reading("{50+}\r\nzu kurz");

        await stream.ReadLineAsync(Ct);

        await Assert.ThrowsAsync<IOException>(async () => await stream.ReadExactlyAsync(50, Ct));
    }

    [Fact]
    public async Task ContentLongerThanTheBufferIsReadCompletely()
    {
        // Der interne Puffer fasst 8 KiB; ein grösseres Skript muss über mehrere
        // Lesevorgänge zusammengesetzt werden.
        var big = new string('x', 20000);
        var length = Encoding.UTF8.GetByteCount(big);

        await using var stream = Reading($"{{{length}+}}\r\n{big}\r\nOK\r\n");

        await stream.ReadLineAsync(Ct);
        var read = await stream.ReadExactlyAsync(length, Ct);

        Assert.Equal(20000, read.Length);
        Assert.Equal(big, read);
        await stream.ReadLineAsync(Ct);
        Assert.Equal("OK", await stream.ReadLineAsync(Ct));
    }

    [Fact]
    public async Task VeryLongLineIsNotCutAtTheBufferBoundary()
    {
        // Die SIEVE-Fähigkeitszeile ist bei Dovecot mehrere hundert Zeichen lang;
        // grundsätzlich darf eine Zeile die Puffergrösse überschreiten.
        var line = "\"SIEVE\" \"" + string.Join(' ', Enumerable.Repeat("erweiterung", 1000)) + "\"";

        await using var stream = Reading(line + "\r\nOK\r\n");

        Assert.Equal(line, await stream.ReadLineAsync(Ct));
        Assert.Equal("OK", await stream.ReadLineAsync(Ct));
    }

    [Fact]
    public async Task WritingSendsCrLf()
    {
        var target = new MemoryStream();
        await using var stream = new SieveStream(target);

        await stream.WriteLineAsync("LISTSCRIPTS", Ct);

        Assert.Equal("LISTSCRIPTS\r\n", Encoding.UTF8.GetString(target.ToArray()));
    }

    [Fact]
    public async Task RawContentIsFollowedByCrLf()
    {
        var target = new MemoryStream();
        await using var stream = new SieveStream(target);

        await stream.WriteRawAsync("if true { stop; }", Ct);

        Assert.Equal("if true { stop; }\r\n", Encoding.UTF8.GetString(target.ToArray()));
    }

    [Fact]
    public async Task UpgradeDiscardsWhatWasBufferedBeforeTls()
    {
        // Alles vor STARTTLS Gelesene stammt aus der unverschlüsselten Phase und
        // darf nach der Umstellung nicht weiterverwendet werden.
        await using var stream = Reading("\"STARTTLS\"\r\nOK\r\nSPAETER\r\n");

        await stream.ReadLineAsync(Ct);

        stream.Upgrade(new MemoryStream(Encoding.UTF8.GetBytes("NACH-TLS\r\n")));

        Assert.Equal("NACH-TLS", await stream.ReadLineAsync(Ct));
    }
}
