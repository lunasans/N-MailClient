using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NMailClient.Services.Update;

public enum UpdateState
{
    /// <summary>Noch nicht nachgesehen.</summary>
    Unknown,

    /// <summary>Diese Fassung ist die neueste.</summary>
    UpToDate,

    /// <summary>Es gibt etwas Neueres.</summary>
    Available,

    /// <summary>Nachsehen war nicht möglich (kein Netz, Fehler bei GitHub).</summary>
    Failed,
}

public record UpdateInfo(
    UpdateState State,
    AppVersion Current,
    AppVersion? Latest,
    string? ReleaseUrl,
    string? Notes,
    DateTimeOffset? Published,
    string? Error)
{
    public static UpdateInfo Unknown(AppVersion current)
        => new(UpdateState.Unknown, current, null, null, null, null, null);

    public string Summary => State switch
    {
        UpdateState.UpToDate => $"Version {Current} ist aktuell.",
        UpdateState.Available => $"Version {Latest} ist verfügbar (installiert: {Current}).",
        UpdateState.Failed => $"Suche nach Aktualisierungen fehlgeschlagen: {Error}",
        _ => $"Version {Current}",
    };
}

/// <summary>
/// Sieht bei den GitHub-Releases nach, ob es etwas Neueres gibt.
///
/// Bewusst <b>ohne</b> Selbstaktualisierung: Herunterladen und Ausführen eines
/// Installationsprogramms im Hintergrund ist eine Kette, die genau einmal
/// schiefgehen muss. Hier wird nur gemeldet und die Release-Seite geöffnet —
/// den Rest entscheidet der Anwender.
///
/// Der Abruf ist eine Anfrage an einen fremden Dienst und passiert deshalb nicht
/// von selbst beim Start, sondern nur auf Wunsch.
/// </summary>
public class UpdateService(HttpClient? http = null)
{
    /// <summary>Vorgabe: das Projektarchiv dieser Anwendung.</summary>
    public const string DefaultRepository = "lunasans/N-MailClient";

    private readonly HttpClient _http = http ?? CreateClient();

    private static HttpClient CreateClient()
    {
        var client = Transport.HttpFactory.Create(TimeSpan.FromSeconds(10));

        // GitHub weist Anfragen ohne Kennung ab.
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("NMailClient", AppVersion.Current.ToString()));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        return client;
    }

    /// <summary>
    /// Nachsehen. Wirft nicht — ein misslungener Abruf ist eine Meldung, kein
    /// Fehlerdialog.
    /// </summary>
    public async Task<UpdateInfo> CheckAsync(
        string repository = DefaultRepository, CancellationToken ct = default)
    {
        var current = AppVersion.Current;

        try
        {
            var url = $"https://api.github.com/repos/{repository}/releases/latest";
            using var response = await _http.GetAsync(url, ct);

            if (!response.IsSuccessStatusCode)
                return Failed(current, $"GitHub antwortete mit {(int)response.StatusCode}");

            var json = await response.Content.ReadAsStringAsync(ct);
            return Evaluate(current, json);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                      or JsonException or InvalidOperationException)
        {
            return Failed(current, ex.Message);
        }
    }

    private static UpdateInfo Failed(AppVersion current, string error)
        => new(UpdateState.Failed, current, null, null, null, null, error);

    /// <summary>
    /// Die Antwort auswerten. Getrennt vom Abruf, damit sich das ohne Netz
    /// prüfen lässt.
    /// </summary>
    public static UpdateInfo Evaluate(AppVersion current, string json)
    {
        GitHubRelease? release;
        try
        {
            release = JsonSerializer.Deserialize<GitHubRelease>(json);
        }
        catch (JsonException ex)
        {
            return Failed(current, ex.Message);
        }

        if (release is null) return Failed(current, "leere Antwort");

        // Entwürfe sind nicht veröffentlicht und gehen niemanden etwas an.
        if (release.Draft) return Failed(current, "neuester Eintrag ist ein Entwurf");

        var latest = AppVersion.Parse(release.TagName) ?? AppVersion.Parse(release.Name);
        if (latest is null)
            return Failed(current, $"Version aus '{release.TagName}' nicht lesbar");

        // Vorabversionen werden nicht angeboten: wer sie will, holt sie selbst.
        if (release.PreRelease)
            return new UpdateInfo(UpdateState.UpToDate, current, latest,
                                  release.HtmlUrl, null, release.PublishedAt, null);

        var state = latest.Value > current ? UpdateState.Available : UpdateState.UpToDate;

        return new UpdateInfo(state, current, latest, release.HtmlUrl,
                              release.Body, release.PublishedAt, null);
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool PreRelease { get; set; }
        [JsonPropertyName("published_at")] public DateTimeOffset? PublishedAt { get; set; }
    }
}
