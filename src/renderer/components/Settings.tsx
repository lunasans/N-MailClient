import { useEffect, useState } from 'react'
import {
  Bell,
  Calendar,
  Check,
  Copy,
  FileCode,
  Info,
  KeyRound,
  Languages,
  Plus,
  Tag,
  Trash2,
  User,
  X
} from 'lucide-react'
import type {
  Account,
  CalendarConfig,
  PgpKeyInfo,
  SieveScript,
  UpdateStatus
} from '@shared/index'
import { useMailStore } from '../store/useMailStore'
import { ACCOUNT_PALETTE, colorForAccount } from '../lib/accountColor'
import AccountSettings from './AccountSettings'
import AccountSetup from './AccountSetup'
import CalDavSetup from './CalDavSetup'

type Section =
  | 'general'
  | 'accounts'
  | 'labels'
  | 'calendar'
  | 'filters'
  | 'pgp'
  | 'translate'
  | 'about'

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
    { id: 'pgp', label: 'Verschlüsselung (PGP)', icon: <KeyRound className="h-4 w-4" /> },
    { id: 'translate', label: 'Übersetzung', icon: <Languages className="h-4 w-4" /> },
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
            {section === 'pgp' && <PgpPanel />}
            {section === 'translate' && <TranslatePanel />}
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
  const [requestDsn, setRequestDsn] = useState(
    () => localStorage.getItem('nmc.requestDsn') === '1'
  )
  const [confirmDelete, setConfirmDelete] = useState(
    () => localStorage.getItem('nmc.confirmDelete') !== '0'
  )
  const [autostart, setAutostart] = useState(false)
  const [notifyInboxOnly, setNotifyInboxOnly] = useState(
    () => localStorage.getItem('nmc.notifyInboxOnly') === '1'
  )
  const [quietFrom, setQuietFrom] = useState(() => localStorage.getItem('nmc.quietFrom') ?? '')
  const [quietTo, setQuietTo] = useState(() => localStorage.getItem('nmc.quietTo') ?? '')

  useEffect(() => {
    window.api.app.getAutostart().then((res) => res.ok && setAutostart(res.data))
  }, [])

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
        <input
          type="checkbox"
          checked={notifyInboxOnly}
          onChange={(e) => {
            setNotifyInboxOnly(e.target.checked)
            localStorage.setItem('nmc.notifyInboxOnly', e.target.checked ? '1' : '0')
          }}
        />
        Nur für den Posteingang benachrichtigen
      </label>

      <div className="flex items-center gap-2 text-sm">
        <span className="text-gray-600">Ruhezeiten (keine Benachrichtigung):</span>
        <input
          type="time"
          className="rounded border px-2 py-1"
          value={quietFrom}
          onChange={(e) => {
            setQuietFrom(e.target.value)
            localStorage.setItem('nmc.quietFrom', e.target.value)
          }}
        />
        <span className="text-gray-400">bis</span>
        <input
          type="time"
          className="rounded border px-2 py-1"
          value={quietTo}
          onChange={(e) => {
            setQuietTo(e.target.value)
            localStorage.setItem('nmc.quietTo', e.target.value)
          }}
        />
      </div>

      <label className="flex items-center gap-2 text-sm">
        <input type="checkbox" checked={darkMode} onChange={(e) => setDarkMode(e.target.checked)} />
        Dunkler Modus
      </label>

      <label className="flex items-center gap-2 text-sm">
        <input
          type="checkbox"
          checked={autostart}
          onChange={(e) => {
            setAutostart(e.target.checked)
            window.api.app.setAutostart(e.target.checked)
          }}
        />
        Beim Systemstart öffnen
      </label>

      <label className="flex items-center gap-2 text-sm">
        <input
          type="checkbox"
          checked={requestDsn}
          onChange={(e) => {
            setRequestDsn(e.target.checked)
            localStorage.setItem('nmc.requestDsn', e.target.checked ? '1' : '0')
          }}
        />
        Zustellbestätigung anfordern (DSN) beim Senden
      </label>

      <label className="flex items-center gap-2 text-sm">
        <input
          type="checkbox"
          checked={confirmDelete}
          onChange={(e) => {
            setConfirmDelete(e.target.checked)
            localStorage.setItem('nmc.confirmDelete', e.target.checked ? '1' : '0')
          }}
        />
        Beim Löschen von Mails nachfragen
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

      <BackupSection />
    </div>
  )
}

function BackupSection(): JSX.Element {
  const [status, setStatus] = useState('')
  const [error, setError] = useState('')

  async function doExport(): Promise<void> {
    setError('')
    setStatus('')
    const prefs: Record<string, string> = {}
    for (let i = 0; i < localStorage.length; i++) {
      const k = localStorage.key(i)
      if (k) prefs[k] = localStorage.getItem(k) ?? ''
    }
    const res = await window.api.settings.export(prefs)
    if (!res.ok) setError(res.error)
    else if (!res.data.canceled) setStatus('Sicherung gespeichert.')
  }

  async function doImport(): Promise<void> {
    setError('')
    setStatus('')
    if (
      !confirm(
        'Aktuelle Konten und Einstellungen durch die Sicherung ersetzen? Die App wird danach neu geladen.'
      )
    )
      return
    const res = await window.api.settings.import()
    if (!res.ok) {
      setError(res.error)
      return
    }
    if (res.data.canceled) return
    localStorage.clear()
    for (const [k, v] of Object.entries(res.data.prefs ?? {})) localStorage.setItem(k, v)
    location.reload()
  }

  return (
    <div className="rounded border p-3">
      <span className="text-sm font-medium text-gray-700">Sicherung</span>
      <p className="mt-0.5 text-xs text-gray-500">
        Exportiert Konten, Etiketten, Kalender/Kontakte-Verbindung, PGP-Schlüssel und alle
        Einstellungen in eine Datei. Passwörter sind gerätegebunden verschlüsselt — auf einem
        anderen Gerät werden Konten importiert, Passwörter müssen aber neu eingegeben werden.
      </p>
      <div className="mt-2 flex gap-2">
        <button onClick={doExport} className="rounded border px-4 py-2 text-sm hover:bg-gray-50">
          Exportieren…
        </button>
        <button onClick={doImport} className="rounded border px-4 py-2 text-sm hover:bg-gray-50">
          Importieren…
        </button>
      </div>
      {error && <div className="mt-2 text-sm text-red-600">{error}</div>}
      {status && !error && <div className="mt-2 text-sm text-green-700">{status}</div>}
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

      {accountId && <SieveRuleBuilder accountId={accountId} />}

      <div className="border-t pt-4">
        <h3 className="text-sm font-semibold text-gray-700">Erweitert: Roh-Skripte</h3>
        <p className="text-xs text-gray-500">
          Direkter Zugriff auf alle Sieve-Skripte des Servers.
        </p>
      </div>

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

function PgpPanel(): JSX.Element {
  const [keys, setKeys] = useState<PgpKeyInfo[]>([])
  const [importText, setImportText] = useState('')
  const [importPass, setImportPass] = useState('')
  const [genName, setGenName] = useState('')
  const [genEmail, setGenEmail] = useState('')
  const [genPass, setGenPass] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [status, setStatus] = useState('')
  const [shown, setShown] = useState<{ title: string; armored: string } | null>(null)
  const [copied, setCopied] = useState(false)

  async function load(): Promise<void> {
    const res = await window.api.pgp.list()
    if (res.ok) setKeys(res.data)
  }
  useEffect(() => {
    void load()
  }, [])

  function reset(): void {
    setError('')
    setStatus('')
  }

  async function doImport(): Promise<void> {
    reset()
    const text = importText.trim()
    if (!text) return
    const isPrivate = /BEGIN PGP PRIVATE KEY/.test(text)
    setBusy(true)
    const res = isPrivate
      ? await window.api.pgp.importPrivate(text, importPass)
      : await window.api.pgp.importPublic(text)
    setBusy(false)
    if (!res.ok) {
      setError(res.error)
      return
    }
    setStatus(`${isPrivate ? 'Privater' : 'Öffentlicher'} Schlüssel importiert.`)
    setImportText('')
    setImportPass('')
    void load()
  }

  async function doGenerate(): Promise<void> {
    reset()
    if (!genName.trim() || !genEmail.trim()) {
      setError('Name und E-Mail sind erforderlich.')
      return
    }
    setBusy(true)
    const res = await window.api.pgp.generate({
      name: genName.trim(),
      email: genEmail.trim(),
      passphrase: genPass || undefined
    })
    setBusy(false)
    if (!res.ok) {
      setError(res.error)
      return
    }
    setStatus('Neues Schlüsselpaar erzeugt.')
    setGenName('')
    setGenEmail('')
    setGenPass('')
    void load()
  }

  async function showPublic(k: PgpKeyInfo): Promise<void> {
    reset()
    const res = await window.api.pgp.exportPublic(k.id)
    if (res.ok) setShown({ title: `Öffentlicher Schlüssel — ${k.userIds[0] ?? k.fingerprint}`, armored: res.data })
    else setError(res.error)
  }

  async function showPrivate(k: PgpKeyInfo): Promise<void> {
    reset()
    if (!confirm('Privaten Schlüssel im Klartext anzeigen? Nur für Backup an einem sicheren Ort.'))
      return
    const res = await window.api.pgp.exportPrivate(k.id)
    if (res.ok) setShown({ title: `Privater Schlüssel — ${k.userIds[0] ?? k.fingerprint}`, armored: res.data })
    else setError(res.error)
  }

  async function remove(k: PgpKeyInfo): Promise<void> {
    if (!confirm(`Schlüssel „${k.userIds[0] ?? k.fingerprint}" löschen?`)) return
    reset()
    const res = await window.api.pgp.remove(k.id)
    if (res.ok) void load()
    else setError(res.error)
  }

  return (
    <div className="space-y-5">
      <p className="text-sm text-gray-600">
        PGP-Schlüssel für signierte und verschlüsselte E-Mails. Private Schlüssel werden
        verschlüsselt auf dem Gerät gespeichert (Windows DPAPI), niemals im Klartext.
      </p>

      {error && <div className="rounded bg-red-50 px-3 py-2 text-sm text-red-700">{error}</div>}
      {status && !error && (
        <div className="rounded bg-green-50 px-3 py-2 text-sm text-green-700">{status}</div>
      )}

      <div className="divide-y rounded border">
        {keys.length === 0 && (
          <div className="px-3 py-2 text-sm text-gray-400">Noch keine Schlüssel.</div>
        )}
        {keys.map((k) => (
          <div key={k.id} className="flex items-center gap-2 px-3 py-2 text-sm">
            <KeyRound className={`h-4 w-4 shrink-0 ${k.hasPrivate ? 'text-brand' : 'text-gray-400'}`} />
            <div className="min-w-0 flex-1">
              <div className="truncate font-medium">{k.userIds[0] ?? '(ohne UID)'}</div>
              <div className="truncate font-mono text-xs text-gray-400">{k.fingerprint}</div>
            </div>
            <span
              className={`shrink-0 rounded-full px-2 py-0.5 text-xs ${
                k.hasPrivate ? 'bg-brand/10 text-brand' : 'bg-gray-100 text-gray-500'
              }`}
            >
              {k.hasPrivate ? 'privat + öffentlich' : 'öffentlich'}
            </span>
            <button
              onClick={() => void showPublic(k)}
              className="shrink-0 rounded border px-2 py-1 text-xs hover:bg-gray-50"
            >
              Öffentl. exportieren
            </button>
            {k.hasPrivate && (
              <button
                onClick={() => void showPrivate(k)}
                className="shrink-0 rounded border px-2 py-1 text-xs hover:bg-gray-50"
              >
                Privat exportieren
              </button>
            )}
            <button
              onClick={() => void remove(k)}
              className="shrink-0 rounded p-1 text-red-500 hover:bg-red-50"
              title="Löschen"
            >
              <Trash2 className="h-4 w-4" />
            </button>
          </div>
        ))}
      </div>

      {shown && (
        <div className="rounded border p-3">
          <div className="mb-2 flex items-center justify-between">
            <span className="text-sm font-medium">{shown.title}</span>
            <div className="flex items-center gap-2">
              <button
                onClick={() => {
                  navigator.clipboard.writeText(shown.armored)
                  setCopied(true)
                  setTimeout(() => setCopied(false), 1500)
                }}
                className="flex items-center gap-1 rounded border px-2 py-1 text-xs hover:bg-gray-50"
              >
                {copied ? <Check className="h-3.5 w-3.5" /> : <Copy className="h-3.5 w-3.5" />}
                {copied ? 'Kopiert' : 'Kopieren'}
              </button>
              <button
                onClick={() => setShown(null)}
                className="rounded p-1 text-gray-400 hover:text-gray-700"
              >
                <X className="h-4 w-4" />
              </button>
            </div>
          </div>
          <textarea
            readOnly
            value={shown.armored}
            className="h-40 w-full rounded border bg-gray-50 px-2 py-1 font-mono text-xs"
          />
        </div>
      )}

      <div className="rounded border p-3">
        <span className="text-sm font-medium text-gray-700">Schlüssel importieren</span>
        <textarea
          className="mt-2 h-28 w-full rounded border px-3 py-2 font-mono text-xs"
          placeholder="-----BEGIN PGP PUBLIC KEY BLOCK----- … (oder PRIVATE KEY)"
          value={importText}
          onChange={(e) => setImportText(e.target.value)}
        />
        <div className="mt-2 flex items-center gap-2">
          <input
            type="password"
            className="flex-1 rounded border px-3 py-2 text-sm"
            placeholder="Passwort (nur bei privatem Schlüssel)"
            value={importPass}
            onChange={(e) => setImportPass(e.target.value)}
          />
          <button
            onClick={() => void doImport()}
            disabled={busy || !importText.trim()}
            className="rounded bg-brand px-4 py-2 text-sm text-white hover:bg-brand-dark disabled:opacity-50"
          >
            Importieren
          </button>
        </div>
      </div>

      <div className="rounded border p-3">
        <span className="text-sm font-medium text-gray-700">Neues Schlüsselpaar erzeugen</span>
        <div className="mt-2 grid grid-cols-2 gap-2">
          <input
            className="rounded border px-3 py-2 text-sm"
            placeholder="Name"
            value={genName}
            onChange={(e) => setGenName(e.target.value)}
          />
          <input
            className="rounded border px-3 py-2 text-sm"
            placeholder="E-Mail"
            value={genEmail}
            onChange={(e) => setGenEmail(e.target.value)}
          />
        </div>
        <div className="mt-2 flex items-center gap-2">
          <input
            type="password"
            className="flex-1 rounded border px-3 py-2 text-sm"
            placeholder="Passwort für Export (optional)"
            value={genPass}
            onChange={(e) => setGenPass(e.target.value)}
          />
          <button
            onClick={() => void doGenerate()}
            disabled={busy || !genName.trim() || !genEmail.trim()}
            className="flex items-center gap-1.5 rounded bg-brand px-4 py-2 text-sm text-white hover:bg-brand-dark disabled:opacity-50"
          >
            <Plus className="h-4 w-4" />
            Erzeugen
          </button>
        </div>
      </div>
    </div>
  )
}

interface SieveRule {
  id: string
  field: 'from' | 'to' | 'cc' | 'subject'
  op: 'contains' | 'is' | 'matches'
  value: string
  /** Action: move to folder, discard, or redirect (forward) to an address. */
  action?: 'move' | 'discard' | 'redirect'
  folder: string
  redirectTo?: string
  markRead: boolean
}

const FIELD_LABELS: Record<SieveRule['field'], string> = {
  from: 'Absender',
  to: 'Empfänger',
  cc: 'Kopie (Cc)',
  subject: 'Betreff'
}
const OP_LABELS: Record<SieveRule['op'], string> = {
  contains: 'enthält',
  is: 'ist genau',
  matches: 'Muster (* ?)'
}
const RULES_SCRIPT = 'nmailclient'

function escSieve(s: string): string {
  return s.replace(/\\/g, '\\\\').replace(/"/g, '\\"')
}

interface VacationConfig {
  enabled: boolean
  days: number
  subject: string
  message: string
}
const DEFAULT_VACATION: VacationConfig = { enabled: false, days: 1, subject: 'Abwesend', message: '' }

function ruleAction(r: SieveRule): string {
  return r.action ?? 'move'
}
function ruleIsValid(r: SieveRule): boolean {
  if (!r.value.trim()) return false
  const a = ruleAction(r)
  if (a === 'move') return !!r.folder
  if (a === 'redirect') return !!r.redirectTo?.trim()
  return true // discard
}

function buildSieveScript(rules: SieveRule[], vacation: VacationConfig): string {
  const valid = rules.filter(ruleIsValid)
  const needFlags = valid.some((r) => r.markRead)
  const needFileinto = valid.some((r) => ruleAction(r) === 'move')
  const useVacation = vacation.enabled && vacation.message.trim() !== ''
  const reqs = [
    ...(needFileinto ? ['fileinto'] : []),
    ...(needFlags ? ['imap4flags'] : []),
    ...(useVacation ? ['vacation'] : [])
  ]
  const out = [
    reqs.length ? `require [${reqs.map((r) => `"${r}"`).join(', ')}];` : '',
    '# N-MailClient — automatisch generiert, nicht von Hand bearbeiten',
    ''
  ]
  if (useVacation) {
    out.push(
      `vacation :days ${Math.max(1, vacation.days)} :subject "${escSieve(vacation.subject.trim() || 'Abwesend')}" "${escSieve(vacation.message.trim())}";`,
      ''
    )
  }
  for (const r of valid) {
    const action = ruleAction(r)
    out.push(`if header :${r.op} "${r.field}" "${escSieve(r.value.trim())}" {`)
    if (r.markRead) out.push('  setflag "\\\\Seen";')
    if (action === 'move') out.push(`  fileinto "${escSieve(r.folder)}";`)
    else if (action === 'redirect') out.push(`  redirect "${escSieve(r.redirectTo!.trim())}";`)
    else out.push('  discard;')
    out.push('}')
  }
  return out.join('\n') + '\n'
}

function SieveRuleBuilder({ accountId }: { accountId: string }): JSX.Element {
  const foldersByAccount = useMailStore((s) => s.foldersByAccount)
  const ensureFolders = useMailStore((s) => s.ensureFolders)
  const [rules, setRules] = useState<SieveRule[]>([])
  const [field, setField] = useState<SieveRule['field']>('from')
  const [op, setOp] = useState<SieveRule['op']>('contains')
  const [value, setValue] = useState('')
  const [folder, setFolder] = useState('')
  const [markRead, setMarkRead] = useState(false)
  const [action, setAction] = useState<'move' | 'discard' | 'redirect'>('move')
  const [redirectTo, setRedirectTo] = useState('')
  const [vacation, setVacation] = useState<VacationConfig>(DEFAULT_VACATION)
  const [busy, setBusy] = useState(false)
  const [msg, setMsg] = useState('')
  const [err, setErr] = useState('')

  const storageKey = `nmc.sieveRules.${accountId}`
  const vacationKey = `nmc.vacation.${accountId}`
  const folders = (foldersByAccount[accountId] ?? []).filter((f) => f.selectable)

  useEffect(() => {
    ensureFolders(accountId)
    try {
      const v = localStorage.getItem(`nmc.vacation.${accountId}`)
      setVacation(v ? { ...DEFAULT_VACATION, ...(JSON.parse(v) as VacationConfig) } : DEFAULT_VACATION)
    } catch {
      setVacation(DEFAULT_VACATION)
    }
    try {
      const raw = localStorage.getItem(`nmc.sieveRules.${accountId}`)
      setRules(raw ? (JSON.parse(raw) as SieveRule[]) : [])
    } catch {
      setRules([])
    }
    setMsg('')
    setErr('')
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [accountId])

  function persist(next: SieveRule[]): void {
    setRules(next)
    localStorage.setItem(storageKey, JSON.stringify(next))
    setMsg('')
  }

  function addRule(): void {
    const candidate: SieveRule = {
      id: crypto.randomUUID(),
      field,
      op,
      value: value.trim(),
      action,
      folder,
      redirectTo: redirectTo.trim() || undefined,
      markRead
    }
    if (!ruleIsValid(candidate)) return
    persist([...rules, candidate])
    setValue('')
    setRedirectTo('')
    setMarkRead(false)
  }

  async function saveAndActivate(): Promise<void> {
    setBusy(true)
    setErr('')
    setMsg('')
    localStorage.setItem(vacationKey, JSON.stringify(vacation))
    const script = buildSieveScript(rules, vacation)
    const put = await window.api.sieve.put(accountId, RULES_SCRIPT, script)
    if (!put.ok) {
      setBusy(false)
      setErr(put.error)
      return
    }
    const act = await window.api.sieve.setActive(accountId, RULES_SCRIPT)
    setBusy(false)
    if (!act.ok) {
      setErr(act.error)
      return
    }
    setMsg('Regeln & Abwesenheitsnotiz gespeichert und aktiviert.')
  }

  return (
    <div className="rounded border p-3">
      <h3 className="text-sm font-semibold text-gray-700">Regel-Baukasten</h3>
      <p className="mt-0.5 text-xs text-gray-500">
        Erzeugt ein serverseitiges Sieve-Skript „{RULES_SCRIPT}". Beim Speichern wird es das
        aktive Skript — vorhandene eigene Skripte bleiben erhalten, aber inaktiv.
      </p>

      {err && <div className="mt-2 rounded bg-red-50 px-3 py-2 text-sm text-red-700">{err}</div>}
      {msg && !err && (
        <div className="mt-2 rounded bg-green-50 px-3 py-2 text-sm text-green-700">{msg}</div>
      )}

      {rules.length > 0 && (
        <div className="mt-2 divide-y rounded border">
          {rules.map((r) => (
            <div key={r.id} className="flex items-center gap-2 px-3 py-1.5 text-sm">
              <span className="min-w-0 flex-1">
                Wenn <strong>{FIELD_LABELS[r.field]}</strong> {OP_LABELS[r.op]}{' '}
                <span className="font-mono">„{r.value}"</span> →{' '}
                {ruleAction(r) === 'discard' ? (
                  <strong>verwerfen</strong>
                ) : ruleAction(r) === 'redirect' ? (
                  <>
                    weiterleiten an <strong>{r.redirectTo}</strong>
                  </>
                ) : (
                  <>
                    verschiebe nach <strong>{r.folder}</strong>
                  </>
                )}
                {r.markRead && ' (als gelesen)'}
              </span>
              <button
                onClick={() => persist(rules.filter((x) => x.id !== r.id))}
                className="shrink-0 rounded p-1 text-red-500 hover:bg-red-50"
                title="Regel entfernen"
              >
                <Trash2 className="h-4 w-4" />
              </button>
            </div>
          ))}
        </div>
      )}

      <div className="mt-2 flex flex-wrap items-center gap-2 text-sm">
        <span className="text-gray-500">Wenn</span>
        <select
          className="rounded border px-2 py-1.5"
          value={field}
          onChange={(e) => setField(e.target.value as SieveRule['field'])}
        >
          {(Object.keys(FIELD_LABELS) as SieveRule['field'][]).map((f) => (
            <option key={f} value={f}>
              {FIELD_LABELS[f]}
            </option>
          ))}
        </select>
        <select
          className="rounded border px-2 py-1.5"
          value={op}
          onChange={(e) => setOp(e.target.value as SieveRule['op'])}
        >
          {(Object.keys(OP_LABELS) as SieveRule['op'][]).map((o) => (
            <option key={o} value={o}>
              {OP_LABELS[o]}
            </option>
          ))}
        </select>
        <input
          className="min-w-[140px] flex-1 rounded border px-2 py-1.5"
          placeholder="Wert (z. B. newsletter@…)"
          value={value}
          onChange={(e) => setValue(e.target.value)}
        />
        <span className="text-gray-500">→</span>
        <select
          className="rounded border px-2 py-1.5"
          value={action}
          onChange={(e) => setAction(e.target.value as 'move' | 'discard' | 'redirect')}
        >
          <option value="move">verschieben nach</option>
          <option value="redirect">weiterleiten an</option>
          <option value="discard">verwerfen</option>
        </select>
        {action === 'move' && (
          <select
            className="rounded border px-2 py-1.5"
            value={folder}
            onChange={(e) => setFolder(e.target.value)}
          >
            <option value="">Ordner wählen…</option>
            {folders.map((f) => (
              <option key={f.path} value={f.path}>
                {f.path}
              </option>
            ))}
          </select>
        )}
        {action === 'redirect' && (
          <input
            className="min-w-[160px] rounded border px-2 py-1.5"
            placeholder="E-Mail-Adresse"
            value={redirectTo}
            onChange={(e) => setRedirectTo(e.target.value)}
          />
        )}
        <label className="flex items-center gap-1 text-gray-600">
          <input type="checkbox" checked={markRead} onChange={(e) => setMarkRead(e.target.checked)} />
          gelesen
        </label>
        <button
          onClick={addRule}
          disabled={
            !value.trim() ||
            (action === 'move' && !folder) ||
            (action === 'redirect' && !redirectTo.trim())
          }
          className="flex items-center gap-1 rounded border px-2 py-1.5 hover:bg-gray-50 disabled:opacity-50"
        >
          <Plus className="h-4 w-4" />
          Regel
        </button>
      </div>

      <div className="mt-4 border-t pt-3">
        <label className="flex items-center gap-2 text-sm font-medium text-gray-700">
          <input
            type="checkbox"
            checked={vacation.enabled}
            onChange={(e) => setVacation((v) => ({ ...v, enabled: e.target.checked }))}
          />
          Abwesenheitsnotiz (Auto-Responder)
        </label>
        {vacation.enabled && (
          <div className="mt-2 space-y-2">
            <div className="flex items-center gap-2 text-sm">
              <input
                className="flex-1 rounded border px-3 py-2"
                placeholder="Betreff (z. B. Abwesend)"
                value={vacation.subject}
                onChange={(e) => setVacation((v) => ({ ...v, subject: e.target.value }))}
              />
              <label className="flex items-center gap-1 text-gray-600">
                alle
                <input
                  type="number"
                  min={1}
                  className="w-16 rounded border px-2 py-2"
                  value={vacation.days}
                  onChange={(e) => setVacation((v) => ({ ...v, days: Number(e.target.value) || 1 }))}
                />
                Tage
              </label>
            </div>
            <textarea
              className="h-24 w-full rounded border px-3 py-2 text-sm"
              placeholder="Antworttext, z. B. Ich bin bis … nicht erreichbar."
              value={vacation.message}
              onChange={(e) => setVacation((v) => ({ ...v, message: e.target.value }))}
            />
          </div>
        )}
      </div>

      <div className="mt-3">
        <button
          onClick={() => void saveAndActivate()}
          disabled={busy}
          className="rounded bg-brand px-4 py-2 text-sm text-white hover:bg-brand-dark disabled:opacity-50"
        >
          {busy ? 'Speichere…' : 'Regeln & Abwesenheit speichern'}
        </button>
      </div>
    </div>
  )
}

function TranslatePanel(): JSX.Element {
  const [configured, setConfigured] = useState(false)
  const [url, setUrl] = useState('')
  const [target, setTarget] = useState('de')
  const [apiKey, setApiKey] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [status, setStatus] = useState('')

  useEffect(() => {
    window.api.translate.get().then((res) => {
      if (res.ok && res.data) {
        setUrl(res.data.url)
        setTarget(res.data.target)
        setConfigured(true)
      }
    })
  }, [])

  async function test(): Promise<void> {
    setBusy(true)
    setError('')
    setStatus('')
    const res = await window.api.translate.test(url.trim(), apiKey)
    setBusy(false)
    if (res.ok) setStatus('Verbindung erfolgreich.')
    else setError(res.error)
  }

  async function save(): Promise<void> {
    setBusy(true)
    setError('')
    setStatus('')
    const res = await window.api.translate.save(url.trim(), target.trim() || 'de', apiKey)
    setBusy(false)
    if (res.ok) {
      setConfigured(true)
      setStatus('Übersetzungsdienst gespeichert.')
    } else setError(res.error)
  }

  async function disconnect(): Promise<void> {
    await window.api.translate.clear()
    setConfigured(false)
    setUrl('')
    setApiKey('')
    setStatus('Verbindung getrennt.')
  }

  return (
    <div className="max-w-md space-y-4">
      <p className="text-sm text-gray-600">
        Übersetzt Mailtexte über eine eigene <strong>LibreTranslate</strong>-Instanz. Der Mailinhalt
        wird nur an diesen Server gesendet — wähle einen Server, dem du vertraust.
      </p>

      {error && <div className="rounded bg-red-50 px-3 py-2 text-sm text-red-700">{error}</div>}
      {status && !error && (
        <div className="rounded bg-green-50 px-3 py-2 text-sm text-green-700">{status}</div>
      )}

      <label className="block">
        <span className="text-sm text-gray-600">Server-URL</span>
        <input
          className="mt-1 w-full rounded border px-3 py-2 text-sm"
          placeholder="https://translate.example.com"
          value={url}
          onChange={(e) => setUrl(e.target.value)}
        />
      </label>

      <label className="block">
        <span className="text-sm text-gray-600">Zielsprache (Code)</span>
        <input
          className="mt-1 w-full rounded border px-3 py-2 text-sm"
          placeholder="de"
          value={target}
          onChange={(e) => setTarget(e.target.value)}
        />
      </label>

      <label className="block">
        <span className="text-sm text-gray-600">API-Key (optional)</span>
        <input
          type="password"
          className="mt-1 w-full rounded border px-3 py-2 text-sm"
          placeholder="nur falls der Server einen verlangt"
          value={apiKey}
          onChange={(e) => setApiKey(e.target.value)}
        />
      </label>

      <div className="flex gap-2">
        <button
          onClick={() => void test()}
          disabled={busy || !url.trim()}
          className="rounded border px-4 py-2 text-sm hover:bg-gray-50 disabled:opacity-50"
        >
          Verbindung testen
        </button>
        <button
          onClick={() => void save()}
          disabled={busy || !url.trim()}
          className="rounded bg-brand px-4 py-2 text-sm text-white hover:bg-brand-dark disabled:opacity-50"
        >
          Speichern
        </button>
        {configured && (
          <button
            onClick={() => void disconnect()}
            className="rounded border px-4 py-2 text-sm text-red-600 hover:bg-red-50"
          >
            Trennen
          </button>
        )}
      </div>
    </div>
  )
}
