import net from 'net'
import { resolveMx } from 'dns/promises'
import type { RecipientTls } from '../types'

// Probe whether a recipient domain's mail server offers transport encryption
// (STARTTLS). This checks hop encryption to the recipient's MX, NOT end-to-end.
// Outbound port 25 may be blocked by the local network → result 'unknown'.

const PORT = 25
const TIMEOUT_MS = 6000
const CACHE_TTL = 30 * 60 * 1000

const cache = new Map<string, { value: RecipientTls; ts: number }>()

/** Read the SMTP greeting + EHLO response and check for STARTTLS. */
function probe(host: string, ehloName: string): Promise<boolean> {
  return new Promise((resolve, reject) => {
    const socket = net.connect({ host, port: PORT })
    let buffer = ''
    let sentEhlo = false
    let settled = false

    const done = (result: boolean | Error): void => {
      if (settled) return
      settled = true
      socket.destroy()
      if (result instanceof Error) reject(result)
      else resolve(result)
    }

    socket.setTimeout(TIMEOUT_MS, () => done(new Error('timeout')))
    socket.on('error', (err) => done(err))
    socket.on('close', () => done(new Error('closed')))
    socket.on('data', (chunk) => {
      buffer += chunk.toString('utf8')
      if (!sentEhlo && /^220[ -]/m.test(buffer)) {
        sentEhlo = true
        buffer = ''
        socket.write(`EHLO ${ehloName}\r\n`)
        return
      }
      if (sentEhlo) {
        if (/^250[ ]/m.test(buffer)) {
          // Final EHLO line received — decide based on advertised STARTTLS.
          done(/starttls/i.test(buffer))
        }
      }
    })
  })
}

/** Fetch the MTA-STS policy mode for a domain (enforce/testing/none). */
async function fetchMtaSts(domain: string): Promise<'enforce' | 'testing' | 'none'> {
  try {
    const ctrl = new AbortController()
    const t = setTimeout(() => ctrl.abort(), 5000)
    const res = await fetch(`https://mta-sts.${domain}/.well-known/mta-sts.txt`, {
      signal: ctrl.signal
    })
    clearTimeout(t)
    if (!res.ok) return 'none'
    const text = await res.text()
    const m = text.match(/mode\s*:\s*(enforce|testing|none)/i)
    return m ? (m[1].toLowerCase() as 'enforce' | 'testing' | 'none') : 'none'
  } catch {
    return 'none'
  }
}

/** Check a recipient domain's MX for STARTTLS + MTA-STS (cached per domain). */
export async function checkRecipientTls(domain: string): Promise<RecipientTls> {
  const key = domain.toLowerCase().trim()
  if (!key) return { domain, status: 'unknown' }
  const hit = cache.get(key)
  if (hit && Date.now() - hit.ts < CACHE_TTL) return hit.value

  let value: RecipientTls
  try {
    const mx = await resolveMx(key)
    if (!mx.length) {
      value = { domain: key, status: 'no-mx' }
    } else {
      const host = mx.sort((a, b) => a.priority - b.priority)[0].exchange
      const [starttls, mtaSts] = await Promise.all([probe(host, 'n-mailclient.local'), fetchMtaSts(key)])
      value = {
        domain: key,
        status: starttls ? 'supported' : 'unsupported',
        mx: host,
        mtaSts
      }
    }
  } catch {
    // DNS failure, port 25 blocked, timeout, etc.
    value = { domain: key, status: 'unknown' }
  }

  cache.set(key, { value, ts: Date.now() })
  return value
}
