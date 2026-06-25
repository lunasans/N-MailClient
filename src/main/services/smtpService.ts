import nodemailer from 'nodemailer'
import MailComposer from 'nodemailer/lib/mail-composer'
import type { SendRequest } from '../types'
import { getCredentials } from './accountStore'
import {
  appendMessage,
  findFolderByRole,
  getAllAttachments,
  markAnswered
} from './imapService'

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

  const mailOptions = {
    from: req.from || (account.name ? `${account.name} <${account.email}>` : account.email),
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
