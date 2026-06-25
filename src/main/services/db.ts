import { app, safeStorage } from 'electron'
import { join } from 'path'
import { readFileSync, writeFileSync, existsSync, mkdirSync } from 'fs'
import type { ArchiveTarget, CalendarConfig, Label, MailboxNode, ServerConfig } from '../types'

/**
 * Lightweight JSON-backed store in the userData directory.
 * Chosen over SQLite for the MVP to avoid native-module rebuilds on Windows.
 * The password is stored separately as a base64 blob encrypted via safeStorage (DPAPI).
 */

export interface StoredAccount {
  id: string
  name: string
  email: string
  user: string
  imap: ServerConfig
  smtp: ServerConfig
  /** base64 of safeStorage-encrypted password */
  secret: string
  /** Bound archive target (local folder or WebDAV). */
  archive?: ArchiveTarget
  /** base64 of safeStorage-encrypted WebDAV password (when archive.type === 'webdav'). */
  webdavSecret?: string
  /** Plain-text signature appended when composing. */
  signature?: string
  /** Custom folder order (full paths). */
  folderOrder?: string[]
  /** Hex color used to mark this account. */
  color?: string
  /** Additional sender addresses (aliases). */
  aliases?: string[]
}

interface DbShape {
  version: number
  accounts: StoredAccount[]
  /** Last-known folder list per account, for instant display on startup. */
  folderCache?: Record<string, MailboxNode[]>
  /** User-defined labels (global across accounts). */
  labels?: Label[]
  /** CalDAV calendar connection (single, global). */
  calendar?: CalendarConfig & { secret: string }
}

let dbPath = ''
let data: DbShape = { version: 1, accounts: [] }

export function initDb(): void {
  const dir = app.getPath('userData')
  if (!existsSync(dir)) mkdirSync(dir, { recursive: true })
  dbPath = join(dir, 'neuhaus-mail.json')
  if (existsSync(dbPath)) {
    try {
      data = JSON.parse(readFileSync(dbPath, 'utf-8')) as DbShape
    } catch {
      data = { version: 1, accounts: [] }
    }
  } else {
    persist()
  }
}

function persist(): void {
  writeFileSync(dbPath, JSON.stringify(data, null, 2), 'utf-8')
}

export function listAccounts(): StoredAccount[] {
  return data.accounts
}

export function getAccount(id: string): StoredAccount | undefined {
  return data.accounts.find((a) => a.id === id)
}

export function addAccount(acc: StoredAccount): void {
  data.accounts.push(acc)
  persist()
}

export function removeAccount(id: string): void {
  data.accounts = data.accounts.filter((a) => a.id !== id)
  persist()
}

export function updateAccount(id: string, patch: Partial<StoredAccount>): void {
  const acc = data.accounts.find((a) => a.id === id)
  if (!acc) return
  Object.assign(acc, patch)
  persist()
}

export function getFolderCache(accountId: string): MailboxNode[] | undefined {
  return data.folderCache?.[accountId]
}

export function setFolderCache(accountId: string, folders: MailboxNode[]): void {
  data.folderCache = { ...(data.folderCache ?? {}), [accountId]: folders }
  persist()
}

export function clearFolderCache(accountId: string): void {
  if (data.folderCache?.[accountId]) {
    delete data.folderCache[accountId]
    persist()
  }
}

export function getLabels(): Label[] {
  return data.labels ?? []
}

export function setLabels(labels: Label[]): void {
  data.labels = labels
  persist()
}

export function getCalendar(): (CalendarConfig & { secret: string }) | undefined {
  return data.calendar
}

export function setCalendar(config: CalendarConfig, password: string): void {
  data.calendar = { ...config, secret: encryptPassword(password) }
  persist()
}

export function clearCalendar(): void {
  delete data.calendar
  persist()
}

/** Encrypts a plaintext password to a base64 blob (DPAPI when available). */
export function encryptPassword(plain: string): string {
  if (safeStorage.isEncryptionAvailable()) {
    return safeStorage.encryptString(plain).toString('base64')
  }
  // Fallback (e.g. dev without OS keychain): clearly mark so we never mistake it.
  return 'plain:' + Buffer.from(plain, 'utf-8').toString('base64')
}

/** Decrypts a stored secret back to plaintext. */
export function decryptPassword(secret: string): string {
  if (secret.startsWith('plain:')) {
    return Buffer.from(secret.slice('plain:'.length), 'base64').toString('utf-8')
  }
  return safeStorage.decryptString(Buffer.from(secret, 'base64'))
}
