import { useState } from 'react'

interface Props {
  initial?: { serverUrl: string; user: string }
  onClose: () => void
  onSaved: () => void
}

export default function CalDavSetup({ initial, onClose, onSaved }: Props): JSX.Element {
  const [serverUrl, setServerUrl] = useState(initial?.serverUrl ?? '')
  const [user, setUser] = useState(initial?.user ?? '')
  const [password, setPassword] = useState('')
  const [busy, setBusy] = useState(false)
  const [status, setStatus] = useState<string | null>(null)
  const [tested, setTested] = useState(false)

  const canSubmit = serverUrl.trim() && user.trim() && password

  async function handleTest(): Promise<void> {
    setBusy(true)
    setStatus('Teste Verbindung…')
    const res = await window.api.calendar.test(serverUrl.trim(), user.trim(), password)
    setBusy(false)
    if (res.ok) {
      setTested(true)
      setStatus(`Verbindung erfolgreich — ${res.data} Kalender gefunden`)
    } else {
      setTested(false)
      setStatus('Fehler: ' + res.error)
    }
  }

  async function handleSave(): Promise<void> {
    setBusy(true)
    setStatus('Speichere…')
    const res = await window.api.calendar.save(serverUrl.trim(), user.trim(), password)
    setBusy(false)
    if (res.ok) {
      onSaved()
      onClose()
    } else {
      setStatus('Fehler: ' + res.error)
    }
  }

  return (
    <div className="fixed inset-0 z-30 flex items-center justify-center bg-black/40">
      <div className="w-[520px] rounded-lg bg-white p-6 shadow-xl">
        <h2 className="mb-1 text-lg font-semibold">Kalender einbinden (CalDAV)</h2>
        <p className="mb-4 text-sm text-gray-500">
          z.&nbsp;B. Nextcloud. Server-URL ist die CalDAV-Basis, etwa
          <code className="mx-1 rounded bg-gray-100 px-1">/remote.php/dav</code>. App-Passwort
          empfohlen.
        </p>

        <label className="mb-3 block">
          <span className="text-sm text-gray-600">Server-URL</span>
          <input
            className="mt-1 w-full rounded border px-3 py-2 text-sm"
            value={serverUrl}
            onChange={(e) => {
              setServerUrl(e.target.value)
              setTested(false)
            }}
            placeholder="https://cloud.neuhaus.or.at/remote.php/dav"
            autoFocus
          />
        </label>

        <div className="mb-4 flex gap-3">
          <label className="block flex-1">
            <span className="text-sm text-gray-600">Benutzer</span>
            <input
              className="mt-1 w-full rounded border px-3 py-2 text-sm"
              value={user}
              onChange={(e) => {
                setUser(e.target.value)
                setTested(false)
              }}
            />
          </label>
          <label className="block flex-1">
            <span className="text-sm text-gray-600">App-Passwort</span>
            <input
              type="password"
              className="mt-1 w-full rounded border px-3 py-2 text-sm"
              value={password}
              onChange={(e) => {
                setPassword(e.target.value)
                setTested(false)
              }}
            />
          </label>
        </div>

        {status && <p className="mb-3 text-sm text-gray-700">{status}</p>}

        <div className="flex justify-end gap-2">
          <button onClick={onClose} className="rounded px-4 py-2 text-gray-600 hover:bg-gray-100">
            Abbrechen
          </button>
          <button
            onClick={handleTest}
            disabled={!canSubmit || busy}
            className="rounded border px-4 py-2 disabled:opacity-50"
          >
            Verbindung testen
          </button>
          <button
            onClick={handleSave}
            disabled={!canSubmit || busy || !tested}
            className="rounded bg-brand px-4 py-2 text-white disabled:opacity-50"
            title={tested ? '' : 'Bitte zuerst die Verbindung testen'}
          >
            Speichern
          </button>
        </div>
      </div>
    </div>
  )
}
