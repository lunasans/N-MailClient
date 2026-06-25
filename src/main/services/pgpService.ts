import { randomUUID } from 'crypto'
import * as openpgp from 'openpgp'
import type { Key, KeyID, VerificationResult } from 'openpgp'
import type { PgpGenerateInput, PgpInfo, PgpKeyInfo } from '../types'
import { decryptPassword, encryptPassword, getPgpKeys, setPgpKeys, type StoredPgpKey } from './db'

// PGP key management (OpenPGP.js). Private keys are stored passphrase-removed but
// encrypted at rest via safeStorage (same approach as IMAP/WebDAV passwords), so
// decryption/signing at runtime needs no extra prompt.

function toInfo(k: StoredPgpKey): PgpKeyInfo {
  return {
    id: k.id,
    fingerprint: k.fingerprint,
    userIds: k.userIds,
    hasPrivate: Boolean(k.privateSecret),
    created: k.created
  }
}

function metaFromKey(key: Key): { fingerprint: string; userIds: string[]; created: string } {
  return {
    fingerprint: key.getFingerprint(),
    userIds: key.getUserIDs(),
    created: key.getCreationTime().toISOString()
  }
}

/** Insert a key, or merge into an existing entry with the same fingerprint. */
function upsert(entry: StoredPgpKey): PgpKeyInfo {
  const keys = getPgpKeys()
  const idx = keys.findIndex((k) => k.fingerprint === entry.fingerprint)
  if (idx >= 0) {
    const merged: StoredPgpKey = {
      ...keys[idx],
      ...entry,
      id: keys[idx].id,
      // Never drop a private key we already hold when re-importing the public one.
      privateSecret: entry.privateSecret ?? keys[idx].privateSecret
    }
    keys[idx] = merged
    setPgpKeys(keys)
    return toInfo(merged)
  }
  keys.push(entry)
  setPgpKeys(keys)
  return toInfo(entry)
}

export function listKeys(): PgpKeyInfo[] {
  return getPgpKeys().map(toInfo)
}

/** Import a public key (e.g. a contact's). */
export async function importPublicKey(armored: string): Promise<PgpKeyInfo> {
  const key = await openpgp.readKey({ armoredKey: armored })
  return upsert({ id: randomUUID(), ...metaFromKey(key), publicArmored: key.toPublic().armor() })
}

/** Import your own private key; the passphrase is used once to unlock it for storage. */
export async function importPrivateKey(armored: string, passphrase: string): Promise<PgpKeyInfo> {
  let priv = await openpgp.readPrivateKey({ armoredKey: armored })
  if (!priv.isDecrypted()) {
    priv = await openpgp.decryptKey({ privateKey: priv, passphrase })
  }
  return upsert({
    id: randomUUID(),
    ...metaFromKey(priv),
    publicArmored: priv.toPublic().armor(),
    privateSecret: encryptPassword(priv.armor())
  })
}

/** Generate a new key pair (Curve25519). */
export async function generateKey(input: PgpGenerateInput): Promise<PgpKeyInfo> {
  const { privateKey, publicKey } = await openpgp.generateKey({
    type: 'ecc',
    curve: 'curve25519',
    userIDs: [{ name: input.name, email: input.email }],
    passphrase: input.passphrase || undefined,
    format: 'armored'
  })
  let priv = await openpgp.readPrivateKey({ armoredKey: privateKey })
  if (input.passphrase) {
    priv = await openpgp.decryptKey({ privateKey: priv, passphrase: input.passphrase })
  }
  return upsert({
    id: randomUUID(),
    ...metaFromKey(priv),
    publicArmored: publicKey,
    privateSecret: encryptPassword(priv.armor())
  })
}

/** Armored public key for sharing. */
export function exportPublicKey(id: string): string {
  const k = getPgpKeys().find((x) => x.id === id)
  if (!k) throw new Error('Schlüssel nicht gefunden.')
  return k.publicArmored
}

/** Armored, passphrase-free private key (own keys only) — for backup/export. */
export function exportPrivateKey(id: string): string {
  const k = getPgpKeys().find((x) => x.id === id)
  if (!k?.privateSecret) throw new Error('Kein privater Schlüssel vorhanden.')
  return decryptPassword(k.privateSecret)
}

export function removeKey(id: string): void {
  setPgpKeys(getPgpKeys().filter((k) => k.id !== id))
}

/** Decrypted private keys (for later decrypt/sign use). */
export async function getPrivateKeys(): Promise<openpgp.PrivateKey[]> {
  const out: openpgp.PrivateKey[] = []
  for (const k of getPgpKeys()) {
    if (!k.privateSecret) continue
    out.push(await openpgp.readPrivateKey({ armoredKey: decryptPassword(k.privateSecret) }))
  }
  return out
}

/** All stored public keys (for verify/encrypt use). */
export async function getPublicKeys(): Promise<openpgp.PublicKey[]> {
  const out: openpgp.PublicKey[] = []
  for (const k of getPgpKeys()) {
    out.push(await openpgp.readKey({ armoredKey: k.publicArmored }))
  }
  return out
}

/** Resolve a signer key id to a stored key's first user id, if known. */
function signerName(keyID: KeyID, pubs: openpgp.PublicKey[]): string | undefined {
  const hex = keyID.toHex()
  for (const p of pubs) {
    if (p.getKeyID().toHex() === hex) return p.getUserIDs()[0]
    if (p.getSubkeys().some((sk) => sk.getKeyID().toHex() === hex)) return p.getUserIDs()[0]
  }
  return hex
}

async function summarize(
  signatures: VerificationResult[] | undefined,
  pubs: openpgp.PublicKey[]
): Promise<{ signed: boolean; verified: boolean | null; signer?: string }> {
  if (!signatures || signatures.length === 0) return { signed: false, verified: null }
  const s = signatures[0]
  const signer = signerName(s.keyID, pubs)
  if (pubs.length === 0) return { signed: true, verified: null, signer }
  try {
    await s.verified // rejects if the signature is invalid
    return { signed: true, verified: true, signer }
  } catch {
    return { signed: true, verified: false, signer }
  }
}

/** Lowercased e-mail addresses found in a key's user IDs. */
function keyEmails(key: Key): string[] {
  return key.getUserIDs().map((u) => {
    const m = u.match(/<([^>]+)>/)
    return (m ? m[1] : u).trim().toLowerCase()
  })
}

/** Match recipient e-mails to stored public keys; report any without a key. */
export async function findRecipientKeys(
  emails: string[]
): Promise<{ keys: openpgp.PublicKey[]; missing: string[] }> {
  const pubs = await getPublicKeys()
  const keys: openpgp.PublicKey[] = []
  const missing: string[] = []
  for (const email of emails) {
    const e = email.toLowerCase()
    const key = pubs.find((p) => keyEmails(p).includes(e))
    if (key) {
      if (!keys.includes(key)) keys.push(key)
    } else {
      missing.push(email)
    }
  }
  return { keys, missing }
}

/** Pick a private key to sign with (preferring one matching the sender). */
export async function findSigningKey(email?: string): Promise<openpgp.PrivateKey | null> {
  const privs = await getPrivateKeys()
  if (privs.length === 0) return null
  if (email) {
    const match = privs.find((p) => keyEmails(p).includes(email.toLowerCase()))
    if (match) return match
  }
  return privs[0]
}

/** Build a PGP/MIME multipart/encrypted body (optionally signed). */
export async function encryptMimeBody(
  innerMime: Buffer,
  recipientKeys: openpgp.PublicKey[],
  signingKey?: openpgp.PrivateKey | null
): Promise<{ contentType: string; body: string }> {
  const message = await openpgp.createMessage({ binary: new Uint8Array(innerMime) })
  const armored = (await openpgp.encrypt({
    message,
    encryptionKeys: recipientKeys,
    signingKeys: signingKey ?? undefined,
    format: 'armored'
  })) as string
  const boundary = 'nmc-pgp-' + randomUUID()
  const body =
    `--${boundary}\r\n` +
    'Content-Type: application/pgp-encrypted\r\n\r\n' +
    'Version: 1\r\n\r\n' +
    `--${boundary}\r\n` +
    'Content-Type: application/octet-stream; name="encrypted.asc"\r\n\r\n' +
    `${armored}\r\n` +
    `--${boundary}--\r\n`
  return {
    contentType: `multipart/encrypted; protocol="application/pgp-encrypted"; boundary="${boundary}"`,
    body
  }
}

/** Build a PGP/MIME multipart/signed body (detached signature). */
export async function signMimeBody(
  innerMime: Buffer,
  signingKey: openpgp.PrivateKey
): Promise<{ contentType: string; body: string }> {
  const text = innerMime.toString('utf8')
  const message = await openpgp.createMessage({ text })
  const signature = (await openpgp.sign({
    message,
    signingKeys: signingKey,
    detached: true,
    format: 'armored'
  })) as string
  const boundary = 'nmc-pgp-' + randomUUID()
  const body =
    `--${boundary}\r\n` +
    `${text}\r\n` +
    `--${boundary}\r\n` +
    'Content-Type: application/pgp-signature; name="signature.asc"\r\n\r\n' +
    `${signature}\r\n` +
    `--${boundary}--\r\n`
  return {
    contentType: `multipart/signed; micalg="pgp-sha512"; protocol="application/pgp-signature"; boundary="${boundary}"`,
    body
  }
}

const PGP_MESSAGE = /-----BEGIN PGP MESSAGE-----[\s\S]*?-----END PGP MESSAGE-----/
const PGP_SIGNED = /-----BEGIN PGP SIGNED MESSAGE-----[\s\S]*?-----END PGP SIGNATURE-----/

/**
 * Detect and process PGP content in a raw message. Handles inline PGP and the
 * armored payload of PGP/MIME (multipart/encrypted). Returns the cleartext plus
 * a status, or null when the message contains no recognizable PGP block.
 */
export async function processIncoming(
  rawText: string
): Promise<{ info: PgpInfo; cleartext: string; isMime: boolean } | null> {
  const enc = rawText.match(PGP_MESSAGE)
  if (enc) {
    const privs = await getPrivateKeys()
    if (privs.length === 0) {
      return {
        info: {
          encrypted: true,
          signed: false,
          verified: null,
          error: 'Kein privater Schlüssel zum Entschlüsseln vorhanden.'
        },
        cleartext: '',
        isMime: false
      }
    }
    try {
      const message = await openpgp.readMessage({ armoredMessage: enc[0] })
      const pubs = await getPublicKeys()
      const { data, signatures } = await openpgp.decrypt({
        message,
        decryptionKeys: privs,
        verificationKeys: pubs.length ? pubs : undefined
      })
      const text = typeof data === 'string' ? data : Buffer.from(data as Uint8Array).toString('utf8')
      const sig = await summarize(signatures, pubs)
      const isMime = /content-type:/i.test(text.slice(0, 800))
      return {
        info: { encrypted: true, signed: sig.signed, verified: sig.verified, signer: sig.signer },
        cleartext: text,
        isMime
      }
    } catch (err) {
      return {
        info: {
          encrypted: true,
          signed: false,
          verified: null,
          error: err instanceof Error ? err.message : 'Entschlüsselung fehlgeschlagen.'
        },
        cleartext: '',
        isMime: false
      }
    }
  }

  const clear = rawText.match(PGP_SIGNED)
  if (clear) {
    try {
      const cleartextMessage = await openpgp.readCleartextMessage({ cleartextMessage: clear[0] })
      const pubs = await getPublicKeys()
      let verified: boolean | null = null
      let signer: string | undefined
      if (pubs.length) {
        const result = await openpgp.verify({ message: cleartextMessage, verificationKeys: pubs })
        const sig = await summarize(result.signatures, pubs)
        verified = sig.verified
        signer = sig.signer
      }
      return {
        info: { encrypted: false, signed: true, verified, signer },
        cleartext: cleartextMessage.getText(),
        isMime: false
      }
    } catch (err) {
      return {
        info: {
          encrypted: false,
          signed: true,
          verified: false,
          error: err instanceof Error ? err.message : 'Signaturprüfung fehlgeschlagen.'
        },
        cleartext: '',
        isMime: false
      }
    }
  }

  return null
}
