import { randomUUID } from 'crypto'
import type { Account, AccountSettings, ArchiveTarget, NewAccount, WebDavConfig } from '../types'
import {
  addAccount,
  clearFolderCache,
  decryptPassword,
  encryptPassword,
  getAccount,
  listAccounts,
  removeAccount,
  updateAccount,
  type StoredAccount
} from './db'

/** Strip all secrets before anything reaches the renderer. */
function toPublic(a: StoredAccount): Account {
  return {
    id: a.id,
    name: a.name,
    email: a.email,
    user: a.user,
    imap: a.imap,
    smtp: a.smtp,
    archive: a.archive, // ArchiveTarget never carries a password
    signature: a.signature,
    aliasSignatures: a.aliasSignatures,
    folderOrder: a.folderOrder,
    color: a.color,
    aliases: a.aliases
  }
}

export function getAccounts(): Account[] {
  return listAccounts().map(toPublic)
}

export function createAccount(input: NewAccount): Account {
  const stored: StoredAccount = {
    id: randomUUID(),
    name: input.name || input.email,
    email: input.email,
    user: input.user || input.email,
    imap: input.imap,
    smtp: input.smtp,
    secret: encryptPassword(input.password)
  }
  addAccount(stored)
  return toPublic(stored)
}

export function deleteAccount(id: string): void {
  removeAccount(id)
  clearFolderCache(id)
}

export function updateSettings(id: string, settings: AccountSettings): Account | null {
  const patch: Partial<StoredAccount> = {
    name: settings.name,
    signature: settings.signature,
    aliasSignatures: settings.aliasSignatures,
    user: settings.user,
    imap: settings.imap,
    smtp: settings.smtp,
    folderOrder: settings.folderOrder,
    color: settings.color,
    aliases: settings.aliases
  }
  // Only replace the stored password when a new one was entered.
  if (settings.password) patch.secret = encryptPassword(settings.password)
  updateAccount(id, patch)
  const acc = getAccount(id)
  return acc ? toPublic(acc) : null
}

export function setLocalArchive(id: string, folder: string): Account | null {
  updateAccount(id, { archive: { type: 'local', folder }, webdavSecret: undefined })
  const acc = getAccount(id)
  return acc ? toPublic(acc) : null
}

export function setWebdavArchive(
  id: string,
  config: WebDavConfig,
  password: string
): Account | null {
  updateAccount(id, {
    archive: { type: 'webdav', webdav: config },
    webdavSecret: encryptPassword(password)
  })
  const acc = getAccount(id)
  return acc ? toPublic(acc) : null
}

/** Main-process only: resolve the archive target for an account. */
export function getArchiveTarget(id: string): ArchiveTarget | null {
  return getAccount(id)?.archive ?? null
}

/** Main-process only: the decrypted WebDAV password for an account. */
export function getWebdavPassword(id: string): string | null {
  const secret = getAccount(id)?.webdavSecret
  return secret ? decryptPassword(secret) : null
}

/** Main-process only: resolve the credentials needed to open a connection. */
export function getCredentials(id: string): { account: StoredAccount; password: string } | null {
  const account = getAccount(id)
  if (!account) return null
  return { account, password: decryptPassword(account.secret) }
}
