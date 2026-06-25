import { randomUUID } from 'crypto'
import nodemailer from 'nodemailer'
import MailComposer from 'nodemailer/lib/mail-composer'
import mimeFuncs from 'nodemailer/lib/mime-funcs'
import type { SendRequest } from '../types'
import { getCredentials } from './accountStore'
import {
  appendMessage,
  findFolderByRole,
  getAllAttachments,
  markAnswered
} from './imapService'
import { encryptMimeBody, findRecipientKeys, findSigningKey, signMimeBody } from './pgpService'

/** Extract lowercased e-mail addresses from one or more address header strings. */
function parseEmails(...parts: (string | undefined)[]): string[] {
  const joined = parts.filter(Boolean).join(',')
  const found = joined.match(/[^\s<>,";]+@[^\s<>,";]+/g) ?? []
  return [...new Set(found.map((e) => e.toLowerCase()))]
}

/** Encode a header value (encodes only non-ASCII words; leaves addresses intact). */
function encHeader(value: string): string {
  return mimeFuncs.encodeWords(value, 'Q', 0)
}

/** Send a message via the account's SMTP server and save a copy to "Sent". */
export async function sendMessage(req: SendRequest): Promise<void> {
  const creds = getCredentials(req.accountId)
  if (!creds) throw new Error('Konto nicht gefunden')
  const { account, password } = creds

  const transport = nodemailer.createTransport({
    host: account.smtp.host,
    port: account.smtp.port,
    secure: account.smtp.secure,
    auth: { user: account.user, pass: password },
    connectionTimeout: 15000,
    greetingTimeout: 15000,
    socketTimeout: 30000
  })

  // Attachments: user-picked files (by path) plus, when forwarding, the
  // original message's attachments (by content).
  const picked = (req.attachments ?? []).map((a) => ({ path: a.path, filename: a.filename }))
  const forwarded = req.forwardFrom
    ? await getAllAttachments(req.accountId, req.forwardFrom.folder, req.forwardFrom.uid)
    : []
  const allAttachments = [...picked, ...forwarded]
  const attachments = allAttachments.length ? allAttachments : undefined

  const fromHeader =
    req.from || (account.name ? `${account.name} <${account.email}>` : account.email)
  const senderEmail = parseEmails(req.from, account.email)[0] || account.email

  // PGP/MIME path: encrypt and/or sign, then send the assembled raw message.
  if (req.pgpEncrypt || req.pgpSign) {
    const inner = await new MailComposer({ text: req.text, html: req.html || undefined, attachments })
      .compile()
      .build()

    let part: { contentType: string; body: string }
    try {
      if (req.pgpEncrypt) {
        const recipients = parseEmails(req.to, req.cc, req.bcc)
        const { keys, missing } = await findRecipientKeys(recipients)
        if (missing.length) {
          throw new Error('Kein öffentlicher PGP-Schlüssel für: ' + missing.join(', '))
        }
        const signingKey = req.pgpSign ? await findSigningKey(senderEmail) : null
        if (req.pgpSign && !signingKey) {
          throw new Error('Kein privater PGP-Schlüssel zum Signieren vorhanden.')
        }
        part = await encryptMimeBody(inner, keys, signingKey)
      } else {
        const signingKey = await findSigningKey(senderEmail)
        if (!signingKey) throw new Error('Kein privater PGP-Schlüssel zum Signieren vorhanden.')
        part = await signMimeBody(inner, signingKey)
      }
    } catch (err) {
      transport.close()
      throw err
    }

    const domain = senderEmail.split('@')[1] || 'localhost'
    const headers =
      `From: ${encHeader(fromHeader)}\r\n` +
      `To: ${encHeader(req.to)}\r\n` +
      (req.cc ? `Cc: ${encHeader(req.cc)}\r\n` : '') +
      `Subject: ${mimeFuncs.encodeWords(req.subject, 'Q', 0)}\r\n` +
      `Date: ${new Date().toUTCString().replace(/GMT$/, '+0000')}\r\n` +
      `Message-ID: <${randomUUID()}@${domain}>\r\n` +
      (req.inReplyTo ? `In-Reply-To: ${req.inReplyTo}\r\n` : '') +
      (req.references && req.references.length ? `References: ${req.references.join(' ')}\r\n` : '') +
      'MIME-Version: 1.0\r\n' +
      `Content-Type: ${part.contentType}\r\n`
    const outer = Buffer.from(headers + '\r\n' + part.body, 'utf8')

    try {
      await transport.sendMail({
        envelope: { from: senderEmail, to: parseEmails(req.to, req.cc, req.bcc) },
        raw: outer
      })
    } finally {
      transport.close()
    }

    try {
      const sentFolder = (await findFolderByRole(req.accountId, 'sent')) ?? 'Sent'
      await appendMessage(req.accountId, sentFolder, outer, ['\\Seen'])
    } catch {
      /* ignore — message was already sent */
    }
    if (req.answeredFrom) {
      try {
        await markAnswered(req.accountId, req.answeredFrom.folder, req.answeredFrom.uid)
      } catch {
        /* ignore */
      }
    }
    return
  }

  const mailOptions = {
    from: fromHeader,
    to: req.to,
    cc: req.cc || undefined,
    bcc: req.bcc || undefined,
    subject: req.subject,
    text: req.text,
    html: req.html || undefined,
    inReplyTo: req.inReplyTo || undefined,
    references: req.references && req.references.length ? req.references : undefined,
    attachments
  }

  try {
    await transport.sendMail(mailOptions)
  } finally {
    transport.close()
  }

  // Best-effort: store a copy in the Sent folder. Never fail the send over this.
  try {
    const raw = await new MailComposer(mailOptions).compile().build()
    const sentFolder = (await findFolderByRole(req.accountId, 'sent')) ?? 'Sent'
    await appendMessage(req.accountId, sentFolder, raw, ['\\Seen'])
  } catch {
    /* ignore — message was already sent */
  }

  // Best-effort: flag the original as answered when this was a reply.
  if (req.answeredFrom) {
    try {
      await markAnswered(req.accountId, req.answeredFrom.folder, req.answeredFrom.uid)
    } catch {
      /* ignore */
    }
  }
}
