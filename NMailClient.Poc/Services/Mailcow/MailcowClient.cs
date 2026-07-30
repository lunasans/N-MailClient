using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace NMailClient.Poc.Services.Mailcow;

/// <summary>Zugang zu einer mailcow-Instanz; wird bei jedem Aufruf frisch gelesen.</summary>
public record MailcowSettings(string BaseUrl, string ApiKey, string Mailbox)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl)
                                && !string.IsNullOrWhiteSpace(ApiKey);
}

public class MailcowException(string message) : Exception(message);

/// <summary>
/// REST-Anbindung an mailcow.
///
/// Der API-Schlüssel liegt im Windows-Anmeldeinformationsverwalter, nicht in
/// <c>db.json</c> — er ist so mächtig wie ein Administratorzugang und hat in
/// einer Klartextdatei nichts verloren.
///
/// Bewusst nur lesende und eng umrissene ändernde Aufrufe. Die mailcow-API kann
/// Domains und Postfächer anlegen und löschen; das gehört in die
/// Administrationsoberfläche und nicht in ein Mailprogramm.
/// </summary>
public class MailcowClient(Func<MailcowSettings> settings, HttpClient? http = null)
{
    private readonly HttpClient _http =
        http ?? Transport.HttpFactory.Create(TimeSpan.FromSeconds(15));

    private MailcowSettings Current
    {
        get
        {
            var config = settings();
            if (!config.IsConfigured)
                throw new MailcowException("Für dieses Konto ist keine mailcow-Anbindung eingerichtet.");
            return config;
        }
    }

    // ---- Anfragen ----------------------------------------------------------

    private HttpRequestMessage Build(HttpMethod method, string path, MailcowSettings config)
    {
        var baseUrl = config.BaseUrl.TrimEnd('/');
        var request = new HttpRequestMessage(method, $"{baseUrl}/api/v1/{path.TrimStart('/')}");

        request.Headers.Add("X-API-Key", config.ApiKey);
        request.Headers.Add("Accept", "application/json");

        return request;
    }

    private async Task<string> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            using var response = await _http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized
                || response.StatusCode == HttpStatusCode.Forbidden)
                throw new MailcowException(
                    "Der API-Schlüssel wurde abgelehnt. Ist er gültig und diese IP freigegeben?");

            if (!response.IsSuccessStatusCode)
                throw new MailcowException($"mailcow antwortete mit {(int)response.StatusCode}.");

            return body;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new MailcowException($"mailcow nicht erreichbar: {ex.Message}");
        }
    }

    private async Task<JsonElement> GetJsonAsync(string path, CancellationToken ct)
    {
        var config = Current;
        var body = await SendAsync(Build(HttpMethod.Get, path, config), ct);

        try
        {
            // Das Ergebnis bleibt am Dokument hängen; deshalb geklont.
            using var document = JsonDocument.Parse(body);
            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new MailcowException($"Antwort nicht lesbar: {ex.Message}");
        }
    }

    private async Task<MailcowResult> PostAsync(string path, object payload, CancellationToken ct)
    {
        var config = Current;
        var request = Build(HttpMethod.Post, path, config);

        request.Content = new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        return MailcowResult.Parse(await SendAsync(request, ct));
    }

    /// <summary>
    /// Ein Feld aus der Antwort lesen. mailcow liefert bei „nichts gefunden"
    /// wahlweise ein leeres Feld, ein leeres Objekt oder <c>{}</c> – alles davon
    /// bedeutet dasselbe.
    /// </summary>
    private static IEnumerable<JsonElement> AsArray(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Array => element.EnumerateArray(),
        JsonValueKind.Object => [element],
        _ => [],
    };

    // ---- Postfach ----------------------------------------------------------

    /// <summary>Belegung und Zustand des eigenen Postfachs.</summary>
    public async Task<MailcowMailbox> GetMailboxAsync(CancellationToken ct = default)
    {
        var mailbox = Current.Mailbox;
        var json = await GetJsonAsync($"get/mailbox/{Uri.EscapeDataString(mailbox)}", ct);

        var result = AsArray(json).Select(MailcowMailbox.Parse).OfType<MailcowMailbox>().FirstOrDefault();

        return result ?? throw new MailcowException($"Postfach {mailbox} nicht gefunden.");
    }

    // ---- Aliase ------------------------------------------------------------

    /// <summary>
    /// Alle Aliase, die auf dieses Postfach zeigen. Die API kennt keine
    /// Einschränkung darauf, also wird hier gefiltert.
    /// </summary>
    public async Task<IReadOnlyList<MailcowAlias>> GetAliasesAsync(CancellationToken ct = default)
    {
        var mailbox = Current.Mailbox;
        var json = await GetJsonAsync("get/alias/all", ct);

        return AsArray(json)
            .Select(MailcowAlias.Parse)
            .OfType<MailcowAlias>()
            .Where(a => a.Targets.Contains(mailbox, StringComparer.OrdinalIgnoreCase))
            .OrderBy(a => a.Address, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public Task<MailcowResult> AddAliasAsync(
        string address, string target, CancellationToken ct = default)
        => PostAsync("add/alias", new
        {
            address,
            @goto = target,
            active = "1",
        }, ct);

    public Task<MailcowResult> DeleteAliasAsync(int id, CancellationToken ct = default)
        => PostAsync("delete/alias", new[] { id.ToString() }, ct);

    // ---- App-Passwörter ----------------------------------------------------

    public async Task<IReadOnlyList<MailcowAppPassword>> GetAppPasswordsAsync(
        CancellationToken ct = default)
    {
        var mailbox = Current.Mailbox;
        var json = await GetJsonAsync($"get/app-passwd/all/{Uri.EscapeDataString(mailbox)}", ct);

        return AsArray(json)
            .Select(MailcowAppPassword.Parse)
            .OfType<MailcowAppPassword>()
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Ein App-Passwort anlegen. Das Passwort wird hier erzeugt und dem Aufrufer
    /// zurückgegeben — mailcow zeigt es später nicht mehr an.
    /// </summary>
    public async Task<(MailcowResult Result, string Password)> AddAppPasswordAsync(
        string name, CancellationToken ct = default)
    {
        var password = GeneratePassword();

        var result = await PostAsync("add/app-passwd", new
        {
            active = "1",
            username = Current.Mailbox,
            app_name = name,
            app_passwd = password,
            app_passwd2 = password,
            protocols = new[] { "imap_access", "smtp_access", "dav_access" },
        }, ct);

        return (result, password);
    }

    public Task<MailcowResult> DeleteAppPasswordAsync(int id, CancellationToken ct = default)
        => PostAsync("delete/app-passwd", new[] { id.ToString() }, ct);

    /// <summary>
    /// Ein zufälliges Passwort. Bewusst ohne leicht zu verwechselnde Zeichen —
    /// es wird abgetippt, nicht kopiert.
    /// </summary>
    public static string GeneratePassword(int length = 24)
    {
        const string alphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var result = new StringBuilder(length);

        for (var i = 0; i < length; i++)
            result.Append(alphabet[System.Security.Cryptography.RandomNumberGenerator
                .GetInt32(alphabet.Length)]);

        return result.ToString();
    }

    // ---- Quarantäne --------------------------------------------------------

    public async Task<IReadOnlyList<MailcowQuarantineItem>> GetQuarantineAsync(
        CancellationToken ct = default)
    {
        var mailbox = Current.Mailbox;
        var json = await GetJsonAsync("get/quarantine/all", ct);

        return AsArray(json)
            .Select(MailcowQuarantineItem.Parse)
            .OfType<MailcowQuarantineItem>()
            .Where(q => q.Recipient.Contains(mailbox, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(q => q.Received)
            .ToList();
    }

    public Task<MailcowResult> HandleQuarantineAsync(
        int id, QuarantineAction action, CancellationToken ct = default)
        => action switch
        {
            QuarantineAction.Delete => PostAsync("delete/qitem", new[] { id.ToString() }, ct),

            // „learn" trainiert rspamd mit; bei Freigabe als erwünscht, sonst als Spam.
            QuarantineAction.Release => PostAsync("edit/qitem", new
            {
                items = new[] { id.ToString() },
                attr = new { action = "release" },
            }, ct),

            _ => PostAsync("edit/qitem", new
            {
                items = new[] { id.ToString() },
                attr = new { action = "learnspam" },
            }, ct),
        };

    // ---- Verbindungstest ---------------------------------------------------

    /// <summary>
    /// Prüft Adresse und Schlüssel, ohne etwas zu verändern.
    /// </summary>
    public async Task<string> TestAsync(CancellationToken ct = default)
    {
        var mailbox = await GetMailboxAsync(ct);
        return $"Verbunden · {mailbox.Address} · {mailbox.QuotaDisplay}";
    }
}
