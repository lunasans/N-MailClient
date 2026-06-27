import { useEffect, useMemo, useState } from 'react'
import { Check, ChevronDown, ChevronUp, GripVertical } from 'lucide-react'
import type { Account, MailboxNode, ServerConfig } from '@shared/index'
import { useMailStore } from '../store/useMailStore'
import { ACCOUNT_PALETTE, colorForAccount } from '../lib/accountColor'

interface Props {
  account: Account
  onClose: () => void
}

const ROLE_RANK: Record<string, number> = {
  inbox: 0,
  sent: 1,
  drafts: 2,
  junk: 3,
  trash: 4,
  archive: 5
}
const roleRank = (role?: string): number => (role ? (ROLE_RANK[role] ?? 50) : 50)

/** Initial folder order: stored order (sanitized) first, then system folders, then rest. */
function initialOrder(folders: MailboxNode[], stored?: string[]): string[] {
  const paths = folders.map((f) => f.path)
  const base = (stored ?? []).filter((p) => paths.includes(p))
  const rest = paths
    .filter((p) => !base.includes(p))
    .sort((a, b) => {
      const fa = folders.find((f) => f.path === a)
      const fb = folders.find((f) => f.path === b)
      const pa = roleRank(fa?.role)
      const pb = roleRank(fb?.role)
      if (pa !== pb) return pa - pb
      return (fa?.name ?? a).localeCompare(fb?.name ?? b, 'de')
    })
  return [...base, ...rest]
}

export default function AccountSettings({ account, onClose }: Props): JSX.Element {
  const loadAccounts = useMailStore((s) => s.loadAccounts)
  const ensureFolders = useMailStore((s) => s.ensureFolders)
  const foldersByAccount = useMailStore((s) => s.foldersByAccount)
  const accounts = useMailStore((s) => s.accounts)
  const accountIndex = Math.max(
    0,
    accounts.findIndex((a) => a.id === account.id)
  )
  const [color, setColor] = useState(colorForAccount(account, accountIndex))
  const [aliasesText, setAliasesText] = useState((account.aliases ?? []).join('\n'))
  const [name, setName] = useState(account.name)
  const [signature, setSignature] = useState(account.signature ?? '')
  const [aliasSigs, setAliasSigs] = useState<Record<string, string>>(
    account.aliasSignatures ?? {}
  )
  const [user, setUser] = useState(account.user)
  const [password, setPassword] = useState('')
  const [imap, setImap] = useState<ServerConfig>(account.imap)
  const [smtp, setSmtp] = useState<ServerConfig>(account.smtp)
  const [order, setOrder] = useState<string[]>([])
  const [dragPath, setDragPath] = useState<string | null>(null)
  const [overPath, setOverPath] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const folders = useMemo(
    () => (foldersByAccount[account.id] ?? []).filter((f) => f.selectable),
    [foldersByAccount, account.id]
  )

  useEffect(() => {
    ensureFolders(account.id)
  }, [account.id, ensureFolders])

  // Initialize the order once folders are available.
  useEffect(() => {
    if (folders.length && order.length === 0) {
      setOrder(initialOrder(folders, account.folderOrder))
    }
  }, [folders, order.length, account.folderOrder])

  function move(path: string, dir: -1 | 1): void {
    setOrder((prev) => {
      const i = prev.indexOf(path)
      const j = i + dir
      if (i === -1 || j < 0 || j >= prev.length) return prev
      const next = [...prev]
      ;[next[i], next[j]] = [next[j], next[i]]
      return next
    })
  }

  /** Move `from` to the position of `to` (drag-and-drop reorder). */
  function reorder(from: string, to: string): void {
    if (from === to) return
    setOrder((prev) => {
      const without = prev.filter((p) => p !== from)
      const idx = without.indexOf(to)
      if (idx === -1) return prev
      without.splice(idx, 0, from)
      return without
    })
  }

  function nameFor(path: string): string {
    const f = folders.find((x) => x.path === path)
    if (!f) return path
    const depth = path.split(f.delimiter || '/').length - 1
    return ' '.repeat(depth * 2) + f.name
  }

  async function handleSave(): Promise<void> {
    setSaving(true)
    setError(null)
    const res = await window.api.accounts.updateSettings(account.id, {
      name,
      signature,
      aliasSignatures: Object.fromEntries(
        Object.entries(aliasSigs).filter(([, v]) => v.trim())
      ),
      user,
      imap,
      smtp,
      password: password || undefined,
      folderOrder: order.length ? order : undefined,
      color,
      aliases: aliasesText
        .split('\n')
        .map((a) => a.trim())
        .filter(Boolean)
    })
    setSaving(false)
    if (!res.ok) {
      setError(res.error)
      return
    }
    await loadAccounts()
    onClose()
  }

  return (
    <div className="fixed inset-0 z-30 flex items-center justify-center bg-black/40">
      <div className="max-h-[92vh] w-[600px] overflow-y-auto rounded-lg bg-white p-6 shadow-xl">
        <h2 className="mb-1 text-lg font-semibold">Konto-Einstellungen</h2>
        <p className="mb-4 text-sm text-gray-500">{account.email}</p>

        <label className="mb-3 block">
          <span className="text-sm text-gray-600">Anzeigename</span>
          <input
            className="mt-1 w-full rounded border px-3 py-2 text-sm"
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder="Max Mustermann"
          />
        </label>

        <div className="mb-3">
          <span className="text-sm text-gray-600">Farbe (gemeinsamer Posteingang)</span>
          <div className="mt-1 flex flex-wrap gap-2">
            {ACCOUNT_PALETTE.map((c) => (
              <button
                key={c}
                onClick={() => setColor(c)}
                style={{ backgroundColor: c }}
                className="flex h-7 w-7 items-center justify-center rounded-full"
                title={c}
              >
                {color === c && <Check className="h-4 w-4 text-white" />}
              </button>
            ))}
          </div>
        </div>

        <label className="mb-3 block">
          <span className="text-sm text-gray-600">Signatur</span>
          <textarea
            className="mt-1 h-28 w-full resize-none rounded border px-3 py-2 font-mono text-sm"
            value={signature}
            onChange={(e) => setSignature(e.target.value)}
            placeholder={'Mit freundlichen Grüßen\nMax Mustermann\nNeuhaus'}
          />
        </label>

        <label className="mb-3 block">
          <span className="text-sm text-gray-600">Alias-Adressen (eine pro Zeile)</span>
          <textarea
            className="mt-1 h-20 w-full resize-none rounded border px-3 py-2 text-sm"
            value={aliasesText}
            onChange={(e) => setAliasesText(e.target.value)}
            placeholder={'info@deinedomain.tld\nMax Mustermann <max@deinedomain.tld>'}
          />
          <span className="text-xs text-gray-400">
            Im Composer als Absender wählbar. Versand läuft weiter über dieses Konto.
          </span>
        </label>

        {aliasesText
          .split('\n')
          .map((a) => a.trim())
          .filter(Boolean)
          .map((alias) => (
            <label key={alias} className="mb-3 block">
              <span className="text-sm text-gray-600">Signatur für {alias}</span>
              <textarea
                className="mt-1 h-20 w-full resize-none rounded border px-3 py-2 font-mono text-sm"
                value={aliasSigs[alias] ?? ''}
                onChange={(e) => setAliasSigs((p) => ({ ...p, [alias]: e.target.value }))}
                placeholder="Leer = Standard-Signatur dieses Kontos"
              />
            </label>
          ))}

        <div className="my-4 border-t pt-4">
          <h3 className="mb-2 text-sm font-semibold text-gray-700">Server &amp; Zugang</h3>

          <label className="mb-3 block">
            <span className="text-sm text-gray-600">Benutzer</span>
            <input
              className="mt-1 w-full rounded border px-3 py-2 text-sm"
              value={user}
              onChange={(e) => setUser(e.target.value)}
            />
          </label>

          <label className="mb-3 block">
            <span className="text-sm text-gray-600">Passwort</span>
            <input
              type="password"
              className="mt-1 w-full rounded border px-3 py-2 text-sm"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              placeholder="Leer lassen = unverändert"
            />
          </label>

          <div className="grid grid-cols-2 gap-4">
            <ServerFields title="IMAP (Empfang)" value={imap} onChange={setImap} />
            <ServerFields title="SMTP (Versand)" value={smtp} onChange={setSmtp} />
          </div>
        </div>

        <div className="my-4 border-t pt-4">
          <h3 className="mb-2 text-sm font-semibold text-gray-700">Ordner-Reihenfolge</h3>
          {order.length === 0 ? (
            <p className="text-xs text-gray-400">Ordner werden geladen…</p>
          ) : (
            <div className="max-h-56 divide-y overflow-y-auto rounded border">
              {order.map((path, i) => (
                <div
                  key={path}
                  draggable
                  onDragStart={() => setDragPath(path)}
                  onDragEnd={() => {
                    setDragPath(null)
                    setOverPath(null)
                  }}
                  onDragOver={(e) => {
                    e.preventDefault()
                    if (overPath !== path) setOverPath(path)
                  }}
                  onDrop={(e) => {
                    e.preventDefault()
                    if (dragPath) reorder(dragPath, path)
                    setDragPath(null)
                    setOverPath(null)
                  }}
                  className={`flex items-center gap-2 px-2 py-1.5 text-sm ${
                    dragPath === path ? 'opacity-40' : ''
                  } ${
                    overPath === path && dragPath !== path
                      ? 'border-t-2 border-brand bg-blue-50'
                      : ''
                  }`}
                >
                  <GripVertical className="h-4 w-4 shrink-0 cursor-grab text-gray-400" />
                  <span className="min-w-0 flex-1 truncate whitespace-pre" title={path}>
                    {nameFor(path)}
                  </span>
                  <button
                    onClick={() => move(path, -1)}
                    disabled={i === 0}
                    className="rounded p-1 text-gray-500 hover:bg-gray-100 disabled:opacity-30"
                    title="Nach oben"
                  >
                    <ChevronUp className="h-4 w-4" />
                  </button>
                  <button
                    onClick={() => move(path, 1)}
                    disabled={i === order.length - 1}
                    className="rounded p-1 text-gray-500 hover:bg-gray-100 disabled:opacity-30"
                    title="Nach unten"
                  >
                    <ChevronDown className="h-4 w-4" />
                  </button>
                </div>
              ))}
            </div>
          )}
          <p className="mt-1 text-xs text-gray-400">
            Per Drag &amp; Drop ziehen oder mit den Pfeilen sortieren.
          </p>
        </div>

        {error && <p className="mb-3 text-sm text-red-600">{error}</p>}

        <div className="flex justify-end gap-2">
          <button onClick={onClose} className="rounded px-4 py-2 text-gray-600 hover:bg-gray-100">
            Abbrechen
          </button>
          <button
            onClick={handleSave}
            disabled={saving}
            className="rounded bg-brand px-4 py-2 text-white disabled:opacity-50"
          >
            {saving ? 'Speichern…' : 'Speichern'}
          </button>
        </div>
      </div>
    </div>
  )
}

function ServerFields({
  title,
  value,
  onChange
}: {
  title: string
  value: ServerConfig
  onChange: (v: ServerConfig) => void
}): JSX.Element {
  return (
    <fieldset className="rounded border p-3">
      <legend className="px-1 text-sm font-medium">{title}</legend>
      <label className="mb-2 block">
        <span className="text-xs text-gray-500">Host</span>
        <input
          className="mt-1 w-full rounded border px-2 py-1 text-sm"
          value={value.host}
          onChange={(e) => onChange({ ...value, host: e.target.value })}
        />
      </label>
      <div className="flex gap-2">
        <label className="block flex-1">
          <span className="text-xs text-gray-500">Port</span>
          <input
            type="number"
            className="mt-1 w-full rounded border px-2 py-1 text-sm"
            value={value.port}
            onChange={(e) => onChange({ ...value, port: Number(e.target.value) })}
          />
        </label>
        <label className="block flex-1">
          <span className="text-xs text-gray-500">TLS</span>
          <select
            className="mt-1 w-full rounded border px-2 py-1 text-sm"
            value={value.secure ? 'ssl' : 'starttls'}
            onChange={(e) => onChange({ ...value, secure: e.target.value === 'ssl' })}
          >
            <option value="ssl">SSL/TLS</option>
            <option value="starttls">STARTTLS</option>
          </select>
        </label>
      </div>
    </fieldset>
  )
}
