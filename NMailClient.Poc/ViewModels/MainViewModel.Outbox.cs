using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Threading;
using NMailClient.Poc.Models;
using NMailClient.Poc.Services;

namespace NMailClient.Poc.ViewModels;

/// <summary>Ausgangs-Warteschlange: wartende und geplante Sendungen.</summary>
public partial class MainViewModel
{
    // ---- Ausgang -----------------------------------------------------------

    private readonly OutboxService _outbox;

    private string _outboxText = "";
    public string OutboxText { get => _outboxText; private set => Set(ref _outboxText, value); }

    private bool _hasOutbox;
    public bool HasOutbox { get => _hasOutbox; private set => Set(ref _hasOutbox, value); }

    /// <summary>Nur zurückholbar, solange die Frist läuft.</summary>
    private string? _cancellableId;

    private bool _canCancelSend;
    public bool CanCancelSend { get => _canCancelSend; private set => Set(ref _canCancelSend, value); }

    /// <summary>
    /// Eine Antwort ist raus: die Marke am Original sofort setzen, statt auf das
    /// nächste Laden zu warten. Kommt aus dem Hintergrund des Ausgangs, deshalb
    /// über den Dispatcher.
    /// </summary>
    private void OnAnswered(string accountId, string folder, uint uid)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (SelectedAccount?.Id != accountId) return;
            if (SelectedFolder?.FullName != folder) return;

            var message = Messages.FirstOrDefault(m => m.Uid == uid);
            if (message is not null) message.Answered = true;
        });
    }

    /// <summary>
    /// Eine Kopie ist im Gesendet-Ordner gelandet. Steht der gerade offen, muss
    /// er nachladen – im gemeinsamen Posteingang zählt er nicht, dort stehen
    /// nur Posteingänge.
    /// </summary>
    private void OnSavedToSent(string accountId, string folder)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (SelectedAccount?.Id != accountId) return;
            if (SelectedFolder is not { IsUnified: false } open) return;
            if (!open.FullName.Equals(folder, StringComparison.OrdinalIgnoreCase)) return;

            // Ohne 'fresh' erschiene erst der gespeicherte Stand ohne die eben
            // gesendete Mail – das sähe aus, als käme sie verspätet an.
            _ = ReloadMessagesAsync(fresh: true);
        });
    }

    private void OnOutboxChanged()
    {
        var items = _outbox.Items;
        if (items.Count == 0)
        {
            HasOutbox = false;
            CanCancelSend = false;
            _cancellableId = null;
            return;
        }

        HasOutbox = true;

        // Der nächste zurückholbare Eintrag bestimmt die Anzeige.
        var pending = items.Where(x => x.IsPending).OrderBy(x => x.SendAt).FirstOrDefault();
        if (pending is not null)
        {
            _cancellableId = pending.Id;
            CanCancelSend = true;

            OutboxText = pending.SecondsLeft <= 60
                ? $"'{pending.Display}' wird in {pending.SecondsLeft} s gesendet"
                : $"'{pending.Display}' geplant für {pending.SendAt.LocalDateTime:dd.MM. HH:mm}";
        }
        else
        {
            _cancellableId = null;
            CanCancelSend = false;

            var failed = items.Where(x => x.LastError is not null).ToList();
            OutboxText = failed.Count > 0
                ? $"{failed.Count} Sendung(en) fehlgeschlagen: {failed[0].LastError}"
                : $"{items.Count} Sendung(en) werden verarbeitet …";
        }
    }

    public void CancelSend()
    {
        if (_cancellableId is not { } id) return;

        if (_outbox.Cancel(id)) Status = "Versand abgebrochen. Die Mail wurde nicht gesendet.";
        else Status = "Zu spät – die Mail ist bereits unterwegs.";
    }

}
