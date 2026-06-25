import { randomUUID } from 'crypto'
import * as openpgp from 'openpgp'
import type { Key } from 'openpgp'
import type { PgpGenerateInput, PgpKeyInfo } from '../types'
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

/** All stored public keys (for later verify/encrypt use). */
export async function getPublicKeys(): Promise<openpgp.PublicKey[]> {
  const out: openpgp.PublicKey[] = []
  for (const k of getPgpKeys()) {
    out.push(await openpgp.readKey({ armoredKey: k.publicArmored }))
  }
  return out
}
