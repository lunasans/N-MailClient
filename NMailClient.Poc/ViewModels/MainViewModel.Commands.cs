using System.Collections.ObjectModel;
using System.IO;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Threading;
using NMailClient.Poc.Models;
using NMailClient.Poc.Services;

namespace NMailClient.Poc.ViewModels;

/// <summary>Alle Commands der Oberflaeche, gebuendelt.</summary>
public partial class MainViewModel
{
    // ---- Commands ----------------------------------------------------------

    private RelayCommand? _refresh, _loadMore, _toggleSeen, _toggleFlag, _delete,
                          _showImages, _accounts, _compose, _reply, _replyAll, _forward, _saveAtt,
                          _archive, _moveTo, _newFolder, _renameFolder, _deleteFolder, _undo, _source,
                          _spam, _ham, _toggleLabel, _openDraft, _folderUp, _folderDown, _cancelSend,
                          _snooze, _followUp, _clearReminder, _selectCategory, _assignCategory, _translateCmd,
                          _archiveAtt, _archiveAllAtt, _trustSender, _previewAtt;

    public RelayCommand RefreshCommand => _refresh ??= new(async _ => await ReloadMessagesAsync(),
        _ => SelectedFolder is { IsSelectable: true });

    public RelayCommand LoadMoreCommand => _loadMore ??= new(async _ => await LoadMoreAsync(),
        _ => CanLoadMore);

    public RelayCommand ToggleSeenCommand => _toggleSeen ??= new(async _ => await ToggleSeenAsync(),
        _ => SelectedMessage != null);

    public RelayCommand ToggleFlagCommand => _toggleFlag ??= new(async _ => await ToggleFlagAsync(),
        _ => SelectedMessage != null);

    public RelayCommand DeleteCommand => _delete ??= new(
        async p => await DeleteSelectedAsync(p as MailSummary),
        p => p is MailSummary || ActionTargets.Count > 0);

    public RelayCommand ArchiveCommand => _archive ??= new(
        async p => await ArchiveSelectedAsync(p as MailSummary),
        p => p is MailSummary || ActionTargets.Count > 0);

    public RelayCommand UndoCommand => _undo ??= new(async _ => await UndoAsync(), _ => CanUndo);

    public RelayCommand CancelSendCommand => _cancelSend ??= new(
        _ => CancelSend(), _ => CanCancelSend);

    /// <summary>Parameter ist die Bezeichnung der Vorgabe („In 1 Stunde" …).</summary>
    /// <summary>Zeigt eine PDF-Datei im eingebauten Betrachter; vom Fenster gesetzt.</summary>
    public Action<string, string>? ShowPdf { get; set; }

    public RelayCommand PreviewAttachmentCommand => _previewAtt ??= new(async p =>
    {
        if (p is not AttachmentInfo att || !att.IsPdf) return;
        if (SelectedAccount is not { } a || SelectedFolder is not { } f || Body is not { } b) return;

        Busy = true;
        try
        {
            // In einen temporären Ordner auspacken – der Betrachter braucht eine Datei.
            var dir = Path.Combine(Path.GetTempPath(), "NMailClient.Poc", "vorschau");
            Directory.CreateDirectory(dir);

            var target = Path.Combine(dir, AttachmentArchive.SafeFileName(att.FileName));
            await ImapFor(a).SaveAttachmentAsync(f.FullName, b.Uid, att.Index, target);

            ShowPdf?.Invoke(target, att.FileName);
        }
        catch (Exception ex) { Fail(ex); }
        finally { Busy = false; }
    }, p => p is AttachmentInfo { IsPdf: true });

    /// <summary>Einen Anhang ins Archiv ablegen (Absender/Jahr/Monat).</summary>
    public RelayCommand ArchiveAttachmentCommand => _archiveAtt ??= new(async p =>
    {
        if (p is not AttachmentInfo att) return;
        if (SelectedAccount is not { } a || SelectedFolder is not { } f || Body is not { } b) return;

        Busy = true;
        try
        {
            var sender = SelectedMessage?.FromAddress;
            if (string.IsNullOrWhiteSpace(sender)) sender = b.From;

            var target = _attachmentArchive.PrepareTarget(sender, b.Date, att.FileName);
            await ImapFor(a).SaveAttachmentAsync(f.FullName, b.Uid, att.Index, target);

            Status = $"Abgelegt: {target}";
        }
        catch (Exception ex) { Fail(ex); }
        finally { Busy = false; }
    });

    /// <summary>Alle Anhänge der Nachricht auf einmal ablegen.</summary>
    public RelayCommand ArchiveAllAttachmentsCommand => _archiveAllAtt ??= new(async _ =>
    {
        if (SelectedAccount is not { } a || SelectedFolder is not { } f
            || Body is not { Attachments.Count: > 0 } b) return;

        Busy = true;
        try
        {
            var sender = SelectedMessage?.FromAddress;
            if (string.IsNullOrWhiteSpace(sender)) sender = b.From;

            foreach (var att in b.Attachments)
            {
                var target = _attachmentArchive.PrepareTarget(sender, b.Date, att.FileName);
                await ImapFor(a).SaveAttachmentAsync(f.FullName, b.Uid, att.Index, target);
            }
            Status = $"{b.Attachments.Count} Anhang/Anhänge abgelegt.";
        }
        catch (Exception ex) { Fail(ex); }
        finally { Busy = false; }
    }, _ => Body is { Attachments.Count: > 0 });

    public RelayCommand TranslateCommand => _translateCmd ??= new(
        async _ => await TranslateAsync(), _ => CanTranslate);

    public RelayCommand SelectCategoryCommand => _selectCategory ??= new(
        p => { if (p is MailCategory c) SelectedCategory = c; });

    public RelayCommand AssignCategoryCommand => _assignCategory ??= new(
        p => { if (p is MailCategory c) AssignCategory(c); },
        _ => ActionTargets.Count > 0);

    public RelayCommand SnoozeCommand => _snooze ??= new(
        async p => await SetReminderAsync(ReminderKind.Snooze, p as string ?? ""),
        _ => ActionTargets.Count > 0);

    public RelayCommand FollowUpCommand => _followUp ??= new(
        async p => await SetReminderAsync(ReminderKind.FollowUp, p as string ?? ""),
        _ => ActionTargets.Count > 0);

    public RelayCommand ClearReminderCommand => _clearReminder ??= new(_ =>
    {
        if (SelectedAccount is not { } a || SelectedFolder is not { } f) return;
        foreach (var m in ActionTargets)
        {
            _reminders.Clear(a.Id, f.FullName, m.Uid);
            m.FollowUpAt = null;
        }
        Status = "Erinnerung entfernt.";
    }, _ => ActionTargets.Any(m => m.HasFollowUp));

    public RelayCommand OpenDraftCommand => _openDraft ??= new(
        async _ => await OpenDraftAsync(),
        _ => InDraftsFolder && SelectedMessage != null);

    public RelayCommand ToggleLabelCommand => _toggleLabel ??= new(
        async p => { if (p is LabelDef l) await ToggleLabelAsync(l); },
        _ => ActionTargets.Count > 0);

    public RelayCommand SpamCommand => _spam ??= new(async _ => await MarkSpamAsync(),
        _ => ActionTargets.Count > 0 && !InJunkFolder);

    public RelayCommand HamCommand => _ham ??= new(async _ => await MarkHamAsync(),
        _ => ActionTargets.Count > 0 && InJunkFolder);

    /// <summary>Zeigt den Quelltext an (Titel, Rohtext) – vom Fenster gesetzt.</summary>
    public Action<string, string>? ShowSource { get; set; }

    public RelayCommand SourceCommand => _source ??= new(async _ =>
    {
        if (SelectedAccount is not { } a || SelectedFolder is not { } f || Body is not { } b) return;
        Busy = true;
        try
        {
            var raw = await ImapFor(a).GetRawMessageAsync(f.FullName, b.Uid);
            ShowSource?.Invoke(b.Subject, raw);
        }
        catch (Exception ex) { Fail(ex); }
        finally { Busy = false; }
    }, _ => Body != null);

    public RelayCommand MoveToCommand => _moveTo ??= new(
        async p => { if (p is FolderNode f) await MoveSelectedAsync(f); },
        _ => ActionTargets.Count > 0);

    public RelayCommand CreateFolderCommand => _newFolder ??= new(
        async _ => await CreateFolderAsync(), _ => SelectedAccount != null);

    public RelayCommand RenameFolderCommand => _renameFolder ??= new(
        async _ => await RenameFolderAsync(), _ => SelectedFolder != null);

    public RelayCommand DeleteFolderCommand => _deleteFolder ??= new(
        async _ => await DeleteFolderAsync(), _ => SelectedFolder != null);

    public RelayCommand MoveFolderUpCommand => _folderUp ??= new(
        _ => MoveFolder(-1), _ => IsMovableFolder);

    public RelayCommand MoveFolderDownCommand => _folderDown ??= new(
        _ => MoveFolder(+1), _ => IsMovableFolder);

    /// <summary>Nur Wurzelordner ausser dem Posteingang lassen sich umsortieren.</summary>
    private bool IsMovableFolder =>
        SelectedFolder is { } f
        && !f.FullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase)
        && Folders.Contains(f);

    public RelayCommand ShowImagesCommand => _showImages ??= new(_ => ShowRemoteImages(),
        _ => ImagesBlocked);

    public RelayCommand TrustSenderCommand => _trustSender ??= new(_ => TrustSender(),
        _ => CanTrustSender);

    public RelayCommand AccountsCommand => _accounts ??= new(_ => ShowAccountsDialog?.Invoke());

    public RelayCommand ComposeCommand => _compose ??= new(
        _ => ShowComposer?.Invoke(ComposeRequest.Blank()),
        _ => SelectedAccount != null);

    public RelayCommand ReplyCommand => _reply ??= new(
        _ => ShowComposer?.Invoke(WithFolder(ComposeRequest.Reply(Body!, false, OwnAddresses))),
        _ => Body != null);

    public RelayCommand ReplyAllCommand => _replyAll ??= new(
        _ => ShowComposer?.Invoke(WithFolder(ComposeRequest.Reply(Body!, true, OwnAddresses))),
        _ => Body != null);

    /// <summary>
    /// Eigene Adressen des Kontos, zu dem die offene Nachricht gehört – samt
    /// Aliasen. Im gemeinsamen Posteingang ist das nicht zwingend das gewählte
    /// Konto, deshalb über die Herkunft der Nachricht.
    /// </summary>
    private IEnumerable<string> OwnAddresses
    {
        get
        {
            var account = SelectedMessage is { } m && OriginOf(m) is var (owner, _)
                ? owner
                : SelectedAccount;

            if (account is null) yield break;

            yield return account.Email;
            foreach (var alias in account.Aliases)
                if (!string.IsNullOrWhiteSpace(alias.Address)) yield return alias.Address;
        }
    }

    /// <summary>Ordner des Originals mitgeben – für das \Answered-Flag nach dem Senden.</summary>
    private ComposeRequest WithFolder(ComposeRequest r)
    {
        r.ReplyToFolder = SelectedFolder?.FullName;
        return r;
    }

    public RelayCommand ForwardCommand => _forward ??= new(
        _ => ShowComposer?.Invoke(ComposeRequest.Forward(Body!)), _ => Body != null);

    public RelayCommand SaveAttachmentCommand => _saveAtt ??= new(async p =>
    {
        if (p is not AttachmentInfo att) return;
        if (SelectedAccount is not { } a || SelectedFolder is not { } f || Body is not { } b) return;

        var target = AskSavePath?.Invoke(att.FileName);
        if (string.IsNullOrEmpty(target)) return;

        Busy = true;
        try
        {
            await ImapFor(a).SaveAttachmentAsync(f.FullName, b.Uid, att.Index, target);
            Status = $"Gespeichert: {target}";
        }
        catch (Exception ex) { Status = $"Fehler beim Speichern: {ex.Message}"; }
        finally { Busy = false; }
    });

}
