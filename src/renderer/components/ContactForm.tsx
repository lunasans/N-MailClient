import { useEffect, useState } from 'react'
import { Plus, X } from 'lucide-react'
import type { AddressBookInfo, Contact } from '@shared/index'

interface Props {
  /** Existing contact to edit, or null to create a new one. */
  contact: Contact | null
  onClose: () => void
  onSaved: () => void
}

export default function ContactForm({ contact, onClose, onSaved }: Props): JSX.Element {
  const editing = !!contact
  const [books, setBooks] = useState<AddressBookInfo[]>([])
  const [addressBookUrl, setAddressBookUrl] = useState('')
  const [fullName, setFullName] = useState(contact?.fullName ?? '')
  const [org, setOrg] = useState(contact?.org ?? '')
  const [emails, setEmails] = useState<string[]>(contact?.emails.length ? contact.emails : [''])
  const [phones, setPhones] = useState<string[]>(contact?.phones.length ? contact.phones : [''])
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (!editing) {
      window.api.contacts.addressBooks().then((res) => {
        if (res.ok) {
          setBooks(res.data)
          if (res.data[0]) setAddressBookUrl(res.data[0].url)
        }
      })
    }
  }, [editing])

  function updateList(
    list: string[],
    setList: (v: string[]) => void,
    i: number,
    value: string
  ): void {
    const next = [...list]
    next[i] = value
    setList(next)
  }

  async function handleSave(): Promise<void> {
    if (!fullName.trim()) return
    setSaving(true)
    setError(null)
    const cleanEmails = emails.map((e) => e.trim()).filter(Boolean)
    const cleanPhones = phones.map((p) => p.trim()).filter(Boolean)
    const res =
      editing && contact
        ? await window.api.contacts.update({
            addressBookUrl: '',
            uid: contact.id,
            href: contact.href,
            etag: contact.etag,
            fullName: fullName.trim(),
            org: org.trim(),
            emails: cleanEmails,
            phones: cleanPhones
          })
        : await window.api.contacts.create({
            addressBookUrl,
            fullName: fullName.trim(),
            org: org.trim(),
            emails: cleanEmails,
            phones: cleanPhones
          })
    setSaving(false)
    if (!res.ok) {
      setError(res.error)
      return
    }
    onSaved()
    onClose()
  }

  async function handleDelete(): Promise<void> {
    if (!contact) return
    if (!confirm(`Kontakt „${contact.fullName}" löschen?`)) return
    setSaving(true)
    const res = await window.api.contacts.delete(contact.href, contact.etag)
    setSaving(false)
    if (!res.ok) {
      setError(res.error)
      return
    }
    onSaved()
    onClose()
  }

  return (
    <div className="fixed inset-0 z-40 flex items-center justify-center bg-black/40">
      <div className="max-h-[90vh] w-[480px] overflow-y-auto rounded-lg bg-white p-6 shadow-xl">
        <div className="mb-4 flex items-center justify-between">
          <h2 className="text-lg font-semibold">{editing ? 'Kontakt bearbeiten' : 'Neuer Kontakt'}</h2>
          <button onClick={onClose} className="text-gray-400 hover:text-gray-700">
            <X className="h-5 w-5" />
          </button>
        </div>

        <label className="mb-3 block">
          <span className="text-sm text-gray-600">Name</span>
          <input
            className="mt-1 w-full rounded border px-3 py-2 text-sm"
            value={fullName}
            onChange={(e) => setFullName(e.target.value)}
            autoFocus
          />
        </label>

        <label className="mb-3 block">
          <span className="text-sm text-gray-600">Firma (optional)</span>
          <input
            className="mt-1 w-full rounded border px-3 py-2 text-sm"
            value={org}
            onChange={(e) => setOrg(e.target.value)}
          />
        </label>

        <ListEditor
          label="E-Mail-Adressen"
          values={emails}
          setValues={setEmails}
          placeholder="name@domain.tld"
          onChange={(i, v) => updateList(emails, setEmails, i, v)}
        />
        <ListEditor
          label="Telefonnummern"
          values={phones}
          setValues={setPhones}
          placeholder="+49 …"
          onChange={(i, v) => updateList(phones, setPhones, i, v)}
        />

        {!editing && (
          <label className="mb-3 block">
            <span className="text-sm text-gray-600">Adressbuch</span>
            <select
              className="mt-1 w-full rounded border px-3 py-2 text-sm"
              value={addressBookUrl}
              onChange={(e) => setAddressBookUrl(e.target.value)}
            >
              {books.length === 0 && <option value="">Lade…</option>}
              {books.map((b) => (
                <option key={b.url} value={b.url}>
                  {b.displayName}
                </option>
              ))}
            </select>
          </label>
        )}

        {error && <p className="mb-3 text-sm text-red-600">{error}</p>}

        <div className="flex items-center gap-2">
          {editing && (
            <button
              onClick={handleDelete}
              disabled={saving}
              className="rounded border px-4 py-2 text-sm text-red-600 hover:bg-red-50 disabled:opacity-50"
            >
              Löschen
            </button>
          )}
          <button onClick={onClose} className="ml-auto rounded px-4 py-2 text-sm text-gray-600 hover:bg-gray-100">
            Abbrechen
          </button>
          <button
            onClick={handleSave}
            disabled={!fullName.trim() || (!editing && !addressBookUrl) || saving}
            className="rounded bg-brand px-4 py-2 text-sm text-white disabled:opacity-50"
          >
            {saving ? 'Speichern…' : 'Speichern'}
          </button>
        </div>
      </div>
    </div>
  )
}

function ListEditor({
  label,
  values,
  setValues,
  placeholder,
  onChange
}: {
  label: string
  values: string[]
  setValues: (v: string[]) => void
  placeholder: string
  onChange: (i: number, v: string) => void
}): JSX.Element {
  return (
    <div className="mb-3">
      <span className="text-sm text-gray-600">{label}</span>
      <div className="mt-1 space-y-1.5">
        {values.map((v, i) => (
          <div key={i} className="flex items-center gap-2">
            <input
              className="flex-1 rounded border px-3 py-2 text-sm"
              placeholder={placeholder}
              value={v}
              onChange={(e) => onChange(i, e.target.value)}
            />
            <button
              onClick={() => setValues(values.filter((_, j) => j !== i))}
              className="shrink-0 rounded p-1 text-gray-400 hover:bg-gray-100"
              title="Entfernen"
            >
              <X className="h-4 w-4" />
            </button>
          </div>
        ))}
        <button
          onClick={() => setValues([...values, ''])}
          className="flex items-center gap-1 text-sm text-brand hover:underline"
        >
          <Plus className="h-3.5 w-3.5" />
          Hinzufügen
        </button>
      </div>
    </div>
  )
}
