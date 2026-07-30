using MailKit.Net.Smtp;
using MailKit.Security;
using AuthenticationException = MailKit.Security.AuthenticationException;
using MimeKit;
using NMailClient.Models;

namespace NMailClient.Services;

/// <summary>Pendant zu internal/mail/smtp.go.</summary>
public class SmtpService
{
    private readonly Account _account;

    /// <summary>
    /// Gesetzt, sobald der Server die Anmeldedaten abgelehnt hat – analog zu
    /// <see cref="ImapService"/>. Damit erzeugt wiederholtes Klicken auf „Senden"
    /// nicht jedes Mal einen weiteren Fehlversuch beim Server.
    ///
    /// Wirksam nur, weil der Service pro Konto wiederverwendet wird
    /// (MainViewModel.SmtpFor); eine Instanz je Sendevorgang könnte sich nichts merken.
    /// </summary>
    private string? _authFailure;

    public SmtpService(Account account) => _account = account;

    /// <summary>Nach Aenderung der Zugangsdaten wieder freigeben.</summary>
    public void ResetAuthFailure() => _authFailure = null;

    /// <summary>
    /// Baut die Nachricht aus den Composer-Feldern – gemeinsam genutzt vom Versand
    /// und vom Entwurfs-Speichern.
    /// </summary>
    /// <param name="lenient">
    /// Für Entwürfe: fehlende Empfänger sind erlaubt, unvollständige Adressen werden
    /// als Freitext übernommen statt abgewiesen – der Nutzer tippt ja noch.
    /// </param>
    /// <param name="html">
    /// Wenn gesetzt, wird multipart/alternative gesendet: HTML plus die Textfassung
    /// aus <paramref name="body"/>. Empfänger ohne HTML-Darstellung bekommen so
    /// etwas Lesbares statt Markup im Klartext.
    /// </param>
    /// <param name="sender">
    /// Absender-Alias; null bedeutet die Hauptadresse des Kontos.
    /// </param>
    /// <param name="requestReceipt">
    /// Setzt <c>Disposition-Notification-To</c> – der Empfänger *kann* eine
    /// Lesebestätigung schicken, muss aber nicht. Kein verlässlicher Nachweis.
    /// </param>
    public static async Task<MimeMessage> BuildMessageAsync(
        Account account, string to, string cc, string subject, string body,
        IEnumerable<string>? attachmentPaths, bool lenient,
        string? inReplyTo = null, IEnumerable<string>? references = null,
        string? html = null, string bcc = "", AliasDef? sender = null,
        bool requestReceipt = false,
        Pgp.PgpService? pgp = null, bool sign = false, bool encrypt = false,
        CancellationToken ct = default)
    {
        var msg = new MimeMessage();

        var from = sender is null || string.IsNullOrWhiteSpace(sender.Address)
            ? new MailboxAddress(account.Name, account.Email)
            : new MailboxAddress(sender.Name, sender.Address);
        msg.From.Add(from);

        if (requestReceipt) msg.Headers.Add("Disposition-Notification-To", from.Address);

        // Thread-Verkettung (RFC 5322): References = Kette des Originals + dessen ID.
        if (!string.IsNullOrEmpty(inReplyTo))
        {
            msg.InReplyTo = inReplyTo;
            foreach (var r in references ?? []) msg.References.Add(r);
            if (!msg.References.Contains(inReplyTo)) msg.References.Add(inReplyTo);
        }

        foreach (var addr in ParseAddresses(to, lenient)) msg.To.Add(addr);
        foreach (var addr in ParseAddresses(cc, lenient)) msg.Cc.Add(addr);
        foreach (var addr in ParseAddresses(bcc, lenient)) msg.Bcc.Add(addr);
        if (!lenient && msg.To.Count == 0 && msg.Cc.Count == 0 && msg.Bcc.Count == 0)
            throw new InvalidOperationException("Kein Empfänger angegeben.");

        msg.Subject = subject;

        var builder = new BodyBuilder { TextBody = body };
        if (!string.IsNullOrWhiteSpace(html))
            builder.HtmlBody = $"<html><body>{html}</body></html>";

        foreach (var path in attachmentPaths ?? Enumerable.Empty<string>())
            await builder.Attachments.AddAsync(path, ct);
        msg.Body = builder.ToMessageBody();

        // OpenPGP zuletzt: signiert und verschlüsselt wird der fertige Körper
        // samt Anhängen. Betreff und Empfänger bleiben sichtbar — das ist bei
        // PGP/MIME so und keine Nachlässigkeit.
        if (pgp is not null && (sign || encrypt))
        {
            // Bcc gehört mit in die Empfängerliste, sonst kann der verdeckte
            // Empfänger die Mail nicht öffnen.
            var recipients = msg.To.Mailboxes
                                .Concat(msg.Cc.Mailboxes)
                                .Concat(msg.Bcc.Mailboxes)
                                .ToList();

            msg.Body = pgp.Protect(from, recipients, msg.Body, sign, encrypt, ct);
        }

        return msg;
    }

    public async Task SendAsync(
        string to, string cc, string subject, string body,
        IEnumerable<string>? attachmentPaths = null,
        string? inReplyTo = null, IEnumerable<string>? references = null,
        string? html = null, string bcc = "", AliasDef? sender = null,
        bool requestReceipt = false,
        Pgp.PgpService? pgp = null, bool sign = false, bool encrypt = false,
        CancellationToken ct = default)
    {
        if (_authFailure is not null) throw new AuthenticationException(_authFailure);

        var msg = await BuildMessageAsync(
            _account, to, cc, subject, body, attachmentPaths, lenient: false,
            inReplyTo, references, html, bcc, sender, requestReceipt,
            pgp, sign, encrypt, ct);

        await SendAsync(msg, ct);
    }

    /// <summary>
    /// Sendet eine fertige Nachricht – Weg der Ausgangs-Warteschlange, die ihre
    /// Mails als .eml zwischenspeichert.
    /// </summary>
    public async Task SendAsync(MimeMessage msg, CancellationToken ct = default)
    {
        if (_authFailure is not null) throw new AuthenticationException(_authFailure);

        using var client = new SmtpClient();
        var options = _account.SmtpPort == 465
            ? SecureSocketOptions.SslOnConnect
            : SecureSocketOptions.StartTls;

        // Siehe HappyEyeballs: eine unerreichbare Adresse vorn in der Liste darf
        // den Versand nicht um die volle TCP-Frist verzögern.
        var socket = await Transport.HappyEyeballs.ConnectAsync(
            _account.SmtpHost, _account.SmtpPort, ct);

        await client.ConnectAsync(socket, _account.SmtpHost, _account.SmtpPort, options, ct);
        try
        {
            await client.AuthenticateAsync(_account.LoginUser, _account.Password, ct);
        }
        catch (AuthenticationException ex)
        {
            _authFailure = $"Anmeldung am Postausgangsserver abgelehnt ({ex.Message}). "
                           + "Zugangsdaten in den Kontoeinstellungen prüfen.";
            AppLog.Warn($"SMTP-Anmeldung für {_account.Email} abgelehnt.");
            throw new AuthenticationException(_authFailure, ex);
        }

        await client.SendAsync(msg, ct);
        await client.DisconnectAsync(true, ct);
    }

    private static IEnumerable<MailboxAddress> ParseAddresses(string input, bool lenient = false)
    {
        if (string.IsNullOrWhiteSpace(input)) yield break;
        foreach (var raw in input.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var s = raw.Trim();
            if (s.Length == 0) continue;

            // MimeKit akzeptiert auch domainlose Eingaben ("foo") – beim Versand
            // würde das erst am Server platzen. Streng heisst deshalb: mit Domain.
            if (MailboxAddress.TryParse(s, out var addr)
                && (lenient || addr.Address.Contains('@')))
                yield return addr;
            else if (!lenient) throw new FormatException($"Ungültige Adresse: {s}");
            // lenient: halbfertige Eingaben still übergehen – der Entwurf hält den
            // Text ohnehin im To-Feld des nächsten Ladevorgangs nicht fest, aber
            // das Speichern darf daran nicht scheitern.
        }
    }
}
