using System.Text.Json.Serialization;

namespace NMailClient.Models;

/// <summary>
/// Feldnamen bewusst identisch zu internal/store/store.go (Go-Version),
/// damit db.json der Go-App perspektivisch gelesen werden kann.
/// </summary>
public class Account
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("email")] public string Email { get; set; } = "";
    [JsonPropertyName("user")] public string User { get; set; } = "";
    [JsonPropertyName("password")] public string Password { get; set; } = "";
    [JsonPropertyName("imapHost")] public string ImapHost { get; set; } = "";
    [JsonPropertyName("imapPort")] public int ImapPort { get; set; } = 993;
    [JsonPropertyName("smtpHost")] public string SmtpHost { get; set; } = "";
    [JsonPropertyName("smtpPort")] public int SmtpPort { get; set; } = 587;
    [JsonPropertyName("signature")] public string Signature { get; set; } = "";
    [JsonPropertyName("color")] public string Color { get; set; } = "#4a7dbd";

    /// <summary>
    /// Eigene Reihenfolge der Ordner (vollständige Namen). Nicht gelistete Ordner
    /// folgen alphabetisch dahinter – so tauchen neue Serverordner nicht auf
    /// zufälligen Positionen auf.
    /// </summary>
    [JsonPropertyName("folderOrder")] public List<string> FolderOrder { get; set; } = [];

    /// <summary>Absender-Aliase mit eigener Signatur.</summary>
    [JsonPropertyName("aliases")] public List<AliasDef> Aliases { get; set; } = [];

    /// <summary>
    /// CardDAV-Adresse (Adressbücher). Leer = Kontakte für dieses Konto aus.
    /// Angemeldet wird mit den Zugangsdaten des Kontos.
    /// </summary>
    [JsonPropertyName("cardDavUrl")] public string CardDavUrl { get; set; } = "";

    /// <summary>CalDAV-Adresse (Kalender). Noch ungenutzt – folgt in 0.5.0.</summary>
    [JsonPropertyName("calDavUrl")] public string CalDavUrl { get; set; } = "";

    /// <summary>
    /// ManageSieve-Server für die Filterregeln. Leer = Sieve für dieses Konto
    /// aus. Bei mailcow und Dovecot ist es derselbe Rechner wie IMAP.
    /// </summary>
    [JsonPropertyName("sieveHost")] public string SieveHost { get; set; } = "";

    /// <summary>Vorgabeport für ManageSieve (RFC 5804).</summary>
    [JsonPropertyName("sievePort")] public int SievePort { get; set; } = 4190;

    /// <summary>
    /// Adresse der mailcow-Oberfläche, etwa https://mail.example.org.
    /// Leer = mailcow-Anbindung für dieses Konto aus.
    ///
    /// Der API-Schlüssel steht bewusst <b>nicht</b> hier, sondern im
    /// Anmeldeinformationsverwalter: er ist so mächtig wie ein
    /// Administratorzugang.
    /// </summary>
    [JsonPropertyName("mailcowUrl")] public string MailcowUrl { get; set; } = "";

    /// <summary>Sieve-Zugangsdaten; angemeldet wird wie bei IMAP.</summary>
    [JsonIgnore]
    public Services.Sieve.SieveSettings Sieve
        => new(SieveHost, SievePort <= 0 ? 4190 : SievePort, LoginUser, Password);

    /// <summary>
    /// Alle wählbaren Absender: das Konto selbst zuerst, dann die Aliase.
    /// </summary>
    [JsonIgnore]
    public IEnumerable<AliasDef> SenderOptions =>
        new[] { new AliasDef { Address = Email, Name = Name, Signature = Signature } }
            .Concat(Aliases);

    /// <summary>
    /// Tiefe Kopie. Es gab drei Stellen, die Felder einzeln übertrugen – jedes neue
    /// Feld ging dort still verloren, bis es jemandem auffiel. Neue Felder gehören
    /// deshalb nur noch hier ergänzt.
    /// </summary>
    public Account Clone() => new()
    {
        Id = Id, Name = Name, Email = Email, User = User, Password = Password,
        ImapHost = ImapHost, ImapPort = ImapPort,
        SmtpHost = SmtpHost, SmtpPort = SmtpPort,
        Signature = Signature, Color = Color,
        CardDavUrl = CardDavUrl, CalDavUrl = CalDavUrl,
        SieveHost = SieveHost, SievePort = SievePort,
        MailcowUrl = MailcowUrl,
        FolderOrder = [.. FolderOrder],
        Aliases = Aliases
            .Select(a => new AliasDef { Address = a.Address, Name = a.Name, Signature = a.Signature })
            .ToList(),
    };

    /// <summary>Anmeldename – fällt auf die Adresse zurück.</summary>
    [JsonIgnore] public string LoginUser => string.IsNullOrWhiteSpace(User) ? Email : User;

    [JsonIgnore] public string Display => string.IsNullOrWhiteSpace(Name) ? Email : $"{Name} <{Email}>";

    public override string ToString() => Display;
}

/// <summary>Ein Absender-Alias. Leere Signatur bedeutet: die des Kontos verwenden.</summary>
public class AliasDef
{
    [JsonPropertyName("address")] public string Address { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("signature")] public string Signature { get; set; } = "";

    [JsonIgnore]
    public string Display => string.IsNullOrWhiteSpace(Name) ? Address : $"{Name} <{Address}>";

    public override string ToString() => Display;
}
