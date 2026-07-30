using System.IO;
using MimeKit;
using MimeKit.Cryptography;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Security;

namespace NMailClient.Poc.Services.Pgp;

public enum PgpKind { None, Encrypted, Signed }

public enum PgpSignatureState { None, Valid, Invalid, UnknownKey }

/// <summary>Was an einer Nachricht kryptografisch dran war — für die Anzeige.</summary>
public record PgpInfo(
    PgpKind Kind,
    bool Decrypted,
    PgpSignatureState Signature,
    string? Signer,
    string? Detail)
{
    public static readonly PgpInfo None =
        new(PgpKind.None, false, PgpSignatureState.None, null, null);

    public bool IsRelevant => Kind != PgpKind.None;

    /// <summary>Rot anzeigen: kaputte Signatur oder nicht entschlüsselbar.</summary>
    public bool IsWarning =>
        Signature == PgpSignatureState.Invalid
        || (Kind == PgpKind.Encrypted && !Decrypted);

    public bool IsGood => Signature == PgpSignatureState.Valid
                          || (Kind == PgpKind.Encrypted && Decrypted
                              && Signature != PgpSignatureState.Invalid);

    public string Summary
    {
        get
        {
            var parts = new List<string>();

            if (Kind == PgpKind.Encrypted)
                parts.Add(Decrypted ? "Entschlüsselt" : "Verschlüsselt (kein passender Schlüssel)");

            parts.Add(Signature switch
            {
                PgpSignatureState.Valid => $"Signatur gültig ({Signer})",
                PgpSignatureState.Invalid => "Signatur UNGÜLTIG",
                PgpSignatureState.UnknownKey => "Signatur nicht prüfbar (Schlüssel fehlt)",
                _ => "",
            });

            var text = string.Join(" · ", parts.Where(p => p.Length > 0));
            return Detail is null ? text : $"{text} — {Detail}";
        }
    }
}

/// <summary>Der ausgepackte Inhalt einer PGP-Nachricht.</summary>
public record PgpOpened(MimeEntity? Content, PgpInfo Info);

/// <summary>
/// OpenPGP: entschlüsseln, prüfen, signieren, verschlüsseln — und der
/// Schlüsselbund dahinter.
///
/// Der Dienst hält den <see cref="PgpKeyring"/> bewusst kurzlebig: MimeKit lädt
/// die Ringdateien im Konstruktor, ein dauerhaft gehaltener Kontext bekäme also
/// von aussen importierte Schlüssel nicht mit.
/// </summary>
public class PgpService(string? directory = null)
{
    private readonly string _dir = directory ?? PgpKeyring.DefaultDirectory;

    /// <summary>
    /// Fragt das Mantra zu einem geheimen Schlüssel ab, wenn es nicht im
    /// Anmeldeinformationsverwalter liegt. Von der Oberfläche gesetzt.
    /// </summary>
    public Func<PgpKeyInfo, string?>? AskPassword { get; set; }

    /// <summary>Merkt sich das Mantra für diese Sitzung — nie auf der Platte.</summary>
    private readonly Dictionary<long, string> _sessionPasswords = [];

    public string Directory => _dir;

    private PgpKeyring Open()
    {
        System.IO.Directory.CreateDirectory(_dir);
        return new PgpKeyring(_dir) { PasswordProvider = ResolvePassword };
    }

    private string? ResolvePassword(PgpSecretKey key)
    {
        if (_sessionPasswords.TryGetValue(key.KeyId, out var cached)) return cached;

        var target = CredentialStore.GlobalTarget($"pgp:{PgpKeyInfo.FormatKeyId(key.KeyId)}");
        var stored = CredentialStore.Read(target);
        if (!string.IsNullOrEmpty(stored)) return stored;

        var answer = AskPassword?.Invoke(Describe(key));
        if (answer is not null) _sessionPasswords[key.KeyId] = answer;
        return answer;
    }

    /// <summary>Mantra dauerhaft hinterlegen (Anmeldeinformationsverwalter).</summary>
    public void RememberPassword(string keyId, string password)
        => CredentialStore.Save(CredentialStore.GlobalTarget($"pgp:{keyId}"), keyId, password);

    public void ForgetPassword(string keyId)
        => CredentialStore.Delete(CredentialStore.GlobalTarget($"pgp:{keyId}"));

    // ---- Erkennen ----------------------------------------------------------

    /// <summary>
    /// Was ist das? Reine Betrachtung der MIME-Struktur, ohne Schlüsselbund —
    /// damit die Leseansicht auch ohne eingerichtetes PGP richtig entscheidet.
    /// </summary>
    public static PgpKind Classify(MimeEntity? body) => body switch
    {
        MultipartEncrypted => PgpKind.Encrypted,
        MultipartSigned => PgpKind.Signed,
        _ => PgpKind.None,
    };

    /// <summary>Gibt es überhaupt einen Schlüssel? Sonst sparen wir uns alles.</summary>
    public bool HasAnyKey()
    {
        try
        {
            using var ring = Open();
            return ring.EnumeratePublicKeys().Any() || ring.EnumerateSecretKeys().Any();
        }
        catch (Exception ex) when (ex is IOException or PgpException)
        {
            return false;
        }
    }

    // ---- Lesen -------------------------------------------------------------

    /// <summary>
    /// Nachricht auspacken. Wirft nicht: eine kaputte oder fremdverschlüsselte
    /// Mail darf die Leseansicht nicht sprengen, sie wird stattdessen als
    /// solche gemeldet.
    /// </summary>
    public PgpOpened OpenMessage(MimeMessage message, CancellationToken ct = default)
        => message.Body switch
        {
            MultipartEncrypted enc => Decrypt(enc, ct),
            MultipartSigned sig => VerifyOnly(sig, ct),
            _ => new PgpOpened(null, PgpInfo.None),
        };

    private PgpOpened Decrypt(MultipartEncrypted enc, CancellationToken ct)
    {
        try
        {
            using var ring = Open();
            var content = enc.Decrypt(ring, out var signatures, ct);

            // Eine verschlüsselte Mail kann innen signiert sein (SignAndEncrypt);
            // oder der Klartext ist selbst noch einmal multipart/signed.
            var (state, signer, detail) = Evaluate(signatures);
            if (state == PgpSignatureState.None && content is MultipartSigned inner)
            {
                var v = VerifyOnly(inner, ct);
                content = v.Content ?? content;
                (state, signer, detail) = (v.Info.Signature, v.Info.Signer, v.Info.Detail);
            }

            return new PgpOpened(content,
                new PgpInfo(PgpKind.Encrypted, true, state, signer, detail));
        }
        catch (Exception ex) when (ex is PrivateKeyNotFoundException or PgpException)
        {
            AppLog.Warn($"PGP: Nachricht nicht entschlüsselbar — {ex.Message}");
            return new PgpOpened(null,
                new PgpInfo(PgpKind.Encrypted, false, PgpSignatureState.None, null, ex.Message));
        }
        catch (Exception ex) when (ex is IOException or FormatException or InvalidOperationException)
        {
            AppLog.Error("PGP: Entschlüsseln fehlgeschlagen.", ex);
            return new PgpOpened(null,
                new PgpInfo(PgpKind.Encrypted, false, PgpSignatureState.None, null, ex.Message));
        }
    }

    private PgpOpened VerifyOnly(MultipartSigned sig, CancellationToken ct)
    {
        // Der Inhalt steht bei multipart/signed im Klartext davor — den zeigen
        // wir auch dann, wenn die Prüfung nicht möglich ist.
        var content = sig.Count > 0 ? sig[0] : null;

        try
        {
            using var ring = Open();
            var (state, signer, detail) = Evaluate(sig.Verify(ring, ct));
            return new PgpOpened(content, new PgpInfo(PgpKind.Signed, false, state, signer, detail));
        }
        catch (Exception ex) when (ex is PublicKeyNotFoundException or CertificateNotFoundException)
        {
            return new PgpOpened(content,
                new PgpInfo(PgpKind.Signed, false, PgpSignatureState.UnknownKey, null, null));
        }
        catch (Exception ex) when (ex is PgpException or FormatException
                                      or IOException or InvalidOperationException)
        {
            AppLog.Warn($"PGP: Signatur nicht prüfbar — {ex.Message}");
            return new PgpOpened(content,
                new PgpInfo(PgpKind.Signed, false, PgpSignatureState.UnknownKey, null, ex.Message));
        }
    }

    /// <summary>
    /// Eine einzige schlechte Signatur genügt für 'ungültig' — bei Sicherheits-
    /// anzeigen gewinnt immer das schlechtere Ergebnis.
    /// </summary>
    private static (PgpSignatureState, string?, string?) Evaluate(DigitalSignatureCollection? sigs)
    {
        if (sigs is null || sigs.Count == 0) return (PgpSignatureState.None, null, null);

        var state = PgpSignatureState.Valid;
        string? signer = null;
        string? detail = null;

        foreach (var s in sigs)
        {
            signer ??= s.SignerCertificate?.Name is { Length: > 0 } n
                ? $"{n} <{s.SignerCertificate.Email}>"
                : s.SignerCertificate?.Email;

            try
            {
                if (!s.Verify()) state = PgpSignatureState.Invalid;
            }
            catch (Exception ex) when (ex is PublicKeyNotFoundException
                                          or CertificateNotFoundException)
            {
                if (state == PgpSignatureState.Valid) state = PgpSignatureState.UnknownKey;
            }
            catch (Exception ex) when (ex is PgpException or FormatException)
            {
                state = PgpSignatureState.Invalid;
                detail ??= ex.Message;
            }
        }

        return (state, signer, detail);
    }

    // ---- Schreiben ---------------------------------------------------------

    /// <summary>Ist ein geheimer Schlüssel für diesen Absender da?</summary>
    public bool CanSign(MailboxAddress from)
    {
        try { using var ring = Open(); return ring.CanSign(from); }
        catch (Exception ex) when (ex is IOException or PgpException) { return false; }
    }

    /// <summary>
    /// Empfänger ohne öffentlichen Schlüssel. Leere Liste heisst: verschlüsseln
    /// geht. Der Aufrufer bekommt die fehlenden Adressen, damit er sie nennen
    /// kann statt nur 'geht nicht' zu sagen.
    /// </summary>
    public IReadOnlyList<string> RecipientsWithoutKey(IEnumerable<MailboxAddress> to)
    {
        try
        {
            using var ring = Open();
            return to.Where(m => !ring.CanEncrypt(m)).Select(m => m.Address).ToList();
        }
        catch (Exception ex) when (ex is IOException or PgpException)
        {
            return to.Select(m => m.Address).ToList();
        }
    }

    /// <summary>
    /// Den Nachrichtenkörper signieren und/oder verschlüsseln. Gibt den neuen
    /// Körper zurück; der Aufrufer hängt ihn an die <see cref="MimeMessage"/>.
    /// </summary>
    public MimeEntity Protect(
        MailboxAddress from, IEnumerable<MailboxAddress> to, MimeEntity body,
        bool sign, bool encrypt, CancellationToken ct = default)
    {
        if (!sign && !encrypt) return body;

        using var ring = Open();
        var recipients = to.ToList();

        if (encrypt && sign)
            return MultipartEncrypted.SignAndEncrypt(
                ring, from, DigestAlgorithm.Sha256, recipients, body, ct);

        if (encrypt)
            return MultipartEncrypted.Encrypt(ring, recipients, body, ct);

        return MultipartSigned.Create(ring, from, DigestAlgorithm.Sha256, body, ct);
    }

    // ---- Schlüsselverwaltung ----------------------------------------------

    public IReadOnlyList<PgpKeyInfo> ListKeys()
    {
        try
        {
            using var ring = Open();

            var secretIds = ring.EnumerateSecretKeys()
                                .Where(k => k.IsMasterKey)
                                .Select(k => k.KeyId)
                                .ToHashSet();

            return ring.EnumeratePublicKeys()
                       .Where(k => k.IsMasterKey)
                       .Select(k => Describe(k, secretIds.Contains(k.KeyId)))
                       .OrderBy(k => k.UserId, StringComparer.CurrentCultureIgnoreCase)
                       .ToList();
        }
        catch (Exception ex) when (ex is IOException or PgpException)
        {
            AppLog.Error("PGP: Schlüsselbund nicht lesbar.", ex);
            return [];
        }
    }

    private static PgpKeyInfo Describe(PgpPublicKey key, bool isSecret)
    {
        var userId = key.GetUserIds().OfType<string>().FirstOrDefault() ?? "(ohne Kennung)";

        // ValidSeconds == 0 heisst 'laeuft nicht ab' — nicht 'sofort abgelaufen'.
        DateTime? expires = key.GetValidSeconds() > 0
            ? key.CreationTime.AddSeconds(key.GetValidSeconds())
            : null;

        return new PgpKeyInfo(
            PgpKeyInfo.FormatKeyId(key.KeyId), userId, PgpKeyInfo.AddressOf(userId),
            key.CreationTime, expires, key.BitStrength, isSecret);
    }

    private static PgpKeyInfo Describe(PgpSecretKey key) => Describe(key.PublicKey, true);

    /// <summary>
    /// Schlüsseldatei einlesen — geheime wie öffentliche. Wir versuchen bewusst
    /// beide Deutungen: eine exportierte Datei sieht von aussen gleich aus.
    /// </summary>
    public int ImportKeys(string path, CancellationToken ct = default)
    {
        var bytes = File.ReadAllBytes(path);
        using var ring = Open();
        var before = CountKeys(ring);

        var imported = false;

        if (TryRead(bytes, s => new PgpSecretKeyRingBundle(s)) is { Count: > 0 } secret)
        {
            ring.Import(secret, ct);
            ring.SaveSecret();
            imported = true;
        }

        if (TryRead(bytes, s => new PgpPublicKeyRingBundle(s)) is { Count: > 0 } pub)
        {
            ring.Import(pub, ct);
            ring.SavePublic();
            imported = true;
        }

        if (!imported)
            throw new InvalidDataException("Die Datei enthält keine OpenPGP-Schlüssel.");

        return Math.Max(0, CountKeys(ring) - before);
    }

    /// <summary>
    /// Zählt so, wie der Verwalter anzeigt: pro Schlüsselbund einen Eintrag.
    /// Ein Bund enthält neben dem Hauptschlüssel meist einen Unterschlüssel zum
    /// Verschlüsseln — den als zweiten Schlüssel zu melden wäre irreführend.
    /// </summary>
    private static int CountKeys(PgpKeyring ring)
        => ring.EnumeratePublicKeys().Where(k => k.IsMasterKey).Select(k => k.KeyId)
               .Concat(ring.EnumerateSecretKeys().Where(k => k.IsMasterKey).Select(k => k.KeyId))
               .Distinct().Count();

    private static T? TryRead<T>(byte[] bytes, Func<Stream, T> read) where T : class
    {
        try
        {
            using var mem = new MemoryStream(bytes, writable: false);
            using var decoded = PgpUtilities.GetDecoderStream(mem);
            return read(decoded);
        }
        catch (Exception ex) when (ex is PgpException or IOException
                                      or ArgumentException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Öffentlichen Schlüssel exportieren (ASCII-armored, zum Verschicken).</summary>
    public void ExportPublicKey(string keyId, string path, CancellationToken ct = default)
    {
        using var ring = Open();
        var key = ring.EnumeratePublicKeys().FirstOrDefault(
                      k => PgpKeyInfo.FormatKeyId(k.KeyId) == keyId && k.IsMasterKey)
                  ?? throw new InvalidOperationException($"Schlüssel {keyId} nicht gefunden.");

        var address = PgpKeyInfo.AddressOf(key.GetUserIds().OfType<string>().FirstOrDefault() ?? "");
        if (address.Length == 0)
            throw new InvalidOperationException(
                "Der Schlüssel trägt keine E-Mail-Adresse und lässt sich nicht exportieren.");

        using var fs = File.Create(path);
        ring.Export([MailboxAddress.Parse(address)], fs, true, ct);
    }

    /// <summary>Schlüssel entfernen — geheimer Teil zuerst, dann der öffentliche.</summary>
    public void DeleteKey(string keyId)
    {
        using var ring = Open();

        var secret = ring.EnumerateSecretKeyRings()
                         .FirstOrDefault(r => PgpKeyInfo.FormatKeyId(
                             r.GetSecretKey().KeyId) == keyId);
        if (secret is not null)
        {
            ring.Delete(secret);
            ring.SaveSecret();
        }

        var pub = ring.EnumeratePublicKeyRings()
                      .FirstOrDefault(r => PgpKeyInfo.FormatKeyId(
                          r.GetPublicKey().KeyId) == keyId);
        if (pub is not null)
        {
            ring.Delete(pub);
            ring.SavePublic();
        }

        ForgetPassword(keyId);
    }

    /// <summary>
    /// Neues Schlüsselpaar erzeugen. Dauert je nach Rechner mehrere Sekunden —
    /// gehört deshalb in einen Hintergrund-Task.
    /// </summary>
    public PgpKeyInfo GenerateKey(MailboxAddress owner, string password, DateTime? expires = null)
    {
        using var ring = Open();
        ring.GenerateKeyPair(owner, password, expires, EncryptionAlgorithm.Aes256,
                             new SecureRandom());

        var key = ring.EnumerateSecretKeys(owner)
                      .Where(k => k.IsMasterKey)
                      .OrderByDescending(k => k.PublicKey.CreationTime)
                      .First();

        return Describe(key);
    }
}
