import { BrowserWindow } from 'electron'
import { ImapFlow } from 'imapflow'
import { getCredentials } from './accountStore'

/**
 * Keeps a persistent IMAP connection per account open on the INBOX. imapflow
 * auto-enters IDLE when no command is running and emits an 'exists' event when
 * new messages arrive. We forward that to the renderer via 'mail:new'.
 */

interface IdleHandle {
  stopped: boolean
  client: ImapFlow | null
}

const handles = new Map<string, IdleHandle>()

function notifyRenderer(payload: { accountId: string; folder: string; count: number }): void {
  for (const win of BrowserWindow.getAllWindows()) {
    win.webContents.send('mail:new', payload)
  }
}

const sleep = (ms: number): Promise<void> => new Promise((r) => setTimeout(r, ms))

async function runLoop(accountId: string, handle: IdleHandle): Promise<void> {
  while (!handle.stopped) {
    const creds = getCredentials(accountId)
    if (!creds) return
    const { account, password } = creds
    const client = new ImapFlow({
      host: account.imap.host,
      port: account.imap.port,
      secure: account.imap.secure,
      auth: { user: account.user, pass: password },
      logger: false,
      // Re-enter IDLE periodically so the connection stays healthy.
      maxIdleTime: 5 * 60 * 1000
    })
    handle.client = client

    client.on('exists', (data: { path: string; count: number; prevCount?: number }) => {
      const delta = data.count - (data.prevCount ?? data.count)
      if (delta > 0) notifyRenderer({ accountId, folder: data.path, count: delta })
    })
    client.on('error', () => {
      /* handled by the reconnect loop below */
    })

    try {
      await client.connect()
      await client.mailboxOpen('INBOX')
      // Stay open until the connection drops; imapflow idles on its own.
      await new Promise<void>((resolve) => {
        client.on('close', () => resolve())
      })
    } catch {
      /* connection failed — fall through to backoff + reconnect */
    } finally {
      try {
        await client.logout()
      } catch {
        /* ignore */
      }
      handle.client = null
    }

    if (handle.stopped) break
    await sleep(10000) // backoff before reconnecting
  }
}

export function startIdle(accountId: string): void {
  if (handles.has(accountId)) return
  const handle: IdleHandle = { stopped: false, client: null }
  handles.set(accountId, handle)
  void runLoop(accountId, handle)
}

export function stopIdle(accountId: string): void {
  const handle = handles.get(accountId)
  if (!handle) return
  handle.stopped = true
  try {
    handle.client?.close()
  } catch {
    /* ignore */
  }
  handles.delete(accountId)
}

/** Restart IDLE for an account (e.g. after its server settings changed). */
export function restartIdle(accountId: string): void {
  stopIdle(accountId)
  startIdle(accountId)
}

export function stopAllIdle(): void {
  for (const id of [...handles.keys()]) stopIdle(id)
}
