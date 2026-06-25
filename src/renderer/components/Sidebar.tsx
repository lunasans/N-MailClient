import { useEffect, useMemo, useState } from 'react'
import {
  Archive,
  Ban,
  ChevronDown,
  ChevronRight,
  FileText,
  Folder,
  FolderPlus,
  Inbox,
  Mail,
  Mails,
  Pencil,
  Send,
  Trash2
} from 'lucide-react'
import type { Account, MailboxNode } from '@shared/index'
import { useMailStore } from '../store/useMailStore'
import { colorForAccount } from '../lib/accountColor'

interface TreeNode extends MailboxNode {
  children: TreeNode[]
}

/** Standard order for system folders: Inbox, Sent, Drafts, Spam, Trash, Archive. */
const ROLE_RANK: Record<string, number> = {
  inbox: 0,
  sent: 1,
  drafts: 2,
  junk: 3,
  trash: 4,
  archive: 5
}
const roleRank = (role?: string): number => (role ? (ROLE_RANK[role] ?? 50) : 50)

function buildTree(folders: MailboxNode[], order: string[] = []): TreeNode[] {
  const byPath = new Map<string, TreeNode>()
  for (const f of folders) byPath.set(f.path, { ...f, children: [] })
  const roots: TreeNode[] = []
  for (const node of byPath.values()) {
    const delim = node.delimiter || '/'
    const idx = node.path.lastIndexOf(delim)
    const parentPath = idx > 0 ? node.path.slice(0, idx) : ''
    const parent = parentPath ? byPath.get(parentPath) : undefined
    if (parent) parent.children.push(node)
    else roots.push(node)
  }
  const rank = (p: string): number => {
    const i = order.indexOf(p)
    return i === -1 ? Infinity : i
  }
  const sortRec = (nodes: TreeNode[]): void => {
    nodes.sort((a, b) => {
      // 1) explicit custom order, 2) system folders in standard order, 3) name.
      const ra = rank(a.path)
      const rb = rank(b.path)
      if (ra !== rb) return ra - rb
      const pa = roleRank(a.role)
      const pb = roleRank(b.role)
      if (pa !== pb) return pa - pb
      return a.name.localeCompare(b.name, 'de')
    })
    nodes.forEach((n) => sortRec(n.children))
  }
  sortRec(roots)
  return roots
}

function FolderIcon({ role }: { role?: string }): JSX.Element {
  const cls = 'h-4 w-4 shrink-0 text-gray-500'
  switch (role) {
    case 'inbox':
      return <Inbox className={cls} />
    case 'sent':
      return <Send className={cls} />
    case 'drafts':
      return <FileText className={cls} />
    case 'trash':
      return <Trash2 className={cls} />
    case 'junk':
      return <Ban className={cls} />
    case 'archive':
      return <Archive className={cls} />
    default:
      return <Folder className={cls} />
  }
}

interface FolderMenu {
  x: number
  y: number
  accountId: string
  node: MailboxNode
}

interface NameDialog {
  mode: 'create' | 'rename'
  accountId: string
  /** parent path for create, original path for rename */
  base: string
  title: string
  value: string
}

export default function Sidebar(): JSX.Element {
  const accounts = useMailStore((s) => s.accounts)
  const foldersByAccount = useMailStore((s) => s.foldersByAccount)
  const activeAccountId = useMailStore((s) => s.activeAccountId)
  const activeFolder = useMailStore((s) => s.activeFolder)
  const loadingFoldersFor = useMailStore((s) => s.loadingFoldersFor)
  const ensureFolders = useMailStore((s) => s.ensureFolders)
  const selectFolder = useMailStore((s) => s.selectFolder)
  const draggingUids = useMailStore((s) => s.draggingUids)
  const draggingFolder = useMailStore((s) => s.draggingFolder)
  const moveToFolder = useMailStore((s) => s.moveToFolder)
  const setDragging = useMailStore((s) => s.setDragging)
  const setDraggingFolder = useMailStore((s) => s.setDraggingFolder)
  const createFolder = useMailStore((s) => s.createFolder)
  const deleteFolder = useMailStore((s) => s.deleteFolder)
  const renameFolder = useMailStore((s) => s.renameFolder)
  const unified = useMailStore((s) => s.unified)
  const loadUnified = useMailStore((s) => s.loadUnified)
  const labels = useMailStore((s) => s.labels)
  const activeLabel = useMailStore((s) => s.activeLabel)
  const loadLabelView = useMailStore((s) => s.loadLabelView)

  const [expanded, setExpanded] = useState<Set<string>>(new Set())
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set())
  const [dropTarget, setDropTarget] = useState<string | null>(null)
  const [menu, setMenu] = useState<FolderMenu | null>(null)
  const [dialog, setDialog] = useState<NameDialog | null>(null)

  useEffect(() => {
    if (activeAccountId) {
      setExpanded((prev) => (prev.has(activeAccountId) ? prev : new Set(prev).add(activeAccountId)))
      ensureFolders(activeAccountId)
    }
  }, [activeAccountId, ensureFolders])

  function delimFor(accountId: string): string {
    return foldersByAccount[accountId]?.[0]?.delimiter || '/'
  }

  function toggleAccount(account: Account): void {
    setExpanded((prev) => {
      const next = new Set(prev)
      if (next.has(account.id)) next.delete(account.id)
      else {
        next.add(account.id)
        ensureFolders(account.id)
      }
      return next
    })
  }

  async function openAccount(account: Account): Promise<void> {
    await ensureFolders(account.id)
    const folders = useMailStore.getState().foldersByAccount[account.id] ?? []
    const inbox = folders.find((f) => f.role === 'inbox') ?? folders.find((f) => f.selectable)
    if (inbox) selectFolder(account.id, inbox.path)
  }

  function toggleFolder(path: string): void {
    setCollapsed((prev) => {
      const next = new Set(prev)
      if (next.has(path)) next.delete(path)
      else next.add(path)
      return next
    })
  }

  function openMenu(e: React.MouseEvent, accountId: string, node: MailboxNode): void {
    e.preventDefault()
    e.stopPropagation()
    setMenu({ x: Math.min(e.clientX, window.innerWidth - 200), y: e.clientY, accountId, node })
  }

  function submitDialog(): void {
    if (!dialog) return
    const name = dialog.value.trim()
    if (!name) return
    const delim = delimFor(dialog.accountId)
    if (dialog.mode === 'create') {
      const path = dialog.base ? dialog.base + delim + name : name
      createFolder(dialog.accountId, path)
    } else {
      const idx = dialog.base.lastIndexOf(delim)
      const parent = idx > 0 ? dialog.base.slice(0, idx) : ''
      const newPath = parent ? parent + delim + name : name
      if (newPath !== dialog.base) renameFolder(dialog.accountId, dialog.base, newPath)
    }
    setDialog(null)
  }

  /** Move (rename) a dragged folder into a target folder. */
  function dropFolderInto(accountId: string, targetPath: string): void {
    const src = draggingFolder
    setDraggingFolder(null)
    if (!src || src.accountId !== accountId) return
    const delim = delimFor(accountId)
    if (targetPath === src.path) return
    if (targetPath.startsWith(src.path + delim)) return // can't move into own descendant
    const name = src.path.split(delim).pop() ?? src.path
    const newPath = targetPath ? targetPath + delim + name : name
    if (newPath !== src.path) renameFolder(accountId, src.path, newPath)
  }

  const renderFolder = (accountId: string, node: TreeNode, depth: number): JSX.Element => {
    const hasChildren = node.children.length > 0
    const isCollapsed = collapsed.has(node.path)
    const isActive = activeAccountId === accountId && activeFolder === node.path
    const key = accountId + '|' + node.path
    const canDropMsgs =
      node.selectable &&
      draggingUids.length > 0 &&
      accountId === activeAccountId &&
      node.path !== activeFolder
    const canDropFolder =
      !!draggingFolder &&
      draggingFolder.accountId === accountId &&
      node.selectable &&
      draggingFolder.path !== node.path &&
      !node.path.startsWith(draggingFolder.path + (node.delimiter || '/'))
    const isDropTarget = dropTarget === key
    return (
      <div key={node.path}>
        <div
          draggable={node.selectable && !node.role}
          onDragStart={(e) => {
            e.stopPropagation()
            setDraggingFolder({ accountId, path: node.path })
            e.dataTransfer.effectAllowed = 'move'
          }}
          onDragEnd={() => setDraggingFolder(null)}
          onDragOver={(e) => {
            if (!canDropMsgs && !canDropFolder) return
            e.preventDefault()
            e.dataTransfer.dropEffect = 'move'
            setDropTarget(key)
          }}
          onDragLeave={() => setDropTarget((t) => (t === key ? null : t))}
          onDrop={(e) => {
            if (!canDropMsgs && !canDropFolder) return
            e.preventDefault()
            setDropTarget(null)
            if (canDropMsgs) {
              const uids = [...draggingUids]
              setDragging([])
              moveToFolder(uids, node.path)
            } else if (canDropFolder) {
              dropFolderInto(accountId, node.path)
            }
          }}
          onContextMenu={(e) => openMenu(e, accountId, node)}
          className={`flex items-center gap-1 py-1 pr-2 text-sm hover:bg-gray-200 ${
            isActive ? 'bg-gray-200 font-medium' : ''
          } ${isDropTarget ? 'rounded bg-blue-50 ring-2 ring-inset ring-brand' : ''}`}
          style={{ paddingLeft: depth * 12 + 24 }}
        >
          {hasChildren ? (
            <button
              onClick={() => toggleFolder(node.path)}
              className="text-gray-400 hover:text-gray-700"
            >
              {isCollapsed ? (
                <ChevronRight className="h-3.5 w-3.5" />
              ) : (
                <ChevronDown className="h-3.5 w-3.5" />
              )}
            </button>
          ) : (
            <span className="w-3.5" />
          )}
          <button
            onClick={() =>
              node.selectable ? selectFolder(accountId, node.path) : toggleFolder(node.path)
            }
            disabled={!node.selectable && !hasChildren}
            className={`flex min-w-0 flex-1 items-center gap-2 text-left ${
              node.selectable ? '' : 'text-gray-500'
            }`}
            title={node.path}
          >
            <FolderIcon role={node.role} />
            <span className={`truncate ${node.unseen ? 'font-semibold' : ''}`}>{node.name}</span>
            {node.unseen ? (
              <span className="ml-auto shrink-0 rounded-full bg-brand px-1.5 py-0.5 text-xs font-medium text-white">
                {node.unseen}
              </span>
            ) : null}
          </button>
        </div>
        {hasChildren && !isCollapsed && (
          <div>{node.children.map((c) => renderFolder(accountId, c, depth + 1))}</div>
        )}
      </div>
    )
  }

  return (
    <div className="flex h-full flex-col overflow-y-auto border-r bg-gray-100">
      {accounts.length > 1 && (
        <button
          onClick={loadUnified}
          className={`flex items-center gap-2 border-b border-gray-200 px-2 py-2 text-sm ${
            unified ? 'bg-gray-200 font-medium' : 'hover:bg-gray-200'
          }`}
        >
          <Mails className="h-4 w-4 shrink-0 text-brand" />
          <span className="truncate">Gemeinsamer Posteingang</span>
        </button>
      )}
      {labels.length > 0 && (
        <div className="mb-3 border-b border-gray-200 pb-1">
          <div className="px-3 py-1.5 text-xs font-semibold uppercase tracking-wide text-gray-500">
            Etiketten
          </div>
          {labels.map((l) => (
            <button
              key={l.id}
              onClick={() => loadLabelView(l)}
              className={`flex w-full items-center gap-2 px-3 py-1.5 text-sm ${
                activeLabel?.id === l.id ? 'bg-gray-200 font-medium' : 'hover:bg-gray-200'
              }`}
              title={l.name}
            >
              <span
                className="h-2.5 w-2.5 shrink-0 rounded-full"
                style={{ backgroundColor: l.color }}
              />
              <span className="truncate">{l.name}</span>
            </button>
          ))}
        </div>
      )}
      {accounts.map((account, accountIndex) => {
        const isExpanded = expanded.has(account.id)
        const tree = buildTree(foldersByAccount[account.id] ?? [], account.folderOrder ?? [])
        const isActiveAccount = activeAccountId === account.id && !unified && !activeLabel
        const accentColor = colorForAccount(account, accountIndex)
        return (
          <div key={account.id} className="border-b border-gray-200">
            <div
              className={`group flex items-center gap-1.5 px-2 py-2 text-sm ${
                isActiveAccount ? 'bg-gray-200' : ''
              }`}
            >
              <button
                onClick={() => toggleAccount(account)}
                className="text-gray-400 hover:text-gray-700"
              >
                {isExpanded ? (
                  <ChevronDown className="h-4 w-4" />
                ) : (
                  <ChevronRight className="h-4 w-4" />
                )}
              </button>
              <button
                onClick={() => openAccount(account)}
                className="flex min-w-0 flex-1 items-center gap-2 text-left"
                title={account.name}
              >
                <span
                  className="h-2.5 w-2.5 shrink-0 rounded-full"
                  style={{ backgroundColor: accentColor }}
                />
                <Mail className="h-4 w-4 shrink-0 text-gray-500" />
                <span className="truncate font-medium">{account.email}</span>
              </button>
              <button
                onClick={() => {
                  ensureFolders(account.id)
                  setDialog({
                    mode: 'create',
                    accountId: account.id,
                    base: '',
                    title: 'Neuer Ordner',
                    value: ''
                  })
                }}
                className="text-gray-400 opacity-0 hover:text-gray-700 group-hover:opacity-100"
                title="Neuen Ordner anlegen"
              >
                <FolderPlus className="h-4 w-4" />
              </button>
            </div>
            {isExpanded && (
              <div className="pb-1">
                {loadingFoldersFor === account.id && !foldersByAccount[account.id] ? (
                  <div className="px-6 py-1 text-xs text-gray-400">Lade Ordner…</div>
                ) : (
                  tree.map((n) => renderFolder(account.id, n, 0))
                )}
              </div>
            )}
          </div>
        )
      })}

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
            <button
              onClick={() => {
                setDialog({
                  mode: 'create',
                  accountId: menu.accountId,
                  base: menu.node.path,
                  title: 'Neuer Unterordner',
                  value: ''
                })
                setMenu(null)
              }}
              className="flex w-full items-center gap-2 px-3 py-1.5 text-left text-gray-700 hover:bg-gray-100"
            >
              <FolderPlus className="h-4 w-4" />
              Neuer Unterordner
            </button>
            <button
              onClick={() => {
                setDialog({
                  mode: 'rename',
                  accountId: menu.accountId,
                  base: menu.node.path,
                  title: 'Ordner umbenennen',
                  value: menu.node.name
                })
                setMenu(null)
              }}
              className="flex w-full items-center gap-2 px-3 py-1.5 text-left text-gray-700 hover:bg-gray-100"
            >
              <Pencil className="h-4 w-4" />
              Umbenennen
            </button>
            <div className="my-1 border-t" />
            <button
              onClick={() => {
                const node = menu.node
                const accountId = menu.accountId
                setMenu(null)
                if (confirm(`Ordner „${node.name}" wirklich löschen? Inhalte gehen verloren.`)) {
                  deleteFolder(accountId, node.path)
                }
              }}
              className="flex w-full items-center gap-2 px-3 py-1.5 text-left text-red-600 hover:bg-red-50"
            >
              <Trash2 className="h-4 w-4" />
              Löschen
            </button>
          </div>
        </>
      )}

      {dialog && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40">
          <div className="w-[400px] rounded-lg bg-white p-5 shadow-xl">
            <h2 className="mb-3 text-base font-semibold">{dialog.title}</h2>
            <input
              autoFocus
              className="w-full rounded border px-3 py-2 text-sm"
              value={dialog.value}
              onChange={(e) => setDialog({ ...dialog, value: e.target.value })}
              onKeyDown={(e) => {
                if (e.key === 'Enter') submitDialog()
                if (e.key === 'Escape') setDialog(null)
              }}
              placeholder="Ordnername"
            />
            <div className="mt-4 flex justify-end gap-2">
              <button
                onClick={() => setDialog(null)}
                className="rounded px-4 py-2 text-sm text-gray-600 hover:bg-gray-100"
              >
                Abbrechen
              </button>
              <button
                onClick={submitDialog}
                disabled={!dialog.value.trim()}
                className="rounded bg-brand px-4 py-2 text-sm text-white disabled:opacity-50"
              >
                {dialog.mode === 'create' ? 'Anlegen' : 'Umbenennen'}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}
