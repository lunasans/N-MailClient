import net from 'net'
import tls from 'tls'
import { getCredentials } from './accountStore'
import type { SieveScript } from '../types'

// Minimal ManageSieve client (RFC 5804) over a raw TCP socket.
// Flow: connect (4190) -> read greeting -> STARTTLS -> TLS handshake ->
// read capabilities -> AUTHENTICATE PLAIN -> run command -> LOGOUT.
// No external dependency: keeps the main bundle lean and avoids ESM/CJS issues.

const PORT = 4190
const TIMEOUT_MS = 15000
const NUL = String.fromCharCode(0)

/** Quote a string for the ManageSieve protocol (RFC 5804 section 1.6). */
function quote(s: string): string {
  return '"' + s.replace(/\\/g, '\\\\').replace(/"/g, '\\"') + '"'
}

/**
 * Reads framed ManageSieve responses from a socket. The protocol mixes
 * CRLF-terminated lines with {n} literals (whose payload may contain CRLF),
 * and every server response ends with a line starting OK / NO / BYE.
 */
class SieveConnection {
  private socket: net.Socket
  private buffer = Buffer.alloc(0)
  private waiter: { resolve: (b: Buffer) => void; reject: (e: Error) => void } | null = null

  constructor(socket: net.Socket) {
    this.socket = socket
    this.bind()
  }

  /** Re-bind to a new socket (after the TLS upgrade) keeping any buffered bytes. */
  attach(socket: net.Socket): void {
    this.socket.removeAllListeners('data')
    this.socket.removeAllListeners('error')
    this.socket.removeAllListeners('close')
    this.socket = socket
    this.bind()
  }

  private bind(): void {
    this.socket.on('data', (chunk: Buffer) => {
      this.buffer = Buffer.concat([this.buffer, chunk])
      this.tryComplete()
    })
    this.socket.on('error', (err: Error) => this.fail(err))
    this.socket.on('close', () => this.fail(new Error('Verbindung geschlossen.')))
  }

  private fail(err: Error): void {
    if (this.waiter) {
      const w = this.waiter
      this.waiter = null
      w.reject(err)
    }
  }

  /** Find the byte offset just past a complete response, or -1 if incomplete. */
  private endOfResponse(buf: Buffer): number {
    let i = 0
    for (;;) {
      const nl = buf.indexOf('\r\n', i)
      if (nl === -1) return -1
      const line = buf.toString('utf8', i, nl)
      const lit = line.match(/\{(\d+)\+?\}\s*$/)
      if (lit) {
        const len = parseInt(lit[1], 10)
        const dataStart = nl + 2
        if (buf.length < dataStart + len) return -1
        i = dataStart + len // skip literal payload, keep scanning
        continue
      }
      if (/^(OK|NO|BYE)\b/i.test(line) || /^(OK|NO|BYE)\r?$/i.test(line)) {
        return nl + 2
      }
      i = nl + 2
    }
  }

  private tryComplete(): void {
    if (!this.waiter) return
    const end = this.endOfResponse(this.buffer)
    if (end === -1) return
    const resp = this.buffer.subarray(0, end)
    this.buffer = this.buffer.subarray(end)
    const w = this.waiter
    this.waiter = null
    w.resolve(resp)
  }

  /** Wait for the next complete server response (e.g. greeting). */
  read(): Promise<Buffer> {
    return new Promise((resolve, reject) => {
      this.waiter = { resolve, reject }
      this.tryComplete()
    })
  }

  /** Write raw bytes then read the response. */
  send(payload: string | Buffer): Promise<Buffer> {
    const p = this.read()
    this.socket.write(payload)
    return p
  }

  end(): void {
    try {
      this.socket.write('LOGOUT\r\n')
    } catch {
      /* ignore */
    }
    this.socket.end()
  }
}

/** Throw a readable error if the response's final status is not OK. */
function expectOk(resp: Buffer): string {
  const text = resp.toString('utf8')
  const lines = text.split('\r\n').filter((l) => l.length > 0)
  const status = lines[lines.length - 1] ?? ''
  if (/^OK\b/i.test(status) || /^OK\r?$/i.test(status)) return text
  // NO/BYE: surface the human-readable reason (quoted or after a literal).
  const m = status.match(/^(?:NO|BYE)\s+(?:\{\d+\+?\}\s*)?(.*)$/i)
  const reason = (m && m[1] ? m[1] : status).replace(/^"|"$/g, '').trim()
  throw new Error(reason || 'ManageSieve-Fehler.')
}

/** Open, STARTTLS, authenticate, run fn, then LOGOUT. */
async function withSieve<T>(accountId: string, fn: (conn: SieveConnection) => Promise<T>): Promise<T> {
  const creds = getCredentials(accountId)
  if (!creds) throw new Error('Konto nicht gefunden.')
  const host = creds.account.imap.host
  const user = creds.account.user
  const password = creds.password

  const plain = net.connect({ host, port: PORT })
  plain.setTimeout(TIMEOUT_MS, () => plain.destroy(new Error('Zeitüberschreitung (ManageSieve).')))

  await new Promise<void>((resolve, reject) => {
    plain.once('connect', resolve)
    plain.once('error', reject)
  })

  const conn = new SieveConnection(plain)
  let secure: tls.TLSSocket | null = null
  try {
    await conn.read() // greeting + capabilities + OK
    expectOk(await conn.send('STARTTLS\r\n'))

    secure = tls.connect({ socket: plain, servername: host, rejectUnauthorized: false })
    await new Promise<void>((resolve, reject) => {
      secure!.once('secureConnect', resolve)
      secure!.once('error', reject)
    })
    secure.setTimeout(TIMEOUT_MS, () => secure!.destroy(new Error('Zeitüberschreitung (ManageSieve).')))
    conn.attach(secure)
    await conn.read() // post-TLS capabilities + OK

    // SASL PLAIN: base64( authzid NUL authcid NUL password ), authzid empty.
    const token = Buffer.from(NUL + user + NUL + password, 'utf8').toString('base64')
    expectOk(await conn.send(`AUTHENTICATE "PLAIN" "${token}"\r\n`))

    return await fn(conn)
  } finally {
    conn.end()
    if (secure) secure.destroy()
    else plain.destroy()
  }
}

/** List all scripts and which one is active. */
export async function listScripts(accountId: string): Promise<SieveScript[]> {
  return withSieve(accountId, async (conn) => {
    const text = expectOk(await conn.send('LISTSCRIPTS\r\n'))
    const scripts: SieveScript[] = []
    for (const raw of text.split('\r\n')) {
      const line = raw.trim()
      if (!line || /^(OK|NO|BYE)\b/i.test(line)) continue
      const m = line.match(/^"((?:[^"\\]|\\.)*)"(\s+ACTIVE)?/i)
      if (m) {
        scripts.push({ name: m[1].replace(/\\(.)/g, '$1'), active: Boolean(m[2]) })
      }
    }
    return scripts
  })
}

/** Fetch a script's source. */
export async function getScript(accountId: string, name: string): Promise<string> {
  return withSieve(accountId, async (conn) => {
    const resp = await conn.send(`GETSCRIPT ${quote(name)}\r\n`)
    expectOk(resp)
    // The body is returned as a literal: {n}\r\n<n bytes>\r\nOK
    const head = resp.indexOf('{')
    if (head === -1) return ''
    const nl = resp.indexOf('\r\n', head)
    const len = parseInt(resp.toString('utf8', head + 1, nl).replace(/[^\d]/g, ''), 10)
    const start = nl + 2
    return resp.toString('utf8', start, start + len)
  })
}

/** Create or overwrite a script. */
export async function putScript(accountId: string, name: string, body: string): Promise<void> {
  await withSieve(accountId, async (conn) => {
    const payload = Buffer.from(body, 'utf8')
    const header = Buffer.from(`PUTSCRIPT ${quote(name)} {${payload.length}+}\r\n`, 'utf8')
    expectOk(await conn.send(Buffer.concat([header, payload, Buffer.from('\r\n')])))
  })
}

/** Activate a script (empty name deactivates all). */
export async function setActiveScript(accountId: string, name: string): Promise<void> {
  await withSieve(accountId, async (conn) => {
    expectOk(await conn.send(`SETACTIVE ${quote(name)}\r\n`))
  })
}

/** Delete a script. */
export async function deleteScript(accountId: string, name: string): Promise<void> {
  await withSieve(accountId, async (conn) => {
    expectOk(await conn.send(`DELETESCRIPT ${quote(name)}\r\n`))
  })
}
