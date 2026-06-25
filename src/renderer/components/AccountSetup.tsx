import { useState } from 'react'
import type { NewAccount, ServerConfig } from '@shared/index'
import { useMailStore } from '../store/useMailStore'

interface Props {
  onClose: () => void
}

const emptyServer: ServerConfig = { host: '', port: 993, secure: true }

export default function AccountSetup({ onClose }: Props): JSX.Element {
  const loadAccounts = useMailStore((s) => s.loadAccounts)

  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [name, setName] = useState('')
  const [imap, setImap] = useState<ServerConfig>(emptyServer)
  const [smtp, setSmtp] = useState<ServerConfig>({ host: '', port: 465, secure: true })
  const [probed, setProbed] = useState(false)
  const [probing, setProbing] = useState(false)
  const [saving, setSaving] = useState(false)
  const [status, setStatus] = useState<string | null>(null)

  async function handleProbe(): Promise<void> {
    setStatus(null)
    setProbing(true)
    const res = await window.api.accounts.probe(email)
    setProbing(false)
    if (!res.ok) {
      setStatus(res.error)
      return
    }
    setImap(res.data.imap)
    setSmtp(res.data.smtp)
    setProbed(true)
    const parts: string[] = []
    parts.push(res.data.imapVerified ? 'IMAP-Server gefunden' : 'IMAP nicht erreichbar')
    parts.push(res.data.smtpVerified ? 'SMTP-Server gefunden' : 'SMTP nicht erreichbar')
    setStatus(parts.join('   ·   ') + ' — bei Bedarf anpassen, dann Passwort eingeben.')
  }

  async function handleSave(): Promise<void> {
    setStatus(null)
    setSaving(true)
    const payload: NewAccount = {
      name: name || email,
      email,
      user: email,
      password,
      imap,
      smtp
    }
    const res = await window.api.accounts.add(payload)
    setSaving(false)
    if (!res.ok) {
      setStatus(res.error)
      return
    }
    await loadAccounts()
    onClose()
  }

  const canSave = email && password && imap.host && smtp.host

  return (
    <div className="fixed inset-0 z-20 flex items-center justify-center bg-black/40">
      <div className="w-[560px] max-h-[90vh] overflow-y-auto rounded-lg bg-white p-6 shadow-xl">
        <h2 className="mb-4 text-lg font-semibold">Konto hinzufügen</h2>

        <label className="mb-3 block">
          <span className="text-sm text-gray-600">E-Mail-Adresse</span>
          <input
            type="email"
            className="mt-1 w-full rounded border px-3 py-2"
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            placeholder="name@deinedomain.tld"
            autoFocus
          />
        </label>

        <label className="mb-3 block">
          <span className="text-sm text-gray-600">Anzeigename (optional)</span>
          <input
            type="text"
            className="mt-1 w-full rounded border px-3 py-2"
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder="Max Mustermann"
          />
        </label>

        <button
          onClick={handleProbe}
          disabled={!email || probing}
          className="mb-4 rounded bg-brand px-4 py-2 text-white disabled:opacity-50"
        >
          {probing ? 'Suche Server…' : 'Server suchen'}
        </button>

        {(probed || imap.host || smtp.host) && (
          <div className="mb-4 grid grid-cols-2 gap-4">
            <ServerFields title="IMAP (Empfang)" value={imap} onChange={setImap} />
            <ServerFields title="SMTP (Versand)" value={smtp} onChange={setSmtp} />
          </div>
        )}

        {status && <p className="mb-3 text-sm text-gray-700">{status}</p>}

        {(probed || imap.host) && (
          <label className="mb-3 block">
            <span className="text-sm text-gray-600">Passwort</span>
            <input
              type="password"
              className="mt-1 w-full rounded border px-3 py-2"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              placeholder="Passwort für dieses Postfach"
              autoFocus
            />
          </label>
        )}

        <div className="flex justify-end gap-2">
          <button onClick={onClose} className="rounded px-4 py-2 text-gray-600 hover:bg-gray-100">
            Abbrechen
          </button>
          <button
            onClick={handleSave}
            disabled={!canSave || saving}
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
