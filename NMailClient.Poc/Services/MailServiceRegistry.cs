using NMailClient.Poc.Models;

namespace NMailClient.Poc.Services;

/// <summary>
/// Hält je Konto genau eine IMAP- und SMTP-Instanz.
///
/// Bewusst als eigener Dienst und nicht im ViewModel: die Lebensdauer ist an die
/// Anwendung gebunden, nicht an eine Ansicht. Ausserdem hängt an der Wiederverwendung
/// die Auth-Sperre – eine neue Instanz je Aufruf könnte sich eine abgelehnte
/// Anmeldung nicht merken und liefe in fail2ban-Sperren.
/// </summary>
public sealed class MailServiceRegistry : IAsyncDisposable
{
    private readonly Dictionary<string, ImapService> _imap = new();
    private readonly Dictionary<string, SmtpService> _smtp = new();
    private readonly Dictionary<string, ImapIdleService> _idle = new();
    private readonly Dictionary<string, Dav.CardDavService> _cardDav = new();
    private readonly Dictionary<string, Dav.CalDavService> _calDav = new();
    private readonly Dictionary<string, Sieve.ManageSieveClient> _sieve = new();
    private readonly Dictionary<string, Mailcow.MailcowClient> _mailcow = new();

    /// <summary>
    /// Der OpenPGP-Schlüsselbund gilt für die ganze Anwendung, nicht je Konto:
    /// wer zwei Adressen hat, will denselben Bestand an fremden Schlüsseln nutzen.
    /// </summary>
    public Pgp.PgpService Pgp { get; } = new();

    /// <summary>
    /// Der lokale Zwischenspeicher, ebenfalls für alle Konten gemeinsam — er
    /// trennt intern nach der Konto-Kennung.
    /// </summary>
    public Cache.MailCache Cache { get; } = new();

    public ImapService Imap(Account account)
    {
        if (!_imap.TryGetValue(account.Id, out var svc))
        {
            svc = new ImapService(account, Pgp, Cache);
            _imap[account.Id] = svc;
        }
        return svc;
    }

    public SmtpService Smtp(Account account)
    {
        if (!_smtp.TryGetValue(account.Id, out var svc))
        {
            svc = new SmtpService(account);
            _smtp[account.Id] = svc;
        }
        return svc;
    }

    /// <summary>
    /// IDLE-Überwachung des Posteingangs – eigene Verbindung je Konto, siehe
    /// <see cref="ImapIdleService"/>. Startet nicht von selbst.
    /// </summary>
    public ImapIdleService Idle(Account account)
    {
        if (!_idle.TryGetValue(account.Id, out var svc))
        {
            svc = new ImapIdleService(account);
            _idle[account.Id] = svc;
        }
        return svc;
    }

    /// <summary>
    /// CardDAV-Zugriff für ein Konto. Die Zugangsdaten stammen vom Konto selbst –
    /// bei mailcow/Dovecot ist das dieselbe Anmeldung wie für IMAP.
    /// </summary>
    public Dav.CardDavService CardDav(Account account)
    {
        if (!_cardDav.TryGetValue(account.Id, out var svc))
        {
            // Werte bei jedem Aufruf frisch lesen: eine Änderung in den
            // Kontoeinstellungen soll ohne Neustart greifen.
            svc = new Dav.CardDavService(
                () => (account.CardDavUrl, account.LoginUser, account.Password));
            _cardDav[account.Id] = svc;
        }
        return svc;
    }

    /// <summary>
    /// ManageSieve-Zugriff für ein Konto. Wiederverwendet wie IMAP und SMTP —
    /// nur so kann sich der Client eine abgelehnte Anmeldung merken.
    /// </summary>
    public Sieve.ManageSieveClient Sieve(Account account)
    {
        if (!_sieve.TryGetValue(account.Id, out var svc))
        {
            svc = new Sieve.ManageSieveClient(() => account.Sieve);
            _sieve[account.Id] = svc;
        }
        return svc;
    }

    /// <summary>
    /// mailcow-Zugriff für ein Konto. Der API-Schlüssel wird bei jedem Aufruf
    /// frisch aus dem Anmeldeinformationsverwalter gelesen — so wirkt eine
    /// Änderung sofort und der Schlüssel liegt nirgends zwischengespeichert.
    /// </summary>
    public Mailcow.MailcowClient Mailcow(Account account)
    {
        if (!_mailcow.TryGetValue(account.Id, out var svc))
        {
            svc = new Mailcow.MailcowClient(() => new Mailcow.MailcowSettings(
                account.MailcowUrl,
                CredentialStore.Read(CredentialStore.TargetFor(account.Id, "mailcow")) ?? "",
                account.Email));

            _mailcow[account.Id] = svc;
        }
        return svc;
    }

    /// <summary>CalDAV-Zugriff für ein Konto, analog zu <see cref="CardDav"/>.</summary>
    public Dav.CalDavService CalDav(Account account)
    {
        if (!_calDav.TryGetValue(account.Id, out var svc))
        {
            svc = new Dav.CalDavService(
                () => (account.CalDavUrl, account.LoginUser, account.Password));
            _calDav[account.Id] = svc;
        }
        return svc;
    }

    /// <summary>
    /// Nach geänderten Zugangsdaten: bestehende Verbindungen und die gemerkte
    /// Auth-Ablehnung verwerfen, damit der nächste Zugriff neu anmeldet.
    /// </summary>
    public void Reset(string accountId)
    {
        if (_imap.Remove(accountId, out var imap)) _ = imap.DisposeAsync();
        if (_idle.Remove(accountId, out var idle)) _ = idle.DisposeAsync();
        _smtp.Remove(accountId);
        _cardDav.Remove(accountId);
        if (_sieve.Remove(accountId, out var sieve)) _ = sieve.DisposeAsync();
        _mailcow.Remove(accountId);
        _calDav.Remove(accountId);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var svc in _idle.Values) await svc.DisposeAsync();
        foreach (var svc in _imap.Values) await svc.DisposeAsync();
        foreach (var svc in _sieve.Values) await svc.DisposeAsync();
        _idle.Clear();
        _imap.Clear();
        _smtp.Clear();
        _sieve.Clear();
        _mailcow.Clear();
    }
}
