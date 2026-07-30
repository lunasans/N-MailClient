using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace NMailClient.Services.Dav;

/// <summary>
/// Dünne WebDAV-Schicht für CalDAV und CardDAV.
///
/// Bewusst direkt auf <see cref="HttpClient"/> und nicht über das Paket
/// <c>WebDav.Client</c>: das kennt zwar PROPFIND, aber nicht die Methode
/// <c>REPORT</c>, auf der beide Protokolle für Abfragen und Abgleich beruhen.
/// Für die Ablage von Anhängen auf WebDAV (0.6.0) bleibt das Paket die richtige Wahl.
/// </summary>
public sealed class DavHttp : IDisposable
{
    public static readonly XNamespace D = "DAV:";
    public static readonly XNamespace CardDav = "urn:ietf:params:xml:ns:carddav";
    public static readonly XNamespace CalDav = "urn:ietf:params:xml:ns:caldav";

    private readonly HttpClient _http;

    public DavHttp(string userName, string password, TimeSpan? timeout = null)
    {
        _http = Transport.HttpFactory.Create(
            timeout ?? TimeSpan.FromSeconds(30),
            new NetworkCredential(userName, password));

        // Manche Server (u.a. SabreDAV, das mailcow nutzt) antworten ohne Basic-Auth
        // im ersten Anlauf mit 401 und erwarten den Header direkt.
        var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{userName}:{password}"));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", raw);
    }

    /// <summary>PROPFIND mit angegebener Tiefe (0 = nur die Ressource, 1 = plus Kinder).</summary>
    public Task<XDocument> PropFindAsync(
        string url, XElement body, int depth, CancellationToken ct = default)
        => SendAsync("PROPFIND", url, body, depth.ToString(), ct);

    /// <summary>REPORT – Grundlage der CalDAV-/CardDAV-Abfragen.</summary>
    public Task<XDocument> ReportAsync(
        string url, XElement body, int depth = 1, CancellationToken ct = default)
        => SendAsync("REPORT", url, body, depth.ToString(), ct);

    private async Task<XDocument> SendAsync(
        string method, string url, XElement body, string depth, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        request.Headers.Add("Depth", depth);
        request.Content = new StringContent(
            new XDocument(body).ToString(), Encoding.UTF8, "application/xml");

        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, method, url, ct);

        var text = await response.Content.ReadAsStringAsync(ct);
        return string.IsNullOrWhiteSpace(text) ? new XDocument() : XDocument.Parse(text);
    }

    public async Task<string> GetStringAsync(string url, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(url, ct);
        await EnsureSuccessAsync(response, "GET", url, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>Anlegen oder Ersetzen einer Ressource. Liefert das neue ETag, falls gemeldet.</summary>
    public async Task<string?> PutAsync(
        string url, string content, string contentType, string? ifMatch = null,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = new StringContent(content, Encoding.UTF8, contentType),
        };

        // ETag mitgeben, damit fremde Änderungen nicht überschrieben werden.
        if (!string.IsNullOrEmpty(ifMatch)) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        else request.Headers.TryAddWithoutValidation("If-None-Match", "*");

        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, "PUT", url, ct);

        return response.Headers.ETag?.Tag;
    }

    public async Task DeleteAsync(string url, string? ifMatch = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, url);
        if (!string.IsNullOrEmpty(ifMatch)) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);

        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, "DELETE", url, ct);
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response, string method, string url, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        var detail = await response.Content.ReadAsStringAsync(ct);
        var hint = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => " – Zugangsdaten prüfen.",
            HttpStatusCode.NotFound => " – Adresse prüfen.",
            HttpStatusCode.PreconditionFailed => " – die Ressource wurde zwischenzeitlich geändert.",
            _ => "",
        };

        throw new DavException(
            $"{method} {url} fehlgeschlagen ({(int)response.StatusCode} {response.ReasonPhrase}){hint}",
            response.StatusCode,
            detail.Length > 400 ? detail[..400] : detail);
    }

    public void Dispose() => _http.Dispose();
}

public class DavException(string message, HttpStatusCode status, string detail)
    : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
    public string Detail { get; } = detail;
}
