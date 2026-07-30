using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;

namespace NMailClient.Poc.Services.Sieve;

/// <summary>Zugangsdaten für den Sieve-Zugriff; werden bei jedem Verbinden frisch gelesen.</summary>
public record SieveSettings(string Host, int Port, string User, string Password)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host)
                                && !string.IsNullOrWhiteSpace(User);
}

/// <summary>Der Server hat die Anfrage abgelehnt (NO oder BYE).</summary>
public class SieveException(string message, string? code = null) : Exception(message)
{
    public string? Code { get; } = code;
}

/// <summary>
/// ManageSieve-Client nach RFC 5804.
///
/// Selbst geschrieben, weil es dafür kein .NET-Paket gibt. Der Ablauf ist
/// überschaubar: verbinden, Fähigkeiten lesen, STARTTLS, anmelden, Befehle.
///
/// Wie bei IMAP und SMTP wird eine <b>abgelehnte Anmeldung gemerkt</b>. Ohne das
/// löste jeder Klick in der Skriptverwaltung einen weiteren Fehlversuch aus —
/// und genau daran hängen serverseitige Sperren.
/// </summary>
public sealed class ManageSieveClient(Func<SieveSettings> settings) : IAsyncDisposable
{
    private TcpClient? _tcp;
    private SieveStream? _stream;
    private string? _authFailure;

    public SieveCapabilities Capabilities { get; private set; } = SieveCapabilities.Empty;

    public bool IsConnected => _tcp is { Connected: true } && _stream is not null;

    /// <summary>Nach geänderten Zugangsdaten wieder freigeben.</summary>
    public void ResetAuthFailure() => _authFailure = null;

    // ---- Verbindung --------------------------------------------------------

    private async Task<SieveStream> ConnectAsync(CancellationToken ct)
    {
        if (IsConnected) return _stream!;
        if (_authFailure is not null) throw new SieveException(_authFailure);

        await CloseAsync();

        var config = settings();
        if (!config.IsConfigured)
            throw new SieveException("Für dieses Konto ist kein Sieve-Server eingetragen.");

        // Wie bei IMAP und SMTP: nicht über den Namen verbinden, sonst wartet
        // eine unerreichbare Adresse vorn in der Liste die volle TCP-Frist ab.
        var socket = await Transport.HappyEyeballs.ConnectAsync(config.Host, config.Port, ct);
        _tcp = new TcpClient { Client = socket };

        _stream = new SieveStream(_tcp.GetStream());

        Capabilities = await ReadCapabilitiesAsync(ct);

        // Unverschlüsselt wird nicht angemeldet. Der Server bietet STARTTLS an
        // oder er bekommt kein Passwort zu sehen.
        if (!Capabilities.StartTls)
            throw new SieveException(
                $"{config.Host} bietet kein STARTTLS an – die Anmeldung würde im Klartext laufen.");

        await StartTlsAsync(config.Host, ct);
        await AuthenticateAsync(config, ct);

        return _stream;
    }

    /// <summary>
    /// Die Fähigkeitszeilen bis zum abschliessenden OK einsammeln. Kommt statt
    /// dessen BYE, weist der Server uns ab.
    /// </summary>
    private async Task<SieveCapabilities> ReadCapabilitiesAsync(CancellationToken ct)
    {
        var lines = new List<string>();

        while (await _stream!.ReadLineAsync(ct) is { } line)
        {
            if (SieveResponse.Parse(line) is { } response)
            {
                if (response.IsOk) return SieveCapabilities.Parse(lines);
                throw new SieveException($"Server lehnt ab: {response.Display}", response.Code);
            }

            lines.Add(line);
        }

        throw new SieveException("Verbindung endete vor der Begrüssung.");
    }

    private async Task StartTlsAsync(string host, CancellationToken ct)
    {
        await _stream!.WriteLineAsync("STARTTLS", ct);
        await ExpectOkAsync("STARTTLS", ct);

        var ssl = new SslStream(_stream.Inner, leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, ct);

        _stream.Upgrade(ssl);

        // Nach der Umstellung schickt der Server seine Fähigkeiten erneut —
        // die vor TLS gelesenen sind nicht vertrauenswürdig.
        Capabilities = await ReadCapabilitiesAsync(ct);
    }

    private async Task AuthenticateAsync(SieveSettings config, CancellationToken ct)
    {
        if (!Capabilities.SupportsSasl("PLAIN"))
            throw new SieveException(
                "Der Server bietet kein SASL PLAIN an; andere Verfahren sind hier nicht umgesetzt.");

        var credentials = SieveString.PlainCredentials(config.User, config.Password);
        await _stream!.WriteLineAsync($"AUTHENTICATE \"PLAIN\" {SieveString.Quote(credentials)}", ct);

        var response = await ReadResponseAsync(ct);

        if (response.IsOk) return;

        // Eine dauerhafte Ablehnung wird gemerkt; ein „später nochmal" nicht.
        var message = $"Anmeldung abgelehnt: {response.Display}";
        if (!response.IsTryLater) _authFailure = message;

        await CloseAsync();
        throw new SieveException(message, response.Code);
    }

    // ---- Antworten ---------------------------------------------------------

    /// <summary>Zeilen überlesen, bis eine Statusantwort kommt.</summary>
    private async Task<SieveResponse> ReadResponseAsync(CancellationToken ct)
    {
        while (await _stream!.ReadLineAsync(ct) is { } line)
        {
            if (SieveResponse.Parse(line) is { } response) return response;
        }

        throw new SieveException("Verbindung endete vor der Antwort.");
    }

    private async Task<SieveResponse> ExpectOkAsync(string what, CancellationToken ct)
    {
        var response = await ReadResponseAsync(ct);
        if (!response.IsOk)
            throw new SieveException($"{what} fehlgeschlagen: {response.Display}", response.Code);

        return response;
    }

    // ---- Befehle -----------------------------------------------------------

    /// <summary>Alle Skripte des Kontos, das aktive ist gekennzeichnet.</summary>
    public async Task<IReadOnlyList<SieveScript>> ListAsync(CancellationToken ct = default)
    {
        var stream = await ConnectAsync(ct);
        await stream.WriteLineAsync("LISTSCRIPTS", ct);

        var scripts = new List<SieveScript>();

        while (await stream.ReadLineAsync(ct) is { } line)
        {
            if (SieveResponse.Parse(line) is { } response)
            {
                if (response.IsOk) return scripts;
                throw new SieveException($"LISTSCRIPTS fehlgeschlagen: {response.Display}", response.Code);
            }

            if (SieveScript.Parse(line) is { } script) scripts.Add(script);
        }

        throw new SieveException("Verbindung endete während der Auflistung.");
    }

    /// <summary>Den Inhalt eines Skripts holen.</summary>
    public async Task<string> GetAsync(string name, CancellationToken ct = default)
    {
        var stream = await ConnectAsync(ct);
        await stream.WriteLineAsync($"GETSCRIPT {SieveString.Quote(name)}", ct);

        var content = "";

        while (await stream.ReadLineAsync(ct) is { } line)
        {
            if (SieveString.LiteralLength(line) is { } length)
            {
                content = await stream.ReadExactlyAsync(length, ct);
                continue;
            }

            if (SieveResponse.Parse(line) is { } response)
            {
                if (response.IsOk) return content;
                throw new SieveException($"Skript nicht lesbar: {response.Display}", response.Code);
            }
        }

        throw new SieveException("Verbindung endete beim Lesen des Skripts.");
    }

    /// <summary>
    /// Skript speichern. Der Server prüft dabei die Syntax und lehnt mit NO ab,
    /// wenn etwas nicht stimmt — die Meldung ist für den Anwender brauchbar.
    /// </summary>
    public async Task PutAsync(string name, string content, CancellationToken ct = default)
    {
        var stream = await ConnectAsync(ct);

        await stream.WriteLineAsync(
            $"PUTSCRIPT {SieveString.Quote(name)} {SieveString.LiteralHeader(content)}", ct);
        await stream.WriteRawAsync(content, ct);

        await ExpectOkAsync("Speichern", ct);
    }

    /// <summary>
    /// Nur prüfen, ohne zu speichern. Liefert die Antwort zurück, damit auch
    /// Warnungen sichtbar werden — ein OK kann welche enthalten.
    /// </summary>
    public async Task<SieveResponse> CheckAsync(string content, CancellationToken ct = default)
    {
        var stream = await ConnectAsync(ct);

        if (!Capabilities.Supports("CHECKSCRIPT") && Capabilities.Version is null)
            throw new SieveException("Der Server kennt CHECKSCRIPT nicht.");

        await stream.WriteLineAsync($"CHECKSCRIPT {SieveString.LiteralHeader(content)}", ct);
        await stream.WriteRawAsync(content, ct);

        return await ReadResponseAsync(ct);
    }

    /// <summary>Skript aktivieren. Ein leerer Name schaltet alle ab.</summary>
    public async Task SetActiveAsync(string name, CancellationToken ct = default)
    {
        var stream = await ConnectAsync(ct);
        await stream.WriteLineAsync($"SETACTIVE {SieveString.Quote(name)}", ct);

        await ExpectOkAsync("Aktivieren", ct);
    }

    public async Task DeleteAsync(string name, CancellationToken ct = default)
    {
        var stream = await ConnectAsync(ct);
        await stream.WriteLineAsync($"DELETESCRIPT {SieveString.Quote(name)}", ct);

        await ExpectOkAsync("Löschen", ct);
    }

    /// <summary>
    /// Umbenennen. Kann der Server es nicht, wird es über Holen, Speichern und
    /// Löschen nachgebildet — mit dem Aktiv-Zustand als letztem Schritt, damit
    /// bei einem Abbruch nie beide Skripte aktiv sind.
    /// </summary>
    public async Task RenameAsync(string from, string to, CancellationToken ct = default)
    {
        var stream = await ConnectAsync(ct);

        if (Capabilities.Supports("RENAMESCRIPT"))
        {
            await stream.WriteLineAsync(
                $"RENAMESCRIPT {SieveString.Quote(from)} {SieveString.Quote(to)}", ct);
            await ExpectOkAsync("Umbenennen", ct);
            return;
        }

        var content = await GetAsync(from, ct);
        var wasActive = (await ListAsync(ct)).Any(s => s.Name == from && s.IsActive);

        await PutAsync(to, content, ct);
        if (wasActive) await SetActiveAsync(to, ct);
        await DeleteAsync(from, ct);
    }

    /// <summary>Verbindungstest für die Einstellungen: meldet die Kennung des Servers.</summary>
    public async Task<string> TestAsync(CancellationToken ct = default)
    {
        await ConnectAsync(ct);

        var name = Capabilities.Implementation ?? "unbekannter Server";
        var extensions = Capabilities.Extensions.Count;

        return $"Verbunden mit {name} · {extensions} Sieve-Erweiterungen";
    }

    // ---- Aufräumen ---------------------------------------------------------

    private async Task CloseAsync()
    {
        if (_stream is not null)
        {
            try { await _stream.WriteLineAsync("LOGOUT", CancellationToken.None); }
            catch (Exception ex) when (ex is IOException or SocketException
                                          or ObjectDisposedException) { }

            await _stream.DisposeAsync();
            _stream = null;
        }

        _tcp?.Dispose();
        _tcp = null;
    }

    public async ValueTask DisposeAsync() => await CloseAsync();
}
