import { useEffect, useState } from 'react'
import { X } from 'lucide-react'
import type { CalendarInfo } from '@shared/index'
import { useMailStore, type NewEventDraft } from '../store/useMailStore'

/** Format a Date as a value for <input type="datetime-local"> (local time). */
function toLocalInput(d: Date): string {
  const p = (n: number): string => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}T${p(d.getHours())}:${p(d.getMinutes())}`
}
function toLocalDate(d: Date): string {
  const p = (n: number): string => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`
}

interface Props {
  initial: NewEventDraft
  /** When set, the form edits an existing event instead of creating one. */
  edit?: { uid: string; href: string; etag: string }
  onClose: () => void
  onSaved: () => void
}

export default function EventForm({ initial, edit, onClose, onSaved }: Props): JSX.Element {
  const closeNewEvent = useMailStore((s) => s.closeNewEvent)

  const startDate = initial.startISO ? new Date(initial.startISO) : (() => {
    const d = new Date()
    d.setMinutes(0, 0, 0)
    d.setHours(d.getHours() + 1)
    return d
  })()
  const endDate = initial.endISO
    ? new Date(initial.endISO)
    : new Date(startDate.getTime() + 60 * 60 * 1000)

  const [calendars, setCalendars] = useState<CalendarInfo[]>([])
  const [calendarUrl, setCalendarUrl] = useState('')
  const [summary, setSummary] = useState(initial.summary ?? '')
  const [allDay, setAllDay] = useState(initial.allDay ?? false)
  const [start, setStart] = useState(toLocalInput(startDate))
  const [end, setEnd] = useState(toLocalInput(endDate))
  const [startDay, setStartDay] = useState(toLocalDate(startDate))
  const [endDay, setEndDay] = useState(toLocalDate(endDate))
  const [location, setLocation] = useState(initial.location ?? '')
  const [description, setDescription] = useState(initial.description ?? '')
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    window.api.calendar.calendars().then((res) => {
      if (res.ok) {
        setCalendars(res.data)
        if (res.data[0]) setCalendarUrl(res.data[0].url)
      }
    })
  }, [])

  function handleClose(): void {
    closeNewEvent()
    onClose()
  }

  async function handleSave(): Promise<void> {
    if (!summary.trim() || (!edit && !calendarUrl)) return
    setSaving(true)
    setError(null)
    const startISO = allDay ? new Date(startDay).toISOString() : new Date(start).toISOString()
    const endISO = allDay ? new Date(endDay).toISOString() : new Date(end).toISOString()
    const fields = {
      summary: summary.trim(),
      startISO,
      endISO,
      allDay,
      location: location.trim(),
      description: description.trim()
    }
    const res = edit
      ? await window.api.calendar.updateEvent({ calendarUrl: '', ...fields, ...edit })
      : await window.api.calendar.createEvent({ calendarUrl, ...fields })
    setSaving(false)
    if (!res.ok) {
      setError(res.error)
      return
    }
    closeNewEvent()
    onSaved()
    onClose()
  }

  async function handleDelete(): Promise<void> {
    if (!edit) return
    if (!confirm('Diesen Termin löschen?')) return
    setSaving(true)
    const res = await window.api.calendar.deleteEvent(edit.href, edit.etag)
    setSaving(false)
    if (!res.ok) {
      setError(res.error)
      return
    }
    onSaved()
    onClose()
  }

  return (
    <div className="fixed inset-0 z-30 flex items-center justify-center bg-black/40">
      <div className="w-[520px] rounded-lg bg-white p-6 shadow-xl">
        <div className="mb-4 flex items-center justify-between">
          <h2 className="text-lg font-semibold">{edit ? 'Termin bearbeiten' : 'Neuer Termin'}</h2>
          <button onClick={handleClose} className="text-gray-400 hover:text-gray-700">
            <X className="h-5 w-5" />
          </button>
        </div>

        <label className="mb-3 block">
          <span className="text-sm text-gray-600">Titel</span>
          <input
            className="mt-1 w-full rounded border px-3 py-2 text-sm"
            value={summary}
            onChange={(e) => setSummary(e.target.value)}
            autoFocus
          />
        </label>

        <label className="mb-3 flex items-center gap-2 text-sm">
          <input type="checkbox" checked={allDay} onChange={(e) => setAllDay(e.target.checked)} />
          Ganztägig
        </label>

        <div className="mb-3 flex gap-3">
          <label className="block flex-1">
            <span className="text-sm text-gray-600">Beginn</span>
            {allDay ? (
              <input
                type="date"
                className="mt-1 w-full rounded border px-3 py-2 text-sm"
                value={startDay}
                onChange={(e) => setStartDay(e.target.value)}
              />
            ) : (
              <input
                type="datetime-local"
                className="mt-1 w-full rounded border px-3 py-2 text-sm"
                value={start}
                onChange={(e) => setStart(e.target.value)}
              />
            )}
          </label>
          <label className="block flex-1">
            <span className="text-sm text-gray-600">Ende</span>
            {allDay ? (
              <input
                type="date"
                className="mt-1 w-full rounded border px-3 py-2 text-sm"
                value={endDay}
                onChange={(e) => setEndDay(e.target.value)}
              />
            ) : (
              <input
                type="datetime-local"
                className="mt-1 w-full rounded border px-3 py-2 text-sm"
                value={end}
                onChange={(e) => setEnd(e.target.value)}
              />
            )}
          </label>
        </div>

        <label className="mb-3 block">
          <span className="text-sm text-gray-600">Ort (optional)</span>
          <input
            className="mt-1 w-full rounded border px-3 py-2 text-sm"
            value={location}
            onChange={(e) => setLocation(e.target.value)}
          />
        </label>

        <label className="mb-3 block">
          <span className="text-sm text-gray-600">Beschreibung (optional)</span>
          <textarea
            className="mt-1 h-20 w-full resize-none rounded border px-3 py-2 text-sm"
            value={description}
            onChange={(e) => setDescription(e.target.value)}
          />
        </label>

        {!edit && (
          <label className="mb-4 block">
            <span className="text-sm text-gray-600">Kalender</span>
            <select
              className="mt-1 w-full rounded border px-3 py-2 text-sm"
              value={calendarUrl}
              onChange={(e) => setCalendarUrl(e.target.value)}
            >
              {calendars.length === 0 && <option value="">Lade Kalender…</option>}
              {calendars.map((c) => (
                <option key={c.url} value={c.url}>
                  {c.displayName}
                </option>
              ))}
            </select>
          </label>
        )}

        {error && <p className="mb-3 text-sm text-red-600">{error}</p>}

        <div className="flex items-center gap-2">
          {edit && (
            <button
              onClick={handleDelete}
              disabled={saving}
              className="rounded border px-4 py-2 text-sm text-red-600 hover:bg-red-50 disabled:opacity-50"
            >
              Löschen
            </button>
          )}
          <button
            onClick={handleClose}
            className="ml-auto rounded px-4 py-2 text-gray-600 hover:bg-gray-100"
          >
            Abbrechen
          </button>
          <button
            onClick={handleSave}
            disabled={!summary.trim() || (!edit && !calendarUrl) || saving}
            className="rounded bg-brand px-4 py-2 text-white disabled:opacity-50"
          >
            {saving ? 'Speichern…' : 'Speichern'}
          </button>
        </div>
      </div>
    </div>
  )
}
