import { Fragment, useMemo, useState } from 'react'
import {
  Archive,
  Ban,
  Check,
  ChevronRight,
  Filter,
  FolderInput,
  Mail,
  MailOpen,
  Paperclip,
  RefreshCw,
  Reply,
  Search,
  Star,
  Tag,
  Trash2,
  X
} from 'lucide-react'
import type { Contact, Label, MessageSummary } from '@shared/index'
import { useMailStore, type UnifiedItem } from '../store/useMailStore'
import { colorById } from '../lib/accountColor'

function emailOf(from: string): string {
  const m = from.match(/<([^>]+)>/)
  return (m ? m[1] : from).trim().toLowerCase()
}

/** Avatar for a sender: contact photo if known, else an initial. */
function SenderAvatar({ from, contacts }: { from: string; contacts: Contact[] }): JSX.Element {
  const email = emailOf(from)
  const contact = contacts.find((c) => c.emails.some((e) => e.toLowerCase() === email))
  const initial = (contact?.fullName || from || '?').trim().slice(0, 1).toUpperCase()
  return contact?.photo ? (
    <img src={contact.photo} alt="" className="h-8 w-8 shrink-0 rounded-full object-cover" />
  ) : (
    <span className="flex h-8 w-8 shrink-0 items-center justify-center rounded-full bg-gray-100 text-sm font-medium text-gray-500">
      {initial}
    </span>
  )
}

/** Colored dots for the labels assigned to a message. */
function LabelDots({ keywords, labels }: { keywords: string[]; labels: Label[] }): JSX.Element | null {
  const matched = labels.filter((l) => keywords.includes(l.keyword))
  if (matched.length === 0) return null
  return (
    <>
      {matched.map((l) => (
        <span
          key={l.id}
          className="h-2 w-2 rounded-full"
          style={{ backgroundColor: l.color }}
          title={l.name}
        />
      ))}
    </>
  )
}

function formatDate(iso: string): string {
  if (!iso) return ''
  const d = new Date(iso)
  const now = new Date()
  const sameDay = d.toDateString() === now.toDateString()
  return sameDay
    ? d.toLocaleTimeString('de-DE', { hour: '2-digit', minute: '2-digit' })
    : d.toLocaleDateString('de-DE', { day: '2-digit', month: '2-digit', year: '2-digit' })
}

/** Monday 00:00 of the week containing d. */
function startOfWeek(d: Date): Date {
  const x = new Date(d.getFullYear(), d.getMonth(), d.getDate())
  x.setDate(x.getDate() - ((x.getDay() + 6) % 7))
  return x
}

/** Bucket a message date into a relative-week section label. */
function dateBucket(iso: string): string {
  if (!iso) return 'Älter'
  const d = new Date(iso).getTime()
  const thisWeek = startOfWeek(new Date()).getTime()
  if (d >= thisWeek) return 'Diese Woche'
  if (d >= thisWeek - 7 * 86400000) return 'Letzte Woche'
  if (d >= thisWeek - 14 * 86400000) return 'Vor zwei Wochen'
  return 'Älter'
}

function DateHeader({ label }: { label: string }): JSX.Element {
  return (
    <div className="sticky top-0 z-10 border-b bg-gray-50 px-3 py-1 text-xs font-semibold text-gray-500">
      {label}
    </div>
  )
}

interface MenuState {
  x: number
  y: number
  msg: MessageSummary
}

export default function MailList(): JSX.Element {
  const aggregated = useMailStore((s) => s.unified || !!s.activeLabel)
  return aggregated ? <UnifiedList /> : <FolderMailList />
}

function FolderMailList(): JSX.Element {
  const messages = useMailStore((s) => s.messages)
  const loading = useMailStore((s) => s.loadingMessages)
  const hasMore = useMailStore((s) => s.hasMore)
  const loadingMore = useMailStore((s) => s.loadingMore)
  const loadMoreMessages = useMailStore((s) => s.loadMoreMessages)
  const selectedUid = useMailStore((s) => s.selectedUid)
  const selectedUids = useMailStore((s) => s.selectedUids)
  const selectMessage = useMailStore((s) => s.selectMessage)
  const toggleSelect = useMailStore((s) => s.toggleSelect)
  const rangeSelect = useMailStore((s) => s.rangeSelect)
  const setDragging = useMailStore((s) => s.setDragging)
  const refresh = useMailStore((s) => s.refreshMessages)
  const activeFolder = useMailStore((s) => s.activeFolder)
  const activeAccountId = useMailStore((s) => s.activeAccountId)
  const foldersByAccount = useMailStore((s) => s.foldersByAccount)
  const setMessagesSeen = useMailStore((s) => s.setMessagesSeen)
  const setFlagged = useMailStore((s) => s.setFlagged)
  const removeMessages = useMailStore((s) => s.removeMessages)
  const spamMessages = useMailStore((s) => s.spamMessages)
  const notSpamMessages = useMailStore((s) => s.notSpamMessages)
  const archiveMessages = useMailStore((s) => s.archiveMessages)
  const moveToFolder = useMailStore((s) => s.moveToFolder)
  const openCompose = useMailStore((s) => s.openCompose)
  const setError = useMailStore((s) => s.setError)
  const labels = useMailStore((s) => s.labels)
  const setKeyword = useMailStore((s) => s.setKeyword)
  const searchQuery = useMailStore((s) => s.searchQuery)
  const runSearch = useMailStore((s) => s.runSearch)
  const clearSearch = useMailStore((s) => s.clearSearch)
  const contacts = useMailStore((s) => s.contacts)

  const [menu, setMenu] = useState<MenuState | null>(null)
  const [showMove, setShowMove] = useState(false)
  const [showLabels, setShowLabels] = useState(false)
  const [searchText, setSearchText] = useState('')
  const [showSearch, setShowSearch] = useState(false)
  const [onlyUnanswered, setOnlyUnanswered] = useState(false)

  const displayed = onlyUnanswered ? messages.filter((m) => !m.answered) : messages

  const accountFolders = foldersByAccount[activeAccountId ?? ''] ?? []
  const moveTargets = accountFolders.filter((f) => f.selectable && f.path !== activeFolder)
  const folderRole = useMemo(
    () => accountFolders.find((f) => f.path === activeFolder)?.role,
    [accountFolders, activeFolder]
  )
  const isDraftsFolder = folderRole === 'drafts'
  const isJunkFolder = folderRole === 'junk'

  async function openDraftForEdit(uid: number): Promise<void> {
    if (!activeAccountId || !activeFolder) return
    const res = await window.api.mail.get(activeAccountId, activeFolder, uid)
    if (!res.ok) {
      setError(res.error)
      return
    }
    const d = res.data
    openCompose({
      to: d.to,
      cc: d.cc,
      bcc: d.bcc,
      subject: d.subject,
      body: d.text ?? '',
      bodyHtml: d.html ?? undefined,
      existingDraft: { folder: activeFolder, uid }
    })
  }

  function handleClick(e: React.MouseEvent, msg: MessageSummary): void {
    if (e.ctrlKey || e.metaKey) toggleSelect(msg.uid)
    else if (e.shiftKey) rangeSelect(msg.uid)
    else if (isDraftsFolder) openDraftForEdit(msg.uid)
    else selectMessage(msg.uid)
  }

  function openMenu(e: React.MouseEvent, msg: MessageSummary): void {
    e.preventDefault()
    if (!selectedUids.includes(msg.uid)) selectMessage(msg.uid)
    const x = Math.min(e.clientX, window.innerWidth - 230)
    const y = Math.min(e.clientY, window.innerHeight - 220)
    setShowMove(false)
    setShowLabels(false)
    setMenu({ x, y, msg })
  }

  /** UIDs an action applies to: the whole selection if the target is part of it. */
  function targetsFor(msg: MessageSummary): number[] {
    return selectedUids.includes(msg.uid) && selectedUids.length > 0 ? selectedUids : [msg.uid]
  }

  function startDrag(e: React.DragEvent, msg: MessageSummary): void {
    const uids = targetsFor(msg)
    setDragging(uids)
    e.dataTransfer.effectAllowed = 'move'
    e.dataTransfer.setData('text/plain', uids.join(','))
  }

  const selectionCount = selectedUids.length

  return (
    <div className="flex h-full min-h-0 flex-col border-r">
      <div className="flex items-center justify-between border-b px-3 py-2">
        <span className="truncate text-sm font-medium">
          {activeFolder ?? 'Kein Ordner'}
          {selectionCount > 1 && (
            <span className="ml-2 text-xs text-brand">{selectionCount} ausgewählt</span>
          )}
        </span>
        <div className="flex items-center gap-1">
          {selectionCount > 1 && (
            <>
              <button
                onClick={() => setMessagesSeen(selectedUids, true)}
                className="rounded p-1.5 text-gray-500 hover:bg-gray-100"
                title="Auswahl als gelesen markieren"
              >
                <MailOpen className="h-4 w-4" />
              </button>
              <button
                onClick={() => archiveMessages(selectedUids)}
                className="rounded p-1.5 text-gray-500 hover:bg-gray-100"
                title="Auswahl archivieren"
              >
                <Archive className="h-4 w-4" />
              </button>
              <button
                onClick={() => spamMessages(selectedUids)}
                className="rounded p-1.5 text-gray-500 hover:bg-gray-100"
                title="Auswahl als Spam"
              >
                <Ban className="h-4 w-4" />
              </button>
              <button
                onClick={() => removeMessages(selectedUids)}
                className="rounded p-1.5 text-red-500 hover:bg-red-50"
                title="Auswahl löschen"
              >
                <Trash2 className="h-4 w-4" />
              </button>
            </>
          )}
          <button
            onClick={() => setOnlyUnanswered((v) => !v)}
            className={`rounded p-1.5 hover:bg-gray-100 ${
              onlyUnanswered ? 'text-brand' : 'text-gray-500'
            }`}
            title="Nur unbeantwortete anzeigen"
          >
            <Filter className="h-4 w-4" />
          </button>
          <button
            onClick={() => {
              setShowSearch((v) => !v)
              if (showSearch) clearSearch()
            }}
            className={`rounded p-1.5 hover:bg-gray-100 ${
              searchQuery ? 'text-brand' : 'text-gray-500'
            }`}
            title="Suchen"
          >
            <Search className="h-4 w-4" />
          </button>
          <button
            onClick={refresh}
            className="rounded p-1.5 text-gray-500 hover:bg-gray-100"
            title="Aktualisieren"
          >
            <RefreshCw className="h-4 w-4" />
          </button>
        </div>
      </div>
      {showSearch && (
        <div className="flex items-center gap-2 border-b px-3 py-1.5">
          <Search className="h-4 w-4 shrink-0 text-gray-400" />
          <input
            autoFocus
            className="w-full text-sm outline-none"
            placeholder="In diesem Ordner suchen (Betreff, Absender, Text)…"
            value={searchText}
            onChange={(e) => setSearchText(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter') runSearch(searchText)
              if (e.key === 'Escape') {
                setShowSearch(false)
                clearSearch()
              }
            }}
          />
          {(searchText || searchQuery) && (
            <button
              onClick={() => {
                setSearchText('')
                clearSearch()
              }}
              className="shrink-0 text-gray-400 hover:text-gray-700"
              title="Suche leeren"
            >
              <X className="h-4 w-4" />
            </button>
          )}
        </div>
      )}
      <div
        className="min-h-0 flex-1 overflow-y-auto"
        onScroll={(e) => {
          if (!hasMore || loadingMore) return
          const el = e.currentTarget
          if (el.scrollHeight - el.scrollTop - el.clientHeight < 300) loadMoreMessages()
        }}
      >
        {loading && <div className="px-3 py-3 text-sm text-gray-400">Lade…</div>}
        {!loading && displayed.length === 0 && (
          <div className="px-3 py-3 text-sm text-gray-400">
            {searchQuery
              ? `Keine Treffer für „${searchQuery}".`
              : onlyUnanswered
                ? 'Keine unbeantworteten Nachrichten.'
                : 'Keine Nachrichten.'}
          </div>
        )}
        {displayed.map((m, i) => {
          const inSelection = selectedUids.includes(m.uid)
          const bucket = dateBucket(m.date)
          const showHeader = i === 0 || dateBucket(displayed[i - 1].date) !== bucket
          return (
            <Fragment key={m.uid}>
              {showHeader && <DateHeader label={bucket} />}
            <div
              draggable
              onDragStart={(e) => startDrag(e, m)}
              onClick={(e) => handleClick(e, m)}
              onContextMenu={(e) => openMenu(e, m)}
              className={`flex w-full cursor-pointer select-none gap-2 border-b px-3 py-2 text-left hover:bg-blue-50 ${
                inSelection ? 'bg-blue-100' : selectedUid === m.uid ? 'bg-blue-50' : ''
              }`}
            >
              <SenderAvatar from={m.from} contacts={contacts} />
              <div className="min-w-0 flex-1">
                <div className="flex items-center justify-between gap-2">
                  <span className={`truncate text-sm ${m.seen ? 'text-gray-700' : 'font-semibold'}`}>
                    {m.from || '(unbekannt)'}
                  </span>
                  <span className="flex shrink-0 items-center gap-1.5 text-xs text-gray-400">
                    {formatDate(m.date)}
                    <button
                      onClick={(e) => {
                        e.stopPropagation()
                        setFlagged([m.uid], !m.flagged)
                      }}
                      title={m.flagged ? 'Markierung entfernen' : 'Markieren'}
                      className="hover:text-amber-500"
                    >
                      <Star
                        className={`h-3.5 w-3.5 ${m.flagged ? 'fill-amber-400 text-amber-400' : ''}`}
                      />
                    </button>
                  </span>
                </div>
                <div className={`truncate text-sm ${m.seen ? 'text-gray-600' : 'font-medium'}`}>
                  {m.subject}
                </div>
                <div className="flex items-center gap-1.5 text-xs text-gray-400">
                  {!m.seen && <span className="h-2 w-2 rounded-full bg-brand" />}
                  {m.answered && <Reply className="h-3 w-3" />}
                  {m.hasAttachments && <Paperclip className="h-3 w-3" />}
                  <LabelDots keywords={m.keywords} labels={labels} />
                </div>
              </div>
            </div>
            </Fragment>
          )
        })}
        {!loading && hasMore && (
          <button
            onClick={() => loadMoreMessages()}
            disabled={loadingMore}
            className="w-full py-2.5 text-center text-sm text-brand hover:bg-gray-50 disabled:opacity-50"
          >
            {loadingMore ? 'Lädt…' : 'Mehr laden'}
          </button>
        )}
      </div>

      {menu && (
        <>
          <div
            className="fixed inset-0 z-40"
            onClick={() => setMenu(null)}
            onContextMenu={(e) => {
              e.preventDefault()
              setMenu(null)
            }}
          />
          <div
            className="fixed z-50 w-56 overflow-hidden rounded-md border bg-white py-1 text-sm shadow-lg"
            style={{ left: menu.x, top: menu.y }}
          >
            {selectedUids.length > 1 && (
              <div className="px-3 py-1 text-xs text-gray-400">
                {selectedUids.length} Nachrichten
              </div>
            )}
            <MenuItem
              icon={menu.msg.seen ? <Mail className="h-4 w-4" /> : <MailOpen className="h-4 w-4" />}
              label={menu.msg.seen ? 'Als ungelesen markieren' : 'Als gelesen markieren'}
              onClick={() => {
                setMessagesSeen(targetsFor(menu.msg), !menu.msg.seen)
                setMenu(null)
              }}
            />
            <MenuItem
              icon={
                <Star
                  className={`h-4 w-4 ${menu.msg.flagged ? 'fill-amber-400 text-amber-400' : ''}`}
                />
              }
              label={menu.msg.flagged ? 'Markierung entfernen' : 'Markieren'}
              onClick={() => {
                setFlagged(targetsFor(menu.msg), !menu.msg.flagged)
                setMenu(null)
              }}
            />
            <MenuItem
              icon={<Archive className="h-4 w-4" />}
              label="Archivieren"
              onClick={() => {
                archiveMessages(targetsFor(menu.msg))
                setMenu(null)
              }}
            />
            {isJunkFolder ? (
              <MenuItem
                icon={<Ban className="h-4 w-4" />}
                label="Kein Spam (in Posteingang)"
                onClick={() => {
                  notSpamMessages(targetsFor(menu.msg))
                  setMenu(null)
                }}
              />
            ) : (
              <MenuItem
                icon={<Ban className="h-4 w-4" />}
                label="Als Spam markieren"
                onClick={() => {
                  spamMessages(targetsFor(menu.msg))
                  setMenu(null)
                }}
              />
            )}
            <button
              onClick={() => setShowMove((v) => !v)}
              className="flex w-full items-center gap-2 px-3 py-1.5 text-left text-gray-700 hover:bg-gray-100"
            >
              <FolderInput className="h-4 w-4" />
              Verschieben nach
              <ChevronRight
                className={`ml-auto h-4 w-4 transition-transform ${showMove ? 'rotate-90' : ''}`}
              />
            </button>
            {showMove && (
              <div className="max-h-48 overflow-y-auto border-y bg-gray-50">
                {moveTargets.length === 0 && (
                  <div className="px-5 py-1.5 text-xs text-gray-400">Keine Zielordner</div>
                )}
                {moveTargets.map((f) => (
                  <button
                    key={f.path}
                    onClick={() => {
                      moveToFolder(targetsFor(menu.msg), f.path)
                      setMenu(null)
                    }}
                    className="block w-full truncate px-5 py-1.5 text-left text-gray-700 hover:bg-gray-100"
                    title={f.path}
                  >
                    {f.name}
                  </button>
                ))}
              </div>
            )}
            <button
              onClick={() => setShowLabels((v) => !v)}
              className="flex w-full items-center gap-2 px-3 py-1.5 text-left text-gray-700 hover:bg-gray-100"
            >
              <Tag className="h-4 w-4" />
              Etiketten
              <ChevronRight
                className={`ml-auto h-4 w-4 transition-transform ${showLabels ? 'rotate-90' : ''}`}
              />
            </button>
            {showLabels && (
              <div className="max-h-48 overflow-y-auto border-y bg-gray-50">
                {labels.length === 0 && (
                  <div className="px-5 py-1.5 text-xs text-gray-400">
                    Keine Etiketten — oben anlegen
                  </div>
                )}
                {labels.map((l) => {
                  const assigned = menu.msg.keywords.includes(l.keyword)
                  return (
                    <button
                      key={l.id}
                      onClick={() => {
                        if (activeAccountId && activeFolder) {
                          setKeyword(
                            activeAccountId,
                            activeFolder,
                            targetsFor(menu.msg),
                            l.keyword,
                            !assigned
                          )
                        }
                        setMenu(null)
                      }}
                      className="flex w-full items-center gap-2 px-5 py-1.5 text-left text-gray-700 hover:bg-gray-100"
                    >
                      <span
                        className="h-3 w-3 shrink-0 rounded-full"
                        style={{ backgroundColor: l.color }}
                      />
                      <span className="min-w-0 flex-1 truncate">{l.name}</span>
                      {assigned && <Check className="h-4 w-4 shrink-0 text-brand" />}
                    </button>
                  )
                })}
              </div>
            )}
            <div className="my-1 border-t" />
            <MenuItem
              icon={<Trash2 className="h-4 w-4" />}
              label="Löschen"
              danger
              onClick={() => {
                removeMessages(targetsFor(menu.msg))
                setMenu(null)
              }}
            />
            <div className="px-3 pt-1 text-xs text-gray-400">
              Tipp: per Drag&amp;Drop in einen Ordner ziehen
            </div>
          </div>
        </>
      )}
    </div>
  )
}

interface UMenu {
  x: number
  y: number
  item: UnifiedItem
}

function UnifiedList(): JSX.Element {
  const items = useMailStore((s) => s.unifiedItems)
  const loading = useMailStore((s) => s.loadingMessages)
  const accounts = useMailStore((s) => s.accounts)
  const selectedUid = useMailStore((s) => s.selectedUid)
  const previewCtx = useMailStore((s) => s.previewCtx)
  const selectUnified = useMailStore((s) => s.selectUnified)
  const flagUnified = useMailStore((s) => s.flagUnified)
  const archiveUnified = useMailStore((s) => s.archiveUnified)
  const spamUnified = useMailStore((s) => s.spamUnified)
  const deleteUnified = useMailStore((s) => s.deleteUnified)
  const refresh = useMailStore((s) => s.refreshMessages)
  const labels = useMailStore((s) => s.labels)
  const setKeyword = useMailStore((s) => s.setKeyword)
  const activeLabel = useMailStore((s) => s.activeLabel)
  const contacts = useMailStore((s) => s.contacts)
  const [menu, setMenu] = useState<UMenu | null>(null)
  const [showLabels, setShowLabels] = useState(false)

  const isActive = (it: UnifiedItem): boolean =>
    selectedUid === it.uid &&
    previewCtx?.accountId === it.accountId &&
    previewCtx?.folder === it.folder

  function openMenu(e: React.MouseEvent, item: UnifiedItem): void {
    e.preventDefault()
    setShowLabels(false)
    setMenu({
      x: Math.min(e.clientX, window.innerWidth - 220),
      y: Math.min(e.clientY, window.innerHeight - 200),
      item
    })
  }

  return (
    <div className="flex h-full min-h-0 flex-col border-r">
      <div className="flex items-center justify-between border-b px-3 py-2">
        <span className="flex min-w-0 items-center gap-1.5 truncate text-sm font-medium">
          {activeLabel && (
            <span
              className="h-2.5 w-2.5 shrink-0 rounded-full"
              style={{ backgroundColor: activeLabel.color }}
            />
          )}
          {activeLabel ? activeLabel.name : 'Gemeinsamer Posteingang'}
        </span>
        <button
          onClick={refresh}
          className="rounded p-1.5 text-gray-500 hover:bg-gray-100"
          title="Aktualisieren"
        >
          <RefreshCw className="h-4 w-4" />
        </button>
      </div>
      <div className="min-h-0 flex-1 overflow-y-auto">
        {loading && <div className="px-3 py-3 text-sm text-gray-400">Lade Nachrichten…</div>}
        {!loading && items.length === 0 && (
          <div className="px-3 py-3 text-sm text-gray-400">Keine Nachrichten.</div>
        )}
        {items.map((it, i) => {
          const color = colorById(accounts, it.accountId)
          const email = accounts.find((a) => a.id === it.accountId)?.email ?? ''
          const bucket = dateBucket(it.date)
          const showHeader = i === 0 || dateBucket(items[i - 1].date) !== bucket
          return (
            <Fragment key={`${it.accountId}|${it.folder}|${it.uid}`}>
              {showHeader && <DateHeader label={bucket} />}
            <div
              onClick={() => selectUnified(it)}
              onContextMenu={(e) => openMenu(e, it)}
              style={{ borderLeftColor: color }}
              className={`flex w-full cursor-pointer select-none gap-2 border-b border-l-4 px-3 py-2 text-left hover:bg-blue-50 ${
                isActive(it) ? 'bg-blue-100' : ''
              }`}
            >
              <SenderAvatar from={it.from} contacts={contacts} />
              <div className="min-w-0 flex-1">
                <div className="flex items-center justify-between gap-2">
                  <span
                    className={`truncate text-sm ${it.seen ? 'text-gray-700' : 'font-semibold'}`}
                  >
                    {it.from || '(unbekannt)'}
                  </span>
                  <span className="flex shrink-0 items-center gap-1.5 text-xs text-gray-400">
                    {formatDate(it.date)}
                    <button
                      onClick={(e) => {
                        e.stopPropagation()
                        flagUnified(it, !it.flagged)
                      }}
                      title={it.flagged ? 'Markierung entfernen' : 'Markieren'}
                      className="hover:text-amber-500"
                    >
                      <Star
                        className={`h-3.5 w-3.5 ${it.flagged ? 'fill-amber-400 text-amber-400' : ''}`}
                      />
                    </button>
                  </span>
                </div>
                <div className={`truncate text-sm ${it.seen ? 'text-gray-600' : 'font-medium'}`}>
                  {it.subject}
                </div>
                <div className="flex items-center gap-1.5 text-xs text-gray-400">
                  {!it.seen && <span className="h-2 w-2 rounded-full bg-brand" />}
                  {it.answered && <Reply className="h-3 w-3" />}
                  {it.hasAttachments && <Paperclip className="h-3 w-3" />}
                  <LabelDots keywords={it.keywords} labels={labels} />
                  <span className="truncate" style={{ color }}>
                    {email}
                  </span>
                </div>
              </div>
            </div>
            </Fragment>
          )
        })}
      </div>

      {menu && (
        <>
          <div
            className="fixed inset-0 z-40"
            onClick={() => setMenu(null)}
            onContextMenu={(e) => {
              e.preventDefault()
              setMenu(null)
            }}
          />
          <div
            className="fixed z-50 w-52 overflow-hidden rounded-md border bg-white py-1 text-sm shadow-lg"
            style={{ left: menu.x, top: menu.y }}
          >
            <MenuItem
              icon={
                <Star
                  className={`h-4 w-4 ${menu.item.flagged ? 'fill-amber-400 text-amber-400' : ''}`}
                />
              }
              label={menu.item.flagged ? 'Markierung entfernen' : 'Markieren'}
              onClick={() => {
                flagUnified(menu.item, !menu.item.flagged)
                setMenu(null)
              }}
            />
            <MenuItem
              icon={<Archive className="h-4 w-4" />}
              label="Archivieren"
              onClick={() => {
                archiveUnified(menu.item)
                setMenu(null)
              }}
            />
            <MenuItem
              icon={<Ban className="h-4 w-4" />}
              label="Als Spam markieren"
              onClick={() => {
                spamUnified(menu.item)
                setMenu(null)
              }}
            />
            <button
              onClick={() => setShowLabels((v) => !v)}
              className="flex w-full items-center gap-2 px-3 py-1.5 text-left text-gray-700 hover:bg-gray-100"
            >
              <Tag className="h-4 w-4" />
              Etiketten
              <ChevronRight
                className={`ml-auto h-4 w-4 transition-transform ${showLabels ? 'rotate-90' : ''}`}
              />
            </button>
            {showLabels && (
              <div className="max-h-48 overflow-y-auto border-y bg-gray-50">
                {labels.length === 0 && (
                  <div className="px-5 py-1.5 text-xs text-gray-400">Keine Etiketten</div>
                )}
                {labels.map((l) => {
                  const assigned = menu.item.keywords.includes(l.keyword)
                  return (
                    <button
                      key={l.id}
                      onClick={() => {
                        setKeyword(
                          menu.item.accountId,
                          menu.item.folder,
                          [menu.item.uid],
                          l.keyword,
                          !assigned
                        )
                        setMenu(null)
                      }}
                      className="flex w-full items-center gap-2 px-5 py-1.5 text-left text-gray-700 hover:bg-gray-100"
                    >
                      <span
                        className="h-3 w-3 shrink-0 rounded-full"
                        style={{ backgroundColor: l.color }}
                      />
                      <span className="min-w-0 flex-1 truncate">{l.name}</span>
                      {assigned && <Check className="h-4 w-4 shrink-0 text-brand" />}
                    </button>
                  )
                })}
              </div>
            )}
            <div className="my-1 border-t" />
            <MenuItem
              icon={<Trash2 className="h-4 w-4" />}
              label="Löschen"
              danger
              onClick={() => {
                deleteUnified(menu.item)
                setMenu(null)
              }}
            />
          </div>
        </>
      )}
    </div>
  )
}

function MenuItem({
  icon,
  label,
  onClick,
  danger
}: {
  icon: JSX.Element
  label: string
  onClick: () => void
  danger?: boolean
}): JSX.Element {
  return (
    <button
      onClick={onClick}
      className={`flex w-full items-center gap-2 px-3 py-1.5 text-left hover:bg-gray-100 ${
        danger ? 'text-red-600' : 'text-gray-700'
      }`}
    >
      {icon}
      {label}
    </button>
  )
}
