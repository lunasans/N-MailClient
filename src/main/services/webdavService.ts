import { createClient, type WebDAVClient } from 'webdav'
import type { ArchiveGroup, ArchiveFile, WebDavConfig } from '../types'

/**
 * WebDAV archive backend (e.g. Nextcloud). Attachments are filed under
 * <basePath>/<sender>/<filename> on the remote collection, mirroring the
 * local-folder layout so both targets behave identically in the UI.
 */

function clientFor(config: WebDavConfig, password: string): WebDAVClient {
  return createClient(config.url.replace(/\/+$/, ''), {
    username: config.user,
    password
  })
}

/** Normalize to a leading-slash, no-trailing-slash remote path. */
function normalizePath(p: string): string {
  const trimmed = p.replace(/\\/g, '/').replace(/\/+$/, '')
  return trimmed.startsWith('/') ? trimmed : '/' + trimmed
}

function joinRemote(...parts: string[]): string {
  return normalizePath(parts.map((p) => p.replace(/^\/+|\/+$/g, '')).join('/'))
}

/** Verify the connection and that the base path is reachable/creatable. */
export async function testConnection(config: WebDavConfig, password: string): Promise<void> {
  const client = clientFor(config, password)
  const base = normalizePath(config.basePath || '/')
  // A simple listing of the root validates credentials + URL.
  await client.getDirectoryContents('/')
  if (base !== '/' && !(await client.exists(base))) {
    await client.createDirectory(base, { recursive: true })
  }
}

async function uniqueRemoteName(
  client: WebDAVClient,
  dir: string,
  filename: string
): Promise<string> {
  if (!(await client.exists(joinRemote(dir, filename)))) return filename
  const dot = filename.lastIndexOf('.')
  const base = dot > 0 ? filename.slice(0, dot) : filename
  const ext = dot > 0 ? filename.slice(dot) : ''
  let i = 1
  // eslint-disable-next-line no-constant-condition
  while (true) {
    const candidate = `${base} (${i})${ext}`
    if (!(await client.exists(joinRemote(dir, candidate)))) return candidate
    i++
  }
}

/** Upload an attachment to <basePath>/<sender>/<filename>; returns the remote path. */
export async function uploadAttachment(
  config: WebDavConfig,
  password: string,
  sender: string,
  filename: string,
  content: Buffer
): Promise<string> {
  const client = clientFor(config, password)
  const dir = joinRemote(config.basePath || '/', sender)
  if (!(await client.exists(dir))) await client.createDirectory(dir, { recursive: true })
  const name = await uniqueRemoteName(client, dir, filename)
  const target = joinRemote(dir, name)
  await client.putFileContents(target, content, { overwrite: false })
  return target
}

/** List archived files grouped by sender subfolder. */
export async function listArchive(
  config: WebDavConfig,
  password: string
): Promise<ArchiveGroup[]> {
  const client = clientFor(config, password)
  const base = normalizePath(config.basePath || '/')
  if (!(await client.exists(base))) return []

  const items = (await client.getDirectoryContents(base, { deep: true })) as Array<{
    filename: string
    basename: string
    type: 'file' | 'directory'
    size: number
    lastmod: string
  }>

  const baseDepth = base === '/' ? 0 : base.split('/').filter(Boolean).length
  const groups = new Map<string, ArchiveFile[]>()

  for (const item of items) {
    if (item.type !== 'file') continue
    const segments = item.filename.split('/').filter(Boolean)
    // Sender is the first segment directly under base.
    const sender = segments[baseDepth] ?? 'unbekannt'
    const list = groups.get(sender) ?? []
    list.push({
      name: item.basename,
      path: item.filename,
      size: item.size,
      modified: new Date(item.lastmod).toISOString()
    })
    groups.set(sender, list)
  }

  return [...groups.entries()]
    .map(([sender, files]) => ({
      sender,
      files: files.sort((a, b) => (a.modified < b.modified ? 1 : -1))
    }))
    .sort((a, b) => a.sender.localeCompare(b.sender, 'de'))
}

/** Download a remote file's bytes. */
export async function downloadFile(
  config: WebDavConfig,
  password: string,
  remotePath: string
): Promise<Buffer> {
  const client = clientFor(config, password)
  const data = (await client.getFileContents(remotePath, { format: 'binary' })) as Buffer | ArrayBuffer
  return Buffer.isBuffer(data) ? data : Buffer.from(data)
}

/** Build a browser-openable URL for a remote path (best effort). */
export function webUrlFor(config: WebDavConfig, remotePath: string): string {
  const origin = config.url.replace(/\/+$/, '')
  return origin + (remotePath.startsWith('/') ? remotePath : '/' + remotePath)
}
