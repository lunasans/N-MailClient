using System.Xml.Linq;

namespace NMailClient.Poc.Services.Dav;

/// <summary>Eine gefundene Sammlung (Adressbuch oder Kalender).</summary>
public record DavCollection(string Url, string DisplayName);

/// <summary>
/// Findet Adressbücher und Kalender zu einer Basisadresse.
///
/// Der Weg ist in RFC 6764 beschrieben und läuft in drei Schritten:
/// <c>/.well-known/carddav</c> → <c>current-user-principal</c> →
/// <c>addressbook-home-set</c> → die Sammlungen darin. Nutzer geben aber gern
/// direkt die Adressbuch-Adresse an, deshalb wird jeder Schritt übersprungen,
/// wenn er schon am Ziel ist.
/// </summary>
public static class DavDiscovery
{
    public enum Kind { CardDav, CalDav }

    public static async Task<List<DavCollection>> FindCollectionsAsync(
        DavHttp dav, string baseUrl, Kind kind, CancellationToken ct = default)
    {
        var start = Normalize(baseUrl, kind);

        // 1. Principal des angemeldeten Nutzers.
        var principal = await FindPrincipalAsync(dav, start, ct) ?? start;

        // 2. Wurzel der Sammlungen dieses Nutzers.
        var home = await FindHomeSetAsync(dav, principal, kind, ct) ?? principal;

        // 3. Sammlungen darunter.
        var collections = await ListCollectionsAsync(dav, home, kind, ct);

        // Zeigte die angegebene Adresse direkt auf eine Sammlung, steht sie hier
        // nicht drin – dann diese Adresse selbst prüfen.
        if (collections.Count == 0)
            collections = await ListCollectionsAsync(dav, start, kind, ct, selfOnly: true);

        return collections;
    }

    /// <summary>Basisadresse aufbereiten; ohne Pfad den .well-known-Einstieg nutzen.</summary>
    private static string Normalize(string baseUrl, Kind kind)
    {
        var url = baseUrl.Trim();
        if (url.Length == 0) throw new ArgumentException("Keine Adresse angegeben.", nameof(baseUrl));

        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;

        var uri = new Uri(url);
        if (uri.AbsolutePath is "/" or "")
        {
            var wellKnown = kind == Kind.CardDav ? "/.well-known/carddav" : "/.well-known/caldav";
            return new Uri(uri, wellKnown).ToString();
        }
        return url;
    }

    private static async Task<string?> FindPrincipalAsync(
        DavHttp dav, string url, CancellationToken ct)
    {
        var body = new XElement(DavHttp.D + "propfind",
            new XAttribute(XNamespace.Xmlns + "d", DavHttp.D),
            new XElement(DavHttp.D + "prop",
                new XElement(DavHttp.D + "current-user-principal")));

        try
        {
            var doc = await dav.PropFindAsync(url, body, 0, ct);
            var href = doc.Descendants(DavHttp.D + "current-user-principal")
                .Descendants(DavHttp.D + "href").FirstOrDefault()?.Value;

            return string.IsNullOrWhiteSpace(href) ? null : Absolute(url, href);
        }
        catch (DavException ex)
        {
            AppLog.Warn($"Principal-Suche auf {url} fehlgeschlagen: {ex.Message}");
            return null;
        }
    }

    private static async Task<string?> FindHomeSetAsync(
        DavHttp dav, string principalUrl, Kind kind, CancellationToken ct)
    {
        var ns = kind == Kind.CardDav ? DavHttp.CardDav : DavHttp.CalDav;
        var name = kind == Kind.CardDav ? "addressbook-home-set" : "calendar-home-set";

        var body = new XElement(DavHttp.D + "propfind",
            new XAttribute(XNamespace.Xmlns + "d", DavHttp.D),
            new XAttribute(XNamespace.Xmlns + "x", ns),
            new XElement(DavHttp.D + "prop", new XElement(ns + name)));

        try
        {
            var doc = await dav.PropFindAsync(principalUrl, body, 0, ct);
            var href = doc.Descendants(ns + name)
                .Descendants(DavHttp.D + "href").FirstOrDefault()?.Value;

            return string.IsNullOrWhiteSpace(href) ? null : Absolute(principalUrl, href);
        }
        catch (DavException ex)
        {
            AppLog.Warn($"Home-Set-Suche auf {principalUrl} fehlgeschlagen: {ex.Message}");
            return null;
        }
    }

    private static async Task<List<DavCollection>> ListCollectionsAsync(
        DavHttp dav, string homeUrl, Kind kind, CancellationToken ct, bool selfOnly = false)
    {
        var body = new XElement(DavHttp.D + "propfind",
            new XAttribute(XNamespace.Xmlns + "d", DavHttp.D),
            new XElement(DavHttp.D + "prop",
                new XElement(DavHttp.D + "resourcetype"),
                new XElement(DavHttp.D + "displayname")));

        var result = new List<DavCollection>();
        try
        {
            var doc = await dav.PropFindAsync(homeUrl, body, selfOnly ? 0 : 1, ct);
            var marker = kind == Kind.CardDav
                ? DavHttp.CardDav + "addressbook"
                : DavHttp.CalDav + "calendar";

            foreach (var response in doc.Descendants(DavHttp.D + "response"))
            {
                var isMatch = response.Descendants(DavHttp.D + "resourcetype")
                    .Elements().Any(e => e.Name == marker);
                if (!isMatch) continue;

                var href = response.Element(DavHttp.D + "href")?.Value;
                if (string.IsNullOrWhiteSpace(href)) continue;

                var name = response.Descendants(DavHttp.D + "displayname").FirstOrDefault()?.Value;
                var url = Absolute(homeUrl, href);

                result.Add(new DavCollection(
                    url, string.IsNullOrWhiteSpace(name) ? LastSegment(url) : name));
            }
        }
        catch (DavException ex)
        {
            AppLog.Warn($"Sammlungen unter {homeUrl} nicht lesbar: {ex.Message}");
        }
        return result;
    }

    /// <summary>Relative href-Angaben gegen die Anfrageadresse auflösen.</summary>
    public static string Absolute(string requestUrl, string href)
        => Uri.TryCreate(href, UriKind.Absolute, out var abs)
            ? abs.ToString()
            : new Uri(new Uri(requestUrl), href).ToString();

    public static string LastSegment(string url)
    {
        var trimmed = url.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash >= 0 && slash < trimmed.Length - 1 ? trimmed[(slash + 1)..] : trimmed;
    }
}
