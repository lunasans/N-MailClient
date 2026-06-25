// Shared types — used by main, preload and renderer (Single Source of Truth).

export interface ServerConfig {
  host: string
  port: number
  /** true = implicit TLS (IMAP 993 / SMTP 465), false = STARTTLS (SMTP 587) */
  secure: boolean
}

/** WebDAV connection for an archive target (password stored separately). */
export interface WebDavConfig {
  /** Base collection URL, e.g. https://cloud.neuhaus.or.at/remote.php/dav/files/USER */
  url: string
  user: string
  /** Subfolder within the WebDAV collection where attachments are filed. */
  basePath: string
}

/** Where an account's attachments are archived. */
export type ArchiveTarget =
  | { type: 'local'; folder: string }
  | { type: 'webdav'; webdav: WebDavConfig }

/** Account as exposed to the renderer — never contains any password. */
export interface Account {
  id: string
  name: string
  email: string
  user: string
  imap: ServerConfig
  smtp: ServerConfig
  /** Bound archive target (local folder or WebDAV), if configured. */
  archive?: ArchiveTarget
  /** Plain-text signature appended when composing from this account. */
  signature?: string
  /** Custom folder order (full paths); folders not listed sort after, alphabetically. */
  folderOrder?: string[]
  /** Hex color used to mark this account (e.g. in the unified inbox). */
  color?: string
  /** Additional sender addresses (aliases) usable as "From". */
  aliases?: string[]
}

/** Editable per-account settings. */
export interface AccountSettings {
  name: string
  signature: string
  user: string
  imap: ServerConfig
  smtp: ServerConfig
  /** New password — only applied when non-empty (leave empty to keep current). */
  password?: string
  /** Custom folder order (full paths). */
  folderOrder?: string[]
  /** Hex color for this account. */
  color?: string
  /** Additional sender addresses (aliases). */
  aliases?: string[]
}

/** A file chosen to be attached to an outgoing message. */
export interface PickedAttachment {
  path: string
  filename: string
  size: number
}

/** Identifies a saved draft message in the Drafts folder. */
export interface DraftRef {
  folder: string
  uid: number
}

/** Payload to create an account (includes the plaintext password, main-process only). */
export interface NewAccount {
  name: string
  email: string
  user: string
  password: string
  imap: ServerConfig
  smtp: ServerConfig
}

/** Result of probing a domain for server settings. */
export interface ProbeResult {
  email: string
  user: string
  imap: ServerConfig
  smtp: ServerConfig
  /** Whether the guessed host/port was reachable (greeting received, no login). */
  imapVerified: boolean
  smtpVerified: boolean
}

export interface MailboxNode {
  path: string
  name: string
  /** Hierarchy delimiter used by the server (e.g. '/' or '.'). */
  delimiter: string
  /** false for container-only folders (\Noselect) that just group children. */
  selectable: boolean
  /** Special-use role if known: 'inbox' | 'sent' | 'drafts' | 'trash' | 'junk' | 'archive' */
  role?: string
  unseen?: number
}

export interface MessageSummary {
  uid: number
  subject: string
  from: string
  to: string
  date: string
  seen: boolean
  flagged: boolean
  /** \Answered flag — message has been replied to. */
  answered: boolean
  hasAttachments: boolean
  /** Custom IMAP keywords set on the message (used for labels). */
  keywords: string[]
  snippet: string
}

/** A user-defined label (mapped to an IMAP keyword). */
export interface Label {
  id: string
  name: string
  color: string
  /** The IMAP keyword stored on messages for this label. */
  keyword: string
}

/** CalDAV connection (password stored separately). */
export interface CalendarConfig {
  /** CalDAV base/server URL, e.g. https://cloud.neuhaus.or.at/remote.php/dav */
  serverUrl: string
  user: string
}

/** A writable calendar (for the event-create picker). */
export interface CalendarInfo {
  url: string
  displayName: string
  color?: string
}

/** Input for creating a new calendar event. */
export interface CalEventInput {
  calendarUrl: string
  summary: string
  startISO: string
  endISO: string
  allDay: boolean
  location: string
  description: string
}

/** An address-book contact (from CardDAV). */
export interface Contact {
  id: string
  fullName: string
  org: string
  emails: string[]
  phones: string[]
  /** Avatar as a data URI or URL, if the vCard has a PHOTO. */
  photo?: string
  /** CardDAV object URL + etag (for editing/deleting). */
  href: string
  etag: string
}

export interface AddressBookInfo {
  url: string
  displayName: string
}

export interface ContactInput {
  addressBookUrl: string
  fullName: string
  org: string
  emails: string[]
  phones: string[]
}

export interface ContactUpdate extends ContactInput {
  uid: string
  href: string
  etag: string
}

/** A single calendar event occurrence. */
export interface CalEvent {
  uid: string
  summary: string
  start: string
  end: string
  allDay: boolean
  location: string
  description: string
  calendar: string
  color?: string
  /** CalDAV object URL + etag (for editing/deleting). */
  href: string
  etag: string
  /** True for recurring-series instances (editing a single instance not supported). */
  recurring: boolean
}

/** Update an existing event (identified by its CalDAV object). */
export interface CalEventUpdate extends CalEventInput {
  uid: string
  href: string
  etag: string
}

export interface AttachmentMeta {
  filename: string
  contentType: string
  size: number
  /** index used to request the attachment content later */
  partId: string
}

export interface MessageDetail {
  uid: number
  subject: string
  from: string
  to: string
  cc: string
  bcc: string
  date: string
  /** Raw (unsanitized) HTML — the RENDERER must sanitize before display. */
  html: string | null
  text: string | null
  attachments: AttachmentMeta[]
  /** RFC822 Message-ID, for threading replies. */
  messageId: string | null
  /** Reference chain (References header), for threading. */
  references: string[]
}

export interface SendRequest {
  accountId: string
  to: string
  cc?: string
  bcc?: string
  subject: string
  text: string
  html?: string
  /** Override the From address (e.g. an alias). Falls back to the account's own. */
  from?: string
  /** Message-ID of the message being replied to (sets In-Reply-To). */
  inReplyTo?: string
  /** Reference chain for threading. */
  references?: string[]
  /** When forwarding, re-attach the original message's attachments. */
  forwardFrom?: { folder: string; uid: number }
  /** When replying, flag the original message as \Answered afterwards. */
  answeredFrom?: { folder: string; uid: number }
  /** Files to attach, by local path (read in the main process). */
  attachments?: { path: string; filename: string }[]
}

export interface SaveResult {
  canceled: boolean
  path?: string
}

export interface ArchiveFile {
  name: string
  path: string
  size: number
  /** Last modified time as ISO string. */
  modified: string
}

/** Attachments grouped by the sender folder they were filed under. */
export interface ArchiveGroup {
  sender: string
  files: ArchiveFile[]
}

export interface ArchiveListing {
  /** The bound archive target for this account, or null if none configured. */
  target: ArchiveTarget | null
  groups: ArchiveGroup[]
}

/** Identifies one attachment within a specific message. */
export interface AttachmentRef {
  accountId: string
  folder: string
  uid: number
  partId: string
  filename: string
}

/** A Sieve script on the server (via ManageSieve). */
export interface SieveScript {
  name: string
  /** True if this is the currently active script. */
  active: boolean
}

/** A PGP key as exposed to the renderer (never includes private material). */
export interface PgpKeyInfo {
  id: string
  /** Key fingerprint (hex, lowercase). */
  fingerprint: string
  /** User IDs on the key, e.g. "Name <email>". */
  userIds: string[]
  /** True if a (decryptable) private key is stored for this entry. */
  hasPrivate: boolean
  /** ISO creation date of the key. */
  created: string
}

/** Input for generating a new PGP key pair. */
export interface PgpGenerateInput {
  name: string
  email: string
  /** Optional passphrase to protect the exported private key. */
  passphrase?: string
}

/** Auto-update progress, pushed from the main process to the renderer. */
export type UpdateStatus =
  | { state: 'checking' }
  | { state: 'available'; version: string }
  | { state: 'none' }
  | { state: 'downloading'; percent: number }
  | { state: 'downloaded'; version: string }
  | { state: 'error'; message: string }

/** Uniform result wrapper so the renderer never has to try/catch IPC. */
export type IpcResult<T> = { ok: true; data: T } | { ok: false; error: string }
