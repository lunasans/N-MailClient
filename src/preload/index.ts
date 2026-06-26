import { contextBridge, ipcRenderer } from 'electron'
import type {
  Account,
  AccountSettings,
  ArchiveListing,
  AttachmentRef,
  CalendarConfig,
  CalendarInfo,
  AddressBookInfo,
  CalEvent,
  CalEventInput,
  CalEventUpdate,
  Contact,
  ContactInput,
  ContactUpdate,
  DraftRef,
  IpcResult,
  Label,
  MailboxNode,
  MessageDetail,
  MessageSummary,
  NewAccount,
  PickedAttachment,
  ProbeResult,
  SaveResult,
  PgpGenerateInput,
  PgpKeyInfo,
  RecipientTls,
  SendRequest,
  SieveScript,
  UpdateStatus,
  WebDavConfig
} from '../main/types'

const api = {
  accounts: {
    list: (): Promise<IpcResult<Account[]>> => ipcRenderer.invoke('accounts:list'),
    probe: (email: string): Promise<IpcResult<ProbeResult>> =>
      ipcRenderer.invoke('accounts:probe', email),
    add: (input: NewAccount): Promise<IpcResult<Account>> =>
      ipcRenderer.invoke('accounts:add', input),
    remove: (id: string): Promise<IpcResult<void>> => ipcRenderer.invoke('accounts:remove', id),
    updateSettings: (id: string, settings: AccountSettings): Promise<IpcResult<Account>> =>
      ipcRenderer.invoke('accounts:updateSettings', id, settings),
    pickArchiveFolder: (accountId: string): Promise<IpcResult<string | null>> =>
      ipcRenderer.invoke('accounts:pickArchiveFolder', accountId),
    testWebdav: (config: WebDavConfig, password: string): Promise<IpcResult<void>> =>
      ipcRenderer.invoke('accounts:testWebdav', config, password),
    setWebdavArchive: (
      accountId: string,
      config: WebDavConfig,
      password: string
    ): Promise<IpcResult<void>> =>
      ipcRenderer.invoke('accounts:setWebdavArchive', accountId, config, password)
  },
  mail: {
    folders: (accountId: string): Promise<IpcResult<MailboxNode[]>> =>
      ipcRenderer.invoke('mail:folders', accountId),
    foldersCached: (accountId: string): Promise<IpcResult<MailboxNode[]>> =>
      ipcRenderer.invoke('mail:foldersCached', accountId),
    createFolder: (accountId: string, path: string): Promise<IpcResult<void>> =>
      ipcRenderer.invoke('folder:create', accountId, path),
    deleteFolder: (accountId: string, path: string): Promise<IpcResult<void>> =>
      ipcRenderer.invoke('folder:delete', accountId, path),
    renameFolder: (accountId: string, oldPath: string, newPath: string): Promise<IpcResult<void>> =>
      ipcRenderer.invoke('folder:rename', accountId, oldPath, newPath),
    list: (
      accountId: string,
      folder: string,
      limit?: number,
      offset?: number
    ): Promise<IpcResult<MessageSummary[]>> =>
      ipcRenderer.invoke('mail:list', accountId, folder, limit, offset),
    get: (accountId: string, folder: string, uid: number): Promise<IpcResult<MessageDetail>> =>
      ipcRenderer.invoke('mail:get', accountId, folder, uid),
    source: (accountId: string, folder: string, uid: number): Promise<IpcResult<string>> =>
      ipcRenderer.invoke('mail:source', accountId, folder, uid),
    setSeen: (
      accountId: string,
      folder: string,
      uids: number[],
      seen: boolean
    ): Promise<IpcResult<void>> =>
      ipcRenderer.invoke('mail:setSeen', accountId, folder, uids, seen),
    delete: (accountId: string, folder: string, uids: number[]): Promise<IpcResult<void>> =>
      ipcRenderer.invoke('mail:delete', accountId, folder, uids),
    markSpam: (accountId: string, folder: string, uids: number[]): Promise<IpcResult<void>> =>
      ipcRenderer.invoke('mail:markSpam', accountId, folder, uids),
    archive: (accountId: string, folder: string, uids: number[]): Promise<IpcResult<void>> =>
      ipcRenderer.invoke('mail:archive', accountId, folder, uids),
    notSpam: (accountId: string, folder: string, uids: number[]): Promise<IpcResult<void>> =>
      ipcRenderer.invoke('mail:notSpam', accountId, folder, uids),
    setFlagged: (
      accountId: string,
      folder: string,
      uids: number[],
      flagged: boolean
    ): Promise<IpcResult<void>> =>
      ipcRenderer.invoke('mail:setFlagged', accountId, folder, uids, flagged),
    setKeyword: (
      accountId: string,
      folder: string,
      uids: number[],
      keyword: string,
      on: boolean
    ): Promise<IpcResult<void>> =>
      ipcRenderer.invoke('mail:setKeyword', accountId, folder, uids, keyword, on),
    move: (
      accountId: string,
      folder: string,
      uids: number[],
      target: string
    ): Promise<IpcResult<void>> => ipcRenderer.invoke('mail:move', accountId, folder, uids, target),
    searchKeyword: (
      accountId: string,
      keyword: string
    ): Promise<IpcResult<Array<MessageSummary & { folder: string }>>> =>
      ipcRenderer.invoke('mail:searchKeyword', accountId, keyword),
    search: (
      accountId: string,
      folder: string,
      query: string
    ): Promise<IpcResult<MessageSummary[]>> =>
      ipcRenderer.invoke('mail:search', accountId, folder, query),
    send: (req: SendRequest): Promise<IpcResult<void>> => ipcRenderer.invoke('mail:send', req),
    pickAttachments: (): Promise<IpcResult<PickedAttachment[]>> =>
      ipcRenderer.invoke('mail:pickAttachments'),
    saveDraft: (req: SendRequest, prev?: DraftRef): Promise<IpcResult<DraftRef | null>> =>
      ipcRenderer.invoke('draft:save', req, prev),
    deleteDraft: (accountId: string, ref: DraftRef): Promise<IpcResult<void>> =>
      ipcRenderer.invoke('draft:delete', accountId, ref),
    saveAttachment: (ref: AttachmentRef): Promise<IpcResult<SaveResult>> =>
      ipcRenderer.invoke('mail:saveAttachment', ref),
    archiveAttachment: (ref: AttachmentRef, sender: string): Promise<IpcResult<string>> =>
      ipcRenderer.invoke('mail:archiveAttachment', ref, sender)
  },
  archive: {
    list: (accountId: string): Promise<IpcResult<ArchiveListing>> =>
      ipcRenderer.invoke('archive:list', accountId),
    open: (accountId: string, path: string): Promise<IpcResult<void>> =>
      ipcRenderer.invoke('archive:open', accountId, path),
    reveal: (accountId: string, path: string): Promise<IpcResult<void>> =>
      ipcRenderer.invoke('archive:reveal', accountId, path),
    delete: (accountId: string, path: string): Promise<IpcResult<void>> =>
      ipcRenderer.invoke('archive:delete', accountId, path)
  },
  pdf: {
    viewAttachment: (ref: AttachmentRef): Promise<IpcResult<void>> =>
      ipcRenderer.invoke('pdf:viewAttachment', ref),
    viewArchive: (accountId: string, path: string): Promise<IpcResult<void>> =>
      ipcRenderer.invoke('pdf:viewArchive', accountId, path)
  },
  labels: {
    list: (): Promise<IpcResult<Label[]>> => ipcRenderer.invoke('labels:list'),
    add: (name: string, color: string): Promise<IpcResult<Label>> =>
      ipcRenderer.invoke('labels:add', name, color),
    remove: (id: string): Promise<IpcResult<void>> => ipcRenderer.invoke('labels:remove', id)
  },
  calendar: {
    get: (): Promise<IpcResult<CalendarConfig | null>> => ipcRenderer.invoke('calendar:get'),
    test: (serverUrl: string, user: string, password: string): Promise<IpcResult<number>> =>
      ipcRenderer.invoke('calendar:test', serverUrl, user, password),
    save: (serverUrl: string, user: string, password: string): Promise<IpcResult<void>> =>
      ipcRenderer.invoke('calendar:save', serverUrl, user, password),
    clear: (): Promise<IpcResult<void>> => ipcRenderer.invoke('calendar:clear'),
    events: (startISO: string, endISO: string): Promise<IpcResult<CalEvent[]>> =>
      ipcRenderer.invoke('calendar:events', startISO, endISO),
    calendars: (): Promise<IpcResult<CalendarInfo[]>> => ipcRenderer.invoke('calendar:calendars'),
    createEvent: (input: CalEventInput): Promise<IpcResult<void>> =>
      ipcRenderer.invoke('calendar:createEvent', input),
    updateEvent: (input: CalEventUpdate): Promise<IpcResult<void>> =>
      ipcRenderer.invoke('calendar:updateEvent', input),
    deleteEvent: (href: string, etag: string): Promise<IpcResult<void>> =>
      ipcRenderer.invoke('calendar:deleteEvent', href, etag)
  },
  contacts: {
    list: (): Promise<IpcResult<Contact[]>> => ipcRenderer.invoke('contacts:list'),
    addressBooks: (): Promise<IpcResult<AddressBookInfo[]>> =>
      ipcRenderer.invoke('contacts:addressBooks'),
    create: (input: ContactInput): Promise<IpcResult<void>> =>
      ipcRenderer.invoke('contacts:create', input),
    update: (input: ContactUpdate): Promise<IpcResult<void>> =>
      ipcRenderer.invoke('contacts:update', input),
    delete: (href: string, etag: string): Promise<IpcResult<void>> =>
      ipcRenderer.invoke('contacts:delete', href, etag)
  },
  sieve: {
    list: (accountId: string): Promise<IpcResult<SieveScript[]>> =>
      ipcRenderer.invoke('sieve:list', accountId),
    get: (accountId: string, name: string): Promise<IpcResult<string>> =>
      ipcRenderer.invoke('sieve:get', accountId, name),
    put: (accountId: string, name: string, body: string): Promise<IpcResult<void>> =>
      ipcRenderer.invoke('sieve:put', accountId, name, body),
    setActive: (accountId: string, name: string): Promise<IpcResult<void>> =>
      ipcRenderer.invoke('sieve:setActive', accountId, name),
    delete: (accountId: string, name: string): Promise<IpcResult<void>> =>
      ipcRenderer.invoke('sieve:delete', accountId, name)
  },
  pgp: {
    list: (): Promise<IpcResult<PgpKeyInfo[]>> => ipcRenderer.invoke('pgp:list'),
    importPublic: (armored: string): Promise<IpcResult<PgpKeyInfo>> =>
      ipcRenderer.invoke('pgp:importPublic', armored),
    importPrivate: (armored: string, passphrase: string): Promise<IpcResult<PgpKeyInfo>> =>
      ipcRenderer.invoke('pgp:importPrivate', armored, passphrase),
    generate: (input: PgpGenerateInput): Promise<IpcResult<PgpKeyInfo>> =>
      ipcRenderer.invoke('pgp:generate', input),
    exportPublic: (id: string): Promise<IpcResult<string>> =>
      ipcRenderer.invoke('pgp:exportPublic', id),
    exportPrivate: (id: string): Promise<IpcResult<string>> =>
      ipcRenderer.invoke('pgp:exportPrivate', id),
    remove: (id: string): Promise<IpcResult<void>> => ipcRenderer.invoke('pgp:remove', id)
  },
  mx: {
    checkTls: (domain: string): Promise<IpcResult<RecipientTls>> =>
      ipcRenderer.invoke('mx:checkTls', domain)
  },
  app: {
    getAutostart: (): Promise<IpcResult<boolean>> => ipcRenderer.invoke('app:getAutostart'),
    setAutostart: (enabled: boolean): Promise<IpcResult<void>> =>
      ipcRenderer.invoke('app:setAutostart', enabled)
  },
  update: {
    check: (): Promise<IpcResult<void>> => ipcRenderer.invoke('update:check'),
    install: (): Promise<IpcResult<void>> => ipcRenderer.invoke('update:install'),
    status: (): Promise<IpcResult<UpdateStatus | null>> => ipcRenderer.invoke('update:status'),
    /** Subscribe to update-status pushes. Returns an unsubscribe fn. */
    onStatus: (cb: (status: UpdateStatus) => void): (() => void) => {
      const listener = (_e: unknown, status: UpdateStatus): void => cb(status)
      ipcRenderer.on('update:status', listener)
      return () => ipcRenderer.removeListener('update:status', listener)
    }
  },
  events: {
    /** Subscribe to new-mail pushes (IMAP IDLE). Returns an unsubscribe fn. */
    onNewMail: (cb: (payload: NewMailEvent) => void): (() => void) => {
      const listener = (_e: unknown, payload: NewMailEvent): void => cb(payload)
      ipcRenderer.on('mail:new', listener)
      return () => ipcRenderer.removeListener('mail:new', listener)
    }
  }
}

export interface NewMailEvent {
  accountId: string
  folder: string
  count: number
}

contextBridge.exposeInMainWorld('api', api)

export type Api = typeof api
