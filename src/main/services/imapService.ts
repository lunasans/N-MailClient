import { ImapFlow, type ListResponse } from 'imapflow'
import { simpleParser } from 'mailparser'
import type {
  AttachmentMeta,
  MailboxNode,
  MessageDetail,
  MessageSummary
} from '../types'
import { getCredentials } from './accountStore'
import { getFolderCache, setFolderCache } from './db'
import { processIncoming } from './pgpService'
import { parseDeliveryReport } from './dsnService'

/**
 * IMAP read operations via imapflow. Each call opens a short-lived connection,
 * does its work and logs out. Simple and robust for the MVP; a pooled/IDLE
 * approach can replace this later without touching the IPC surface.
 */

async function withClient<T>(accountId: string, fn: (client: ImapFlow) => Promise<T>): Promise<T> {
  const creds = getCredentials(accountId)
  if (!creds) throw new Error('Konto nicht gefunden')
  const { account, password } = creds
  const client = new ImapFlow({
    host: account.imap.host,
    port: account.imap.port,
    secure: account.imap.secure,
    auth: { user: account.user, pass: password },
    logger: false,
    socketTimeout: 30000,
    greetingTimeout: 10000,
    connectionTimeout: 10000
  })
  await client.connect()
  try {
    return await fn(client)
  } finally {
    try {
      await client.logout()
    } catch {
      /* ignore */
    }
  }
}

function roleFor(box: ListResponse): string | undefined {
  const use = box.specialUse // e.g. '\\Sent'
  if (!use && box.path.toLowerCase() === 'inbox') return 'inbox'
  switch (use) {
    case '\\Sent':
      return 'sent'
    case '\\Drafts':
      return 'drafts'
    case '\\Trash':
      return 'trash'
    case '\\Junk':
      return 'junk'
    case '\\Archive':
      return 'archive'
    default:
      return undefined
  }
}

export async function listFolders(accountId: string): Promise<MailboxNode[]> {
  return withClient(accountId, async (client) => {
    const list = await client.list()
    // Keep container-only (\Noselect) folders so the hierarchy stays intact;
    // just mark them as not selectable for the UI.
    const nodes: MailboxNode[] = list.map((b) => ({
      path: b.path,
      name: b.name,
      delimiter: b.delimiter || '/',
      selectable: !b.flags?.has('\\Noselect'),
      role: roleFor(b)
    }))

    // Fetch the unread count for each selectable folder (one STATUS each).
    for (const node of nodes) {
      if (!node.selectable) continue
      try {
        const status = await client.status(node.path, { unseen: true })
        node.unseen = status.unseen ?? 0
      } catch {
        /* folder may not support STATUS — leave undefined */
      }
    }
    // Persist for instant display on next startup.
    setFolderCache(accountId, nodes)
    return nodes
  })
}

/** Last-known folder list from the persistent cache (instant, no network). */
export function getCachedFolders(accountId: string): MailboxNode[] {
  return getFolderCache(accountId) ?? []
}

function addr(value: unknown): string {
  if (!value) return ''
  // imapflow envelope addresses: [{ name, address }]
  if (Array.isArray(value)) {
    return value
      .map((a: { name?: string; address?: string }) =>
        a.name ? `${a.name} <${a.address ?? ''}>` : (a.address ?? '')
      )
      .join(', ')
  }
  return String(value)
}

export async function listMessages(
  accountId: string,
  folder: string,
  limit = 50,
  offset = 0
): Promise<MessageSummary[]> {
  return withClient(accountId, async (client) => {
    const lock = await client.getMailboxLock(folder)
    try {
      const mailbox = client.mailbox
      const total = mailbox && typeof mailbox !== 'boolean' ? mailbox.exists : 0
      if (!total) return []
      // Newest block is offset 0; larger offsets page into older messages.
      const end = total - offset
      if (end < 1) return []
      const start = Math.max(1, end - limit + 1)
      const range = `${start}:${end}`
      const out: MessageSummary[] = []
      for await (const msg of client.fetch(
        range,
        { uid: true, envelope: true, flags: true, bodyStructure: true },
        { uid: false }
      )) {
        out.push(toSummary(msg))
      }
      // Newest first.
      out.sort((a, b) => (a.date < b.date ? 1 : -1))
      return out
    } finally {
      lock.release()
    }
  })
}

/** Build a MessageSummary from a fetched imapflow message object. */
function toSummary(msg: {
  uid: number
  envelope?: { subject?: string; from?: unknown; to?: unknown; date?: Date | string }
  flags?: Set<string>
  bodyStructure?: unknown
}): MessageSummary {
  const flags = msg.flags ?? new Set<string>()
  return {
    uid: msg.uid,
    subject: msg.envelope?.subject ?? '(kein Betreff)',
    from: addr(msg.envelope?.from),
    to: addr(msg.envelope?.to),
    date: msg.envelope?.date ? new Date(msg.envelope.date).toISOString() : '',
    seen: flags.has('\\Seen'),
    flagged: flags.has('\\Flagged'),
    answered: flags.has('\\Answered'),
    hasAttachments: hasAttachments(msg.bodyStructure),
    keywords: [...flags].filter((f) => !f.startsWith('\\')),
    snippet: ''
  }
}

/** Full-text search within a folder (subject/from/to/body via IMAP). */
export async function searchMessages(
  accountId: string,
  folder: string,
  query: string,
  limit = 100
): Promise<MessageSummary[]> {
  const q = query.trim()
  if (!q) return []
  return withClient(accountId, async (client) => {
    const lock = await client.getMailboxLock(folder)
    try {
      const uids = (await client.search(
        { or: [{ subject: q }, { from: q }, { to: q }, { body: q }] },
        { uid: true }
      )) || []
      if (uids.length === 0) return []
      // Most recent matches first; cap to keep it responsive.
      const wanted = uids.slice(-limit)
      const out: MessageSummary[] = []
      for await (const msg of client.fetch(
        wanted,
        { uid: true, envelope: true, flags: true, bodyStructure: true },
        { uid: true }
      )) {
        out.push(toSummary(msg))
      }
      out.sort((a, b) => (a.date < b.date ? 1 : -1))
      return out
    } finally {
      lock.release()
    }
  })
}

/** Search all selectable folders of an account for a custom keyword (label). */
export async function searchByKeyword(
  accountId: string,
  keyword: string
): Promise<Array<MessageSummary & { folder: string }>> {
  return withClient(accountId, async (client) => {
    const out: Array<MessageSummary & { folder: string }> = []
    const boxes = await client.list()
    for (const box of boxes) {
      if (box.flags?.has('\\Noselect')) continue
      const lock = await client.getMailboxLock(box.path)
      try {
        const uids = (await client.search({ keyword }, { uid: true })) || []
        if (uids.length) {
          for await (const msg of client.fetch(
            uids,
            { uid: true, envelope: true, flags: true, bodyStructure: true },
            { uid: true }
          )) {
            out.push({ ...toSummary(msg), folder: box.path })
          }
        }
      } catch {
        /* folder may not support keyword search — skip */
      } finally {
        lock.release()
      }
    }
    out.sort((a, b) => (a.date < b.date ? 1 : -1))
    return out
  })
}

function hasAttachments(structure: unknown): boolean {
  if (!structure || typeof structure !== 'object') return false
  const node = structure as { disposition?: string; childNodes?: unknown[] }
  if (node.disposition && node.disposition.toLowerCase() === 'attachment') return true
  if (Array.isArray(node.childNodes)) return node.childNodes.some((c) => hasAttachments(c))
  return false
}

function icsDateToISO(v: string): string {
  const m = v.match(/(\d{4})(\d{2})(\d{2})(?:T(\d{2})(\d{2})(\d{2})(Z)?)?/)
  if (!m) return new Date().toISOString()
  const [, y, mo, d, h, mi, s, z] = m
  if (!h) return new Date(+y, +mo - 1, +d).toISOString()
  if (z) return new Date(Date.UTC(+y, +mo - 1, +d, +h, +mi, +(s || 0))).toISOString()
  return new Date(+y, +mo - 1, +d, +h, +mi, +(s || 0)).toISOString()
}

/** Parse a minimal calendar invitation from an iCalendar string. */
function parseInvite(ics: string): import('../types').InviteInfo | null {
  if (!/BEGIN:VEVENT/i.test(ics)) return null
  const g = (k: string): string | undefined =>
    ics.match(new RegExp('^' + k + '[^:\\r\\n]*:(.+)$', 'im'))?.[1]?.trim()
  const dtstart = g('DTSTART')
  if (!dtstart) return null
  const dtend = g('DTEND')
  const startLine = ics.match(/^DTSTART[^:\r\n]*:/im)?.[0] ?? ''
  const allDay = /VALUE=DATE/i.test(startLine) || /^\d{8}$/.test(dtstart)
  return {
    summary: g('SUMMARY') ?? '(Termin)',
    startISO: icsDateToISO(dtstart),
    endISO: dtend ? icsDateToISO(dtend) : icsDateToISO(dtstart),
    allDay,
    location: g('LOCATION') ?? '',
    description: g('DESCRIPTION') ?? ''
  }
}

export async function getMessage(
  accountId: string,
  folder: string,
  uid: number
): Promise<MessageDetail> {
  return withClient(accountId, async (client) => {
    const lock = await client.getMailboxLock(folder)
    try {
      const { content } = await client.download(`${uid}`, undefined, { uid: true })
      const chunks: Buffer[] = []
      for await (const chunk of content) chunks.push(chunk as Buffer)
      const raw = Buffer.concat(chunks)
      const parsed = await simpleParser(raw)

      const attachments: AttachmentMeta[] = (parsed.attachments ?? []).map((a, i) => ({
        filename: a.filename ?? `anhang-${i + 1}`,
        contentType: a.contentType ?? 'application/octet-stream',
        size: a.size ?? a.content?.length ?? 0,
        partId: (a as { partId?: string }).partId ?? String(i)
      }))

      const references = Array.isArray(parsed.references)
        ? parsed.references
        : parsed.references
          ? [parsed.references]
          : []

      const detail: MessageDetail = {
        uid,
        subject: parsed.subject ?? '(kein Betreff)',
        from: parsed.from?.text ?? '',
        to: Array.isArray(parsed.to)
          ? parsed.to.map((t) => t.text).join(', ')
          : (parsed.to?.text ?? ''),
        cc: Array.isArray(parsed.cc)
          ? parsed.cc.map((t) => t.text).join(', ')
          : (parsed.cc?.text ?? ''),
        bcc: Array.isArray(parsed.bcc)
          ? parsed.bcc.map((t) => t.text).join(', ')
          : (parsed.bcc?.text ?? ''),
        date: parsed.date ? parsed.date.toISOString() : '',
        html: parsed.html || null,
        text: parsed.text ?? null,
        attachments,
        messageId: parsed.messageId ?? null,
        references
      }

      // PGP: decrypt / verify if the message carries an armored block.
      try {
        const pgpRes = await processIncoming(raw.toString('utf8'))
        if (pgpRes) {
          detail.pgp = pgpRes.info
          if (pgpRes.cleartext) {
            if (pgpRes.isMime) {
              const inner = await simpleParser(Buffer.from(pgpRes.cleartext))
              detail.html = inner.html || null
              detail.text = inner.text ?? (inner.html ? null : pgpRes.cleartext)
              // Inner (decrypted) attachments: cache content + expose with a pgp: partId.
              detail.attachments = (inner.attachments ?? []).map((a, i) => {
                const partId = `pgp:${i}`
                const filename = a.filename ?? `anhang-${i + 1}`
                const contentType = a.contentType ?? 'application/octet-stream'
                cachePgpAttachment(accountId, folder, uid, partId, {
                  content: a.content as Buffer,
                  contentType,
                  filename
                })
                return { filename, contentType, size: a.size ?? a.content?.length ?? 0, partId }
              })
            } else {
              detail.text = pgpRes.cleartext
              detail.html = null
              if (pgpRes.info.encrypted) detail.attachments = []
            }
          }
        }
      } catch {
        /* leave the original (possibly ciphertext) body if PGP handling fails */
      }

      // Delivery status notifications (bounces / delivery reports).
      try {
        const delivery = parseDeliveryReport(raw.toString('utf8'))
        if (delivery) detail.delivery = delivery
      } catch {
        /* ignore — not a parseable report */
      }

      // Calendar invitation (text/calendar part).
      try {
        const calAtt = (parsed.attachments ?? []).find((a) =>
          /calendar|\.ics$/i.test(`${a.contentType ?? ''} ${a.filename ?? ''}`)
        )
        const icsText = calAtt
          ? (calAtt.content as Buffer).toString('utf8')
          : raw.toString('utf8').match(/BEGIN:VCALENDAR[\s\S]*?END:VCALENDAR/i)?.[0]
        if (icsText) {
          const invite = parseInvite(icsText)
          if (invite) detail.invite = invite
        }
      } catch {
        /* ignore — not a parseable invitation */
      }

      return detail
    } finally {
      lock.release()
    }
  })
}

/** Fetch the raw RFC822 source of a message (for the "show source" view). */
export async function getRawSource(
  accountId: string,
  folder: string,
  uid: number
): Promise<string> {
  return withClient(accountId, async (client) => {
    const lock = await client.getMailboxLock(folder)
    try {
      const { content } = await client.download(`${uid}`, undefined, { uid: true })
      const chunks: Buffer[] = []
      for await (const chunk of content) chunks.push(chunk as Buffer)
      return Buffer.concat(chunks).toString('utf-8')
    } finally {
      lock.release()
    }
  })
}

/** Fetch all attachments of a message as nodemailer-ready objects (for forwarding). */
export async function getAllAttachments(
  accountId: string,
  folder: string,
  uid: number
): Promise<Array<{ filename: string; content: Buffer; contentType: string }>> {
  return withClient(accountId, async (client) => {
    const lock = await client.getMailboxLock(folder)
    try {
      const { content } = await client.download(`${uid}`, undefined, { uid: true })
      const chunks: Buffer[] = []
      for await (const chunk of content) chunks.push(chunk as Buffer)
      const parsed = await simpleParser(Buffer.concat(chunks))
      return (parsed.attachments ?? []).map((a, i) => ({
        filename: a.filename ?? `anhang-${i + 1}`,
        content: a.content as Buffer,
        contentType: a.contentType ?? 'application/octet-stream'
      }))
    } finally {
      lock.release()
    }
  })
}

/** Download a single attachment's bytes by re-parsing the message. */
// Cache of attachments extracted from decrypted PGP/MIME messages (their content
// isn't fetchable via the normal IMAP part path). Keyed by account|folder|uid|partId.
const pgpAttachmentCache = new Map<string, { content: Buffer; contentType: string; filename: string }>()

function pgpAttKey(accountId: string, folder: string, uid: number, partId: string): string {
  return `${accountId}|${folder}|${uid}|${partId}`
}

export function cachePgpAttachment(
  accountId: string,
  folder: string,
  uid: number,
  partId: string,
  att: { content: Buffer; contentType: string; filename: string }
): void {
  if (pgpAttachmentCache.size > 200) pgpAttachmentCache.clear()
  pgpAttachmentCache.set(pgpAttKey(accountId, folder, uid, partId), att)
}

export async function downloadAttachment(
  accountId: string,
  folder: string,
  uid: number,
  partId: string,
  filename: string
): Promise<{ content: Buffer; contentType: string; filename: string }> {
  if (partId.startsWith('pgp:')) {
    const hit = pgpAttachmentCache.get(pgpAttKey(accountId, folder, uid, partId))
    if (hit) return hit
    throw new Error('Entschlüsselter Anhang nicht mehr im Cache — Mail erneut öffnen.')
  }
  return withClient(accountId, async (client) => {
    const lock = await client.getMailboxLock(folder)
    try {
      const { content } = await client.download(`${uid}`, undefined, { uid: true })
      const chunks: Buffer[] = []
      for await (const chunk of content) chunks.push(chunk as Buffer)
      const parsed = await simpleParser(Buffer.concat(chunks))
      const list = parsed.attachments ?? []
      const match =
        list.find((a) => (a as { partId?: string }).partId === partId) ??
        list.find((a) => a.filename === filename)
      if (!match) throw new Error('Anhang nicht gefunden')
      return {
        content: match.content as Buffer,
        contentType: match.contentType ?? 'application/octet-stream',
        filename: match.filename ?? filename
      }
    } finally {
      lock.release()
    }
  })
}

export async function setSeen(
  accountId: string,
  folder: string,
  uids: number[],
  seen: boolean
): Promise<void> {
  if (uids.length === 0) return
  const seq = uids.join(',')
  await withClient(accountId, async (client) => {
    const lock = await client.getMailboxLock(folder)
    try {
      if (seen) {
        await client.messageFlagsAdd(seq, ['\\Seen'], { uid: true })
      } else {
        await client.messageFlagsRemove(seq, ['\\Seen'], { uid: true })
      }
    } finally {
      lock.release()
    }
  })
}

/** Add or remove a custom keyword (label) on messages. */
export async function setKeyword(
  accountId: string,
  folder: string,
  uids: number[],
  keyword: string,
  on: boolean
): Promise<void> {
  if (uids.length === 0) return
  const seq = uids.join(',')
  await withClient(accountId, async (client) => {
    const lock = await client.getMailboxLock(folder)
    try {
      if (on) await client.messageFlagsAdd(seq, [keyword], { uid: true })
      else await client.messageFlagsRemove(seq, [keyword], { uid: true })
    } finally {
      lock.release()
    }
  })
}

/** Toggle the \Flagged (star) flag on messages. */
export async function setFlagged(
  accountId: string,
  folder: string,
  uids: number[],
  flagged: boolean
): Promise<void> {
  if (uids.length === 0) return
  const seq = uids.join(',')
  await withClient(accountId, async (client) => {
    const lock = await client.getMailboxLock(folder)
    try {
      if (flagged) await client.messageFlagsAdd(seq, ['\\Flagged'], { uid: true })
      else await client.messageFlagsRemove(seq, ['\\Flagged'], { uid: true })
    } finally {
      lock.release()
    }
  })
}

/** Mark a message as answered (\Answered), e.g. after sending a reply. */
export async function markAnswered(
  accountId: string,
  folder: string,
  uid: number
): Promise<void> {
  await withClient(accountId, async (client) => {
    const lock = await client.getMailboxLock(folder)
    try {
      await client.messageFlagsAdd(`${uid}`, ['\\Answered'], { uid: true })
    } finally {
      lock.release()
    }
  })
}

/** Move messages out of Junk back to the inbox (trains "ham" on Dovecot/Rspamd). */
export async function markNotSpam(
  accountId: string,
  folder: string,
  uids: number[]
): Promise<void> {
  if (uids.length === 0) return
  const inbox = (await findFolderByRole(accountId, 'inbox')) ?? 'INBOX'
  if (inbox === folder) return
  await withClient(accountId, async (client) => {
    const lock = await client.getMailboxLock(folder)
    try {
      await client.messageMove(uids.join(','), inbox, { uid: true })
    } finally {
      lock.release()
    }
  })
}

/** Move messages into the Archive folder, if the server exposes one. */
export async function archiveMessages(
  accountId: string,
  folder: string,
  uids: number[]
): Promise<void> {
  if (uids.length === 0) return
  const archive = await findFolderByRole(accountId, 'archive')
  if (!archive) throw new Error('Kein Archiv-Ordner vorhanden')
  if (archive === folder) return
  await withClient(accountId, async (client) => {
    const lock = await client.getMailboxLock(folder)
    try {
      await client.messageMove(uids.join(','), archive, { uid: true })
    } finally {
      lock.release()
    }
  })
}

/** Move messages to another folder. */
export async function moveMessages(
  accountId: string,
  folder: string,
  uids: number[],
  target: string
): Promise<void> {
  if (uids.length === 0 || target === folder) return
  await withClient(accountId, async (client) => {
    const lock = await client.getMailboxLock(folder)
    try {
      await client.messageMove(uids.join(','), target, { uid: true })
    } finally {
      lock.release()
    }
  })
}

/**
 * Delete a message: move it to the Trash folder if one exists (and we're not
 * already in Trash), otherwise flag \Deleted and expunge.
 */
export async function deleteMessages(
  accountId: string,
  folder: string,
  uids: number[]
): Promise<void> {
  if (uids.length === 0) return
  const seq = uids.join(',')
  const trash = await findFolderByRole(accountId, 'trash')
  await withClient(accountId, async (client) => {
    const lock = await client.getMailboxLock(folder)
    try {
      if (trash && trash !== folder) {
        await client.messageMove(seq, trash, { uid: true })
      } else {
        await client.messageDelete(seq, { uid: true })
      }
    } finally {
      lock.release()
    }
  })
}

/** Move a message into the Junk/Spam folder, if the server exposes one. */
export async function markAsSpam(
  accountId: string,
  folder: string,
  uids: number[]
): Promise<void> {
  if (uids.length === 0) return
  const junk = await findFolderByRole(accountId, 'junk')
  if (!junk) throw new Error('Kein Spam-/Junk-Ordner vorhanden')
  if (junk === folder) return
  await withClient(accountId, async (client) => {
    const lock = await client.getMailboxLock(folder)
    try {
      await client.messageMove(uids.join(','), junk, { uid: true })
    } finally {
      lock.release()
    }
  })
}

/** Append a raw RFC822 message to a folder (used to save sent mail). */
export async function appendMessage(
  accountId: string,
  folder: string,
  raw: Buffer,
  flags: string[] = ['\\Seen']
): Promise<void> {
  await withClient(accountId, async (client) => {
    await client.append(folder, raw, flags)
  })
}

/** Append a draft and return its new UID (when the server reports UIDPLUS). */
export async function appendDraft(
  accountId: string,
  folder: string,
  raw: Buffer
): Promise<number | undefined> {
  return withClient(accountId, async (client) => {
    const res = await client.append(folder, raw, ['\\Draft', '\\Seen'])
    return res && typeof res !== 'boolean' ? res.uid : undefined
  })
}

/** Permanently delete a message (flag \Deleted + expunge), e.g. an old draft. */
export async function expungeMessage(
  accountId: string,
  folder: string,
  uid: number
): Promise<void> {
  await withClient(accountId, async (client) => {
    const lock = await client.getMailboxLock(folder)
    try {
      await client.messageDelete(`${uid}`, { uid: true })
    } finally {
      lock.release()
    }
  })
}

/** Create a new mailbox/folder at the given full path. */
export async function createFolder(accountId: string, path: string): Promise<void> {
  await withClient(accountId, async (client) => {
    await client.mailboxCreate(path)
  })
}

/** Delete a mailbox/folder. */
export async function deleteFolder(accountId: string, path: string): Promise<void> {
  await withClient(accountId, async (client) => {
    await client.mailboxDelete(path)
  })
}

/** Rename or move a mailbox/folder (move = change the parent in the new path). */
export async function renameFolder(
  accountId: string,
  oldPath: string,
  newPath: string
): Promise<void> {
  await withClient(accountId, async (client) => {
    await client.mailboxRename(oldPath, newPath)
  })
}

/** Find the path of a special-use folder (e.g. 'sent'), if the server exposes one. */
export async function findFolderByRole(
  accountId: string,
  role: string
): Promise<string | null> {
  const folders = await listFolders(accountId)
  return folders.find((f) => f.role === role)?.path ?? null
}
