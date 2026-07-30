using System.IO;
using MimeKit;
using MimeKit.Cryptography;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace NMailClient.Poc.Services.Pgp;

/// <summary>
/// Der OpenPGP-Schlüsselbund der Anwendung.
///
/// MimeKit bringt mit <see cref="GnuPGContext"/> bereits das Lesen und Schreiben
/// der klassischen Ringdateien mit; wir geben ihm ein <em>eigenes</em> Verzeichnis
/// (<c>%APPDATA%\NMailClient.Poc\pgp</c>) statt <c>%APPDATA%\gnupg</c>. Grund: das
/// heutige GnuPG legt öffentliche Schlüssel im Keybox-Format (<c>pubring.kbx</c>)
/// ab, das MimeKit nicht liest — wir würden also entweder nichts finden oder,
/// schlimmer, den Bestand des Benutzers überschreiben.
///
/// Mantras dieser Klasse: Passwörter kommen aus dem Anmeldeinformationsverwalter,
/// niemals aus einer Datei.
/// </summary>
public sealed class PgpKeyring : GnuPGContext
{
    /// <summary>Liefert das Mantra zu einem geheimen Schlüssel; von aussen gesetzt.</summary>
    public Func<PgpSecretKey, string?>? PasswordProvider { get; set; }

    public PgpKeyring(string directory) : base(directory) { }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NMailClient.Poc", "pgp");

    protected override string GetPasswordForKey(PgpSecretKey key)
        => PasswordProvider?.Invoke(key) ?? "";

    /// <summary>
    /// Die beiden Speichermethoden sind in MimeKit <c>protected</c>; da Import
    /// und Delete auf dem Bündel im Arbeitsspeicher arbeiten, muss der Dienst
    /// sie erreichen können.
    /// </summary>
    public void SavePublic() => SavePublicKeyRingBundle();

    public void SaveSecret() => SaveSecretKeyRingBundle();
}

/// <summary>Ein Schlüssel, aufbereitet für die Anzeige im Verwalter.</summary>
public record PgpKeyInfo(
    string KeyId,
    string UserId,
    string Address,
    DateTime Created,
    DateTime? Expires,
    int BitStrength,
    bool IsSecret)
{
    /// <summary>Abgelaufen? Ein Schlüssel ohne Ablaufdatum gilt unbegrenzt.</summary>
    public bool IsExpired => Expires is { } e && e < DateTime.UtcNow;

    public string ExpiryDisplay => Expires switch
    {
        null => "unbegrenzt",
        { } e when e < DateTime.UtcNow => $"abgelaufen am {e.ToLocalTime():d}",
        { } e => $"bis {e.ToLocalTime():d}",
    };

    public string Kind => IsSecret ? "Geheim" : "Öffentlich";

    public string Display => $"{UserId} — {KeyId} ({BitStrength} Bit, {ExpiryDisplay})";

    /// <summary>
    /// Die kurze Kennung, wie PGP-Werkzeuge sie zeigen: die letzten 16 Stellen.
    /// </summary>
    public static string FormatKeyId(long id) => ((ulong)id).ToString("X16");

    /// <summary>
    /// Aus der Benutzerkennung („Rene &lt;rene@example.org&gt;") die reine Adresse
    /// ziehen. Schlüssel ohne wohlgeformte Kennung liefern einen leeren String,
    /// damit die Zuordnung zu einem Empfänger nicht versehentlich zufällig trifft.
    /// </summary>
    public static string AddressOf(string userId)
    {
        var open = userId.LastIndexOf('<');
        var close = userId.LastIndexOf('>');
        if (open >= 0 && close > open)
            return userId[(open + 1)..close].Trim();

        // Manche Schlüssel tragen nur die nackte Adresse als Kennung.
        return userId.Contains('@') && !userId.Contains(' ') ? userId.Trim() : "";
    }
}
