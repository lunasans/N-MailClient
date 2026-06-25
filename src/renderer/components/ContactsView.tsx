import { useEffect, useMemo, useState } from 'react'
import { Building2, Mail, Pencil, Phone, Plus, RefreshCw, Search, User, X } from 'lucide-react'
import type { Contact } from '@shared/index'
import { useMailStore } from '../store/useMailStore'
import ContactForm from './ContactForm'

export default function ContactsView(): JSX.Element {
  const openCompose = useMailStore((s) => s.openCompose)
  const contacts = useMailStore((s) => s.contacts)
  const loading = useMailStore((s) => s.loadingContacts)
  const load = useMailStore((s) => s.loadContacts)
  const [query, setQuery] = useState('')
  const [editing, setEditing] = useState<Contact | null>(null)
  const [creating, setCreating] = useState(false)

  useEffect(() => {
    if (contacts.length === 0) load()
  }, [contacts.length, load])

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase()
    if (!q) return contacts
    return contacts.filter(
      (c) =>
        c.fullName.toLowerCase().includes(q) ||
        c.org.toLowerCase().includes(q) ||
        c.emails.some((e) => e.toLowerCase().includes(q))
    )
  }, [contacts, query])

  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="flex items-center gap-2 border-b bg-white px-6 py-3">
        <h1 className="flex items-center gap-2 text-lg font-semibold">
          <User className="h-5 w-5" />
          Kontakte
        </h1>
        <span className="text-sm text-gray-400">{contacts.length}</span>
        <div className="ml-auto flex items-center gap-2">
          {loading && <span className="text-xs text-gray-400">Lade…</span>}
          <button
            onClick={() => setCreating(true)}
            className="flex items-center gap-1.5 rounded bg-brand px-3 py-1.5 text-sm text-white hover:bg-brand-dark"
          >
            <Plus className="h-4 w-4" />
            Neuer Kontakt
          </button>
          <button onClick={load} className="rounded border p-1.5 hover:bg-gray-50" title="Aktualisieren">
            <RefreshCw className="h-4 w-4" />
          </button>
        </div>
      </div>

      <div className="border-b bg-white px-6 py-2">
        <div className="flex items-center gap-2 rounded border px-2 py-1.5">
          <Search className="h-4 w-4 shrink-0 text-gray-400" />
          <input
            className="w-full text-sm outline-none"
            placeholder="Kontakte durchsuchen (Name, E-Mail, Firma)…"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
          />
          {query && (
            <button onClick={() => setQuery('')} className="shrink-0 text-gray-400 hover:text-gray-700">
              <X className="h-4 w-4" />
            </button>
          )}
        </div>
      </div>

      <div className="min-h-0 flex-1 overflow-y-auto px-6 py-4">
        {!loading && contacts.length === 0 && (
          <div className="text-sm text-gray-400">
            Keine Kontakte. Bitte zuerst im Kalender-Tab eine Nextcloud/DAV-Verbindung einbinden.
          </div>
        )}
        {!loading && contacts.length > 0 && filtered.length === 0 && (
          <div className="text-sm text-gray-400">Keine Treffer für „{query}".</div>
        )}

        <div className="grid grid-cols-1 gap-2 md:grid-cols-2">
          {filtered.map((c) => (
            <div key={c.id} className="rounded border p-3">
              <div className="flex items-center gap-2">
                {c.photo ? (
                  <img
                    src={c.photo}
                    alt=""
                    className="h-8 w-8 shrink-0 rounded-full object-cover"
                  />
                ) : (
                  <span className="flex h-8 w-8 shrink-0 items-center justify-center rounded-full bg-gray-100 text-sm font-medium text-gray-600">
                    {c.fullName.slice(0, 1).toUpperCase()}
                  </span>
                )}
                <div className="min-w-0 flex-1">
                  <div className="truncate font-medium">{c.fullName}</div>
                  {c.org && (
                    <div className="flex items-center gap-1 truncate text-xs text-gray-500">
                      <Building2 className="h-3 w-3" />
                      {c.org}
                    </div>
                  )}
                </div>
                <button
                  onClick={() => setEditing(c)}
                  className="shrink-0 rounded p-1 text-gray-400 hover:bg-gray-100 hover:text-gray-700"
                  title="Bearbeiten"
                >
                  <Pencil className="h-4 w-4" />
                </button>
              </div>
              <div className="mt-2 space-y-1">
                {c.emails.map((e) => (
                  <button
                    key={e}
                    onClick={() => openCompose({ to: e })}
                    className="flex w-full items-center gap-1.5 truncate text-left text-sm text-brand hover:underline"
                    title={`E-Mail an ${e}`}
                  >
                    <Mail className="h-3.5 w-3.5 shrink-0" />
                    {e}
                  </button>
                ))}
                {c.phones.map((p) => (
                  <div key={p} className="flex items-center gap-1.5 text-sm text-gray-600">
                    <Phone className="h-3.5 w-3.5 shrink-0 text-gray-400" />
                    {p}
                  </div>
                ))}
              </div>
            </div>
          ))}
        </div>
      </div>

      {creating && (
        <ContactForm contact={null} onClose={() => setCreating(false)} onSaved={load} />
      )}
      {editing && (
        <ContactForm contact={editing} onClose={() => setEditing(null)} onSaved={load} />
      )}
    </div>
  )
}
