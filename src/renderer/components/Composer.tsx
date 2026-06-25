import { useEffect, useRef, useState } from 'react'
import { Lock, Paperclip, PenLine, X } from 'lucide-react'
import type { DraftRef, PickedAttachment, SendRequest } from '@shared/index'
import { useMailStore, type ComposeDraft } from '../store/useMailStore'
import RichTextEditor, { type RichEditorHandle } from './RichTextEditor'
import RecipientInput from './RecipientInput'

const AUTOSAVE_MS = 10000

function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KB`
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`
}

function buildInitialBody(draft: ComposeDraft, signature: string): string {
  // An existing draft / undo-reopen already contains its full body verbatim.
  if (draft.existingDraft || draft.raw) return draft.body ?? ''
  const sig = signature ? `-- \n${signature}` : ''
  if (draft.body) {
    return `\n\n${sig ? sig + '\n\n' : ''}${draft.body}`
  }
  return sig ? `\n\n${sig}` : ''
}

function escapeHtml(s: string): string {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
}

/** HTML seed for the editor: explicit HTML if given, else the plain seed as HTML. */
function buildInitialHtml(draft: ComposeDraft, signature: string): string {
  if (draft.bodyHtml) return draft.bodyHtml
  return escapeHtml(buildInitialBody(draft, signature)).replace(/\n/g, '<br>')
}

export default function Composer(): JSX.Element {
  const accounts = useMailStore((s) => s.accounts)
  // Fall back to the first account when none is active (e.g. unified inbox).
  const accountId = useMailStore((s) => s.activeAccountId) ?? accounts[0]?.id ?? null
  const draft = useMailStore((s) => s.compose) ?? {}
  const closeCompose = useMailStore((s) => s.closeCompose)
  const account = accounts.find((a) => a.id === accountId)
  const signature = account?.signature ?? ''
  const mainFrom = account
    ? account.name
      ? `${account.name} <${account.email}>`
      : account.email
    : ''
  const senderOptions = account ? [mainFrom, ...(account.aliases ?? [])] : []

  const [from, setFrom] = useState(draft.from ?? mainFrom)
  const [to, setTo] = useState(draft.to ?? '')
  const [cc, setCc] = useState(draft.cc ?? '')
  const [bcc, setBcc] = useState(draft.bcc ?? '')
  const [showBcc, setShowBcc] = useState(!!draft.bcc)
  const [subject, setSubject] = useState(draft.subject ?? '')
  const [attachments, setAttachments] = useState<PickedAttachment[]>(draft.attachments ?? [])
  const [pgpEncrypt, setPgpEncrypt] = useState(!!draft.pgpEncrypt)
  const [pgpSign, setPgpSign] = useState(!!draft.pgpSign)
  const [sending, setSending] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [draftStatus, setDraftStatus] = useState<string | null>(null)

  // Rich-text editor: uncontrolled DOM, read on demand via the ref.
  const editorRef = useRef<RichEditorHandle>(null)
  const initialHtml = useRef(buildInitialHtml(draft, signature)).current
  const initialHtmlRef = useRef('')
  const editorHtml = (): string => editorRef.current?.getHTML() ?? ''
  const editorText = (): string => editorRef.current?.getText() ?? ''

  // Capture the normalized initial HTML after mount (to detect real changes).
  useEffect(() => {
    initialHtmlRef.current = editorHtml()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  // Refs so the auto-save interval always reads the latest values.
  const draftRef = useRef<DraftRef | null>(draft.existingDraft ?? null)
  const lastSavedRef = useRef<string>('')
  const savingRef = useRef(false)
  const closingRef = useRef(false)
  const stateRef = useRef({ to, cc, bcc, subject, attachments, sending })
  stateRef.current = { to, cc, bcc, subject, attachments, sending }

  function buildReq(): SendRequest {
    const s = stateRef.current
    return {
      accountId: accountId as string,
      from: from || undefined,
      to: s.to,
      cc: s.cc || undefined,
      bcc: s.bcc || undefined,
      subject: s.subject,
      text: editorText(),
      html: editorHtml(),
      inReplyTo: draft.inReplyTo,
      references: draft.references,
      forwardFrom: draft.forwardFrom,
      answeredFrom: draft.answeredFrom,
      attachments: s.attachments.map((a) => ({ path: a.path, filename: a.filename })),
      pgpEncrypt: pgpEncrypt || undefined,
      pgpSign: pgpSign || undefined,
      requestDsn: localStorage.getItem('nmc.requestDsn') === '1' || undefined
    }
  }

  function isMeaningful(s: typeof stateRef.current): boolean {
    return !!(
      s.to.trim() ||
      s.cc.trim() ||
      s.bcc.trim() ||
      s.subject.trim() ||
      editorHtml() !== initialHtmlRef.current
    )
  }

  function serialOf(s: typeof stateRef.current): string {
    return JSON.stringify({
      to: s.to,
      cc: s.cc,
      bcc: s.bcc,
      subject: s.subject,
      html: editorHtml(),
      att: s.attachments.map((a) => a.path)
    })
  }

  function refreshIfDraftsOpen(): void {
    const st = useMailStore.getState()
    const role = (st.foldersByAccount[st.activeAccountId ?? ''] ?? []).find(
      (f) => f.path === st.activeFolder
    )?.role
    if (role === 'drafts') st.refreshMessages()
  }

  /** Save the current content to Drafts (skips if unchanged, empty, or busy). */
  async function persistDraft(): Promise<void> {
    const s = stateRef.current
    if (!accountId || s.sending || savingRef.current || !isMeaningful(s)) return
    const serial = serialOf(s)
    if (serial === lastSavedRef.current) return
    // Guard against concurrent saves (e.g. rapid clicks) creating duplicates.
    savingRef.current = true
    lastSavedRef.current = serial
    try {
      const res = await window.api.mail.saveDraft(buildReq(), draftRef.current ?? undefined)
      if (res.ok) {
        draftRef.current = res.data
        setDraftStatus('Entwurf gespeichert ' + new Date().toLocaleTimeString('de-DE'))
        refreshIfDraftsOpen()
      } else {
        lastSavedRef.current = '' // allow retry on next tick
        setDraftStatus('Entwurf konnte nicht gespeichert werden: ' + res.error)
      }
    } finally {
      savingRef.current = false
    }
  }

  // Auto-save the draft every few seconds while there's meaningful content.
  useEffect(() => {
    if (!accountId) return
    const id = setInterval(persistDraft, AUTOSAVE_MS)
    return () => clearInterval(id)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [accountId])

  /** Close and KEEP the draft (final save first). Used by the X / outside. */
  async function saveAndClose(): Promise<void> {
    if (closingRef.current) return // ignore rapid repeated clicks
    closingRef.current = true
    await persistDraft()
    closeCompose()
  }

  /** Explicitly delete the draft and close. Used by the "Verwerfen" button. */
  async function discardDraft(): Promise<void> {
    if (accountId && draftRef.current) {
      await window.api.mail.deleteDraft(accountId, draftRef.current)
      draftRef.current = null
      refreshIfDraftsOpen()
    }
    closeCompose()
  }

  async function addAttachments(): Promise<void> {
    const res = await window.api.mail.pickAttachments()
    if (!res.ok || res.data.length === 0) return
    setAttachments((prev) => {
      const seen = new Set(prev.map((a) => a.path))
      return [...prev, ...res.data.filter((a) => !seen.has(a.path))]
    })
  }

  function removeAttachment(path: string): void {
    setAttachments((prev) => prev.filter((a) => a.path !== path))
  }

  const title = draft.existingDraft
    ? 'Entwurf'
    : draft.forwardFrom
      ? 'Weiterleiten'
      : draft.inReplyTo
        ? 'Antwort'
        : 'Neue Nachricht'

  async function handleSend(): Promise<void> {
    if (!accountId) return
    setError(null)
    const req = buildReq()

    const delay = Number(localStorage.getItem('nmc.undoDelay') ?? '5')
    if (delay > 0) {
      // Delayed send: hand off to the store and close; the "undo" banner runs there.
      const undoDraft: ComposeDraft = {
        from,
        to,
        cc,
        bcc,
        subject,
        bodyHtml: editorHtml(),
        raw: true,
        attachments,
        inReplyTo: draft.inReplyTo,
        references: draft.references,
        forwardFrom: draft.forwardFrom,
        answeredFrom: draft.answeredFrom,
        existingDraft: draftRef.current ?? undefined,
        pgpEncrypt,
        pgpSign
      }
      useMailStore.getState().scheduleSend(req, undoDraft, draftRef.current, delay)
      closeCompose()
      return
    }

    // Immediate send.
    setSending(true)
    const res = await window.api.mail.send(req)
    if (!res.ok) {
      setSending(false)
      setError(res.error)
      return
    }
    let draftRemoved = false
    if (draftRef.current) {
      await window.api.mail.deleteDraft(accountId, draftRef.current)
      draftRef.current = null
      draftRemoved = true
    }
    setSending(false)
    if (draft.answeredFrom || draftRemoved) useMailStore.getState().refreshMessages()
    closeCompose()
  }

  return (
    <div className="fixed inset-0 z-20 flex items-center justify-center bg-black/40">
      <div
        className="flex max-h-[90vh] w-[640px] flex-col rounded-lg bg-white shadow-xl"
        onKeyDown={(e) => {
          if ((e.ctrlKey || e.metaKey) && e.key === 'Enter' && to && subject && !sending) {
            e.preventDefault()
            handleSend()
          }
        }}
      >
        <div className="flex items-center justify-between border-b px-5 py-3">
          <h2 className="text-lg font-semibold">{title}</h2>
          <button
            onClick={saveAndClose}
            title="Schließen (Entwurf behalten)"
            className="text-gray-400 hover:text-gray-700"
          >
            <X className="h-5 w-5" />
          </button>
        </div>
        <div className="flex flex-col gap-2 p-5">
          {senderOptions.length > 1 && (
            <label className="flex items-center gap-2 text-sm">
              <span className="text-gray-500">Von:</span>
              <select
                className="flex-1 rounded border px-3 py-2 text-sm"
                value={from}
                onChange={(e) => setFrom(e.target.value)}
              >
                {senderOptions.map((opt) => (
                  <option key={opt} value={opt}>
                    {opt}
                  </option>
                ))}
              </select>
            </label>
          )}
          <RecipientInput
            placeholder="An (mehrere mit Komma trennen)"
            value={to}
            onChange={setTo}
            autoFocus={!draft.to}
          />
          <div className="flex items-center gap-2">
            <RecipientInput className="flex-1" placeholder="Cc (optional)" value={cc} onChange={setCc} />
            {!showBcc && (
              <button
                onClick={() => setShowBcc(true)}
                className="shrink-0 rounded border px-3 py-2 text-sm text-gray-600 hover:bg-gray-50"
              >
                + Bcc
              </button>
            )}
          </div>
          {showBcc && (
            <RecipientInput placeholder="Bcc (optional)" value={bcc} onChange={setBcc} />
          )}
          <input
            className="rounded border px-3 py-2 text-sm"
            placeholder="Betreff"
            value={subject}
            onChange={(e) => setSubject(e.target.value)}
          />
          <RichTextEditor ref={editorRef} initialHtml={initialHtml} />
          {draft.forwardFrom && (
            <p className="text-xs text-gray-400">Original-Anhänge werden mitgesendet.</p>
          )}
          {attachments.length > 0 && (
            <div className="flex flex-col gap-1">
              {attachments.map((a) => (
                <div
                  key={a.path}
                  className="flex items-center gap-2 rounded bg-gray-100 px-2 py-1 text-xs text-gray-700"
                >
                  <Paperclip className="h-3.5 w-3.5 shrink-0" />
                  <span className="truncate" title={a.path}>
                    {a.filename}
                  </span>
                  <span className="shrink-0 text-gray-400">{formatSize(a.size)}</span>
                  <button
                    onClick={() => removeAttachment(a.path)}
                    className="ml-auto shrink-0 text-gray-400 hover:text-red-600"
                    title="Entfernen"
                  >
                    <X className="h-3.5 w-3.5" />
                  </button>
                </div>
              ))}
            </div>
          )}
          {error && <p className="text-sm text-red-600">{error}</p>}
        </div>
        <div className="flex items-center gap-2 border-t px-5 py-3">
          <button
            onClick={addAttachments}
            className="flex items-center gap-1.5 rounded border px-3 py-2 text-sm hover:bg-gray-50"
          >
            <Paperclip className="h-4 w-4" />
            Anhang
          </button>
          <button
            onClick={() => setPgpEncrypt((v) => !v)}
            title="Mit PGP verschlüsseln (Empfänger brauchen einen importierten öffentlichen Schlüssel)"
            className={`flex items-center gap-1.5 rounded border px-3 py-2 text-sm ${
              pgpEncrypt ? 'border-brand bg-brand/10 text-brand' : 'hover:bg-gray-50'
            }`}
          >
            <Lock className="h-4 w-4" />
            Verschlüsseln
          </button>
          <button
            onClick={() => setPgpSign((v) => !v)}
            title="Mit PGP signieren (eigener privater Schlüssel erforderlich)"
            className={`flex items-center gap-1.5 rounded border px-3 py-2 text-sm ${
              pgpSign ? 'border-brand bg-brand/10 text-brand' : 'hover:bg-gray-50'
            }`}
          >
            <PenLine className="h-4 w-4" />
            Signieren
          </button>
          {draftStatus && <span className="text-xs text-gray-400">{draftStatus}</span>}
          <button
            onClick={discardDraft}
            className="ml-auto rounded px-4 py-2 text-gray-600 hover:bg-gray-100"
          >
            Verwerfen
          </button>
          <button
            onClick={handleSend}
            disabled={!to || !subject || sending}
            className="rounded bg-brand px-4 py-2 text-white disabled:opacity-50"
          >
            {sending ? 'Senden…' : 'Senden'}
          </button>
        </div>
      </div>
    </div>
  )
}
