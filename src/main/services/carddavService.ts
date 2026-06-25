import { randomUUID } from 'crypto'
import { DAVClient } from 'tsdav'
import type { AddressBookInfo, Contact, ContactInput, ContactUpdate } from '../types'
import { decryptPassword, getCalendar } from './db'

function clientFor(creds: { serverUrl: string; user: string; password: string }): DAVClient {
  return new DAVClient({
    serverUrl: creds.serverUrl,
    credentials: { username: creds.user, password: creds.password },
    authMethod: 'Basic',
    defaultAccountType: 'carddav'
  })
}

/**
 * Contacts via CardDAV. Reuses the stored DAV connection (the same one used for
 * the calendar — Nextcloud serves CalDAV and CardDAV under the same base URL).
 */

function resolveCreds(): { serverUrl: string; user: string; password: string } | null {
  const cfg = getCalendar()
  if (!cfg) return null
  return { serverUrl: cfg.serverUrl, user: cfg.user, password: decryptPassword(cfg.secret) }
}

/** Unfold (RFC 6350) and split a vCard into logical lines. */
function unfold(data: string): string[] {
  return data.replace(/\r\n[ \t]/g, '').replace(/\n[ \t]/g, '').split(/\r?\n/)
}

function parseVCard(data: string, href: string, etag: string): Contact {
  const emails: string[] = []
  const phones: string[] = []
  let fn = ''
  let nName = ''
  let org = ''
  let uid = ''
  let photo: string | undefined
  for (const line of unfold(data)) {
    const idx = line.indexOf(':')
    if (idx < 0) continue
    const head = line.slice(0, idx)
    const val = line.slice(idx + 1).trim()
    const name = head.split(';')[0].toUpperCase()
    if (name === 'FN') fn = val
    else if (name === 'EMAIL' && val) emails.push(val)
    else if (name === 'TEL' && val) phones.push(val)
    else if (name === 'ORG') org = val.replace(/;/g, ' ').trim()
    else if (name === 'N') nName = val.split(';').filter(Boolean).reverse().join(' ')
    else if (name === 'UID') uid = val
    else if (name === 'PHOTO' && val && !photo) {
      if (val.startsWith('data:') || /^https?:/i.test(val)) {
        photo = val
      } else {
        // inline base64 (vCard 3): PHOTO;ENCODING=b;TYPE=JPEG:<base64>
        const type = (head.match(/TYPE=([A-Za-z]+)/i)?.[1] ?? 'jpeg').toLowerCase()
        photo = `data:image/${type};base64,${val.replace(/\s+/g, '')}`
      }
    }
  }
  return {
    id: uid || href,
    fullName: fn || nName || emails[0] || '(ohne Namen)',
    org,
    emails: [...new Set(emails)],
    phones: [...new Set(phones)],
    photo,
    href,
    etag
  }
}

function icsEscape(s: string): string {
  return s.replace(/\\/g, '\\\\').replace(/;/g, '\\;').replace(/,/g, '\\,').replace(/\n/g, '\\n')
}

function buildVCard(uid: string, input: ContactInput): string {
  const lines = ['BEGIN:VCARD', 'VERSION:3.0', `UID:${uid}`]
  lines.push(`FN:${icsEscape(input.fullName)}`, `N:${icsEscape(input.fullName)};;;;`)
  if (input.org) lines.push(`ORG:${icsEscape(input.org)}`)
  for (const e of input.emails) if (e.trim()) lines.push(`EMAIL:${e.trim()}`)
  for (const p of input.phones) if (p.trim()) lines.push(`TEL:${p.trim()}`)
  lines.push('END:VCARD')
  return lines.join('\r\n')
}

export async function fetchContacts(): Promise<Contact[]> {
  const creds = resolveCreds()
  if (!creds) return []
  const client = clientFor(creds)
  await client.login()
  const books = await client.fetchAddressBooks()
  const out: Contact[] = []
  for (const book of books) {
    let cards
    try {
      cards = await client.fetchVCards({ addressBook: book })
    } catch {
      continue
    }
    for (const card of cards) {
      if (!card.data) continue
      out.push(parseVCard(card.data, card.url, card.etag ?? ''))
    }
  }
  return out.sort((a, b) => a.fullName.localeCompare(b.fullName, 'de'))
}

export async function listAddressBooks(): Promise<AddressBookInfo[]> {
  const creds = resolveCreds()
  if (!creds) return []
  const client = clientFor(creds)
  await client.login()
  const books = await client.fetchAddressBooks()
  return books.map((b) => ({
    url: b.url,
    displayName: typeof b.displayName === 'string' ? b.displayName : b.url
  }))
}

export async function createContact(input: ContactInput): Promise<void> {
  const creds = resolveCreds()
  if (!creds) throw new Error('Keine Verbindung')
  const client = clientFor(creds)
  await client.login()
  const uid = `${randomUUID()}@neuhaus-mail`
  await client.createVCard({
    addressBook: { url: input.addressBookUrl } as Parameters<typeof client.createVCard>[0]['addressBook'],
    filename: `${uid}.vcf`,
    vCardString: buildVCard(uid, input)
  })
}

export async function updateContact(input: ContactUpdate): Promise<void> {
  const creds = resolveCreds()
  if (!creds) throw new Error('Keine Verbindung')
  const client = clientFor(creds)
  await client.login()
  await client.updateVCard({
    vCard: { url: input.href, etag: input.etag, data: buildVCard(input.uid, input) }
  })
}

export async function deleteContact(href: string, etag: string): Promise<void> {
  const creds = resolveCreds()
  if (!creds) throw new Error('Keine Verbindung')
  const client = clientFor(creds)
  await client.login()
  await client.deleteVCard({ vCard: { url: href, etag } })
}
