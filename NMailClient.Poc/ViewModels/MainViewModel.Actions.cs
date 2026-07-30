using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Threading;
using NMailClient.Poc.Models;
using NMailClient.Poc.Services;

namespace NMailClient.Poc.ViewModels;

/// <summary>Aktionen auf Nachrichten: Flags, Verschieben, Etiketten, Spam, Rueckgaengig.</summary>
public partial class MainViewModel
{
    // ---- Aktionen ----------------------------------------------------------

    /// <summary>
    /// Bei gemischter Auswahl wird gesetzt, nicht umgeschaltet – sonst wäre das
    /// Ergebnis von der Reihenfolge abhängig und für den Nutzer nicht vorhersehbar.
    /// </summary>
    public async Task ToggleFlagAsync()
    {
        var groups = GroupTargets();
        var targets = groups.SelectMany(g => g.Messages).ToList();
        if (targets.Count == 0) return;

        // Der Wert wird über die ganze Auswahl bestimmt, nicht je Gruppe – sonst
        // hinge das Ergebnis daran, wie sich die Auswahl auf Konten verteilt.
        bool value = !targets.All(m => m.Flagged);
        try
        {
            foreach (var (account, folder, messages) in groups)
                await ImapFor(account)
                    .SetFlaggedAsync(folder, messages.Select(m => m.Uid).ToList(), value);

            foreach (var m in targets) m.Flagged = value;
            Status = targets.Count > 1
                ? $"{targets.Count} Nachrichten {(value ? "markiert" : "Markierung entfernt")}."
                : Status;
        }
        catch (Exception ex) { Fail(ex); }
    }

    public async Task ToggleSeenAsync()
    {
        var groups = GroupTargets();
        var targets = groups.SelectMany(g => g.Messages).ToList();
        if (targets.Count == 0) return;

        bool value = !targets.All(m => m.Seen);
        try
        {
            foreach (var (account, folder, messages) in groups)
                await ImapFor(account)
                    .SetSeenAsync(folder, messages.Select(m => m.Uid).ToList(), value);

            int changed = targets.Count(m => m.Seen != value);
            foreach (var m in targets) m.Seen = value;

            if (SelectedFolder is { } node)
                node.Unread = Math.Max(0, node.Unread + (value ? -changed : changed));

            if (targets.Count > 1)
                Status = $"{targets.Count} Nachrichten als {(value ? "gelesen" : "ungelesen")} markiert.";
        }
        catch (Exception ex) { Fail(ex); }
    }

    /// <summary>Wird vor dem Löschen aufgerufen; null = keine Rückfrage gewünscht.</summary>
    public Func<string, bool>? ConfirmDelete { get; set; }

    /// <param name="single">
    /// Gesetzt bei Hover-Schnellaktionen: wirkt nur auf diese Zeile, unabhängig
    /// von der Auswahl – sonst löschte ein Hover-Klick versehentlich die ganze
    /// Mehrfachauswahl.
    /// </param>
    public async Task DeleteSelectedAsync(MailSummary? single = null)
    {
        var groups = GroupOf(single);
        var targets = groups.SelectMany(g => g.Messages).ToList();
        if (targets.Count == 0) return;

        if (Settings.ConfirmBeforeDelete && ConfirmDelete is { } ask)
        {
            var frage = targets.Count == 1
                ? $"'{targets[0].Subject}' löschen?"
                : $"{targets.Count} Nachrichten löschen?";
            if (!ask(frage)) return;
        }

        try
        {
            foreach (var (account, folder, messages) in groups)
            {
                var outcome = await ImapFor(account)
                    .DeleteAsync(folder, messages.Select(m => m.Uid).ToList());

                // Zurücknehmen nur, wenn genau ein Postfach betroffen ist: eine
                // Rücknahme über mehrere Konten hinweg wäre nur halb umkehrbar,
                // und eine Schaltfläche, die das verspricht, wäre eine Lüge.
                if (groups.Count == 1 && outcome.TargetFolder is not null)
                    OfferUndo(outcome, folder,
                        targets.Count == 1 ? "Nachricht gelöscht" : $"{targets.Count} Nachrichten gelöscht");
                else if (groups.Count == 1)
                    Status = $"{targets.Count} endgültig gelöscht.";
            }

            RemoveFromList(targets, SelectedFolder);
            if (groups.Count > 1)
                Status = $"{targets.Count} Nachrichten aus {groups.Count} Konten gelöscht.";
        }
        catch (Exception ex) { Fail(ex); }
    }

    public async Task ArchiveSelectedAsync(MailSummary? single = null)
    {
        var groups = GroupOf(single);
        var targets = groups.SelectMany(g => g.Messages).ToList();
        if (targets.Count == 0) return;

        try
        {
            foreach (var (account, folder, messages) in groups)
            {
                var outcome = await ImapFor(account)
                    .ArchiveAsync(folder, messages.Select(m => m.Uid).ToList());

                if (groups.Count == 1)
                    OfferUndo(outcome, folder,
                        targets.Count == 1 ? "Nachricht archiviert" : $"{targets.Count} Nachrichten archiviert");
            }

            RemoveFromList(targets, SelectedFolder);
            if (groups.Count > 1)
                Status = $"{targets.Count} Nachrichten aus {groups.Count} Konten archiviert.";
        }
        catch (Exception ex) { Fail(ex); }
    }

    public async Task MoveSelectedAsync(FolderNode target)
    {
        var groups = GroupTargets();
        var targets = groups.SelectMany(g => g.Messages).ToList();
        if (targets.Count == 0) return;

        // Verschieben geht nur innerhalb eines Postfachs: IMAP kennt kein
        // Verschieben über Serverkonten hinweg. Lieber deutlich ablehnen als
        // stillschweigend das Falsche tun.
        if (groups.Count > 1)
        {
            Status = "Verschieben geht nur innerhalb eines Kontos – "
                     + "die Auswahl umfasst mehrere.";
            return;
        }

        var (account, folder, messages) = groups[0];
        try
        {
            var outcome = await ImapFor(account)
                .MoveAsync(folder, messages.Select(m => m.Uid).ToList(), target.FullName);
            RemoveFromList(targets, SelectedFolder);
            OfferUndo(outcome, folder, $"{targets.Count} nach '{target.Name}' verschoben");
        }
        catch (Exception ex) { Fail(ex); }
    }


    // ---- Etiketten ---------------------------------------------------------

    /// <summary>
    /// Etikett-Definitionen als beobachtbare Liste – die Rohliste in den Einstellungen
    /// meldet keine Änderungen, das Kontextmenü bliebe sonst auf dem alten Stand.
    /// </summary>
    public ObservableCollection<LabelDef> Labels { get; } = new();

    /// <summary>Nach Änderungen an den Definitionen: speichern und Zuordnung erneuern.</summary>
    public void SaveLabels()
    {
        Settings.Labels = Labels.ToList();
        _store.Save();
        RefreshLabels();
    }

    /// <summary>Keywords der Nachricht auf definierte Etiketten abbilden.</summary>
    private void ResolveLabels(MailSummary m)
        => m.Labels = Labels.Where(l => m.Keywords.Contains(l.Keyword)).ToList();

    public void RefreshLabels()
    {
        foreach (var m in Messages) ResolveLabels(m);
    }

    /// <summary>Wie bei Flags: gemischte Auswahl wird gesetzt, nicht umgeschaltet.</summary>
    public async Task ToggleLabelAsync(LabelDef label)
    {
        var targets = ActionTargets;
        if (SelectedAccount is not { } a || SelectedFolder is not { } f || targets.Count == 0) return;

        bool value = !targets.All(m => m.Keywords.Contains(label.Keyword));
        try
        {
            await ImapFor(a).SetKeywordAsync(
                f.FullName, targets.Select(m => m.Uid).ToList(), label.Keyword, value);

            foreach (var m in targets)
            {
                m.SetKeyword(label.Keyword, value);
                ResolveLabels(m);
            }
        }
        catch (Exception ex) { Fail(ex); }
    }

    /// <summary>Ist der gerade offene Ordner der Entwürfe-Ordner?</summary>
    public bool InDraftsFolder =>
        SelectedFolder is { } f
        && (f.Name.Equals("Drafts", StringComparison.OrdinalIgnoreCase)
            || f.Name.Equals("Entwürfe", StringComparison.OrdinalIgnoreCase));

    /// <summary>Entwurf im Composer weiterschreiben statt nur anzuzeigen.</summary>
    public async Task OpenDraftAsync()
    {
        if (SelectedAccount is not { } a || SelectedFolder is not { } f
            || SelectedMessage is not { } m || !InDraftsFolder) return;

        Busy = true;
        try
        {
            var draft = await ImapFor(a).GetDraftAsync(f.FullName, m.Uid);
            ShowComposer?.Invoke(ComposeRequest.FromDraft(draft));
        }
        catch (Exception ex) { Fail(ex); }
        finally { Busy = false; }
    }

    /// <summary>Ist der gerade offene Ordner der Spam-Ordner?</summary>
    public bool InJunkFolder =>
        SelectedFolder is { } f
        && (f.Name.Equals("Junk", StringComparison.OrdinalIgnoreCase)
            || f.Name.Equals("Spam", StringComparison.OrdinalIgnoreCase));

    public async Task MarkSpamAsync()
    {
        var targets = ActionTargets;
        if (SelectedAccount is not { } a || SelectedFolder is not { } f || targets.Count == 0) return;

        try
        {
            var outcome = await ImapFor(a).MarkSpamAsync(f.FullName, targets.Select(m => m.Uid).ToList());
            RemoveFromList(targets, f);
            OfferUndo(outcome, f.FullName,
                targets.Count == 1 ? "Als Spam eingestuft" : $"{targets.Count} als Spam eingestuft");
        }
        catch (Exception ex) { Fail(ex); }
    }

    public async Task MarkHamAsync()
    {
        var targets = ActionTargets;
        if (SelectedAccount is not { } a || SelectedFolder is not { } f || targets.Count == 0) return;

        try
        {
            var outcome = await ImapFor(a).MarkHamAsync(f.FullName, targets.Select(m => m.Uid).ToList());
            RemoveFromList(targets, f);
            OfferUndo(outcome, f.FullName,
                targets.Count == 1 ? "In den Posteingang zurückgelegt" : $"{targets.Count} zurückgelegt");
        }
        catch (Exception ex) { Fail(ex); }
    }

    /// <summary>Aus der Liste entfernen und die Ungelesen-Zahl mitziehen.</summary>
    private void RemoveFromList(List<MailSummary> targets, FolderNode folder)
    {
        int unread = targets.Count(m => !m.Seen);
        foreach (var m in targets) Messages.Remove(m);
        folder.Unread = Math.Max(0, folder.Unread - unread);

        SelectedMessages.Clear();
        Body = null;
        BodyHtml = "";
    }

    // ---- Rückgängig --------------------------------------------------------

    private MoveOutcome? _undoOutcome;
    private string? _undoSourceFolder;
    private DispatcherTimer? _undoTimer;

    private string _undoText = "";
    public string UndoText { get => _undoText; private set => Set(ref _undoText, value); }

    private bool _canUndo;
    public bool CanUndo { get => _canUndo; private set => Set(ref _canUndo, value); }

    /// <summary>Wie lange der Rückgängig-Hinweis stehen bleibt.</summary>
    private static readonly TimeSpan UndoWindow = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Bietet Rückgängig an – aber nur, wenn der Server Ziel-UIDs geliefert hat.
    /// Ohne UIDPLUS wäre nicht bestimmbar, welche Nachrichten zurückzuholen sind;
    /// dann bleibt es bei der Statusmeldung, statt eine Möglichkeit vorzutäuschen.
    /// </summary>
    private void OfferUndo(MoveOutcome outcome, string sourceFolder, string description)
    {
        Status = description + ".";

        if (!outcome.CanUndo)
        {
            DismissUndo();
            return;
        }

        _undoOutcome = outcome;
        _undoSourceFolder = sourceFolder;
        UndoText = description;
        CanUndo = true;

        _undoTimer ??= new DispatcherTimer();
        _undoTimer.Stop();
        _undoTimer.Interval = UndoWindow;
        _undoTimer.Tick -= UndoExpired;
        _undoTimer.Tick += UndoExpired;
        _undoTimer.Start();
    }

    private void UndoExpired(object? sender, EventArgs e) => DismissUndo();

    public void DismissUndo()
    {
        _undoTimer?.Stop();
        _undoOutcome = null;
        _undoSourceFolder = null;
        UndoText = "";
        CanUndo = false;
    }

    public async Task UndoAsync()
    {
        if (SelectedAccount is not { } a
            || _undoOutcome is not { TargetFolder: { } target } outcome
            || _undoSourceFolder is not { } source) return;

        var uids = outcome.NewUids;
        DismissUndo();

        Busy = true;
        try
        {
            await ImapFor(a).MoveAsync(target, uids, source);
            Status = "Rückgängig gemacht.";

            // Position und Sortierung lassen sich nicht verlässlich rekonstruieren –
            // deshalb den Ordner neu laden statt die Zeilen zu erraten.
            if (SelectedFolder?.FullName == source) await ReloadMessagesAsync();
        }
        catch (Exception ex) { Fail(ex); }
        finally { Busy = false; }
    }

    private void Fail(Exception ex)
    {
        Status = $"Fehler: {ex.Message}";
        AppLog.Error("Aktion fehlgeschlagen.", ex);
    }

}
