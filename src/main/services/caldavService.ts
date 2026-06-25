import { randomUUID } from 'crypto'
import { DAVClient } from 'tsdav'
import nodeIcal from 'node-ical'
import type {
  CalEvent,
  CalEventInput,
  CalEventUpdate,
  CalendarConfig,
  CalendarInfo
} from '../types'
import { clearCalendar, decryptPassword, getCalendar, setCalendar } from './db'

interface Creds {
  serverUrl: string
  user: string
  password: string
}

function clientFor(c: Creds): DAVClient {
  return new DAVClient({
    serverUrl: c.serverUrl,
    credentials: { username: c.user, password: c.password },
    authMethod: 'Basic',
    defaultAccountType: 'caldav'
  })
}

/** node-ical fields can be a string or a { val } object — normalize to string. */
function str(v: unknown): string {
  if (typeof v === 'string') return v
  if (v && typeof v === 'object' && 'val' in v) return String((v as { val: unknown }).val ?? '')
  return ''
}

function resolveCreds(): Creds | null {
  const cfg = getCalendar()
  if (!cfg) return null
  return { serverUrl: cfg.serverUrl, user: cfg.user, password: decryptPassword(cfg.secret) }
}

/** Validate a connection and return the number of discovered calendars. */
export async function testConnection(
  serverUrl: string,
  user: string,
  password: string
): Promise<number> {
  const client = clientFor({ serverUrl, user, password })
  await client.login()
  const cals = await client.fetchCalendars()
  return cals.length
}

/** Public calendar config (no password) for the renderer. */
export function getPublicConfig(): CalendarConfig | null {
  const c = getCalendar()
  return c ? { serverUrl: c.serverUrl, user: c.user } : null
}

/** Validate then persist the calendar connection. */
export async function saveConfig(
  serverUrl: string,
  user: string,
  password: string
): Promise<void> {
  await testConnection(serverUrl, user, password)
  setCalendar({ serverUrl, user }, password)
}

export function clearConfig(): void {
  clearCalendar()
}

/** List calendars that accept events (for the create picker). */
export async function listCalendars(): Promise<CalendarInfo[]> {
  const creds = resolveCreds()
  if (!creds) return []
  const client = clientFor(creds)
  await client.login()
  const cals = await client.fetchCalendars()
  return cals
    .filter((c) => {
      const comps = (c.components ?? []) as string[]
      return comps.length === 0 || comps.includes('VEVENT')
    })
    .map((c) => ({
      url: c.url,
      displayName: typeof c.displayName === 'string' ? c.displayName : c.url,
      color: typeof c.calendarColor === 'string' ? c.calendarColor : undefined
    }))
}

function icsEscape(s: string): string {
  return s.replace(/\\/g, '\\\\').replace(/;/g, '\\;').replace(/,/g, '\\,').replace(/\n/g, '\\n')
}
function utcStamp(d: Date): string {
  return d.toISOString().replace(/[-:]/g, '').replace(/\.\d{3}/, '')
}
function dateStamp(d: Date): string {
  return `${d.getFullYear()}${String(d.getMonth() + 1).padStart(2, '0')}${String(d.getDate()).padStart(2, '0')}`
}

function buildIcs(uid: string, input: CalEventInput): string {
  const start = new Date(input.startISO)
  const end = new Date(input.endISO)
  const lines = ['BEGIN:VCALENDAR', 'VERSION:2.0', 'PRODID:-//N-MailClient//DE', 'BEGIN:VEVENT']
  lines.push(`UID:${uid}`, `DTSTAMP:${utcStamp(new Date())}`)
  if (input.allDay) {
    const endExclusive = new Date(end.getFullYear(), end.getMonth(), end.getDate() + 1)
    lines.push(`DTSTART;VALUE=DATE:${dateStamp(start)}`, `DTEND;VALUE=DATE:${dateStamp(endExclusive)}`)
  } else {
    lines.push(`DTSTART:${utcStamp(start)}`, `DTEND:${utcStamp(end)}`)
  }
  lines.push(`SUMMARY:${icsEscape(input.summary)}`)
  if (input.location) lines.push(`LOCATION:${icsEscape(input.location)}`)
  if (input.description) lines.push(`DESCRIPTION:${icsEscape(input.description)}`)
  lines.push('END:VEVENT', 'END:VCALENDAR')
  return lines.join('\r\n')
}

/** Create a new event on the chosen calendar. */
export async function createEvent(input: CalEventInput): Promise<void> {
  const creds = resolveCreds()
  if (!creds) throw new Error('Kein Kalender eingebunden')
  const client = clientFor(creds)
  await client.login()
  const uid = `${randomUUID()}@neuhaus-mail`
  await client.createCalendarObject({
    calendar: { url: input.calendarUrl } as Parameters<
      typeof client.createCalendarObject
    >[0]['calendar'],
    filename: `${uid}.ics`,
    iCalString: buildIcs(uid, input)
  })
}

/** Update an existing event (overwrites the CalDAV object). */
export async function updateEvent(input: CalEventUpdate): Promise<void> {
  const creds = resolveCreds()
  if (!creds) throw new Error('Kein Kalender eingebunden')
  const client = clientFor(creds)
  await client.login()
  await client.updateCalendarObject({
    calendarObject: { url: input.href, etag: input.etag, data: buildIcs(input.uid, input) }
  })
}

/** Delete a calendar event object. */
export async function deleteEvent(href: string, etag: string): Promise<void> {
  const creds = resolveCreds()
  if (!creds) throw new Error('Kein Kalender eingebunden')
  const client = clientFor(creds)
  await client.login()
  await client.deleteCalendarObject({ calendarObject: { url: href, etag } })
}

/** Fetch events (with simple recurrence expansion) within a date range. */
export async function fetchEvents(startISO: string, endISO: string): Promise<CalEvent[]> {
  const creds = resolveCreds()
  if (!creds) return []
  const client = clientFor(creds)
  await client.login()
  const calendars = await client.fetchCalendars()
  const rangeStart = new Date(startISO)
  const rangeEnd = new Date(endISO)
  const out: CalEvent[] = []

  for (const cal of calendars) {
    const comps = (cal.components ?? []) as string[]
    if (comps.length && !comps.includes('VEVENT')) continue // skip task/contact collections
    const calName = typeof cal.displayName === 'string' ? cal.displayName : ''
    const color = typeof cal.calendarColor === 'string' ? cal.calendarColor : undefined

    let objects
    try {
      objects = await client.fetchCalendarObjects({
        calendar: cal,
        timeRange: { start: startISO, end: endISO }
      })
    } catch {
      continue
    }

    for (const obj of objects) {
      if (!obj.data) continue
      // node-ical's types are loose/complex; treat parsed entries as records.
      let parsed: Record<string, Record<string, unknown>>
      try {
        parsed = nodeIcal.sync.parseICS(obj.data) as unknown as Record<
          string,
          Record<string, unknown>
        >
      } catch {
        continue
      }
      for (const key of Object.keys(parsed)) {
        const v = parsed[key]
        if (!v || v.type !== 'VEVENT' || !v.start) continue
        const startD = v.start as Date
        const endD = (v.end as Date) ?? startD
        const durMs = endD.getTime() - startD.getTime()
        const allDay = v.datetype === 'date'
        const base = {
          uid: str(v.uid) || key,
          summary: str(v.summary) || '(ohne Titel)',
          allDay,
          location: str(v.location),
          description: str(v.description),
          calendar: calName,
          color,
          href: obj.url,
          etag: obj.etag ?? ''
        }
        const rrule = v.rrule as
          | { between: (a: Date, b: Date, inc?: boolean) => Date[] }
          | undefined
        if (rrule) {
          for (const occ of rrule.between(rangeStart, rangeEnd, true)) {
            out.push({
              ...base,
              recurring: true,
              start: occ.toISOString(),
              end: new Date(occ.getTime() + durMs).toISOString()
            })
          }
        } else if (endD >= rangeStart && startD <= rangeEnd) {
          out.push({
            ...base,
            recurring: false,
            start: startD.toISOString(),
            end: endD.toISOString()
          })
        }
      }
    }
  }

  out.sort((a, b) => (a.start < b.start ? -1 : 1))
  return out
}
