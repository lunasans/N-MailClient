import net from 'net'
import tls from 'tls'
import type { ProbeResult, ServerConfig } from '../types'

/**
 * Guess IMAP/SMTP settings from an email address WITHOUT needing a password.
 * We try the conventional hostnames (mail./imap./smtp.<domain>) on the standard
 * ports and confirm reachability via a TLS/TCP handshake + the server greeting
 * (IMAP "* OK", SMTP "220"). No authentication happens here — the password is
 * only entered afterwards and checked on save / first real connection.
 * The renderer always lets the user correct the result.
 */

function domainOf(email: string): string {
  const at = email.lastIndexOf('@')
  return at === -1 ? email : email.slice(at + 1)
}

/**
 * Open a connection to host:port (implicit TLS when `secure`) and read the
 * server greeting. Resolves true if it looks like the expected service.
 */
function checkGreeting(cfg: ServerConfig, expect: RegExp, timeoutMs = 6000): Promise<boolean> {
  return new Promise((resolve) => {
    let settled = false
    const done = (ok: boolean, socket: net.Socket): void => {
      if (settled) return
      settled = true
      try {
        socket.destroy()
      } catch {
        /* ignore */
      }
      resolve(ok)
    }

    const socket = cfg.secure
      ? tls.connect({ host: cfg.host, port: cfg.port, servername: cfg.host, rejectUnauthorized: false })
      : net.connect({ host: cfg.host, port: cfg.port })

    socket.setTimeout(timeoutMs, () => done(false, socket))
    socket.once('error', () => done(false, socket))
    socket.once('data', (chunk: Buffer) => {
      done(expect.test(chunk.toString('utf-8')), socket)
    })
  })
}

const IMAP_GREETING = /^\*\s+OK/i
const SMTP_GREETING = /^220[\s-]/

async function firstReachable(
  candidates: ServerConfig[],
  expect: RegExp
): Promise<{ config: ServerConfig; reachable: boolean }> {
  for (const cand of candidates) {
    if (await checkGreeting(cand, expect)) {
      return { config: cand, reachable: true }
    }
  }
  return { config: candidates[0], reachable: false }
}

export async function probeAccount(email: string): Promise<ProbeResult> {
  const domain = domainOf(email)
  const user = email

  const imapCandidates: ServerConfig[] = [
    { host: `mail.${domain}`, port: 993, secure: true },
    { host: `imap.${domain}`, port: 993, secure: true },
    { host: domain, port: 993, secure: true }
  ]
  const smtpCandidates: ServerConfig[] = [
    { host: `mail.${domain}`, port: 465, secure: true },
    { host: `smtp.${domain}`, port: 465, secure: true },
    { host: `mail.${domain}`, port: 587, secure: false },
    { host: `smtp.${domain}`, port: 587, secure: false },
    { host: domain, port: 465, secure: true }
  ]

  const [imap, smtp] = await Promise.all([
    firstReachable(imapCandidates, IMAP_GREETING),
    firstReachable(smtpCandidates, SMTP_GREETING)
  ])

  return {
    email,
    user,
    imap: imap.config,
    smtp: smtp.config,
    imapVerified: imap.reachable,
    smtpVerified: smtp.reachable
  }
}
