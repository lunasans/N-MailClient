using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace NMailClient.Poc.Services;

public record TranslationResult(string Text, string DetectedLanguage);

/// <summary>
/// Übersetzung über eine LibreTranslate-Instanz.
///
/// Bewusst selbst gehostet, kein kommerzieller Dienst: der gesamte Mailtext geht
/// an den Server. Deshalb gibt es auch keine Voreinstellung – ohne konfigurierte
/// Adresse ist die Funktion aus.
/// </summary>
public class TranslateService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly Func<(string Url, string ApiKey)> _config;

    public TranslateService(Func<(string, string)> config) => _config = config;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_config().Url);

    private sealed class Request
    {
        [JsonPropertyName("q")] public string Q { get; set; } = "";
        [JsonPropertyName("source")] public string Source { get; set; } = "auto";
        [JsonPropertyName("target")] public string Target { get; set; } = "de";

        /// <summary>"html" erhält das Markup, "text" für reine Textmails.</summary>
        [JsonPropertyName("format")] public string Format { get; set; } = "text";

        [JsonPropertyName("api_key")] public string? ApiKey { get; set; }
    }

    private sealed class Response
    {
        [JsonPropertyName("translatedText")] public string TranslatedText { get; set; } = "";

        /// <summary>Bei source=auto liefert LibreTranslate die erkannte Sprache mit.</summary>
        [JsonPropertyName("detectedLanguage")] public Detected? DetectedLanguage { get; set; }

        internal sealed class Detected
        {
            [JsonPropertyName("language")] public string Language { get; set; } = "";
        }
    }

    /// <param name="isHtml">
    /// HTML-Mails mit <c>format=html</c> übersetzen, damit die Auszeichnung erhalten
    /// bleibt – sonst käme Markup als Fliesstext zurück.
    /// </param>
    public async Task<TranslationResult> TranslateAsync(
        string content, string targetLanguage, bool isHtml, CancellationToken ct = default)
    {
        var (url, apiKey) = _config();
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException(
                "Keine Übersetzungs-Adresse hinterlegt (Einstellungen → Allgemein).");

        var endpoint = url.TrimEnd('/') + "/translate";

        var request = new Request
        {
            Q = content,
            Target = string.IsNullOrWhiteSpace(targetLanguage) ? "de" : targetLanguage,
            Format = isHtml ? "html" : "text",
            ApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey,
        };

        using var response = await Http.PostAsJsonAsync(endpoint, request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Übersetzung fehlgeschlagen ({(int)response.StatusCode}): {Shorten(body)}");
        }

        var result = await response.Content.ReadFromJsonAsync<Response>(ct)
            ?? throw new InvalidOperationException("Unerwartete Antwort des Übersetzungsdienstes.");

        return new TranslationResult(
            result.TranslatedText, result.DetectedLanguage?.Language ?? "");
    }

    private static string Shorten(string s)
        => s.Length <= 200 ? s : s[..200] + " …";
}
