using System.Xml.Linq;
using FolkerKinzel.VCards;
using FolkerKinzel.VCards.Enums;
using FolkerKinzel.VCards.Extensions;   // FirstOrNull auf den Property-Listen
using FolkerKinzel.VCards.Models;       // ContactID, NameBuilder
using NMailClient.Poc.Models;

namespace NMailClient.Poc.Services.Dav;

/// <summary>
/// CardDAV-Zugriff: Adressbücher finden, Kontakte lesen, anlegen, ändern, löschen.
///
/// Das vCard-Format übernimmt <c>FolkerKinzel.VCards</c>; selbst geschrieben ist
/// nur die Protokollschicht darum herum – für die es kein gepflegtes .NET-Paket gibt.
/// </summary>
public class CardDavService
{
    private readonly Func<(string Url, string User, string Password)> _config;

    public CardDavService(Func<(string, string, string)> config) => _config = config;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_config().Url);

    private DavHttp CreateClient()
    {
        var (_, user, password) = _config();
        return new DavHttp(user, password);
    }

    public async Task<List<DavCollection>> ListAddressBooksAsync(CancellationToken ct = default)
    {
        var (url, _, _) = _config();
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("Keine CardDAV-Adresse hinterlegt.");

        using var dav = CreateClient();
        return await DavDiscovery.FindCollectionsAsync(dav, url, DavDiscovery.Kind.CardDav, ct);
    }

    /// <summary>Alle Kontakte eines Adressbuchs laden.</summary>
    public async Task<List<Contact>> GetContactsAsync(
        string addressBookUrl, CancellationToken ct = default)
    {
        using var dav = CreateClient();

        // addressbook-query mit address-data holt Karten und ETags in einem Rutsch;
        // ein PROPFIND je Karte wäre bei hunderten Kontakten unbrauchbar langsam.
        var body = new XElement(DavHttp.CardDav + "addressbook-query",
            new XAttribute(XNamespace.Xmlns + "d", DavHttp.D),
            new XAttribute(XNamespace.Xmlns + "c", DavHttp.CardDav),
            new XElement(DavHttp.D + "prop",
                new XElement(DavHttp.D + "getetag"),
                new XElement(DavHttp.CardDav + "address-data")));

        var doc = await dav.ReportAsync(addressBookUrl, body, 1, ct);
        var result = new List<Contact>();

        foreach (var response in doc.Descendants(DavHttp.D + "response"))
        {
            var href = response.Element(DavHttp.D + "href")?.Value;
            var data = response.Descendants(DavHttp.CardDav + "address-data").FirstOrDefault()?.Value;
            if (string.IsNullOrWhiteSpace(href) || string.IsNullOrWhiteSpace(data)) continue;

            var etag = response.Descendants(DavHttp.D + "getetag").FirstOrDefault()?.Value;

            foreach (var contact in Parse(data))
            {
                contact.Url = DavDiscovery.Absolute(addressBookUrl, href);
                contact.ETag = etag;
                result.Add(contact);
            }
        }

        return result
            .OrderBy(c => c.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Anlegen oder Ändern. Liefert den Kontakt mit neuer Adresse und ETag.</summary>
    public async Task<Contact> SaveAsync(
        string addressBookUrl, Contact contact, CancellationToken ct = default)
    {
        using var dav = CreateClient();

        if (string.IsNullOrWhiteSpace(contact.Uid)) contact.Uid = Guid.NewGuid().ToString();

        var url = contact.IsNew
            ? addressBookUrl.TrimEnd('/') + "/" + contact.Uid + ".vcf"
            : contact.Url;

        var etag = await dav.PutAsync(
            url, Serialize(contact), "text/vcard; charset=utf-8", contact.ETag, ct);

        contact.Url = url;

        // Nicht jeder Server meldet das ETag beim PUT; dann beim nächsten Laden holen.
        contact.ETag = etag;
        return contact;
    }

    public async Task DeleteAsync(Contact contact, CancellationToken ct = default)
    {
        if (contact.IsNew) return;

        using var dav = CreateClient();
        await dav.DeleteAsync(contact.Url, contact.ETag, ct);
    }

    // ---- vCard ---------------------------------------------------------------

    /// <summary>vCard-Text zu Kontakten. Öffentlich, weil eigenständig prüfbar.</summary>
    public static List<Contact> Parse(string vcardText)
    {
        var result = new List<Contact>();
        try
        {
            foreach (var card in Vcf.Parse(vcardText)) result.Add(FromVCard(card));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            AppLog.Warn($"vCard nicht lesbar: {ex.Message}");
        }
        return result;
    }

    private static Contact FromVCard(VCard card)
    {
        var id = card.ContactID?.Value;
        var contact = new Contact
        {
            // UID kann Text, GUID oder URI sein – alle drei kommen in freier Wildbahn vor.
            Uid = id?.String ?? id?.Guid?.ToString() ?? id?.Uri?.ToString() ?? "",
            DisplayName = card.DisplayNames?.FirstOrNull()?.Value ?? "",
            Organization = card.Organizations?.FirstOrNull()?.Value.Name ?? "",
        };

        var name = card.NameViews?.FirstOrNull()?.Value;
        if (name is not null)
        {
            contact.FirstName = string.Join(" ", name.Given);
            contact.LastName = string.Join(" ", name.Surnames);
        }

        contact.Emails = card.EMails?
            .Where(e => e is not null && !string.IsNullOrWhiteSpace(e.Value))
            .Select(e => e!.Value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

        contact.Phones = card.Phones?
            .Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Value))
            .Select(p => p!.Value.Trim())
            .ToList() ?? [];

        var birthday = card.BirthDayViews?.FirstOrNull()?.Value;
        if (birthday is not null && birthday.TryAsDateOnly(out var date))
            contact.Birthday = date.ToDateTime(TimeOnly.MinValue);

        contact.Groups = card.Categories?
            .Where(c => c is not null)
            .SelectMany(c => c!.Value ?? [])
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Select(g => g!.Trim())
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList() ?? [];

        return contact;
    }

    public static string Serialize(Contact contact)
    {
        var builder = VCardBuilder.Create()
            .ContactID.Set(ContactID.Create(contact.Uid))
            .DisplayNames.Add(contact.Label);

        if (!string.IsNullOrWhiteSpace(contact.FirstName) || !string.IsNullOrWhiteSpace(contact.LastName))
        {
            var name = NameBuilder.Create()
                .AddSurname(contact.LastName)
                .AddGiven(contact.FirstName)
                .Build();
            builder.NameViews.Add(name);
        }

        if (!string.IsNullOrWhiteSpace(contact.Organization))
            builder.Organizations.Add(contact.Organization);

        foreach (var mail in contact.Emails.Where(e => !string.IsNullOrWhiteSpace(e)))
            builder.EMails.Add(mail);

        foreach (var phone in contact.Phones.Where(p => !string.IsNullOrWhiteSpace(p)))
            builder.Phones.Add(phone);

        if (contact.Birthday is { } bday)
            builder.BirthDayViews.Add(DateOnly.FromDateTime(bday));

        var groups = contact.Groups.Where(g => !string.IsNullOrWhiteSpace(g)).ToArray();
        if (groups.Length > 0) builder.Categories.Add(groups);

        // Version 4.0 – von Dovecot/SabreDAV und allen gängigen Clients unterstützt.
        return Vcf.AsString([builder.VCard], VCdVersion.V4_0);
    }
}
