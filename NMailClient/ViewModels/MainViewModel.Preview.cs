using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Threading;
using NMailClient.Models;
using NMailClient.Services;

namespace NMailClient.ViewModels;

/// <summary>Vorschau: Nachricht laden, darstellen, Bilder, Uebersetzung.</summary>
public partial class MainViewModel
{
    private async Task LoadBodyAsync()
    {
        _showRemoteImages = false;
        RaiseAuthChanged();

        // Übersetzung gehört zur vorherigen Nachricht.
        _translatedHtml = null;
        ShowingTranslation = false;
        TranslationInfo = "";

        if (SelectedMessage is not { } msg || OriginOf(msg) is not var (account, folder))
        {
            Body = null;
            BodyHtml = "";
            return;
        }

        Busy = true;
        try
        {
            // Konto und Ordner kommen von der Nachricht, nicht von der Auswahl:
            // im gemeinsamen Posteingang stammt die Zeile darunter womöglich
            // aus einem anderen Postfach.
            Body = await ImapFor(account).GetBodyAsync(folder, msg.Uid);

            // Vertrauter Absender: Bilder gleich laden, ohne Banner.
            _showRemoteImages = Settings.IsTrusted(Body.FromAddress);

            RaiseAuthChanged();
            RenderBody();

            if (!msg.Seen)
            {
                await ImapFor(account).SetSeenAsync(folder, msg.Uid, true);
                msg.Seen = true;
                if (SelectedFolder is { } node && node.Unread > 0) node.Unread--;
            }
            Status = Body.Subject;
        }
        catch (Exception ex)
        {
            Status = $"Fehler beim Öffnen: {ex.Message}";
        }
        finally { Busy = false; }
    }

    private void RenderBody()
    {
        if (Body is not { } b)
        {
            BodyHtml = "";
            ImagesBlocked = false;
            return;
        }

        // Übersetzung hat Vorrang, solange sie angezeigt wird.
        if (ShowingTranslation && _translatedHtml is { } translated)
        {
            if (_translatedIsHtml)
            {
                var (html, blocked) = HtmlSanitizer.Sanitize(translated, _showRemoteImages);
                BodyHtml = html;
                ImagesBlocked = blocked;
            }
            else
            {
                BodyHtml = HtmlSanitizer.FromPlainText(translated);
                ImagesBlocked = false;
            }
            return;
        }

        if (!string.IsNullOrWhiteSpace(b.Html))
        {
            var (html, blocked) = HtmlSanitizer.Sanitize(b.Html!, _showRemoteImages);
            BodyHtml = html;
            ImagesBlocked = blocked;
        }
        else
        {
            BodyHtml = HtmlSanitizer.FromPlainText(b.Text);
            ImagesBlocked = false;
        }
    }

    public void ShowRemoteImages()
    {
        _showRemoteImages = true;
        RenderBody();
    }

    // ---- Absender-Authentifizierung -----------------------------------------

    public bool HasAuthInfo => Body?.Authentication.HasAnyResult == true
                               || Body?.Authentication.DisplayNameSpoof == true;

    public bool AuthIsWarning => Body?.Authentication.IsWarning == true;

    public string AuthSummary => Body?.Authentication.Summary ?? "";

    /// <summary>Farbige Marken je Prüfverfahren.</summary>
    public IReadOnlyList<AuthBadge> AuthBadges => Body?.Authentication.Badges ?? [];

    /// <summary>Erläuterung zum vorgetäuschten Absender; leer, wenn alles stimmt.</summary>
    public string SpoofWarning => Body?.Authentication.SpoofDetail ?? "";

    public bool HasSpoofWarning => Body?.Authentication.DisplayNameSpoof == true;

    /// <summary>Alle von der Authentifizierung abgeleiteten Anzeigen erneuern.</summary>
    private void RaiseAuthChanged()
    {
        OnPropertyChanged(nameof(HasAuthInfo));
        OnPropertyChanged(nameof(AuthIsWarning));
        OnPropertyChanged(nameof(AuthSummary));
        OnPropertyChanged(nameof(AuthBadges));
        OnPropertyChanged(nameof(SpoofWarning));
        OnPropertyChanged(nameof(HasSpoofWarning));
        OnPropertyChanged(nameof(CanTrustSender));

        OnPropertyChanged(nameof(HasPgpInfo));
        OnPropertyChanged(nameof(PgpIsWarning));
        OnPropertyChanged(nameof(PgpIsGood));
        OnPropertyChanged(nameof(PgpSummary));
    }

    // ---- OpenPGP ------------------------------------------------------------

    /// <summary>Nur zeigen, wenn die Nachricht überhaupt PGP verwendet.</summary>
    public bool HasPgpInfo => Body?.Pgp.IsRelevant == true;

    public bool PgpIsWarning => Body?.Pgp.IsWarning == true;

    public bool PgpIsGood => Body?.Pgp.IsGood == true;

    public string PgpSummary => Body?.Pgp.Summary ?? "";

    // ---- Vertrauenswürdige Absender -----------------------------------------

    public bool CanTrustSender =>
        Body is { FromAddress.Length: > 0 } b && !Settings.IsTrusted(b.FromAddress);

    /// <summary>
    /// Absender dauerhaft vertrauen: externe Bilder werden künftig ohne Nachfrage
    /// geladen. Gilt für die Adresse, nicht für die ganze Domain.
    /// </summary>
    public void TrustSender()
    {
        if (Body is not { FromAddress.Length: > 0 } body) return;

        Settings.TrustedSenders.Add(body.FromAddress);
        _store.Save();

        _showRemoteImages = true;
        RenderBody();

        OnPropertyChanged(nameof(CanTrustSender));
        Status = $"Bilder von '{body.FromAddress}' werden künftig geladen.";
    }

    // ---- Termin-Einladungen -------------------------------------------------

    public bool HasInvitation => Body?.HasInvitation == true;

    public string InvitationText => Body?.Invitation is { } item
        ? $"Termin-Einladung: {item.Summary} – {item.RangeDisplay}"
        : "";

    /// <summary>Öffnet den Kalender-Dialog zum Übernehmen; vom Fenster gesetzt.</summary>
    public Func<CalendarItem, bool>? AddToCalendar { get; set; }

    public void AcceptInvitation()
    {
        if (Body?.Invitation is not { } invitation) return;

        if (AddToCalendar?.Invoke(invitation) == true)
            Status = $"'{invitation.Summary}' zum Kalender hinzugefügt.";
    }

    // ---- Übersetzung -------------------------------------------------------

    private readonly TranslateService _translate;
    private readonly AttachmentArchive _attachmentArchive;

    /// <summary>Übersetzter Text, solange die Übersetzung angezeigt wird.</summary>
    private string? _translatedHtml;
    private bool _translatedIsHtml;

    private string _translationInfo = "";
    public string TranslationInfo { get => _translationInfo; private set => Set(ref _translationInfo, value); }

    private bool _showingTranslation;
    public bool ShowingTranslation { get => _showingTranslation; private set => Set(ref _showingTranslation, value); }

    public bool CanTranslate => _translate.IsConfigured && Body != null;

    public async Task TranslateAsync()
    {
        if (Body is not { } body) return;

        // Bereits übersetzt: nur umschalten, nicht erneut anfragen.
        if (_translatedHtml is not null)
        {
            ShowingTranslation = !ShowingTranslation;
            RenderBody();
            return;
        }

        var isHtml = !string.IsNullOrWhiteSpace(body.Html);
        var content = isHtml ? body.Html! : body.Text;
        if (string.IsNullOrWhiteSpace(content)) return;

        Busy = true;
        Status = "Übersetze …";
        try
        {
            var result = await _translate.TranslateAsync(
                content, Settings.TranslateTarget, isHtml);

            _translatedHtml = result.Text;
            _translatedIsHtml = isHtml;
            ShowingTranslation = true;

            TranslationInfo = string.IsNullOrWhiteSpace(result.DetectedLanguage)
                ? "Übersetzung wird angezeigt."
                : $"Übersetzt aus '{result.DetectedLanguage}'.";

            RenderBody();
            Status = TranslationInfo;
        }
        catch (Exception ex) { Fail(ex); }
        finally { Busy = false; }
    }

    /// <summary>Nach einem Theme-Wechsel: HTML mit den neuen Farben neu aufbauen.</summary>
    public void RerenderBody()
    {
        // BodyHtml würde sonst gleich bleiben und kein PropertyChanged auslösen.
        _bodyHtml = "";
        RenderBody();
    }

    public void SetStatus(string text) => Status = text;

}
