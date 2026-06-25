import { app, BrowserWindow, dialog, shell } from 'electron'
import { basename, dirname, join, resolve, sep } from 'path'
import {
  existsSync,
  mkdirSync,
  readdirSync,
  readFileSync,
  rmdirSync,
  statSync,
  unlinkSync,
  writeFileSync
} from 'fs'
import type {
  ArchiveListing,
  AttachmentRef,
  PickedAttachment,
  SaveResult,
  WebDavConfig
} from '../types'
import {
  getArchiveTarget,
  getWebdavPassword,
  setLocalArchive,
  setWebdavArchive
} from './accountStore'
import { downloadAttachment } from './imapService'
import { openPdf } from './pdfService'
import * as webdav from './webdavService'

// Characters that are illegal in Windows file/folder names.
const ILLEGAL_CHARS = ['<', '>', ':', '"', '/', '\\', '|', '?', '*']

/** Make a string safe to use as a single path segment (file or folder name). */
function sanitizeSegment(input: string): string {
  let out = ''
  for (const ch of input) {
    out += ch.charCodeAt(0) < 32 || ILLEGAL_CHARS.includes(ch) ? '_' : ch
  }
  out = out
    .replace(/\s+/g, ' ')
    .replace(/^\.+/, '')
    .replace(/[. ]+$/, '')
    .slice(0, 120)
    .trim()
  return out || 'unbekannt'
}

/** Derive a tidy folder name from a "Name <email>" sender string. */
function senderFolder(sender: string): string {
  const match = sender.match(/<([^>]+)>/)
  const email = match ? match[1] : sender
  return sanitizeSegment(email.trim())
}

/** Avoid overwriting: append " (1)", " (2)", … before the extension. */
function uniquePath(dir: string, filename: string): string {
  let candidate = join(dir, filename)
  if (!existsSync(candidate)) return candidate
  const dot = filename.lastIndexOf('.')
  const base = dot > 0 ? filename.slice(0, dot) : filename
  const ext = dot > 0 ? filename.slice(dot) : ''
  let i = 1
  do {
    candidate = join(dir, `${base} (${i})${ext}`)
    i++
  } while (existsSync(candidate))
  return candidate
}

/** Save an attachment via the native "Save As" dialog. */
export async function saveAttachmentAs(ref: AttachmentRef): Promise<SaveResult> {
  const att = await downloadAttachment(ref.accountId, ref.folder, ref.uid, ref.partId, ref.filename)
  const win = BrowserWindow.getFocusedWindow() ?? undefined
  const result = await dialog.showSaveDialog(win!, {
    title: 'Anhang speichern',
    defaultPath: att.filename
  })
  if (result.canceled || !result.filePath) return { canceled: true }
  writeFileSync(result.filePath, att.content)
  return { canceled: false, path: result.filePath }
}

/** Bind a local folder as the archive target for an account. */
export async function pickArchiveFolder(accountId: string): Promise<string | null> {
  const win = BrowserWindow.getFocusedWindow() ?? undefined
  const result = await dialog.showOpenDialog(win!, {
    title: 'Ordner für Anhänge wählen',
    properties: ['openDirectory', 'createDirectory']
  })
  if (result.canceled || !result.filePaths[0]) return null
  const folder = result.filePaths[0]
  setLocalArchive(accountId, folder)
  return folder
}

/** Native multi-file picker for composing attachments. Returns local paths. */
export async function pickAttachments(): Promise<PickedAttachment[]> {
  const win = BrowserWindow.getFocusedWindow() ?? undefined
  const result = await dialog.showOpenDialog(win!, {
    title: 'Anhänge auswählen',
    properties: ['openFile', 'multiSelections']
  })
  if (result.canceled) return []
  return result.filePaths.map((p) => {
    let size = 0
    try {
      size = statSync(p).size
    } catch {
      /* ignore */
    }
    return { path: p, filename: basename(p), size }
  })
}

/** Verify a WebDAV connection without saving it. */
export async function testWebdav(config: WebDavConfig, password: string): Promise<void> {
  await webdav.testConnection(config, password)
}

/** Bind a WebDAV target as the archive for an account (after a successful test). */
export async function configureWebdav(
  accountId: string,
  config: WebDavConfig,
  password: string
): Promise<void> {
  await webdav.testConnection(config, password)
  setWebdavArchive(accountId, config, password)
}

/**
 * Archive an attachment under <target>/<sender>/<filename>.
 * Throws NO_ARCHIVE_TARGET if nothing is bound yet so the renderer can react.
 */
export async function archiveAttachment(ref: AttachmentRef, sender: string): Promise<string> {
  const target = getArchiveTarget(ref.accountId)
  if (!target) throw new Error('NO_ARCHIVE_TARGET')

  const att = await downloadAttachment(ref.accountId, ref.folder, ref.uid, ref.partId, ref.filename)
  const senderName = senderFolder(sender)
  const filename = sanitizeSegment(att.filename)

  if (target.type === 'local') {
    const dir = join(target.folder, senderName)
    if (!existsSync(dir)) mkdirSync(dir, { recursive: true })
    const out = uniquePath(dir, filename)
    writeFileSync(out, att.content)
    return out
  }

  const password = getWebdavPassword(ref.accountId)
  if (!password) throw new Error('WebDAV-Passwort fehlt')
  return webdav.uploadAttachment(target.webdav, password, senderName, filename, att.content)
}

/** Read the bound archive target, grouped by sender. */
export async function listArchive(accountId: string): Promise<ArchiveListing> {
  const target = getArchiveTarget(accountId)
  if (!target) return { target: null, groups: [] }

  if (target.type === 'local') {
    const folder = target.folder
    if (!existsSync(folder)) return { target, groups: [] }
    const groups = readdirSync(folder, { withFileTypes: true })
      .filter((d) => d.isDirectory())
      .map((d) => {
        const senderDir = join(folder, d.name)
        const files = readdirSync(senderDir, { withFileTypes: true })
          .filter((f) => f.isFile())
          .map((f) => {
            const full = join(senderDir, f.name)
            const st = statSync(full)
            return { name: f.name, path: full, size: st.size, modified: st.mtime.toISOString() }
          })
          .sort((a, b) => (a.modified < b.modified ? 1 : -1))
        return { sender: d.name, files }
      })
      .filter((g) => g.files.length > 0)
      .sort((a, b) => a.sender.localeCompare(b.sender, 'de'))
    return { target, groups }
  }

  const password = getWebdavPassword(accountId)
  if (!password) throw new Error('WebDAV-Passwort fehlt')
  const groups = await webdav.listArchive(target.webdav, password)
  return { target, groups }
}

/** Delete an archived file (local or WebDAV); cleans up an emptied sender folder. */
export async function deleteArchiveFile(accountId: string, filePath: string): Promise<void> {
  const target = getArchiveTarget(accountId)
  if (!target) throw new Error('NO_ARCHIVE_TARGET')

  if (target.type === 'webdav') {
    const password = getWebdavPassword(accountId)
    if (!password) throw new Error('WebDAV-Passwort fehlt')
    await webdav.deleteFile(target.webdav, password, filePath)
    return
  }

  // Local: refuse anything outside the bound archive folder.
  const root = resolve(target.folder)
  const full = resolve(filePath)
  if (full !== root && !full.startsWith(root + sep)) {
    throw new Error('Pfad liegt außerhalb des Archivordners.')
  }
  if (!existsSync(full)) return
  unlinkSync(full)
  // Remove the sender subfolder if it became empty.
  const dir = dirname(full)
  try {
    if (dir !== root && readdirSync(dir).length === 0) rmdirSync(dir)
  } catch {
    /* ignore */
  }
}

/** Open an archived file with the OS default application. */
export async function openArchiveFile(accountId: string, path: string): Promise<void> {
  const target = getArchiveTarget(accountId)
  if (target?.type === 'webdav') {
    const password = getWebdavPassword(accountId)
    if (!password) throw new Error('WebDAV-Passwort fehlt')
    const data = await webdav.downloadFile(target.webdav, password, path)
    const tmp = join(app.getPath('temp'), path.split('/').pop() || 'anhang')
    writeFileSync(tmp, data)
    const err = await shell.openPath(tmp)
    if (err) throw new Error(err)
    return
  }
  const err = await shell.openPath(path)
  if (err) throw new Error(err)
}

/** Reveal a file: in Explorer (local) or in the browser (WebDAV). */
export async function revealArchiveFile(accountId: string, path: string): Promise<void> {
  const target = getArchiveTarget(accountId)
  if (target?.type === 'webdav') {
    await shell.openExternal(webdav.webUrlFor(target.webdav, path))
    return
  }
  shell.showItemInFolder(path)
}

/** Open a mail attachment in the built-in PDF viewer. */
export async function viewAttachmentPdf(ref: AttachmentRef): Promise<void> {
  const att = await downloadAttachment(ref.accountId, ref.folder, ref.uid, ref.partId, ref.filename)
  openPdf(att.content, att.filename)
}

/** Open an archived file (local or WebDAV) in the built-in PDF viewer. */
export async function viewArchivePdf(accountId: string, path: string): Promise<void> {
  const target = getArchiveTarget(accountId)
  let content: Buffer
  if (target?.type === 'webdav') {
    const password = getWebdavPassword(accountId)
    if (!password) throw new Error('WebDAV-Passwort fehlt')
    content = await webdav.downloadFile(target.webdav, password, path)
  } else {
    content = readFileSync(path)
  }
  openPdf(content, basename(path))
}
