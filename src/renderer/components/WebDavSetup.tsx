import { useState } from 'react'
import type { WebDavConfig } from '@shared/index'

interface Props {
  accountId: string
  initial?: WebDavConfig
  onClose: () => void
  onSaved: () => void
}

export default function WebDavSetup({ accountId, initial, onClose, onSaved }: Props): JSX.Element {
  const [url, setUrl] = useState(initial?.url ?? '')
  const [user, setUser] = useState(initial?.user ?? '')
  const [password, setPassword] = useState('')
  const [basePath, setBasePath] = useState(initial?.basePath ?? 'Mail-Anhänge')
  const [busy, setBusy] = useState(false)
  const [status, setStatus] = useState<string | null>(null)
  const [tested, setTested] = useState(false)

  const config = (): WebDavConfig => ({ url: url.trim(), user: user.trim(), basePath: basePath.trim() })
  const canSubmit = url.trim() && user.trim() && password

  async function handleTest(): Promise<void> {
    setBusy(true)
    setStatus('Teste Verbindung…')
    const res = await window.api.accounts.testWebdav(config(), password)
    setBusy(false)
    if (res.ok) {
      setTested(true)
      setStatus('Verbindung erfolgreich')
    } else {
      setTested(false)
      setStatus('Fehler: ' + res.error)
    }
  }

  async function handleSave(): Promise<void> {
    setBusy(true)
    setStatus('Speichere…')
    const res = await window.api.accounts.setWebdavArchive(accountId, config(), password)
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
        <h2 className="mb-1 text-lg font-semibold">WebDAV-Ordner einbinden</h2>
        <p className="mb-4 text-sm text-gray-500">
          z.&nbsp;B. Nextcloud. Die Server-URL findest du in Nextcloud unter Dateien → Einstellungen
          („WebDAV-Adresse"). Nutze am besten ein App-Passwort.
        </p>

        <label className="mb-3 block">
          <span className="text-sm text-gray-600">Server-URL</span>
          <input
            className="mt-1 w-full rounded border px-3 py-2 text-sm"
            value={url}
            onChange={(e) => {
              setUrl(e.target.value)
              setTested(false)
            }}
            placeholder="https://cloud.neuhaus.or.at/remote.php/dav/files/DEINUSER"
            autoFocus
          />
        </label>

        <div className="mb-3 flex gap-3">
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

        <label className="mb-4 block">
          <span className="text-sm text-gray-600">Unterordner für Anhänge</span>
          <input
            className="mt-1 w-full rounded border px-3 py-2 text-sm"
            value={basePath}
            onChange={(e) => setBasePath(e.target.value)}
            placeholder="Mail-Anhänge"
          />
        </label>

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
