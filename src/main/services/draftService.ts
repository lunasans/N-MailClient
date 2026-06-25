import MailComposer from 'nodemailer/lib/mail-composer'
import type { DraftRef, SendRequest } from '../types'
import { getCredentials } from './accountStore'
import { appendDraft, expungeMessage, findFolderByRole } from './imapService'

/**
 * Auto-save support: serialize the in-progress message to MIME and APPEND it to
 * the Drafts folder. To avoid piling up versions, the previously saved draft is
 * expunged first. Returns a reference to the new draft.
 */
export async function saveDraft(req: SendRequest, prev?: DraftRef): Promise<DraftRef | null> {
  const creds = getCredentials(req.accountId)
  if (!creds) throw new Error('Konto nicht gefunden')
  const { account } = creds

  const mailOptions = {
    from: req.from || (account.name ? `${account.name} <${account.email}>` : account.email),
    to: req.to || undefined,
    cc: req.cc || undefined,
    bcc: req.bcc || undefined,
    subject: req.subject,
    text: req.text,
    html: req.html || undefined,
    inReplyTo: req.inReplyTo || undefined,
    references: req.references && req.references.length ? req.references : undefined,
    // Picked files (by path); forwarded originals are only attached on real send.
    attachments: (req.attachments ?? []).map((a) => ({ path: a.path, filename: a.filename }))
  }

  const raw = await new MailComposer(mailOptions).compile().build()
  const folder = (await findFolderByRole(req.accountId, 'drafts')) ?? 'Drafts'

  // Remove the previous version first so the folder keeps a single draft.
  if (prev) {
    try {
      await expungeMessage(req.accountId, prev.folder, prev.uid)
    } catch {
      /* ignore — previous draft may already be gone */
    }
  }

  const uid = await appendDraft(req.accountId, folder, raw)
  return uid !== undefined ? { folder, uid } : null
}

/** Delete a saved draft (e.g. after sending or discarding). */
export async function deleteDraft(accountId: string, ref: DraftRef): Promise<void> {
  await expungeMessage(accountId, ref.folder, ref.uid)
}
