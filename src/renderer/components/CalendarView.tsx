import { useCallback, useEffect, useRef, useState } from 'react'
import { CalendarPlus, ChevronLeft, ChevronRight, Plus, RefreshCw, X } from 'lucide-react'
import type { CalEvent, CalendarConfig } from '@shared/index'
import { useMailStore } from '../store/useMailStore'
import CalDavSetup from './CalDavSetup'
import EventForm from './EventForm'

type Mode = 'month' | 'week' | 'day'

const WEEKDAYS = ['Mo', 'Di', 'Mi', 'Do', 'Fr', 'Sa', 'So']
const HOUR_H = 48

function startOfDay(d: Date): Date {
  return new Date(d.getFullYear(), d.getMonth(), d.getDate())
}
function addDays(d: Date, n: number): Date {
  return new Date(d.getFullYear(), d.getMonth(), d.getDate() + n)
}
function sameDay(a: Date, b: Date): boolean {
  return startOfDay(a).getTime() === startOfDay(b).getTime()
}
function fmtTime(iso: string): string {
  return new Date(iso).toLocaleTimeString('de-DE', { hour: '2-digit', minute: '2-digit' })
}

interface LaidEvent {
  e: CalEvent
  top: number
  height: number
  leftPct: number
  widthPct: number
}

/**
 * Arrange a day's timed events into side-by-side columns so overlapping events
 * don't fully cover each other. Events are grouped into clusters of mutual
 * overlap; within a cluster each event gets the first free column.
 */
function layoutTimed(events: CalEvent[], day: Date): LaidEvent[] {
  const ds = startOfDay(day).getTime()
  const items = events
    .filter((e) => !e.allDay)
    .map((e) => {
      const startMin = Math.max(0, (new Date(e.start).getTime() - ds) / 60000)
      const endMin = Math.min(24 * 60, (new Date(e.end).getTime() - ds) / 60000)
      return { e, startMin, endMin: Math.max(endMin, startMin + 15) }
    })
    .sort((a, b) => a.startMin - b.startMin || a.endMin - b.endMin)

  const out: LaidEvent[] = []
  let cluster: typeof items = []
  let clusterEnd = -1

  const flush = (): void => {
    const colEnds: number[] = []
    const colOf = cluster.map((it) => {
      let c = colEnds.findIndex((end) => it.startMin >= end)
      if (c === -1) {
        c = colEnds.length
        colEnds.push(0)
      }
      colEnds[c] = it.endMin
      return c
    })
    const cols = colEnds.length
    cluster.forEach((it, idx) => {
      out.push({
        e: it.e,
        top: (it.startMin / 60) * HOUR_H,
        height: Math.max(18, ((it.endMin - it.startMin) / 60) * HOUR_H),
        leftPct: (colOf[idx] / cols) * 100,
        widthPct: (1 / cols) * 100
      })
    })
  }

  for (const it of items) {
    if (cluster.length && it.startMin >= clusterEnd) {
      flush()
      cluster = []
      clusterEnd = -1
    }
    cluster.push(it)
    clusterEnd = Math.max(clusterEnd, it.endMin)
  }
  if (cluster.length) flush()
  return out
}

export default function CalendarView(): JSX.Element {
  const [config, setConfig] = useState<CalendarConfig | null>(null)
  const [loadedConfig, setLoadedConfig] = useState(false)
  const [cursor, setCursor] = useState(() => new Date())
  const [mode, setMode] = useState<Mode>('month')
  const [events, setEvents] = useState<CalEvent[]>([])
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [selected, setSelected] = useState<CalEvent | null>(null)
  const [editing, setEditing] = useState<CalEvent | null>(null)
  const [showSetup, setShowSetup] = useState(false)
  const newEventDraft = useMailStore((s) => s.newEventDraft)
  const openNewEvent = useMailStore((s) => s.openNewEvent)
  const today = new Date()
  const scrollRef = useRef<HTMLDivElement>(null)

  // Visible period + day columns by mode.
  let rangeStart: Date
  let rangeEnd: Date
  let gridDays: Date[]
  if (mode === 'month') {
    const first = new Date(cursor.getFullYear(), cursor.getMonth(), 1)
    const dow = (first.getDay() + 6) % 7
    const gs = addDays(first, -dow)
    gridDays = Array.from({ length: 42 }, (_, i) => addDays(gs, i))
    rangeStart = gs
    rangeEnd = addDays(gs, 42)
  } else if (mode === 'week') {
    const dow = (cursor.getDay() + 6) % 7
    const ws = addDays(startOfDay(cursor), -dow)
    gridDays = Array.from({ length: 7 }, (_, i) => addDays(ws, i))
    rangeStart = ws
    rangeEnd = addDays(ws, 7)
  } else {
    const ds = startOfDay(cursor)
    gridDays = [ds]
    rangeStart = ds
    rangeEnd = addDays(ds, 1)
  }
  const rangeKey = rangeStart.toISOString() + rangeEnd.toISOString()

  useEffect(() => {
    window.api.calendar.get().then((res) => {
      if (res.ok) setConfig(res.data)
      setLoadedConfig(true)
    })
  }, [])

  const loadEvents = useCallback(async () => {
    if (!config) return
    setLoading(true)
    setError(null)
    const res = await window.api.calendar.events(rangeStart.toISOString(), rangeEnd.toISOString())
    setLoading(false)
    if (res.ok) setEvents(res.data)
    else setError(res.error)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [config, rangeKey])

  useEffect(() => {
    loadEvents()
  }, [loadEvents])

  // Scroll the time grid to ~7am when entering week/day view.
  useEffect(() => {
    if (mode !== 'month' && scrollRef.current) scrollRef.current.scrollTop = 7 * HOUR_H
  }, [mode])

  function eventsForDay(day: Date): CalEvent[] {
    return events.filter((e) => {
      const s = startOfDay(new Date(e.start))
      const last = startOfDay(new Date(new Date(e.end).getTime() - 1))
      const d = startOfDay(day)
      return d >= s && d <= last
    })
  }

  function prev(): void {
    if (mode === 'month') setCursor(new Date(cursor.getFullYear(), cursor.getMonth() - 1, 1))
    else setCursor(addDays(cursor, mode === 'week' ? -7 : -1))
  }
  function next(): void {
    if (mode === 'month') setCursor(new Date(cursor.getFullYear(), cursor.getMonth() + 1, 1))
    else setCursor(addDays(cursor, mode === 'week' ? 7 : 1))
  }

  const title =
    mode === 'month'
      ? cursor.toLocaleDateString('de-DE', { month: 'long', year: 'numeric' })
      : mode === 'week'
        ? `${gridDays[0].toLocaleDateString('de-DE', { day: '2-digit', month: 'short' })} – ${gridDays[6].toLocaleDateString('de-DE', { day: '2-digit', month: 'short', year: 'numeric' })}`
        : cursor.toLocaleDateString('de-DE', {
            weekday: 'long',
            day: '2-digit',
            month: 'long',
            year: 'numeric'
          })

  function createAt(day: Date, hour: number): void {
    const s = new Date(day.getFullYear(), day.getMonth(), day.getDate(), hour, 0)
    openNewEvent({
      startISO: s.toISOString(),
      endISO: new Date(s.getTime() + 60 * 60 * 1000).toISOString()
    })
  }

  if (loadedConfig && !config) {
    return (
      <div className="flex h-full flex-col items-center justify-center text-center text-gray-500">
        <p className="mb-1 text-lg">Kein Kalender eingebunden.</p>
        <p className="mb-4 text-sm">Verbinde einen CalDAV-Kalender (z.&nbsp;B. Nextcloud).</p>
        <button
          onClick={() => setShowSetup(true)}
          className="flex items-center gap-2 rounded bg-brand px-5 py-2.5 text-white hover:bg-brand-dark"
        >
          <CalendarPlus className="h-4 w-4" />
          Kalender einbinden
        </button>
        {showSetup && (
          <CalDavSetup
            onClose={() => setShowSetup(false)}
            onSaved={() => window.api.calendar.get().then((res) => res.ok && setConfig(res.data))}
          />
        )}
      </div>
    )
  }

  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="flex items-center gap-2 border-b bg-white px-6 py-3">
        <h1 className="text-lg font-semibold">{title}</h1>
        <button onClick={prev} className="rounded border p-1.5 hover:bg-gray-50" title="Zurück">
          <ChevronLeft className="h-4 w-4" />
        </button>
        <button onClick={next} className="rounded border p-1.5 hover:bg-gray-50" title="Weiter">
          <ChevronRight className="h-4 w-4" />
        </button>
        <button
          onClick={() => setCursor(new Date())}
          className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50"
        >
          Heute
        </button>
        <div className="ml-3 flex overflow-hidden rounded border text-sm">
          {(['month', 'week', 'day'] as Mode[]).map((m) => (
            <button
              key={m}
              onClick={() => setMode(m)}
              className={`px-3 py-1 ${mode === m ? 'bg-brand text-white' : 'hover:bg-gray-50'}`}
            >
              {m === 'month' ? 'Monat' : m === 'week' ? 'Woche' : 'Tag'}
            </button>
          ))}
        </div>
        <div className="ml-auto flex items-center gap-2">
          {loading && <span className="text-xs text-gray-400">Lade…</span>}
          <button
            onClick={() => openNewEvent({})}
            className="flex items-center gap-1.5 rounded bg-brand px-3 py-1.5 text-sm text-white hover:bg-brand-dark"
          >
            <Plus className="h-4 w-4" />
            Termin
          </button>
          <button onClick={loadEvents} className="rounded border p-1.5 hover:bg-gray-50" title="Aktualisieren">
            <RefreshCw className="h-4 w-4" />
          </button>
          <button
            onClick={() => setShowSetup(true)}
            className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50"
          >
            Kalender ändern
          </button>
        </div>
      </div>

      {error && <div className="bg-red-50 px-6 py-2 text-sm text-red-700">{error}</div>}

      {mode === 'month' ? (
        <>
          <div className="grid grid-cols-7 border-b text-xs font-medium text-gray-500">
            {WEEKDAYS.map((w) => (
              <div key={w} className="px-2 py-1.5 text-center">
                {w}
              </div>
            ))}
          </div>
          <div className="grid min-h-0 flex-1 grid-cols-7 grid-rows-6">
            {gridDays.map((day) => {
              const inMonth = day.getMonth() === cursor.getMonth()
              const isToday = sameDay(day, today)
              const dayEvents = eventsForDay(day)
              return (
                <div
                  key={day.toISOString()}
                  onClick={() => createAt(day, 9)}
                  className={`min-h-0 cursor-pointer overflow-hidden border-b border-r p-1 hover:bg-blue-50 ${
                    inMonth ? 'bg-white' : 'bg-gray-50 text-gray-400'
                  }`}
                >
                  <div className="mb-0.5 flex justify-end">
                    <span
                      className={`flex h-5 w-5 items-center justify-center rounded-full text-xs ${
                        isToday ? 'bg-brand font-semibold text-white' : ''
                      }`}
                    >
                      {day.getDate()}
                    </span>
                  </div>
                  <div className="space-y-0.5">
                    {dayEvents.slice(0, 3).map((e, i) => (
                      <button
                        key={e.uid + i}
                        onClick={(ev) => {
                          ev.stopPropagation()
                          setSelected(e)
                        }}
                        style={{ backgroundColor: (e.color ?? '#2563eb') + '22', color: e.color ?? '#1d4ed8' }}
                        className="block w-full truncate rounded px-1 py-0.5 text-left text-xs"
                        title={e.summary}
                      >
                        {!e.allDay && <span className="mr-1 opacity-70">{fmtTime(e.start)}</span>}
                        {e.summary}
                      </button>
                    ))}
                    {dayEvents.length > 3 && (
                      <div className="px-1 text-xs text-gray-400">+{dayEvents.length - 3} mehr</div>
                    )}
                  </div>
                </div>
              )
            })}
          </div>
        </>
      ) : (
        <>
          {/* Day headers */}
          <div className="flex border-b text-xs font-medium text-gray-500">
            <div className="w-14 shrink-0" />
            {gridDays.map((day) => (
              <div key={day.toISOString()} className="flex-1 border-l px-2 py-1.5 text-center">
                <span className={sameDay(day, today) ? 'font-semibold text-brand' : ''}>
                  {day.toLocaleDateString('de-DE', { weekday: 'short', day: '2-digit' })}
                </span>
              </div>
            ))}
          </div>
          {/* All-day strip */}
          <div className="flex border-b bg-gray-50">
            <div className="w-14 shrink-0 py-1 pr-1 text-right text-[10px] text-gray-400">ganztägig</div>
            {gridDays.map((day) => (
              <div key={day.toISOString()} className="min-h-[1.5rem] flex-1 space-y-0.5 border-l p-1">
                {eventsForDay(day)
                  .filter((e) => e.allDay)
                  .map((e, i) => (
                    <button
                      key={e.uid + i}
                      onClick={() => setSelected(e)}
                      style={{ backgroundColor: (e.color ?? '#2563eb') + '22', color: e.color ?? '#1d4ed8' }}
                      className="block w-full truncate rounded px-1 py-0.5 text-left text-xs"
                    >
                      {e.summary}
                    </button>
                  ))}
              </div>
            ))}
          </div>
          {/* Time grid */}
          <div ref={scrollRef} className="min-h-0 flex-1 overflow-y-auto">
            <div className="flex" style={{ height: 24 * HOUR_H }}>
              <div className="w-14 shrink-0">
                {Array.from({ length: 24 }, (_, h) => (
                  <div
                    key={h}
                    className="relative border-b text-right pr-1 text-[10px] text-gray-400"
                    style={{ height: HOUR_H }}
                  >
                    <span className="absolute -top-1.5 right-1">{h.toString().padStart(2, '0')}:00</span>
                  </div>
                ))}
              </div>
              {gridDays.map((day) => (
                <div
                  key={day.toISOString()}
                  className="relative flex-1 border-l"
                  onClick={(e) => {
                    const rect = e.currentTarget.getBoundingClientRect()
                    createAt(day, Math.floor((e.clientY - rect.top) / HOUR_H))
                  }}
                >
                  {Array.from({ length: 24 }, (_, h) => (
                    <div key={h} className="border-b" style={{ height: HOUR_H }} />
                  ))}
                  {layoutTimed(eventsForDay(day), day).map(({ e, top, height, leftPct, widthPct }, i) => (
                    <button
                      key={e.uid + i}
                      onClick={(ev) => {
                        ev.stopPropagation()
                        setSelected(e)
                      }}
                      style={{
                        top,
                        height,
                        left: `calc(${leftPct}% + 1px)`,
                        width: `calc(${widthPct}% - 2px)`,
                        backgroundColor: (e.color ?? '#2563eb') + '33',
                        borderColor: e.color ?? '#2563eb',
                        color: e.color ?? '#1d4ed8'
                      }}
                      className="absolute overflow-hidden rounded border-l-2 px-1 py-0.5 text-left text-xs"
                      title={e.summary}
                    >
                      <span className="mr-1 opacity-70">{fmtTime(e.start)}</span>
                      {e.summary}
                    </button>
                  ))}
                </div>
              ))}
            </div>
          </div>
        </>
      )}

      {selected && (
        <div
          className="fixed inset-0 z-30 flex items-center justify-center bg-black/40"
          onClick={() => setSelected(null)}
        >
          <div
            className="w-[440px] rounded-lg bg-white p-6 shadow-xl"
            onClick={(e) => e.stopPropagation()}
          >
            <div className="mb-3 flex items-start justify-between gap-2">
              <h2 className="text-lg font-semibold">{selected.summary}</h2>
              <button onClick={() => setSelected(null)} className="text-gray-400 hover:text-gray-700">
                <X className="h-5 w-5" />
              </button>
            </div>
            <div className="space-y-1 text-sm text-gray-700">
              <div>
                {selected.allDay
                  ? new Date(selected.start).toLocaleDateString('de-DE', { dateStyle: 'full' })
                  : `${new Date(selected.start).toLocaleString('de-DE')} – ${new Date(
                      selected.end
                    ).toLocaleTimeString('de-DE', { hour: '2-digit', minute: '2-digit' })}`}
              </div>
              {selected.location && (
                <div>
                  <span className="text-gray-400">Ort:</span> {selected.location}
                </div>
              )}
              {selected.calendar && (
                <div className="flex items-center gap-1.5">
                  <span
                    className="h-2.5 w-2.5 rounded-full"
                    style={{ backgroundColor: selected.color ?? '#2563eb' }}
                  />
                  {selected.calendar}
                </div>
              )}
              {selected.description && (
                <p className="whitespace-pre-wrap pt-2 text-gray-600">{selected.description}</p>
              )}
            </div>
            <div className="mt-4 flex items-center gap-2">
              {selected.recurring && (
                <span className="text-xs text-gray-400">Wiederkehrender Termin</span>
              )}
              <button
                onClick={async () => {
                  if (!confirm('Diesen Termin löschen?')) return
                  const res = await window.api.calendar.deleteEvent(selected.href, selected.etag)
                  if (res.ok) {
                    setSelected(null)
                    loadEvents()
                  } else {
                    setError(res.error)
                  }
                }}
                className="ml-auto rounded border px-3 py-1.5 text-sm text-red-600 hover:bg-red-50"
              >
                Löschen
              </button>
              {!selected.recurring && (
                <button
                  onClick={() => {
                    setEditing(selected)
                    setSelected(null)
                  }}
                  className="rounded bg-brand px-3 py-1.5 text-sm text-white hover:bg-brand-dark"
                >
                  Bearbeiten
                </button>
              )}
            </div>
          </div>
        </div>
      )}

      {showSetup && (
        <CalDavSetup
          initial={config ?? undefined}
          onClose={() => setShowSetup(false)}
          onSaved={() => window.api.calendar.get().then((res) => res.ok && setConfig(res.data))}
        />
      )}

      {newEventDraft && (
        <EventForm initial={newEventDraft} onClose={() => {}} onSaved={loadEvents} />
      )}

      {editing && (
        <EventForm
          initial={{
            summary: editing.summary,
            startISO: editing.start,
            endISO: editing.end,
            allDay: editing.allDay,
            location: editing.location,
            description: editing.description
          }}
          edit={{ uid: editing.uid, href: editing.href, etag: editing.etag }}
          onClose={() => setEditing(null)}
          onSaved={loadEvents}
        />
      )}
    </div>
  )
}
