import { create } from 'zustand'
import type {
  Account,
  Contact,
  IpcResult,
  Label,
  MailboxNode,
  MessageDetail,
  MessageSummary,
  PickedAttachment,
  SendRequest
} from '@shared/index'

function toggleKeyword(kws: string[], keyword: string, on: boolean): string[] {
  if (on) return kws.includes(keyword) ? kws : [...kws, keyword]
  return kws.filter((k) => k !== keyword)
}

function unwrap<T>(r: IpcResult<T>): T {
  if (!r.ok) throw new Error(r.error)
  return r.data
}

/** Dedupe concurrent folder loads per account (module-scoped, not reactive). */
const loadingAccounts = new Set<string>()

export type View = 'mail' | 'archive' | 'calendar' | 'contacts'

/** Seed values for the calendar event form (e.g. when creating from a mail). */
export interface NewEventDraft {
  summary?: string
  startISO?: string
  endISO?: string
  allDay?: boolean
  location?: string
  description?: string
}

/** A message in the unified inbox, tagged with its source account/folder. */
export interface UnifiedItem extends MessageSummary {
  accountId: string
  folder: string
}

function isSameItem(a: UnifiedItem, b: UnifiedItem): boolean {
  return a.accountId === b.accountId && a.folder === b.folder && a.uid === b.uid
}

/** A draft the Composer opens with (empty for a brand-new mail). */
export interface ComposeDraft {
  from?: string
  to?: string
  cc?: string
  bcc?: string
  subject?: string
  body?: string
  /** HTML seed for the rich editor (takes precedence over body). */
  bodyHtml?: string
  /** When true, body is used verbatim (no signature appended). */
  raw?: boolean
  attachments?: PickedAttachment[]
  inReplyTo?: string
  references?: string[]
  forwardFrom?: { folder: string; uid: number }
  answeredFrom?: { folder: string; uid: number }
  /** When editing an existing draft from the Drafts folder. */
  existingDraft?: { folder: string; uid: number }
  /** PGP: encrypt / sign on send. */
  pgpEncrypt?: boolean
  pgpSign?: boolean
}

/** Data kept while a send is delayed and can still be undone. */
interface PendingSend {
  req: SendRequest
  undoDraft: ComposeDraft
  draftRef: { folder: string; uid: number } | null
}
let pendingTimer: ReturnType<typeof setTimeout> | null = null
let pendingData: PendingSend | null = null

interface MailState {
  accounts: Account[]
  /** Folder trees per account id, loaded lazily. */
  foldersByAccount: Record<string, MailboxNode[]>
  loadingFoldersFor: string | null
  /** Account whose folder is currently open. */
  activeAccountId: string | null
  activeFolder: string | null
  /** Active full-text search within the folder, or null. */
  searchQuery: string | null
  messages: MessageSummary[]
  /** The message shown in the preview pane. */
  selectedUid: number | null
  /** Multi-selection (always includes selectedUid after a plain click). */
  selectedUids: number[]
  /** Anchor for shift-range selection. */
  anchorUid: number | null
  /** UIDs currently being dragged onto a folder. */
  draggingUids: number[]
  /** Folder currently being dragged onto another folder. */
  draggingFolder: { accountId: string; path: string } | null
  message: MessageDetail | null
  loadingMessages: boolean
  loadingMessage: boolean
  error: string | null
  /** Open composer draft, or null when the composer is closed. */
  compose: ComposeDraft | null
  /** A delayed send awaiting its timer (for the "undo send" banner). */
  pendingSend: { subject: string; deadline: number } | null
  /** True when the unified inbox (all accounts) is shown. */
  unified: boolean
  /** Active label view (all messages with this label), or null. */
  activeLabel: Label | null
  /** Merged messages across accounts (unified inbox or label view). */
  unifiedItems: UnifiedItem[]
  /** Account + folder of the previewed message (works in both modes). */
  previewCtx: { accountId: string; folder: string } | null
  /** User-defined labels (global). */
  labels: Label[]
  /** Active top-level view (mail / archive / calendar). */
  view: View
  /** Seed for the calendar event form, or null when closed. */
  newEventDraft: NewEventDraft | null
  /** Address-book contacts (CardDAV), for the view and recipient suggestions. */
  contacts: Contact[]
  loadingContacts: boolean
  /** New-mail sound + desktop notifications enabled. */
  notifyOn: boolean
  /** Mail layout: reading pane on the right or below the list. */
  mailLayout: 'right' | 'bottom'
  /** Dark theme enabled. */
  darkMode: boolean

  setNotifyOn: (on: boolean) => void
  setMailLayout: (layout: 'right' | 'bottom') => void
  setDarkMode: (on: boolean) => void
  loadContacts: () => Promise<void>
  setView: (view: View) => void
  openNewEvent: (initial?: NewEventDraft) => void
  closeNewEvent: () => void
  loadLabels: () => Promise<void>
  addLabel: (name: string, color: string) => Promise<void>
  removeLabel: (id: string) => Promise<void>
  setKeyword: (
    accountId: string,
    folder: string,
    uids: number[],
    keyword: string,
    on: boolean
  ) => Promise<void>
  openCompose: (draft?: ComposeDraft) => void
  closeCompose: () => void
  scheduleSend: (
    req: SendRequest,
    undoDraft: ComposeDraft,
    draftRef: { folder: string; uid: number } | null,
    delaySeconds: number
  ) => void
  undoSend: () => void
  loadAccounts: () => Promise<void>
  loadUnified: () => Promise<void>
  loadLabelView: (label: Label) => Promise<void>
  selectUnified: (item: UnifiedItem) => Promise<void>
  flagUnified: (item: UnifiedItem, flagged: boolean) => Promise<void>
  archiveUnified: (item: UnifiedItem) => Promise<void>
  spamUnified: (item: UnifiedItem) => Promise<void>
  deleteUnified: (item: UnifiedItem) => Promise<void>
  ensureFolders: (accountId: string) => Promise<void>
  reloadFolders: (accountId: string) => Promise<void>
  createFolder: (accountId: string, path: string) => Promise<void>
  deleteFolder: (accountId: string, path: string) => Promise<void>
  renameFolder: (accountId: string, oldPath: string, newPath: string) => Promise<void>
  setDraggingFolder: (v: { accountId: string; path: string } | null) => void
  selectFolder: (accountId: string, path: string) => Promise<void>
  runSearch: (query: string) => Promise<void>
  clearSearch: () => Promise<void>
  selectMessage: (uid: number) => Promise<void>
  toggleSelect: (uid: number) => void
  rangeSelect: (uid: number) => void
  selectAll: () => void
  setDragging: (uids: number[]) => void
  refreshMessages: () => Promise<void>
  setMessagesSeen: (uids: number[], seen: boolean) => Promise<void>
  setFlagged: (uids: number[], flagged: boolean) => Promise<void>
  removeMessages: (uids: number[]) => Promise<void>
  spamMessages: (uids: number[]) => Promise<void>
  notSpamMessages: (uids: number[]) => Promise<void>
  archiveMessages: (uids: number[]) => Promise<void>
  moveToFolder: (uids: number[], target: string) => Promise<void>
  onNewMail: (accountId: string, folder: string, count: number) => void
  removeAccount: (id: string) => Promise<void>
  setError: (msg: string | null) => void
}

/** Adjust the unread badge of a folder (in a given account) by delta, clamped at 0. */
function adjustUnseen(
  map: Record<string, MailboxNode[]>,
  accountId: string | null,
  path: string | null,
  delta: number
): Record<string, MailboxNode[]> {
  if (!accountId || !path || delta === 0 || !map[accountId]) return map
  return {
    ...map,
    [accountId]: map[accountId].map((f) =>
      f.path === path ? { ...f, unseen: Math.max(0, (f.unseen ?? 0) + delta) } : f
    )
  }
}

export const useMailStore = create<MailState>((set, get) => ({
  accounts: [],
  foldersByAccount: {},
  loadingFoldersFor: null,
  activeAccountId: null,
  activeFolder: null,
  searchQuery: null,
  messages: [],
  selectedUid: null,
  selectedUids: [],
  anchorUid: null,
  draggingUids: [],
  draggingFolder: null,
  message: null,
  loadingMessages: false,
  loadingMessage: false,
  error: null,
  compose: null,
  pendingSend: null,
  unified: false,
  activeLabel: null,
  unifiedItems: [],
  previewCtx: null,
  labels: [],
  view: 'mail',
  newEventDraft: null,
  contacts: [],
  loadingContacts: false,
  notifyOn: typeof localStorage !== 'undefined' && localStorage.getItem('nmc.notify') !== 'off',
  mailLayout:
    typeof localStorage !== 'undefined' && localStorage.getItem('nmc.mailLayout') === 'bottom'
      ? 'bottom'
      : 'right',
  darkMode: typeof localStorage !== 'undefined' && localStorage.getItem('nmc.dark') === 'on',

  setNotifyOn: (on) => {
    localStorage.setItem('nmc.notify', on ? 'on' : 'off')
    set({ notifyOn: on })
  },

  setMailLayout: (layout) => {
    localStorage.setItem('nmc.mailLayout', layout)
    set({ mailLayout: layout })
  },

  setDarkMode: (on) => {
    localStorage.setItem('nmc.dark', on ? 'on' : 'off')
    document.documentElement.classList.toggle('dark', on)
    set({ darkMode: on })
  },

  loadContacts: async () => {
    set({ loadingContacts: true })
    const res = await window.api.contacts.list()
    if (res.ok) set({ contacts: res.data, loadingContacts: false })
    else set({ loadingContacts: false, error: res.error })
  },

  setView: (view) => set({ view }),
  openNewEvent: (initial) => set({ view: 'calendar', newEventDraft: initial ?? {} }),
  closeNewEvent: () => set({ newEventDraft: null }),

  setError: (msg) => set({ error: msg }),

  scheduleSend: (req, undoDraft, draftRef, delaySeconds) => {
    finalizePendingNow(get, set) // flush any previous delayed send first
    pendingData = { req, undoDraft, draftRef }
    set({ pendingSend: { subject: req.subject, deadline: Date.now() + delaySeconds * 1000 } })
    pendingTimer = setTimeout(() => finalizePendingNow(get, set), delaySeconds * 1000)
  },

  undoSend: () => {
    if (pendingTimer) clearTimeout(pendingTimer)
    pendingTimer = null
    const data = pendingData
    pendingData = null
    set({ pendingSend: null })
    if (data) get().openCompose(data.undoDraft)
  },

  loadLabels: async () => {
    const res = await window.api.labels.list()
    if (res.ok) set({ labels: res.data })
  },

  addLabel: async (name, color) => {
    const res = await window.api.labels.add(name, color)
    if (res.ok) set((s) => ({ labels: [...s.labels, res.data] }))
    else set({ error: res.error })
  },

  removeLabel: async (id) => {
    const res = await window.api.labels.remove(id)
    if (res.ok) set((s) => ({ labels: s.labels.filter((l) => l.id !== id) }))
    else set({ error: res.error })
  },

  setKeyword: async (accountId, folder, uids, keyword, on) => {
    if (uids.length === 0) return
    try {
      unwrap(await window.api.mail.setKeyword(accountId, folder, uids, keyword, on))
      set((s) => {
        const updated = s.unifiedItems.map((m) =>
          m.accountId === accountId && m.folder === folder && uids.includes(m.uid)
            ? { ...m, keywords: toggleKeyword(m.keywords, keyword, on) }
            : m
        )
        // In a label view, removing that label drops the message from the list.
        const dropFromView = s.activeLabel?.keyword === keyword && !on
        return {
          messages: s.messages.map((m) =>
            uids.includes(m.uid) ? { ...m, keywords: toggleKeyword(m.keywords, keyword, on) } : m
          ),
          unifiedItems: dropFromView
            ? updated.filter((m) => m.keywords.includes(keyword))
            : updated
        }
      })
    } catch (e) {
      set({ error: (e as Error).message })
    }
  },
  setDragging: (uids) => set({ draggingUids: uids }),
  setDraggingFolder: (v) => set({ draggingFolder: v }),
  openCompose: (draft) => set({ compose: draft ?? {} }),
  closeCompose: () => set({ compose: null }),

  loadAccounts: async () => {
    const accounts = unwrap(await window.api.accounts.list())
    set({ accounts })
    const { activeAccountId } = get()
    if (accounts.length && !activeAccountId) {
      // Open the first account's inbox by default.
      const first = accounts[0]
      await get().ensureFolders(first.id)
      const folders = get().foldersByAccount[first.id] ?? []
      const inbox = folders.find((f) => f.role === 'inbox') ?? folders.find((f) => f.selectable)
      if (inbox) await get().selectFolder(first.id, inbox.path)
    } else if (!accounts.length) {
      set({
        activeAccountId: null,
        activeFolder: null,
        foldersByAccount: {},
        messages: [],
        message: null
      })
    }
  },

  ensureFolders: async (accountId) => {
    if (get().foldersByAccount[accountId] || loadingAccounts.has(accountId)) return
    loadingAccounts.add(accountId)

    // 1) Instant: show the persisted folder list if we have one.
    let haveCache = false
    try {
      const cached = await window.api.mail.foldersCached(accountId)
      if (cached.ok && cached.data.length) {
        haveCache = true
        set((s) => ({ foldersByAccount: { ...s.foldersByAccount, [accountId]: cached.data } }))
      }
    } catch {
      /* ignore cache errors */
    }
    if (!haveCache) set({ loadingFoldersFor: accountId })

    // 2) Background: fetch the live list (incl. unread counts) and update.
    const refresh = window.api.mail
      .folders(accountId)
      .then((live) => {
        if (live.ok) {
          set((s) => ({
            foldersByAccount: { ...s.foldersByAccount, [accountId]: live.data },
            loadingFoldersFor: s.loadingFoldersFor === accountId ? null : s.loadingFoldersFor
          }))
        } else {
          set((s) => ({
            loadingFoldersFor: s.loadingFoldersFor === accountId ? null : s.loadingFoldersFor,
            error: haveCache ? s.error : live.error
          }))
        }
      })
      .catch((e) => set({ loadingFoldersFor: null, error: (e as Error).message }))
      .finally(() => loadingAccounts.delete(accountId))

    // With a cache we return immediately (refresh continues in the background);
    // on a cold first load we wait so callers can select the inbox.
    if (!haveCache) await refresh
  },

  reloadFolders: async (accountId) => {
    // Fetch first and replace atomically so the old list stays visible
    // (no empty "loading" flash) until the new folders arrive.
    try {
      const folders = unwrap(await window.api.mail.folders(accountId))
      set((s) => ({ foldersByAccount: { ...s.foldersByAccount, [accountId]: folders } }))
    } catch (e) {
      set({ error: (e as Error).message })
      return
    }
    // If the active folder vanished (deleted/renamed), fall back to the inbox.
    const { activeAccountId, activeFolder } = get()
    if (activeAccountId === accountId && activeFolder) {
      const folders = get().foldersByAccount[accountId] ?? []
      if (!folders.some((f) => f.path === activeFolder)) {
        const inbox = folders.find((f) => f.role === 'inbox') ?? folders.find((f) => f.selectable)
        if (inbox) await get().selectFolder(accountId, inbox.path)
      }
    }
  },

  createFolder: async (accountId, path) => {
    try {
      unwrap(await window.api.mail.createFolder(accountId, path))
      await get().reloadFolders(accountId)
    } catch (e) {
      set({ error: (e as Error).message })
    }
  },

  deleteFolder: async (accountId, path) => {
    try {
      unwrap(await window.api.mail.deleteFolder(accountId, path))
      await get().reloadFolders(accountId)
    } catch (e) {
      set({ error: (e as Error).message })
    }
  },

  renameFolder: async (accountId, oldPath, newPath) => {
    try {
      unwrap(await window.api.mail.renameFolder(accountId, oldPath, newPath))
      // If we renamed the active folder, follow it to the new path.
      const { activeAccountId, activeFolder } = get()
      const wasActive = activeAccountId === accountId && activeFolder === oldPath
      await get().reloadFolders(accountId)
      if (wasActive) await get().selectFolder(accountId, newPath)
    } catch (e) {
      set({ error: (e as Error).message })
    }
  },

  selectFolder: async (accountId, path) => {
    set({
      unified: false,
      activeLabel: null,
      searchQuery: null,
      activeAccountId: accountId,
      activeFolder: path,
      messages: [],
      selectedUid: null,
      selectedUids: [],
      anchorUid: null,
      message: null,
      previewCtx: null,
      loadingMessages: true,
      error: null
    })
    await get().refreshMessages()
  },

  refreshMessages: async () => {
    const label = get().activeLabel
    if (label) {
      await get().loadLabelView(label)
      return
    }
    if (get().unified) {
      await get().loadUnified()
      return
    }
    const { activeAccountId, activeFolder, searchQuery } = get()
    if (!activeAccountId || !activeFolder) return
    set({ loadingMessages: true })
    try {
      const messages = searchQuery
        ? unwrap(await window.api.mail.search(activeAccountId, activeFolder, searchQuery))
        : unwrap(await window.api.mail.list(activeAccountId, activeFolder, 50))
      set({ messages, loadingMessages: false })
    } catch (e) {
      set({ loadingMessages: false, error: (e as Error).message })
    }
  },

  runSearch: async (query) => {
    const q = query.trim()
    if (!q) {
      await get().clearSearch()
      return
    }
    set({ searchQuery: q, selectedUid: null, selectedUids: [], message: null })
    await get().refreshMessages()
  },

  clearSearch: async () => {
    if (!get().searchQuery) return
    set({ searchQuery: null, selectedUid: null, selectedUids: [], message: null })
    await get().refreshMessages()
  },

  loadUnified: async () => {
    set({
      unified: true,
      activeLabel: null,
      activeAccountId: null,
      activeFolder: null,
      messages: [],
      selectedUid: null,
      selectedUids: [],
      message: null,
      previewCtx: null,
      loadingMessages: true,
      error: null
    })
    const accounts = get().accounts
    try {
      const lists = await Promise.all(
        accounts.map(async (acc) => {
          await get().ensureFolders(acc.id)
          const folders = get().foldersByAccount[acc.id] ?? []
          const inbox = folders.find((f) => f.role === 'inbox')?.path ?? 'INBOX'
          const res = await window.api.mail.list(acc.id, inbox, 50)
          return res.ok
            ? res.data.map<UnifiedItem>((m) => ({ ...m, accountId: acc.id, folder: inbox }))
            : []
        })
      )
      const items = lists.flat().sort((a, b) => (a.date < b.date ? 1 : -1))
      set({ unifiedItems: items, loadingMessages: false })
    } catch (e) {
      set({ loadingMessages: false, error: (e as Error).message })
    }
  },

  loadLabelView: async (label) => {
    set({
      activeLabel: label,
      unified: false,
      activeAccountId: null,
      activeFolder: null,
      messages: [],
      selectedUid: null,
      selectedUids: [],
      message: null,
      previewCtx: null,
      unifiedItems: [],
      loadingMessages: true,
      error: null
    })
    const accounts = get().accounts
    try {
      const lists = await Promise.all(
        accounts.map(async (acc) => {
          const res = await window.api.mail.searchKeyword(acc.id, label.keyword)
          return res.ok
            ? res.data.map<UnifiedItem>((m) => ({ ...m, accountId: acc.id }))
            : []
        })
      )
      const items = lists.flat().sort((a, b) => (a.date < b.date ? 1 : -1))
      set({ unifiedItems: items, loadingMessages: false })
    } catch (e) {
      set({ loadingMessages: false, error: (e as Error).message })
    }
  },

  selectUnified: async (item) => {
    set({
      selectedUid: item.uid,
      previewCtx: { accountId: item.accountId, folder: item.folder },
      message: null,
      loadingMessage: true,
      error: null
    })
    try {
      const message = unwrap(await window.api.mail.get(item.accountId, item.folder, item.uid))
      set({ message, loadingMessage: false })
      if (!item.seen) {
        await window.api.mail.setSeen(item.accountId, item.folder, [item.uid], true)
        set((s) => ({
          unifiedItems: s.unifiedItems.map((m) => (isSameItem(m, item) ? { ...m, seen: true } : m)),
          foldersByAccount: adjustUnseen(s.foldersByAccount, item.accountId, item.folder, -1)
        }))
      }
    } catch (e) {
      set({ loadingMessage: false, error: (e as Error).message })
    }
  },

  flagUnified: async (item, flagged) => {
    try {
      unwrap(await window.api.mail.setFlagged(item.accountId, item.folder, [item.uid], flagged))
      set((s) => ({
        unifiedItems: s.unifiedItems.map((m) => (isSameItem(m, item) ? { ...m, flagged } : m))
      }))
    } catch (e) {
      set({ error: (e as Error).message })
    }
  },

  archiveUnified: async (item) => {
    await removeUnified(get, set, item, (a, f, u) => window.api.mail.archive(a, f, u))
  },

  spamUnified: async (item) => {
    await removeUnified(get, set, item, (a, f, u) => window.api.mail.markSpam(a, f, u))
  },

  deleteUnified: async (item) => {
    await removeUnified(get, set, item, (a, f, u) => window.api.mail.delete(a, f, u))
  },

  selectMessage: async (uid) => {
    const { activeAccountId, activeFolder, messages } = get()
    if (!activeAccountId || !activeFolder) return
    const wasUnseen = messages.find((m) => m.uid === uid)?.seen === false
    set({
      selectedUid: uid,
      selectedUids: [uid],
      anchorUid: uid,
      message: null,
      previewCtx: { accountId: activeAccountId, folder: activeFolder },
      loadingMessage: true,
      error: null
    })
    try {
      const message = unwrap(await window.api.mail.get(activeAccountId, activeFolder, uid))
      set({ message, loadingMessage: false })
      await window.api.mail.setSeen(activeAccountId, activeFolder, [uid], true)
      set((s) => ({
        messages: s.messages.map((m) => (m.uid === uid ? { ...m, seen: true } : m)),
        foldersByAccount: wasUnseen
          ? adjustUnseen(s.foldersByAccount, s.activeAccountId, s.activeFolder, -1)
          : s.foldersByAccount
      }))
    } catch (e) {
      set({ loadingMessage: false, error: (e as Error).message })
    }
  },

  toggleSelect: (uid) =>
    set((s) => {
      const has = s.selectedUids.includes(uid)
      const selectedUids = has
        ? s.selectedUids.filter((u) => u !== uid)
        : [...s.selectedUids, uid]
      return { selectedUids, anchorUid: uid }
    }),

  rangeSelect: (uid) =>
    set((s) => {
      const order = s.messages.map((m) => m.uid)
      const anchor = s.anchorUid ?? s.selectedUid ?? uid
      const a = order.indexOf(anchor)
      const b = order.indexOf(uid)
      if (a === -1 || b === -1) return { selectedUids: [uid] }
      const [lo, hi] = a < b ? [a, b] : [b, a]
      return { selectedUids: order.slice(lo, hi + 1) }
    }),

  selectAll: () =>
    set((s) => ({ selectedUids: s.messages.map((m) => m.uid) })),

  setMessagesSeen: async (uids, seen) => {
    const { activeAccountId, activeFolder, messages } = get()
    if (!activeAccountId || !activeFolder || uids.length === 0) return
    const changing = messages.filter((m) => uids.includes(m.uid) && m.seen !== seen).length
    try {
      unwrap(await window.api.mail.setSeen(activeAccountId, activeFolder, uids, seen))
      set((s) => ({
        messages: s.messages.map((m) => (uids.includes(m.uid) ? { ...m, seen } : m)),
        foldersByAccount: adjustUnseen(
          s.foldersByAccount,
          s.activeAccountId,
          s.activeFolder,
          seen ? -changing : changing
        )
      }))
    } catch (e) {
      set({ error: (e as Error).message })
    }
  },

  setFlagged: async (uids, flagged) => {
    const { activeAccountId, activeFolder } = get()
    if (!activeAccountId || !activeFolder || uids.length === 0) return
    try {
      unwrap(await window.api.mail.setFlagged(activeAccountId, activeFolder, uids, flagged))
      set((s) => ({
        messages: s.messages.map((m) => (uids.includes(m.uid) ? { ...m, flagged } : m))
      }))
    } catch (e) {
      set({ error: (e as Error).message })
    }
  },

  removeMessages: async (uids) => {
    await applyRemoval(get, set, uids, (acc, folder) => window.api.mail.delete(acc, folder, uids))
  },

  spamMessages: async (uids) => {
    await applyRemoval(get, set, uids, (acc, folder) => window.api.mail.markSpam(acc, folder, uids))
  },

  notSpamMessages: async (uids) => {
    await applyRemoval(get, set, uids, (acc, folder) => window.api.mail.notSpam(acc, folder, uids))
  },

  archiveMessages: async (uids) => {
    await applyRemoval(get, set, uids, (acc, folder) => window.api.mail.archive(acc, folder, uids))
  },

  moveToFolder: async (uids, target) => {
    if (!target) return
    await applyRemoval(
      get,
      set,
      uids,
      (acc, folder) => window.api.mail.move(acc, folder, uids, target),
      target
    )
  },

  onNewMail: (accountId, folder, count) => {
    const s = get()
    const folders = s.foldersByAccount[accountId] ?? []
    const inboxPath = folders.find((f) => f.role === 'inbox')?.path ?? folder
    // Bump the inbox unread badge.
    set((st) => ({ foldersByAccount: adjustUnseen(st.foldersByAccount, accountId, inboxPath, count) }))
    // Refresh whatever is currently visible and affected.
    if (s.unified) {
      void get().loadUnified()
    } else if (s.activeAccountId === accountId && s.activeFolder === inboxPath) {
      void get().refreshMessages()
    }
  },

  removeAccount: async (id) => {
    unwrap(await window.api.accounts.remove(id))
    set((s) => {
      const map = { ...s.foldersByAccount }
      delete map[id]
      return {
        foldersByAccount: map,
        activeAccountId: s.activeAccountId === id ? null : s.activeAccountId,
        activeFolder: s.activeAccountId === id ? null : s.activeFolder,
        messages: s.activeAccountId === id ? [] : s.messages,
        message: s.activeAccountId === id ? null : s.message
      }
    })
    await get().loadAccounts()
  }
}))

type Getter = () => MailState
type Setter = (partial: Partial<MailState> | ((s: MailState) => Partial<MailState>)) => void

/** Send the currently pending (delayed) message immediately and clean up. */
function finalizePendingNow(get: Getter, set: Setter): void {
  if (pendingTimer) clearTimeout(pendingTimer)
  pendingTimer = null
  const data = pendingData
  pendingData = null
  set({ pendingSend: null })
  if (!data) return
  void (async () => {
    const res = await window.api.mail.send(data.req)
    if (!res.ok) {
      set({ error: res.error })
      return
    }
    if (data.draftRef) {
      await window.api.mail.deleteDraft(data.req.accountId, data.draftRef)
    }
    // Reflect \Answered after a reply, or refresh the Drafts list.
    if (data.req.answeredFrom || data.draftRef) get().refreshMessages()
  })()
}

/** Remove a single unified-inbox item (delete/spam/archive) and fix badges/preview. */
async function removeUnified(
  get: Getter,
  set: Setter,
  item: UnifiedItem,
  call: (accountId: string, folder: string, uids: number[]) => Promise<IpcResult<void>>
): Promise<void> {
  try {
    unwrap(await call(item.accountId, item.folder, [item.uid]))
    set((s) => {
      const previewed =
        s.selectedUid === item.uid &&
        s.previewCtx?.accountId === item.accountId &&
        s.previewCtx?.folder === item.folder
      return {
        unifiedItems: s.unifiedItems.filter((m) => !isSameItem(m, item)),
        selectedUid: previewed ? null : s.selectedUid,
        message: previewed ? null : s.message,
        foldersByAccount: item.seen
          ? s.foldersByAccount
          : adjustUnseen(s.foldersByAccount, item.accountId, item.folder, -1)
      }
    })
  } catch (e) {
    set({ error: (e as Error).message })
  }
}

/**
 * Shared logic for actions that remove messages from the current folder
 * (delete / spam / move): run the IPC call, drop the rows, fix selection,
 * adjust both the source and (optionally) target folder unread badges.
 */
async function applyRemoval(
  get: Getter,
  set: Setter,
  uids: number[],
  call: (accountId: string, folder: string) => Promise<IpcResult<void>>,
  targetFolder?: string
): Promise<void> {
  const { activeAccountId, activeFolder, messages } = get()
  if (!activeAccountId || !activeFolder || uids.length === 0) return
  const unseenMoved = messages.filter((m) => uids.includes(m.uid) && !m.seen).length
  try {
    unwrap(await call(activeAccountId, activeFolder))
    set((s) => {
      let map = adjustUnseen(s.foldersByAccount, s.activeAccountId, s.activeFolder, -unseenMoved)
      if (targetFolder) map = adjustUnseen(map, s.activeAccountId, targetFolder, unseenMoved)
      const removed = new Set(uids)
      const clearedPreview = s.selectedUid !== null && removed.has(s.selectedUid)
      return {
        messages: s.messages.filter((m) => !removed.has(m.uid)),
        selectedUids: s.selectedUids.filter((u) => !removed.has(u)),
        selectedUid: clearedPreview ? null : s.selectedUid,
        message: clearedPreview ? null : s.message,
        foldersByAccount: map
      }
    })
  } catch (e) {
    set({ error: (e as Error).message })
  }
}
