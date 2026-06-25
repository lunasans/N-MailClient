import { ipcMain } from 'electron'
import type {
  AccountSettings,
  AttachmentRef,
  CalEventInput,
  CalEventUpdate,
  ContactInput,
  ContactUpdate,
  DraftRef,
  IpcResult,
  NewAccount,
  SendRequest,
  WebDavConfig
} from '../types'
import {
  createAccount,
  deleteAccount,
  getAccounts,
  updateSettings
} from '../services/accountStore'
import { probeAccount } from '../services/autodiscover'
import {
  archiveMessages,
  createFolder,
  deleteFolder,
  deleteMessages,
  getCachedFolders,
  getMessage,
  getRawSource,
  listFolders,
  listMessages,
  markAsSpam,
  markNotSpam,
  moveMessages,
  renameFolder,
  searchByKeyword,
  searchMessages,
  setFlagged,
  setKeyword,
  setSeen
} from '../services/imapService'
import { addLabel, listLabels, removeLabel } from '../services/labelsService'
import {
  clearConfig as clearCalendarConfig,
  createEvent,
  deleteEvent,
  fetchEvents,
  getPublicConfig as getCalendarConfig,
  listCalendars,
  saveConfig as saveCalendarConfig,
  testConnection as testCalendar,
  updateEvent
} from '../services/caldavService'
import {
  createContact,
  deleteContact,
  fetchContacts,
  listAddressBooks,
  updateContact
} from '../services/carddavService'
import {
  deleteScript,
  getScript,
  listScripts,
  putScript,
  setActiveScript
} from '../services/sieveService'
import { sendMessage } from '../services/smtpService'
import { deleteDraft, saveDraft } from '../services/draftService'
import { restartIdle, startIdle, stopIdle } from '../services/idleService'
import {
  archiveAttachment,
  configureWebdav,
  listArchive,
  openArchiveFile,
  pickArchiveFolder,
  pickAttachments,
  revealArchiveFile,
  saveAttachmentAs,
  testWebdav,
  viewArchivePdf,
  viewAttachmentPdf
} from '../services/attachmentService'

/** Wrap a handler so the renderer always receives a uniform IpcResult. */
function handle<T>(channel: string, fn: (...args: any[]) => Promise<T> | T): void {
  ipcMain.handle(channel, async (_event, ...args): Promise<IpcResult<T>> => {
    try {
      const data = await fn(...args)
      return { ok: true, data }
    } catch (err) {
      return { ok: false, error: toMessage(err) }
    }
  })
}

function toMessage(err: unknown): string {
  if (err instanceof Error) {
    const m = err.message || ''
    if (/auth|credentials|login/i.test(m)) return 'Anmeldung fehlgeschlagen — Benutzer oder Passwort falsch.'
    if (/ENOTFOUND|EAI_AGAIN|getaddrinfo/i.test(m)) return 'Server nicht gefunden — Hostname prüfen.'
    if (/ECONNREFUSED/i.test(m)) return 'Verbindung abgelehnt — Port/Verschlüsselung prüfen.'
    if (/ETIMEDOUT|timeout/i.test(m)) return 'Zeitüberschreitung — Server nicht erreichbar.'
    if (/certificate|self.signed|tls|ssl/i.test(m)) return 'TLS-/Zertifikatsfehler beim Verbinden.'
    return m
  }
  return String(err)
}

export function registerIpc(): void {
  handle('accounts:list', () => getAccounts())
  handle('accounts:probe', (email: string) => probeAccount(email))
  handle('accounts:add', (input: NewAccount) => {
    const acc = createAccount(input)
    startIdle(acc.id)
    return acc
  })
  handle('accounts:remove', (id: string) => {
    deleteAccount(id)
    stopIdle(id)
  })
  handle('accounts:updateSettings', (id: string, settings: AccountSettings) => {
    const acc = updateSettings(id, settings)
    restartIdle(id) // server/credentials may have changed
    return acc
  })
  handle('accounts:pickArchiveFolder', (accountId: string) => pickArchiveFolder(accountId))
  handle('accounts:testWebdav', (config: WebDavConfig, password: string) =>
    testWebdav(config, password)
  )
  handle('accounts:setWebdavArchive', (accountId: string, config: WebDavConfig, password: string) =>
    configureWebdav(accountId, config, password)
  )

  handle('mail:folders', (accountId: string) => listFolders(accountId))
  handle('mail:foldersCached', (accountId: string) => getCachedFolders(accountId))
  handle('folder:create', (accountId: string, path: string) => createFolder(accountId, path))
  handle('folder:delete', (accountId: string, path: string) => deleteFolder(accountId, path))
  handle('folder:rename', (accountId: string, oldPath: string, newPath: string) =>
    renameFolder(accountId, oldPath, newPath)
  )
  handle('mail:list', (accountId: string, folder: string, limit?: number) =>
    listMessages(accountId, folder, limit)
  )
  handle('mail:get', (accountId: string, folder: string, uid: number) =>
    getMessage(accountId, folder, uid)
  )
  handle('mail:source', (accountId: string, folder: string, uid: number) =>
    getRawSource(accountId, folder, uid)
  )
  handle('mail:setSeen', (accountId: string, folder: string, uids: number[], seen: boolean) =>
    setSeen(accountId, folder, uids, seen)
  )
  handle('mail:delete', (accountId: string, folder: string, uids: number[]) =>
    deleteMessages(accountId, folder, uids)
  )
  handle('mail:markSpam', (accountId: string, folder: string, uids: number[]) =>
    markAsSpam(accountId, folder, uids)
  )
  handle('mail:archive', (accountId: string, folder: string, uids: number[]) =>
    archiveMessages(accountId, folder, uids)
  )
  handle('mail:notSpam', (accountId: string, folder: string, uids: number[]) =>
    markNotSpam(accountId, folder, uids)
  )
  handle('mail:setFlagged', (accountId: string, folder: string, uids: number[], flagged: boolean) =>
    setFlagged(accountId, folder, uids, flagged)
  )
  handle(
    'mail:setKeyword',
    (accountId: string, folder: string, uids: number[], keyword: string, on: boolean) =>
      setKeyword(accountId, folder, uids, keyword, on)
  )

  handle('labels:list', () => listLabels())
  handle('labels:add', (name: string, color: string) => addLabel(name, color))
  handle('labels:remove', (id: string) => removeLabel(id))

  handle('calendar:get', () => getCalendarConfig())
  handle('calendar:test', (serverUrl: string, user: string, password: string) =>
    testCalendar(serverUrl, user, password)
  )
  handle('calendar:save', (serverUrl: string, user: string, password: string) =>
    saveCalendarConfig(serverUrl, user, password)
  )
  handle('calendar:clear', () => clearCalendarConfig())
  handle('calendar:events', (startISO: string, endISO: string) => fetchEvents(startISO, endISO))
  handle('calendar:calendars', () => listCalendars())
  handle('calendar:createEvent', (input: CalEventInput) => createEvent(input))
  handle('calendar:updateEvent', (input: CalEventUpdate) => updateEvent(input))
  handle('calendar:deleteEvent', (href: string, etag: string) => deleteEvent(href, etag))

  handle('contacts:list', () => fetchContacts())
  handle('contacts:addressBooks', () => listAddressBooks())
  handle('contacts:create', (input: ContactInput) => createContact(input))
  handle('contacts:update', (input: ContactUpdate) => updateContact(input))
  handle('contacts:delete', (href: string, etag: string) => deleteContact(href, etag))
  handle('mail:move', (accountId: string, folder: string, uids: number[], target: string) =>
    moveMessages(accountId, folder, uids, target)
  )
  handle('mail:searchKeyword', (accountId: string, keyword: string) =>
    searchByKeyword(accountId, keyword)
  )
  handle('mail:search', (accountId: string, folder: string, query: string) =>
    searchMessages(accountId, folder, query)
  )

  handle('sieve:list', (accountId: string) => listScripts(accountId))
  handle('sieve:get', (accountId: string, name: string) => getScript(accountId, name))
  handle('sieve:put', (accountId: string, name: string, body: string) =>
    putScript(accountId, name, body)
  )
  handle('sieve:setActive', (accountId: string, name: string) => setActiveScript(accountId, name))
  handle('sieve:delete', (accountId: string, name: string) => deleteScript(accountId, name))

  handle('mail:send', (req: SendRequest) => sendMessage(req))
  handle('mail:pickAttachments', () => pickAttachments())
  handle('draft:save', (req: SendRequest, prev?: DraftRef) => saveDraft(req, prev))
  handle('draft:delete', (accountId: string, ref: DraftRef) => deleteDraft(accountId, ref))

  handle('mail:saveAttachment', (ref: AttachmentRef) => saveAttachmentAs(ref))
  handle('mail:archiveAttachment', (ref: AttachmentRef, sender: string) =>
    archiveAttachment(ref, sender)
  )

  handle('archive:list', (accountId: string) => listArchive(accountId))
  handle('archive:open', (accountId: string, path: string) => openArchiveFile(accountId, path))
  handle('archive:reveal', (accountId: string, path: string) => revealArchiveFile(accountId, path))

  handle('pdf:viewAttachment', (ref: AttachmentRef) => viewAttachmentPdf(ref))
  handle('pdf:viewArchive', (accountId: string, path: string) => viewArchivePdf(accountId, path))
}
