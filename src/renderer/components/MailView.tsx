import { useEffect, useMemo, useState } from 'react'
import DOMPurify from 'dompurify'
import {
  Archive,
  CalendarPlus,
  Code,
  Eye,
  FolderInput,
  Forward,
  ImageOff,
  Clock,
  Languages,
  Lock,
  MailCheck,
  MailX,
  Paperclip,
  Printer,
  Reply,
  ReplyAll,
  Save,
  ShieldAlert,
  ShieldCheck,
  ShieldQuestion
} from 'lucide-react'
import type { AttachmentMeta, AttachmentRef, MessageDetail } from '@shared/index'
import { useMailStore, type ComposeDraft } from '../store/useMailStore'

// Open links in the system browser (handled by the main process) and never
// allow mail HTML to navigate the app or run scripts.
DOMPurify.addHook('afterSanitizeAttributes', (node) => {
  if (node.tagName === 'A') {
    node.setAttribute('target', '_blank')
    node.setAttribute('rel', 'noopener noreferrer')
  }
})

/**
 * Sanitize mail HTML. By default remote images are blocked (privacy — they are
 * commonly used as tracking pixels); their src is parked in data-blocked-src so
 * we can restore it when the user opts in. Returns the count of blocked images.
 */
function sanitizeBody(html: string, showImages: boolean): { html: string; blocked: number } {
  const fragment = DOMPurify.sanitize(html, {
    FORBID_TAGS: ['script', 'style', 'iframe', 'form', 'object', 'embed'],
    FORBID_ATTR: ['onerror', 'onload', 'onclick'],
    ADD_ATTR: ['target'],
    RETURN_DOM_FRAGMENT: true
  })

  let blocked = 0
  fragment.querySelectorAll('img').forEach((img) => {
    const src = img.getAttribute('src') ?? ''
    if (/^https?:/i.test(src)) {
      if (showImages) {
        return
      }
      img.setAttribute('data-blocked-src', src)
      img.removeAttribute('src')
      img.setAttribute('alt', img.getAttribute('alt') || 'Bild blockiert')
      blocked++
    }
  })

  const wrapper = document.createElement('div')
  wrapper.appendChild(fragment)
  return { html: wrapper.innerHTML, blocked }
}

function stripPrefix(subject: string, re: RegExp): string {
  return subject.replace(re, '').trim()
}

/** Build a "> "-quoted reply body from the original message. */
function quote(msg: MessageDetail): string {
  const when = msg.date ? new Date(msg.date).toLocaleString('de-DE') : ''
  const intro = `Am ${when} schrieb ${msg.from}:`
  const body = (msg.text ?? '(kein Textinhalt)')
    .split('\n')
    .map((l) => '> ' + l)
    .join('\n')
  return `${intro}\n${body}`
}

function escapeHtml(s: string): string {
  return s
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
}

/** Recipients of original to/cc minus our own address (for reply-all). */
function othersExcept(addresses: string[], ownEmail: string): string {
  return addresses
    .flatMap((a) => a.split(','))
    .map((a) => a.trim())
    .filter((a) => a && !a.toLowerCase().includes(ownEmail.toLowerCase()))
    .join(', ')
}

/** Trusted senders/domains whose external images load automatically. */
function senderEmailOf(from: string): string {
  const m = from.match(/<([^>]+)>/)
  return (m ? m[1] : from).trim().toLowerCase()
}
function imageWhitelist(): string[] {
  try {
    return JSON.parse(localStorage.getItem('nmc.imageWhitelist') ?? '[]') as string[]
  } catch {
    return []
  }
}
function isTrustedSender(from: string): boolean {
  const email = senderEmailOf(from)
  const domain = email.split('@')[1] ?? ''
  const wl = imageWhitelist()
  return wl.includes(email) || (domain !== '' && wl.includes(domain))
}
function trustSender(from: string): void {
  const email = senderEmailOf(from)
  const wl = imageWhitelist()
  if (!wl.includes(email)) {
    wl.push(email)
    localStorage.setItem('nmc.imageWhitelist', JSON.stringify(wl))
  }
}

export default function MailView(): JSX.Element {
  const message = useMailStore((s) => s.message)
  const loading = useMailStore((s) => s.loadingMessage)
  const previewCtx = useMailStore((s) => s.previewCtx)
  const accounts = useMailStore((s) => s.accounts)
  const openCompose = useMailStore((s) => s.openCompose)
  const openNewEvent = useMailStore((s) => s.openNewEvent)
  const setView = useMailStore((s) => s.setView)
  const [showImages, setShowImages] = useState(false)
  const [source, setSource] = useState<string | null>(null)
  const [translated, setTranslated] = useState<{
    text: string
    detected?: string
    isHtml: boolean
  } | null>(null)
  const [translating, setTranslating] = useState(false)
  const [translateError, setTranslateError] = useState<string | null>(null)

  const accountId = previewCtx?.accountId ?? null
  const folder = previewCtx?.folder ?? null
  const ownEmail = accounts.find((a) => a.id === accountId)?.email ?? ''

  // Re-block images (unless the sender is trusted) / reset translation on message change.
  useEffect(() => {
    setShowImages(message ? isTrustedSender(message.from) : false)
    setSource(null)
    setTranslated(null)
    setTranslateError(null)
  }, [message?.uid])

  async function translateMessage(): Promise<void> {
    if (!message) return
    // Prefer translating the HTML body (markup is preserved); else plain text.
    const isHtml = !!message.html
    const payload = message.html ?? message.text ?? ''
    if (!payload.trim()) return
    setTranslating(true)
    setTranslateError(null)
    const res = await window.api.translate.run(payload, isHtml)
    setTranslating(false)
    if (res.ok) setTranslated({ ...res.data, isHtml })
    else setTranslateError(res.error)
  }

  function reply(all: boolean): void {
    if (!message || !folder) return
    const refs = [...message.references, ...(message.messageId ? [message.messageId] : [])]
    const draft: ComposeDraft = {
      to: message.from,
      cc: all ? othersExcept([message.to, message.cc], ownEmail) : undefined,
      subject: 'Re: ' + stripPrefix(message.subject, /^(re:\s*)+/i),
      body: '\n\n' + quote(message),
      inReplyTo: message.messageId ?? undefined,
      references: refs,
      answeredFrom: { folder, uid: message.uid }
    }
    openCompose(draft)
  }

  function forward(): void {
    if (!message || !folder) return
    const draft: ComposeDraft = {
      subject: 'Fwd: ' + stripPrefix(message.subject, /^(fwd:\s*|fw:\s*)+/i),
      body:
        '\n\n---------- Weitergeleitete Nachricht ----------\n' +
        `Von: ${message.from}\nDatum: ${message.date ? new Date(message.date).toLocaleString('de-DE') : ''}\n` +
        `Betreff: ${message.subject}\nAn: ${message.to}\n\n` +
        (message.text ?? ''),
      forwardFrom: { folder, uid: message.uid }
    }
    openCompose(draft)
  }

  function archiveCurrent(): void {
    if (!message) return
    const st = useMailStore.getState()
    if (st.unified && previewCtx) {
      const item = st.unifiedItems.find(
        (m) =>
          m.uid === message.uid &&
          m.accountId === previewCtx.accountId &&
          m.folder === previewCtx.folder
      )
      if (item) st.archiveUnified(item)
    } else {
      st.archiveMessages([message.uid])
    }
  }

  async function showSource(): Promise<void> {
    if (!message || !accountId || !folder) return
    setSource('Lade…')
    const res = await window.api.mail.source(accountId, folder, message.uid)
    setSource(res.ok ? res.data : 'Fehler: ' + res.error)
  }

  /** Print the open message via a hidden iframe (uses the sanitized body). */
  function printMail(): void {
    if (!message) return
    const body = rendered
      ? rendered.html
      : `<pre style="white-space:pre-wrap;font-family:inherit">${escapeHtml(message.text ?? '')}</pre>`
    const header =
      `<div style="border-bottom:1px solid #ccc;padding-bottom:8px;margin-bottom:12px">` +
      `<h1 style="font-size:18px;margin:0 0 8px">${escapeHtml(message.subject)}</h1>` +
      `<div style="font-size:12px;color:#444">` +
      `<div><b>Von:</b> ${escapeHtml(message.from)}</div>` +
      `<div><b>An:</b> ${escapeHtml(message.to)}</div>` +
      (message.date ? `<div>${new Date(message.date).toLocaleString('de-DE')}</div>` : '') +
      `</div></div>`
    const iframe = document.createElement('iframe')
    iframe.style.cssText = 'position:fixed;right:0;bottom:0;width:0;height:0;border:0'
    document.body.appendChild(iframe)
    const doc = iframe.contentWindow!.document
    doc.open()
    doc.write(
      `<!doctype html><html><head><meta charset="utf-8"><title>${escapeHtml(message.subject)}</title>` +
        `<style>body{font-family:'Segoe UI',system-ui,sans-serif;color:#111;margin:24px}img{max-width:100%}</style>` +
        `</head><body>${header}${body}</body></html>`
    )
    doc.close()
    const w = iframe.contentWindow!
    w.focus()
    w.print()
    setTimeout(() => iframe.remove(), 1000)
  }

  const rendered = useMemo(() => {
    if (!message?.html) return null
    return sanitizeBody(message.html, showImages)
  }, [message, showImages])

  if (loading) {
    return <div className="p-6 text-gray-400">Lade Nachricht…</div>
  }
  if (!message) {
    return (
      <div className="flex h-full items-center justify-center text-gray-400">
        Wähle eine Nachricht aus.
      </div>
    )
  }

  return (
    <div className="flex h-full min-h-0 flex-col overflow-hidden">
      <div className="border-b px-6 py-4">
        <div className="mb-3 flex flex-wrap items-center gap-2">
          <button
            onClick={() => reply(false)}
            className="flex items-center gap-1.5 rounded border px-3 py-1.5 text-sm hover:bg-gray-50"
          >
            <Reply className="h-4 w-4" />
            Antworten
          </button>
          <button
            onClick={() => reply(true)}
            className="flex items-center gap-1.5 rounded border px-3 py-1.5 text-sm hover:bg-gray-50"
          >
            <ReplyAll className="h-4 w-4" />
            Allen antworten
          </button>
          <button
            onClick={forward}
            className="flex items-center gap-1.5 rounded border px-3 py-1.5 text-sm hover:bg-gray-50"
          >
            <Forward className="h-4 w-4" />
            Weiterleiten
          </button>
          <button
            onClick={archiveCurrent}
            className="flex items-center gap-1.5 rounded border px-3 py-1.5 text-sm hover:bg-gray-50"
            title="Archivieren"
          >
            <Archive className="h-4 w-4" />
            Archivieren
          </button>
          <button
            onClick={() =>
              openNewEvent({
                summary: message.subject,
                description: `Aus E-Mail von ${message.from}`
              })
            }
            className="flex items-center gap-1.5 rounded border px-3 py-1.5 text-sm hover:bg-gray-50"
            title="Termin aus dieser Mail erstellen"
          >
            <CalendarPlus className="h-4 w-4" />
            Als Termin
          </button>
          <button
            onClick={translateMessage}
            disabled={translating}
            className="ml-auto flex items-center gap-1.5 rounded border px-3 py-1.5 text-sm text-gray-600 hover:bg-gray-50 disabled:opacity-50"
            title="Mailtext übersetzen"
          >
            <Languages className="h-4 w-4" />
            {translating ? 'Übersetze…' : 'Übersetzen'}
          </button>
          <button
            onClick={printMail}
            className="flex items-center gap-1.5 rounded border px-3 py-1.5 text-sm text-gray-600 hover:bg-gray-50"
            title="Drucken"
          >
            <Printer className="h-4 w-4" />
            Drucken
          </button>
          <button
            onClick={showSource}
            className="flex items-center gap-1.5 rounded border px-3 py-1.5 text-sm text-gray-600 hover:bg-gray-50"
            title="Quelltext anzeigen"
          >
            <Code className="h-4 w-4" />
            Quelltext
          </button>
        </div>
        <h1 className="mb-2 text-xl font-semibold">{message.subject}</h1>
        <div className="text-sm text-gray-600">
          <div>
            <span className="text-gray-400">Von:</span> {message.from}
          </div>
          <div>
            <span className="text-gray-400">An:</span> {message.to}
          </div>
          {message.cc && (
            <div>
              <span className="text-gray-400">Cc:</span> {message.cc}
            </div>
          )}
          {message.date && (
            <div className="text-gray-400">{new Date(message.date).toLocaleString('de-DE')}</div>
          )}
        </div>
        {message.attachments.length > 0 && (
          <div className="mt-3 flex flex-col gap-1.5">
            {message.attachments.map((a, i) => (
              <AttachmentItem key={i} att={a} sender={message.from} uid={message.uid} />
            ))}
          </div>
        )}
      </div>

      {message.pgp && (
        <div
          className={`flex flex-wrap items-center gap-x-4 gap-y-1 border-b px-6 py-2 text-sm ${
            message.pgp.error || message.pgp.verified === false
              ? 'bg-red-50 text-red-700'
              : message.pgp.verified === true
                ? 'bg-green-50 text-green-700'
                : 'bg-blue-50 text-blue-700'
          }`}
        >
          {message.pgp.encrypted && (
            <span className="flex items-center gap-1.5">
              <Lock className="h-4 w-4" />
              {message.pgp.error
                ? `Entschlüsselung fehlgeschlagen: ${message.pgp.error}`
                : 'Ende-zu-Ende verschlüsselt (PGP)'}
            </span>
          )}
          {message.pgp.signed && (
            <span className="flex items-center gap-1.5">
              {message.pgp.verified === true ? (
                <ShieldCheck className="h-4 w-4" />
              ) : message.pgp.verified === false ? (
                <ShieldAlert className="h-4 w-4" />
              ) : (
                <ShieldQuestion className="h-4 w-4" />
              )}
              {message.pgp.verified === true
                ? `Signatur gültig${message.pgp.signer ? ' — ' + message.pgp.signer : ''}`
                : message.pgp.verified === false
                  ? 'Ungültige Signatur'
                  : 'Signiert (Schlüssel unbekannt)'}
            </span>
          )}
        </div>
      )}

      {message.delivery && (
        <div
          className={`flex items-center gap-2 border-b px-6 py-2 text-sm ${
            message.delivery.status === 'delivered'
              ? 'bg-green-50 text-green-700'
              : message.delivery.status === 'failed'
                ? 'bg-red-50 text-red-700'
                : message.delivery.status === 'delayed'
                  ? 'bg-amber-50 text-amber-800'
                  : 'bg-gray-50 text-gray-600'
          }`}
        >
          {message.delivery.status === 'delivered' ? (
            <MailCheck className="h-4 w-4 shrink-0" />
          ) : message.delivery.status === 'failed' ? (
            <MailX className="h-4 w-4 shrink-0" />
          ) : (
            <Clock className="h-4 w-4 shrink-0" />
          )}
          <span>
            {message.delivery.status === 'delivered'
              ? 'Zustellbericht: erfolgreich zugestellt'
              : message.delivery.status === 'failed'
                ? 'Zustellbericht: Zustellung fehlgeschlagen'
                : message.delivery.status === 'delayed'
                  ? 'Zustellbericht: Zustellung verzögert'
                  : 'Zustellbericht'}
            {message.delivery.recipient ? ` an ${message.delivery.recipient}` : ''}
            {message.delivery.code ? ` (${message.delivery.code})` : ''}
            {message.delivery.diagnostic ? ` — ${message.delivery.diagnostic}` : ''}
          </span>
        </div>
      )}

      {message.invite && (
        <div className="flex flex-wrap items-center gap-3 border-b bg-indigo-50 px-6 py-2 text-sm text-indigo-800">
          <CalendarPlus className="h-4 w-4 shrink-0" />
          <span>
            Termineinladung: <strong>{message.invite.summary}</strong>
            {' — '}
            {new Date(message.invite.startISO).toLocaleString('de-DE')}
          </span>
          <button
            onClick={() => {
              const inv = message.invite!
              openNewEvent({
                summary: inv.summary,
                startISO: inv.startISO,
                endISO: inv.endISO,
                allDay: inv.allDay,
                location: inv.location,
                description: inv.description
              })
              setView('calendar')
            }}
            className="ml-auto rounded bg-indigo-600 px-3 py-1 text-white hover:bg-indigo-700"
          >
            Zum Kalender hinzufügen
          </button>
        </div>
      )}

      {rendered && rendered.blocked > 0 && !showImages && (
        <div className="flex items-center justify-between gap-3 bg-amber-50 px-6 py-2 text-sm text-amber-800">
          <span className="flex items-center gap-2">
            <ImageOff className="h-4 w-4" />
            {rendered.blocked} externe{rendered.blocked === 1 ? 's Bild' : ' Bilder'} blockiert
            (Schutz vor Tracking).
          </span>
          <span className="flex shrink-0 gap-2">
            <button
              onClick={() => setShowImages(true)}
              className="rounded bg-amber-600 px-3 py-1 text-white hover:bg-amber-700"
            >
              Bilder anzeigen
            </button>
            <button
              onClick={() => {
                trustSender(message.from)
                setShowImages(true)
              }}
              className="rounded border border-amber-400 px-3 py-1 hover:bg-amber-100"
              title="Externe Bilder dieses Absenders künftig automatisch laden"
            >
              Absender vertrauen
            </button>
          </span>
        </div>
      )}

      {translateError && (
        <div className="bg-red-50 px-6 py-2 text-sm text-red-700">{translateError}</div>
      )}
      {translated && (
        <div className="flex items-center justify-between gap-3 border-b bg-blue-50 px-6 py-2 text-sm text-blue-700">
          <span className="flex items-center gap-2">
            <Languages className="h-4 w-4" />
            Übersetzt{translated.detected ? ` aus „${translated.detected}"` : ''}
          </span>
          <button
            onClick={() => setTranslated(null)}
            className="shrink-0 rounded border border-blue-300 px-3 py-1 hover:bg-blue-100"
          >
            Original anzeigen
          </button>
        </div>
      )}

      <div className="min-h-0 flex-1 overflow-y-auto px-6 py-4">
        {translated ? (
          translated.isHtml ? (
            <div
              className="mail-body"
              dangerouslySetInnerHTML={{ __html: sanitizeBody(translated.text, showImages).html }}
            />
          ) : (
            <pre className="whitespace-pre-wrap font-sans text-sm text-gray-800">
              {translated.text}
            </pre>
          )
        ) : rendered ? (
          <div className="mail-body" dangerouslySetInnerHTML={{ __html: rendered.html }} />
        ) : (
          <pre className="whitespace-pre-wrap font-sans text-sm text-gray-800">
            {message.text ?? '(Kein Inhalt)'}
          </pre>
        )}
      </div>

      {source !== null && (
        <div className="fixed inset-0 z-30 flex items-center justify-center bg-black/40">
          <div className="flex h-[80vh] w-[80vw] max-w-4xl flex-col rounded-lg bg-white shadow-xl">
            <div className="flex items-center justify-between border-b px-5 py-3">
              <h2 className="text-lg font-semibold">Quelltext</h2>
              <button
                onClick={() => setSource(null)}
                className="rounded px-3 py-1 text-sm text-gray-600 hover:bg-gray-100"
              >
                Schließen
              </button>
            </div>
            <pre className="flex-1 overflow-auto whitespace-pre-wrap break-all bg-gray-50 p-4 font-mono text-xs text-gray-800">
              {source}
            </pre>
          </div>
        </div>
      )}
    </div>
  )
}

function AttachmentItem({
  att,
  sender,
  uid
}: {
  att: AttachmentMeta
  sender: string
  uid: number
}): JSX.Element {
  const accountId = useMailStore((s) => s.activeAccountId)
  const folder = useMailStore((s) => s.activeFolder)
  const [status, setStatus] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const makeRef = (): AttachmentRef | null =>
    accountId && folder
      ? { accountId, folder, uid, partId: att.partId, filename: att.filename }
      : null

  const isPdf =
    att.contentType.toLowerCase() === 'application/pdf' ||
    att.filename.toLowerCase().endsWith('.pdf')

  async function handleViewPdf(): Promise<void> {
    const ref = makeRef()
    if (!ref) return
    setBusy(true)
    setStatus('Öffne…')
    const res = await window.api.pdf.viewAttachment(ref)
    setBusy(false)
    setStatus(res.ok ? null : 'Fehler: ' + res.error)
  }

  async function handleSave(): Promise<void> {
    const ref = makeRef()
    if (!ref) return
    setBusy(true)
    setStatus('Lade…')
    const res = await window.api.mail.saveAttachment(ref)
    setBusy(false)
    if (!res.ok) setStatus('Fehler: ' + res.error)
    else setStatus(res.data.canceled ? null : 'Gespeichert')
  }

  async function handleArchive(): Promise<void> {
    const ref = makeRef()
    if (!ref) return
    setBusy(true)
    setStatus('Lege ab…')
    const res = await window.api.mail.archiveAttachment(ref, sender)
    setBusy(false)
    if (!res.ok) {
      setStatus(
        res.error === 'NO_ARCHIVE_TARGET'
          ? 'Erst im Archiv-Tab einen Ordner einbinden'
          : 'Fehler: ' + res.error
      )
      return
    }
    setStatus('Abgelegt in: ' + shortPath(res.data))
  }

  return (
    <div className="flex items-center gap-2 rounded bg-gray-100 px-2 py-1.5 text-xs text-gray-700">
      <span
        className="flex items-center gap-1 truncate"
        title={`${att.contentType} · ${Math.round(att.size / 1024)} KB`}
      >
        <Paperclip className="h-3.5 w-3.5 shrink-0" />
        {att.filename}
      </span>
      <span className="ml-auto flex shrink-0 items-center gap-1">
        {status && <span className="text-gray-500">{status}</span>}
        {isPdf && (
          <button
            onClick={handleViewPdf}
            disabled={busy}
            className="flex items-center gap-1 rounded border bg-white px-2 py-0.5 hover:bg-gray-50 disabled:opacity-50"
          >
            <Eye className="h-3.5 w-3.5" />
            PDF ansehen
          </button>
        )}
        <button
          onClick={handleSave}
          disabled={busy}
          className="flex items-center gap-1 rounded border bg-white px-2 py-0.5 hover:bg-gray-50 disabled:opacity-50"
        >
          <Save className="h-3.5 w-3.5" />
          Speichern unter…
        </button>
        <button
          onClick={handleArchive}
          disabled={busy}
          className="flex items-center gap-1 rounded bg-brand px-2 py-0.5 text-white hover:bg-brand-dark disabled:opacity-50"
          title="In den eingebundenen Ordner ablegen, sortiert nach Absender"
        >
          <FolderInput className="h-3.5 w-3.5" />
          In Ordner ablegen
        </button>
      </span>
    </div>
  )
}

function shortPath(p: string): string {
  const parts = p.replace(/\\/g, '/').split('/')
  return parts.length > 2 ? '…/' + parts.slice(-2).join('/') : p
}
