using System.IO;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using NMailClient.Models;
using AuthenticationException = MailKit.Security.AuthenticationException;

namespace NMailClient.Services;

public class NewMailEventArgs(string folder, int count) : EventArgs
{
    public string Folder { get; } = folder;
    public int Count { get; } = count;
}

/// <summary>
/// Überwacht den Posteingang per IMAP IDLE (RFC 2177) und meldet neue Nachrichten.
///
/// Nutzt eine **eigene** Verbindung, nicht die von <see cref="ImapService"/>: IDLE
/// belegt die Verbindung dauerhaft, während dort jede Operation über dasselbe
/// Semaphor läuft. Geteilt würde jede Aktion blockieren, bis IDLE endet.
/// </summary>
public sealed class ImapIdleService : IAsyncDisposable
{
    private readonly Account _account;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>
    /// Server müssen IDLE spätestens nach 29 Minuten neu bestätigt bekommen (RFC 2177).
    /// 9 Minuten liegen deutlich darunter und halten zugleich NAT-Zuordnungen wach.
    /// </summary>
    private static readonly TimeSpan IdleRenew = TimeSpan.FromMinutes(9);

    /// <summary>Fallback für Server ohne IDLE.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);

    public event EventHandler<NewMailEventArgs>? NewMail;

    public ImapIdleService(Account account) => _account = account;

    public void Start()
    {
        if (_loop is not null) return;

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        // Wartezeit nach Verbindungsfehlern, verdoppelt sich bis 5 Minuten.
        var backoff = TimeSpan.FromSeconds(10);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await WatchAsync(ct);
                backoff = TimeSpan.FromSeconds(10); // sauber gelaufen
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (AuthenticationException ex)
            {
                // Zugangsdaten ändern sich nicht von selbst – nicht endlos neu anmelden,
                // sonst sammelt der Server Fehlversuche und sperrt die IP.
                AppLog.Warn($"IDLE für {_account.Email} beendet: Anmeldung abgelehnt ({ex.Message}).");
                return;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"IDLE für {_account.Email} unterbrochen: {ex.Message}. "
                            + $"Neuer Versuch in {backoff.TotalSeconds:0} s.");
            }

            try { await Task.Delay(backoff, ct); }
            catch (OperationCanceledException) { return; }

            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 300));
        }
    }

    private async Task WatchAsync(CancellationToken ct)
    {
        using var client = new ImapClient();

        var options = _account.ImapPort == 993
            ? SecureSocketOptions.SslOnConnect
            : SecureSocketOptions.StartTls;

        var socket = await Transport.HappyEyeballs.ConnectAsync(
            _account.ImapHost, _account.ImapPort, ct);

        await client.ConnectAsync(socket, _account.ImapHost, _account.ImapPort, options, ct);
        await client.AuthenticateAsync(_account.LoginUser, _account.Password, ct);

        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly, ct);

        int known = inbox.Count;

        // CountChanged feuert auch bei Löschungen; die Differenz entscheidet.
        void OnCountChanged(object? sender, EventArgs e)
        {
            var now = inbox.Count;
            if (now > known) NewMail?.Invoke(this, new NewMailEventArgs(inbox.FullName, now - known));
            known = now;
        }

        inbox.CountChanged += OnCountChanged;
        try
        {
            var supportsIdle = client.Capabilities.HasFlag(ImapCapabilities.Idle);
            if (!supportsIdle)
                AppLog.Info($"{_account.ImapHost} kennt kein IDLE – es wird alle "
                            + $"{PollInterval.TotalSeconds:0} s nachgesehen.");

            while (!ct.IsCancellationRequested)
            {
                if (supportsIdle)
                {
                    using var renew = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    renew.CancelAfter(IdleRenew);
                    try
                    {
                        await client.IdleAsync(renew.Token, ct);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // Nur die Erneuerungsfrist abgelaufen – erneut in IDLE gehen.
                    }
                }
                else
                {
                    await Task.Delay(PollInterval, ct);
                    await client.NoOpAsync(ct);
                }
            }
        }
        finally
        {
            inbox.CountChanged -= OnCountChanged;
            if (client.IsConnected)
            {
                try { await client.DisconnectAsync(true, CancellationToken.None); }
                catch (Exception ex) when (ex is IOException or ImapProtocolException) { }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is null) return;

        await _cts.CancelAsync();
        try
        {
            if (_loop is not null) await _loop;
        }
        catch (Exception ex) when (ex is OperationCanceledException or ImapProtocolException)
        {
            // Beim Beenden ist ein sauberer Abbau nice-to-have.
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _loop = null;
        }
    }
}
