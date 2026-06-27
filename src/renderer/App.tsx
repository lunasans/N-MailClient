import { useEffect, useState } from 'react'
import { Archive, CalendarDays, Mail, PenSquare, Settings, User, X } from 'lucide-react'
import { useMailStore } from './store/useMailStore'
import { playChime, showMailNotification } from './lib/notify'
import AccountSetup from './components/AccountSetup'
import ArchiveView from './components/ArchiveView'
import CalendarView from './components/CalendarView'
import Composer from './components/Composer'
import ContactsView from './components/ContactsView'
import MailList from './components/MailList'
import MailView from './components/MailView'
import SettingsModal from './components/Settings'
import Sidebar from './components/Sidebar'
import UndoSendBanner from './components/UndoSendBanner'
import UpdateBanner from './components/UpdateBanner'

/** Optional brand logo (drop a file at src/renderer/assets/logo.*). */
const logoUrl: string | null = (() => {
  const mods = import.meta.glob('./assets/logo.{png,svg,jpg,jpeg,webp}', {
    eager: true,
    query: '?url',
    import: 'default'
  })
  return (Object.values(mods)[0] as string | undefined) ?? null
})()

/** True if the current time falls within the configured quiet hours. */
function inQuietHours(): boolean {
  const from = localStorage.getItem('nmc.quietFrom')
  const to = localStorage.getItem('nmc.quietTo')
  if (!from || !to) return false
  const now = new Date()
  const cur = now.getHours() * 60 + now.getMinutes()
  const [fh, fm] = from.split(':').map(Number)
  const [th, tm] = to.split(':').map(Number)
  const f = fh * 60 + fm
  const t = th * 60 + tm
  if (f === t) return false
  return f < t ? cur >= f && cur < t : cur >= f || cur < t
}

/** Whether a new-mail notification should be shown for this account/folder. */
function notifyAllowed(accountId: string, folder: string): boolean {
  if (inQuietHours()) return false
  if (localStorage.getItem('nmc.notifyInboxOnly') === '1') {
    const role = (useMailStore.getState().foldersByAccount[accountId] ?? []).find(
      (f) => f.path === folder
    )?.role
    if (role !== 'inbox') return false
  }
  return true
}

export default function App(): JSX.Element {
  const accounts = useMailStore((s) => s.accounts)
  const activeAccountId = useMailStore((s) => s.activeAccountId)
  const loadAccounts = useMailStore((s) => s.loadAccounts)
  const loadLabels = useMailStore((s) => s.loadLabels)
  const loadContacts = useMailStore((s) => s.loadContacts)
  const error = useMailStore((s) => s.error)
  const setError = useMailStore((s) => s.setError)
  const compose = useMailStore((s) => s.compose)
  const openCompose = useMailStore((s) => s.openCompose)
  const view = useMailStore((s) => s.view)
  const setView = useMailStore((s) => s.setView)
  const mailLayout = useMailStore((s) => s.mailLayout)

  const [showSetup, setShowSetup] = useState(false)
  const [showSettings, setShowSettings] = useState(false)

  useEffect(() => {
    loadAccounts()
    loadLabels()
    loadContacts()
  }, [loadAccounts, loadLabels, loadContacts])

  // Subscribe to live new-mail pushes (IMAP IDLE).
  useEffect(() => {
    const unsub = window.api.events.onNewMail((p) => {
      const st = useMailStore.getState()
      st.onNewMail(p.accountId, p.folder, p.count)
      if (st.notifyOn && notifyAllowed(p.accountId, p.folder)) {
        playChime()
        const acc = st.accounts.find((a) => a.id === p.accountId)
        const what = p.count === 1 ? 'Neue Nachricht' : `${p.count} neue Nachrichten`
        showMailNotification(what, acc?.email ?? '')
      }
    })
    return unsub
  }, [])

  // Global keyboard shortcuts (ignored while typing or composing).
  useEffect(() => {
    function onKey(e: KeyboardEvent): void {
      const t = e.target as HTMLElement | null
      if (t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.isContentEditable)) return
      const st = useMailStore.getState()
      if (st.compose || st.view !== 'mail' || st.unified || st.activeLabel) return
      const msgs = st.messages
      const i = msgs.findIndex((m) => m.uid === st.selectedUid)
      if ((e.key === 'a' || e.key === 'A') && (e.ctrlKey || e.metaKey)) {
        if (msgs.length) {
          e.preventDefault()
          st.selectAll()
        }
      } else if (e.key === 'Delete' && st.selectedUids.length) {
        e.preventDefault()
        st.removeMessages(st.selectedUids)
      } else if (e.key === 'j' || e.key === 'ArrowDown') {
        const next = msgs[i + 1] ?? msgs[0]
        if (next) {
          e.preventDefault()
          st.selectMessage(next.uid)
        }
      } else if (e.key === 'k' || e.key === 'ArrowUp') {
        const prev = i > 0 ? msgs[i - 1] : msgs[msgs.length - 1]
        if (prev) {
          e.preventDefault()
          st.selectMessage(prev.uid)
        }
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [])

  // Schedule calendar event reminders (notification + chime) while the app runs.
  useEffect(() => {
    let timers: number[] = []
    const fired = new Set<string>()

    async function schedule(): Promise<void> {
      timers.forEach((t) => clearTimeout(t))
      timers = []
      const lead = Number(localStorage.getItem('nmc.reminderLead') ?? '15')
      if (lead <= 0) return
      const cfg = await window.api.calendar.get()
      if (!cfg.ok || !cfg.data) return
      const now = new Date()
      const horizon = new Date(now.getTime() + 12 * 3600 * 1000)
      const res = await window.api.calendar.events(now.toISOString(), horizon.toISOString())
      if (!res.ok) return
      for (const e of res.data) {
        if (e.allDay) continue
        const key = e.uid + e.start
        if (fired.has(key)) continue
        const fireDelay = new Date(e.start).getTime() - lead * 60000 - Date.now()
        if (fireDelay > 0 && fireDelay < 12 * 3600 * 1000) {
          const id = window.setTimeout(() => {
            fired.add(key)
            showMailNotification(`Termin in ${lead} Min`, e.summary)
            playChime()
          }, fireDelay)
          timers.push(id)
        }
      }
    }

    schedule()
    const interval = window.setInterval(schedule, 30 * 60 * 1000)
    return () => {
      clearInterval(interval)
      timers.forEach((t) => clearTimeout(t))
    }
  }, [])

  const hasAccounts = accounts.length > 0

  return (
    <div className="flex h-full flex-col">
      {/* Top bar */}
      <header className="flex items-center gap-3 border-b bg-white px-4 py-2">
        <span className="flex items-center gap-2 font-semibold text-brand">
          {logoUrl ? (
            <img src={logoUrl} alt="Logo" className="h-6 w-auto" />
          ) : (
            <Mail className="h-5 w-5" />
          )}
          N-MailClient
        </span>
        {hasAccounts && (
          <div className="flex overflow-hidden rounded border text-sm">
            <button
              onClick={() => setView('mail')}
              className={`flex items-center gap-1.5 px-3 py-1 ${
                view === 'mail' ? 'bg-brand text-white' : 'hover:bg-gray-50'
              }`}
            >
              <Mail className="h-4 w-4" />
              Mail
            </button>
            <button
              onClick={() => setView('archive')}
              className={`flex items-center gap-1.5 px-3 py-1 ${
                view === 'archive' ? 'bg-brand text-white' : 'hover:bg-gray-50'
              }`}
            >
              <Archive className="h-4 w-4" />
              Archiv
            </button>
            <button
              onClick={() => setView('calendar')}
              className={`flex items-center gap-1.5 px-3 py-1 ${
                view === 'calendar' ? 'bg-brand text-white' : 'hover:bg-gray-50'
              }`}
            >
              <CalendarDays className="h-4 w-4" />
              Kalender
            </button>
            <button
              onClick={() => setView('contacts')}
              className={`flex items-center gap-1.5 px-3 py-1 ${
                view === 'contacts' ? 'bg-brand text-white' : 'hover:bg-gray-50'
              }`}
            >
              <User className="h-4 w-4" />
              Kontakte
            </button>
          </div>
        )}
        <div className="ml-auto flex items-center gap-2">
          {hasAccounts && (
            <button
              onClick={() => openCompose()}
              className="flex items-center gap-1.5 rounded bg-brand px-3 py-1.5 text-sm text-white hover:bg-brand-dark"
            >
              <PenSquare className="h-4 w-4" />
              Schreiben
            </button>
          )}
          <button
            onClick={() => setShowSettings(true)}
            className="flex items-center gap-1.5 rounded border px-3 py-1.5 text-sm hover:bg-gray-50"
            title="Einstellungen"
          >
            <Settings className="h-4 w-4" />
            Einstellungen
          </button>
        </div>
      </header>

      <UpdateBanner />

      {error && (
        <div className="flex items-center justify-between bg-red-50 px-4 py-2 text-sm text-red-700">
          <span>{error}</span>
          <button onClick={() => setError(null)} className="text-red-400 hover:text-red-700">
            <X className="h-4 w-4" />
          </button>
        </div>
      )}

      {/* Body */}
      {hasAccounts ? (
        view === 'mail' ? (
          mailLayout === 'bottom' ? (
            <div className="grid flex-1 grid-cols-[240px_1fr] grid-rows-1 overflow-hidden">
              <Sidebar />
              <div className="grid min-h-0 grid-rows-[minmax(0,2fr)_minmax(0,3fr)] overflow-hidden">
                <MailList />
                <MailView />
              </div>
            </div>
          ) : (
            <div className="grid flex-1 grid-cols-[240px_340px_1fr] grid-rows-1 overflow-hidden">
              <Sidebar />
              <MailList />
              <MailView />
            </div>
          )
        ) : view === 'archive' ? (
          <div className="grid flex-1 grid-cols-[240px_1fr] grid-rows-1 overflow-hidden">
            <Sidebar />
            <ArchiveView />
          </div>
        ) : view === 'calendar' ? (
          <div className="min-h-0 flex-1 overflow-hidden">
            <CalendarView />
          </div>
        ) : (
          <div className="min-h-0 flex-1 overflow-hidden">
            <ContactsView />
          </div>
        )
      ) : (
        <div className="flex flex-1 flex-col items-center justify-center text-center text-gray-500">
          <p className="mb-4 text-lg">Noch kein Konto eingerichtet.</p>
          <button
            onClick={() => setShowSetup(true)}
            className="rounded bg-brand px-5 py-2.5 text-white hover:bg-brand-dark"
          >
            Erstes Konto hinzufügen
          </button>
        </div>
      )}

      {showSetup && <AccountSetup onClose={() => setShowSetup(false)} />}
      {compose && <Composer />}
      {showSettings && <SettingsModal onClose={() => setShowSettings(false)} />}
      <UndoSendBanner />
      <UndoToast />
    </div>
  )
}

function UndoToast(): JSX.Element | null {
  const undoToast = useMailStore((s) => s.undoToast)
  if (!undoToast) return null
  return (
    <div className="fixed bottom-4 left-1/2 z-40 flex -translate-x-1/2 items-center gap-3 rounded-lg bg-gray-900 px-4 py-2.5 text-sm text-white shadow-lg">
      <span>{undoToast.label}</span>
      <button
        onClick={undoToast.onUndo}
        className="font-medium text-blue-300 hover:text-blue-200"
      >
        Rückgängig
      </button>
    </div>
  )
}
