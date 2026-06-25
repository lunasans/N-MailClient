import { useEffect, useState } from 'react'
import { Bell, Calendar, Check, FileCode, Info, Plus, Tag, Trash2, User, X } from 'lucide-react'
import type { Account, CalendarConfig, SieveScript, UpdateStatus } from '@shared/index'
import { useMailStore } from '../store/useMailStore'
import { ACCOUNT_PALETTE, colorForAccount } from '../lib/accountColor'
import AccountSettings from './AccountSettings'
import AccountSetup from './AccountSetup'
import CalDavSetup from './CalDavSetup'

type Section = 'general' | 'accounts' | 'labels' | 'calendar' | 'filters' | 'about'

interface Props {
  onClose: () => void
}

const UNDO_OPTIONS = [0, 5, 10, 20, 30]
const REMINDER_OPTIONS = [0, 5, 10, 15, 30, 60]

export default function Settings({ onClose }: Props): JSX.Element {
  const [section, setSection] = useState<Section>('general')

  const NAV: { id: Section; label: string; icon: JSX.Element }[] = [
    { id: 'general', label: 'Allgemein', icon: <Bell className="h-4 w-4" /> },
    { id: 'accounts', label: 'Konten', icon: <User className="h-4 w-4" /> },
    { id: 'labels', label: 'Etiketten', icon: <Tag className="h-4 w-4" /> },
    { id: 'calendar', label: 'Kalender & Kontakte', icon: <Calendar className="h-4 w-4" /> },
    { id: 'filters', label: 'Filter (Sieve)', icon: <FileCode className="h-4 w-4" /> },
    { id: 'about', label: 'Über', icon: <Info className="h-4 w-4" /> }
  ]

  return (
    <div className="fixed inset-0 z-30 flex items-center justify-center bg-black/40">
      <div className="flex h-[80vh] w-[760px] overflow-hidden rounded-lg bg-white shadow-xl">
        <div className="w-48 shrink-0 border-r bg-gray-50 p-2">
          <div className="mb-2 px-2 py-1 text-sm font-semibold">Einstellungen</div>
          {NAV.map((n) => (
            <button
              key={n.id}
              onClick={() => setSection(n.id)}
              className={`mb-0.5 flex w-full items-center gap-2 rounded px-2 py-1.5 text-left text-sm ${
                section === n.id ? 'bg-brand text-white' : 'hover:bg-gray-200'
              }`}
            >
              {n.icon}
              {n.label}
            </button>
          ))}
        </div>
        <div className="flex min-w-0 flex-1 flex-col">
          <div className="flex items-center justify-between border-b px-5 py-3">
            <h2 className="text-lg font-semibold">{NAV.find((n) => n.id === section)?.label}</h2>
            <button onClick={onClose} className="text-gray-400 hover:text-gray-700">
              <X className="h-5 w-5" />
            </button>
          </div>
          <div className="min-h-0 flex-1 overflow-y-auto p-5">
            {section === 'general' && <GeneralPanel />}
            {section === 'accounts' && <AccountsPanel />}
            {section === 'labels' && <LabelsPanel />}
            {section === 'calendar' && <CalendarPanel />}
            {section === 'filters' && <FiltersPanel />}
            {section === 'about' && <AboutPanel />}
          </div>
        </div>
      </div>
    </div>
  )
}

function GeneralPanel(): JSX.Element {
  const notifyOn = useMailStore((s) => s.notifyOn)
  const setNotifyOn = useMailStore((s) => s.setNotifyOn)
  const mailLayout = useMailStore((s) => s.mailLayout)
  const setMailLayout = useMailStore((s) => s.setMailLayout)
  const darkMode = useMailStore((s) => s.darkMode)
  const setDarkMode = useMailStore((s) => s.setDarkMode)
  const [undo, setUndo] = useState(() => Number(localStorage.getItem('nmc.undoDelay') ?? '5'))
  const [reminder, setReminder] = useState(() =>
    Number(localStorage.getItem('nmc.reminderLead') ?? '15')
  )

  return (
    <div className="max-w-md space-y-4">
      <label className="flex items-center gap-2 text-sm">
        <input
          type="checkbox"
          checked={notifyOn}
          onChange={(e) => setNotifyOn(e.target.checked)}
        />
        Benachrichtigungston + Desktop-Benachrichtigung bei neuen Mails
      </label>

      <label className="flex items-center gap-2 text-sm">
        <input type="checkbox" checked={darkMode} onChange={(e) => setDarkMode(e.target.checked)} />
        Dunkler Modus
      </label>

      <label className="block">
        <span className="text-sm text-gray-600">Mail-Layout</span>
        <select
          className="mt-1 w-full rounded border px-3 py-2 text-sm"
          value={mailLayout}
          onChange={(e) => setMailLayout(e.target.value as 'right' | 'bottom')}
        >
          <option value="right">Vorschau rechts (Spalten)</option>
          <option value="bottom">Vorschau unten (Liste oben)</option>
        </select>
      </label>

      <label className="block">
        <span className="text-sm text-gray-600">Versand rückgängig — Verzögerung</span>
        <select
          className="mt-1 w-full rounded border px-3 py-2 text-sm"
          value={undo}
          onChange={(e) => {
            setUndo(Number(e.target.value))
            localStorage.setItem('nmc.undoDelay', e.target.value)
          }}
        >
          {UNDO_OPTIONS.map((v) => (
            <option key={v} value={v}>
              {v === 0 ? 'Aus (sofort senden)' : `${v} Sekunden`}
            </option>
          ))}
        </select>
      </label>

      <label className="block">
        <span className="text-sm text-gray-600">Termin-Erinnerung</span>
        <select
          className="mt-1 w-full rounded border px-3 py-2 text-sm"
          value={reminder}
          onChange={(e) => {
            setReminder(Number(e.target.value))
            localStorage.setItem('nmc.reminderLead', e.target.value)
          }}
        >
          {REMINDER_OPTIONS.map((v) => (
            <option key={v} value={v}>
              {v === 0 ? 'Aus' : v === 60 ? '1 Stunde vorher' : `${v} Minuten vorher`}
            </option>
          ))}
        </select>
      </label>
    </div>
  )
}

function AccountsPanel(): JSX.Element {
  const accounts = useMailStore((s) => s.accounts)
  const removeAccount = useMailStore((s) => s.removeAccount)
  const [editing, setEditing] = useState<Account | null>(null)
  const [adding, setAdding] = useState(false)

  return (
    <div className="space-y-3">
      <div className="divide-y rounded border">
        {accounts.length === 0 && (
          <div className="px-3 py-2 text-sm text-gray-400">Noch kein Konto.</div>
        )}
        {accounts.map((a, i) => (
          <div key={a.id} className="flex items-center gap-2 px-3 py-2 text-sm">
            <span
              className="h-2.5 w-2.5 shrink-0 rounded-full"
              style={{ backgroundColor: colorForAccount(a, i) }}
            />
            <div className="min-w-0 flex-1">
              <div className="truncate font-medium">{a.name}</div>
              <div className="truncate text-xs text-gray-500">{a.email}</div>
            </div>
            <button
              onClick={() => setEditing(a)}
              className="rounded border px-2 py-1 text-xs hover:bg-gray-50"
            >
              Bearbeiten
            </button>
            <button
              onClick={() => {
                if (confirm(`Konto „${a.email}" entfernen?`)) removeAccount(a.id)
              }}
              className="rounded p-1 text-red-500 hover:bg-red-50"
              title="Entfernen"
            >
              <Trash2 className="h-4 w-4" />
            </button>
          </div>
        ))}
      </div>
      <button
        onClick={() => setAdding(true)}
        className="flex items-center gap-1.5 rounded bg-brand px-4 py-2 text-sm text-white hover:bg-brand-dark"
      >
        <Plus className="h-4 w-4" />
        Konto hinzufügen
      </button>

      {editing && <AccountSettings account={editing} onClose={() => setEditing(null)} />}
      {adding && <AccountSetup onClose={() => setAdding(false)} />}
    </div>
  )
}

function LabelsPanel(): JSX.Element {
  const labels = useMailStore((s) => s.labels)
  const addLabel = useMailStore((s) => s.addLabel)
  const removeLabel = useMailStore((s) => s.removeLabel)
  const [name, setName] = useState('')
  const [color, setColor] = useState(ACCOUNT_PALETTE[0])

  return (
    <div className="max-w-md space-y-4">
      <div className="divide-y rounded border">
        {labels.length === 0 && (
          <div className="px-3 py-2 text-sm text-gray-400">Noch keine Etiketten.</div>
        )}
        {labels.map((l) => (
          <div key={l.id} className="flex items-center gap-2 px-3 py-2 text-sm">
            <span className="h-3.5 w-3.5 shrink-0 rounded-full" style={{ backgroundColor: l.color }} />
            <span className="min-w-0 flex-1 truncate">{l.name}</span>
            <button
              onClick={() => removeLabel(l.id)}
              className="rounded p-1 text-gray-400 hover:bg-red-50 hover:text-red-600"
              title="Löschen"
            >
              <Trash2 className="h-4 w-4" />
            </button>
          </div>
        ))}
      </div>

      <div className="rounded border p-3">
        <span className="text-sm font-medium text-gray-700">Neues Etikett</span>
        <input
          className="mt-2 w-full rounded border px-3 py-2 text-sm"
          placeholder="Name (z. B. Wichtig, Rechnung)"
          value={name}
          onChange={(e) => setName(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === 'Enter' && name.trim()) {
              addLabel(name.trim(), color)
              setName('')
            }
          }}
        />
        <div className="mt-2 flex flex-wrap gap-2">
          {ACCOUNT_PALETTE.map((c) => (
            <button
              key={c}
              onClick={() => setColor(c)}
              style={{ backgroundColor: c }}
              className="flex h-6 w-6 items-center justify-center rounded-full"
            >
              {color === c && <Check className="h-3.5 w-3.5 text-white" />}
            </button>
          ))}
        </div>
        <div className="mt-3 flex justify-end">
          <button
            onClick={() => {
              if (name.trim()) {
                addLabel(name.trim(), color)
                setName('')
              }
            }}
            disabled={!name.trim()}
            className="rounded bg-brand px-4 py-2 text-sm text-white disabled:opacity-50"
          >
            Hinzufügen
          </button>
        </div>
      </div>
    </div>
  )
}

function CalendarPanel(): JSX.Element {
  const [config, setConfig] = useState<CalendarConfig | null>(null)
  const [showSetup, setShowSetup] = useState(false)
  const loadContacts = useMailStore((s) => s.loadContacts)

  function refresh(): void {
    window.api.calendar.get().then((res) => res.ok && setConfig(res.data))
  }
  useEffect(refresh, [])

  return (
    <div className="max-w-md space-y-4">
      <p className="text-sm text-gray-600">
        Eine CalDAV/CardDAV-Verbindung (z. B. Nextcloud) wird für Kalender <em>und</em> Kontakte
        genutzt.
      </p>
      <div className="rounded border p-3 text-sm">
        {config ? (
          <>
            <div className="font-medium">Verbunden</div>
            <div className="truncate text-gray-500">{config.serverUrl}</div>
            <div className="text-gray-500">Benutzer: {config.user}</div>
          </>
        ) : (
          <div className="text-gray-400">Nicht verbunden.</div>
        )}
      </div>
      <div className="flex gap-2">
        <button
          onClick={() => setShowSetup(true)}
          className="rounded bg-brand px-4 py-2 text-sm text-white hover:bg-brand-dark"
        >
          {config ? 'Ändern' : 'Verbinden'}
        </button>
        {config && (
          <button
            onClick={async () => {
              await window.api.calendar.clear()
              refresh()
            }}
            className="rounded border px-4 py-2 text-sm text-red-600 hover:bg-red-50"
          >
            Trennen
          </button>
        )}
      </div>

      {showSetup && (
        <CalDavSetup
          initial={config ?? undefined}
          onClose={() => setShowSetup(false)}
          onSaved={() => {
            refresh()
            loadContacts()
          }}
        />
      )}
    </div>
  )
}

const SIEVE_TEMPLATE = `require ["fileinto"];

# Beispiel: Mails von einem Absender in einen Ordner einsortieren
if header :contains "from" "newsletter@example.com" {
  fileinto "INBOX/Newsletter";
}
`

function FiltersPanel(): JSX.Element {
  const accounts = useMailStore((s) => s.accounts)
  const [accountId, setAccountId] = useState(() => accounts[0]?.id ?? '')
  const [scripts, setScripts] = useState<SieveScript[]>([])
  const [selected, setSelected] = useState<string | null>(null)
  const [body, setBody] = useState('')
  const [dirty, setDirty] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [status, setStatus] = useState('')
  const [naming, setNaming] = useState(false)
  const [newName, setNewName] = useState('')

  async function loadScripts(id: string): Promise<void> {
    setError('')
    setStatus('')
    setBusy(true)
    const res = await window.api.sieve.list(id)
    setBusy(false)
    if (!res.ok) {
      setScripts([])
      setSelected(null)
      setBody('')
      setError(res.error)
      return
    }
    setScripts(res.data)
    const active = res.data.find((s) => s.active) ?? res.data[0]
    if (active) await openScript(id, active.name)
    else {
      setSelected(null)
      setBody('')
    }
  }

  async function openScript(id: string, name: string): Promise<void> {
    setError('')
    setStatus('')
    setBusy(true)
    const res = await window.api.sieve.get(id, name)
    setBusy(false)
    if (!res.ok) {
      setError(res.error)
      return
    }
    setSelected(name)
    setBody(res.data)
    setDirty(false)
  }

  const active = scripts.find((s) => s.active)?.name ?? null

  async function save(): Promise<void> {
    if (!selected) return
    setBusy(true)
    setError('')
    const res = await window.api.sieve.put(accountId, selected, body)
    setBusy(false)
    if (!res.ok) {
      setError(res.error)
      return
    }
    setDirty(false)
    setStatus(`„${selected}" gespeichert.`)
    void loadScripts(accountId)
  }

  async function activate(name: string): Promise<void> {
    setBusy(true)
    setError('')
    const res = await window.api.sieve.setActive(accountId, name)
    setBusy(false)
    if (!res.ok) {
      setError(res.error)
      return
    }
    setStatus(`„${name}" ist jetzt aktiv.`)
    void loadScripts(accountId)
  }

  async function remove(name: string): Promise<void> {
    if (!confirm(`Skript „${name}" löschen?`)) return
    setBusy(true)
    setError('')
    const res = await window.api.sieve.delete(accountId, name)
    setBusy(false)
    if (!res.ok) {
      setError(res.error)
      return
    }
    setStatus(`„${name}" gelöscht.`)
    void loadScripts(accountId)
  }

  function confirmNewScript(): void {
    const name = newName.trim()
    if (!name) return
    if (scripts.some((s) => s.name === name)) {
      setError('Ein Skript mit diesem Namen existiert bereits.')
      return
    }
    setSelected(name)
    setBody(SIEVE_TEMPLATE)
    setDirty(true)
    setError('')
    setStatus('Neues Skript — zum Anlegen speichern.')
    setNaming(false)
    setNewName('')
  }

  return (
    <div className="space-y-4">
      <p className="text-sm text-gray-600">
        Serverseitige Filter (Sieve) werden direkt auf dem Mailserver ausgeführt (auch wenn der
        Client geschlossen ist). Verbindung über ManageSieve (Port 4190, STARTTLS).
      </p>

      <label className="block max-w-md">
        <span className="text-sm text-gray-600">Konto</span>
        <select
          className="mt-1 w-full rounded border px-3 py-2 text-sm"
          value={accountId}
          onChange={(e) => {
            setAccountId(e.target.value)
            setScripts([])
            setSelected(null)
            setBody('')
            setStatus('')
            setError('')
          }}
        >
          {accounts.length === 0 && <option value="">Kein Konto</option>}
          {accounts.map((a) => (
            <option key={a.id} value={a.id}>
              {a.name} ({a.email})
            </option>
          ))}
        </select>
      </label>

      <div className="flex flex-wrap items-center gap-2">
        <button
          onClick={() => void loadScripts(accountId)}
          disabled={!accountId || busy}
          className="rounded bg-brand px-4 py-2 text-sm text-white hover:bg-brand-dark disabled:opacity-50"
        >
          {busy ? 'Lädt…' : 'Verbinden / Laden'}
        </button>
        <button
          onClick={() => {
            setNaming(true)
            setNewName('')
            setError('')
          }}
          disabled={!accountId}
          className="flex items-center gap-1.5 rounded border px-3 py-2 text-sm hover:bg-gray-50 disabled:opacity-50"
        >
          <Plus className="h-4 w-4" />
          Neues Skript
        </button>
      </div>

      {naming && (
        <div className="flex items-center gap-2">
          <input
            autoFocus
            className="flex-1 rounded border px-3 py-2 text-sm"
            placeholder="Name des neuen Skripts"
            value={newName}
            onChange={(e) => setNewName(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter') confirmNewScript()
              if (e.key === 'Escape') {
                setNaming(false)
                setNewName('')
              }
            }}
          />
          <button
            onClick={confirmNewScript}
            disabled={!newName.trim()}
            className="rounded bg-brand px-4 py-2 text-sm text-white hover:bg-brand-dark disabled:opacity-50"
          >
            Anlegen
          </button>
          <button
            onClick={() => {
              setNaming(false)
              setNewName('')
            }}
            className="rounded border px-3 py-2 text-sm hover:bg-gray-50"
          >
            Abbrechen
          </button>
        </div>
      )}

      {error && <div className="rounded bg-red-50 px-3 py-2 text-sm text-red-700">{error}</div>}
      {status && !error && (
        <div className="rounded bg-green-50 px-3 py-2 text-sm text-green-700">{status}</div>
      )}

      {scripts.length > 0 && (
        <div className="divide-y rounded border">
          {scripts.map((s) => (
            <div
              key={s.name}
              className={`flex items-center gap-2 px-3 py-2 text-sm ${
                selected === s.name ? 'bg-brand/10' : ''
              }`}
            >
              <button
                onClick={() => void openScript(accountId, s.name)}
                className="min-w-0 flex-1 truncate text-left hover:underline"
              >
                {s.name}
              </button>
              {s.active ? (
                <span className="rounded-full bg-green-100 px-2 py-0.5 text-xs text-green-700">
                  aktiv
                </span>
              ) : (
                <button
                  onClick={() => void activate(s.name)}
                  className="rounded border px-2 py-0.5 text-xs hover:bg-gray-50"
                >
                  Aktiv setzen
                </button>
              )}
              <button
                onClick={() => void remove(s.name)}
                className="rounded p-1 text-red-500 hover:bg-red-50"
                title="Löschen"
              >
                <Trash2 className="h-4 w-4" />
              </button>
            </div>
          ))}
        </div>
      )}

      {selected !== null && (
        <div className="space-y-2">
          <div className="flex items-center justify-between">
            <span className="text-sm font-medium">
              {selected}
              {active === selected && <span className="ml-2 text-xs text-green-700">(aktiv)</span>}
            </span>
            {dirty && <span className="text-xs text-amber-600">ungespeichert</span>}
          </div>
          <textarea
            className="h-64 w-full rounded border px-3 py-2 font-mono text-xs"
            spellCheck={false}
            value={body}
            onChange={(e) => {
              setBody(e.target.value)
              setDirty(true)
            }}
          />
          <div className="flex gap-2">
            <button
              onClick={() => void save()}
              disabled={busy || !dirty}
              className="rounded bg-brand px-4 py-2 text-sm text-white hover:bg-brand-dark disabled:opacity-50"
            >
              Speichern
            </button>
            {active !== selected && (
              <button
                onClick={() => void activate(selected)}
                disabled={busy || dirty}
                className="rounded border px-4 py-2 text-sm hover:bg-gray-50 disabled:opacity-50"
                title={dirty ? 'Erst speichern' : undefined}
              >
                Aktiv setzen
              </button>
            )}
          </div>
        </div>
      )}
    </div>
  )
}

/** Minimal renderer for the bundled CHANGELOG.md (headings, sub-headings, bullets). */
function ChangelogView({ text }: { text: string }): JSX.Element {
  // Drop the top-level "# Changelog" title + intro; start at the first version.
  const start = text.indexOf('## [')
  const body = start >= 0 ? text.slice(start) : text
  const lines = body.split('\n')
  const out: JSX.Element[] = []
  let bullets: string[] = []

  const flush = (key: string): void => {
    if (bullets.length) {
      out.push(
        <ul key={key} className="ml-4 list-disc space-y-1 text-sm text-gray-600">
          {bullets.map((b, i) => (
            <li key={i}>{b}</li>
          ))}
        </ul>
      )
      bullets = []
    }
  }

  lines.forEach((raw, idx) => {
    const line = raw.trimEnd()
    if (line.startsWith('## ')) {
      flush(`b${idx}`)
      out.push(
        <h3 key={idx} className="mt-4 text-sm font-semibold first:mt-0">
          {line.replace(/^##\s*/, '').replace(/[[\]]/g, '')}
        </h3>
      )
    } else if (line.startsWith('### ')) {
      flush(`b${idx}`)
      out.push(
        <div key={idx} className="mt-2 text-xs font-semibold uppercase tracking-wide text-gray-400">
          {line.replace(/^###\s*/, '')}
        </div>
      )
    } else if (line.startsWith('- ')) {
      // Strip markdown bold markers for plain rendering.
      bullets.push(line.replace(/^-\s*/, '').replace(/\*\*/g, ''))
    } else if (line === '---' || line === '') {
      flush(`b${idx}`)
    }
  })
  flush('last')
  return <div className="space-y-1">{out}</div>
}

function AboutPanel(): JSX.Element {
  return (
    <div className="max-w-lg space-y-5">
      <div className="flex items-baseline gap-2">
        <span className="text-xl font-semibold">N-MailClient</span>
        <span className="rounded bg-gray-100 px-2 py-0.5 text-sm text-gray-600">
          v{__APP_VERSION__}
        </span>
      </div>
      <p className="text-sm text-gray-600">
        Desktop-E-Mail-Client für beliebige IMAP/SMTP-Konten mit Kalender (CalDAV) und Kontakten
        (CardDAV). Updates werden automatisch aus den GitHub-Releases bezogen.
      </p>
      <UpdateChecker />

      <div>
        <h2 className="mb-2 text-sm font-semibold text-gray-700">Änderungsverlauf</h2>
        <div className="max-h-[40vh] overflow-y-auto rounded border p-4">
          <ChangelogView text={__CHANGELOG__} />
        </div>
      </div>
    </div>
  )
}

function updateStatusText(s: UpdateStatus): string {
  switch (s.state) {
    case 'checking':
      return 'Suche nach Updates…'
    case 'available':
      return `Version ${s.version} gefunden — wird heruntergeladen…`
    case 'downloading':
      return `Lädt Update… ${s.percent}%`
    case 'downloaded':
      return `Version ${s.version} bereit — wird beim Neustart installiert.`
    case 'none':
      return 'Du verwendest die neueste Version.'
    case 'error':
      return s.message
  }
}

function UpdateChecker(): JSX.Element {
  const [status, setStatus] = useState<UpdateStatus | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    window.api.update.status().then((res) => res.ok && res.data && setStatus(res.data))
    return window.api.update.onStatus(setStatus)
  }, [])

  async function check(): Promise<void> {
    setBusy(true)
    await window.api.update.check()
    setBusy(false)
  }

  return (
    <div className="flex items-center gap-3">
      <button
        onClick={check}
        disabled={busy || status?.state === 'checking'}
        className="rounded border px-4 py-2 text-sm hover:bg-gray-50 disabled:opacity-50"
      >
        Nach Updates suchen
      </button>
      {status && (
        <span
          className={`text-sm ${status.state === 'error' ? 'text-red-600' : 'text-gray-600'}`}
        >
          {updateStatusText(status)}
        </span>
      )}
      {status?.state === 'downloaded' && (
        <button
          onClick={() => window.api.update.install()}
          className="rounded bg-brand px-3 py-2 text-sm text-white hover:bg-brand-dark"
        >
          Neu starten &amp; installieren
        </button>
      )}
    </div>
  )
}
