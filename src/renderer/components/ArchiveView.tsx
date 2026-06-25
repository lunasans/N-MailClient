import { useCallback, useEffect, useState } from 'react'
import {
  Archive,
  Cloud,
  ExternalLink,
  File,
  FileText,
  FolderOpen,
  Globe,
  HardDrive,
  RefreshCw,
  Search,
  User,
  X
} from 'lucide-react'
import type { ArchiveListing, ArchiveTarget } from '@shared/index'
import { useMailStore } from '../store/useMailStore'
import WebDavSetup from './WebDavSetup'

function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KB`
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`
}

function isPdf(name: string): boolean {
  return name.toLowerCase().endsWith('.pdf')
}

function targetLabel(target: ArchiveTarget): string {
  return target.type === 'local'
    ? target.folder
    : `WebDAV: ${target.webdav.url}/${target.webdav.basePath}`
}

export default function ArchiveView(): JSX.Element {
  const accountId = useMailStore((s) => s.activeAccountId)
  const loadAccounts = useMailStore((s) => s.loadAccounts)

  const [listing, setListing] = useState<ArchiveListing | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [showWebdav, setShowWebdav] = useState(false)
  const [query, setQuery] = useState('')

  const reload = useCallback(async () => {
    if (!accountId) return
    setLoading(true)
    setError(null)
    const res = await window.api.archive.list(accountId)
    setLoading(false)
    if (res.ok) setListing(res.data)
    else setError(res.error)
  }, [accountId])

  useEffect(() => {
    reload()
  }, [reload])

  async function bindLocal(): Promise<void> {
    if (!accountId) return
    const res = await window.api.accounts.pickArchiveFolder(accountId)
    if (res.ok && res.data) {
      await loadAccounts()
      await reload()
    }
  }

  async function afterWebdavSaved(): Promise<void> {
    await loadAccounts()
    await reload()
  }

  if (!accountId) {
    return <div className="flex h-full items-center justify-center text-gray-400">Kein Konto.</div>
  }

  const target = listing?.target ?? null
  const isWebdav = target?.type === 'webdav'

  const q = query.trim().toLowerCase()
  const filteredGroups = (listing?.groups ?? [])
    .map((g) => ({
      sender: g.sender,
      files: q
        ? g.files.filter(
            (f) => f.name.toLowerCase().includes(q) || g.sender.toLowerCase().includes(q)
          )
        : g.files
    }))
    .filter((g) => g.files.length > 0)

  return (
    <div className="flex h-full flex-col overflow-hidden">
      <div className="flex items-center gap-2 border-b bg-white px-6 py-3">
        <h1 className="flex items-center gap-2 text-lg font-semibold">
          <Archive className="h-5 w-5" />
          Anhang-Archiv
        </h1>
        <div className="ml-auto flex items-center gap-2 text-sm">
          {target && (
            <>
              <span
                className="flex max-w-[320px] items-center gap-1.5 truncate text-gray-500"
                title={targetLabel(target)}
              >
                {target.type === 'webdav' ? (
                  <Cloud className="h-4 w-4 shrink-0" />
                ) : (
                  <HardDrive className="h-4 w-4 shrink-0" />
                )}
                <span className="truncate">{targetLabel(target)}</span>
              </span>
              <button onClick={reload} className="rounded border p-1.5 hover:bg-gray-50" title="Aktualisieren">
                <RefreshCw className="h-4 w-4" />
              </button>
              <button
                onClick={bindLocal}
                className="flex items-center gap-1 rounded border px-2 py-1 hover:bg-gray-50"
                title="Lokalen Ordner einbinden"
              >
                <HardDrive className="h-4 w-4" />
                Lokal
              </button>
              <button
                onClick={() => setShowWebdav(true)}
                className="flex items-center gap-1 rounded border px-2 py-1 hover:bg-gray-50"
                title="WebDAV-Ordner einbinden"
              >
                <Cloud className="h-4 w-4" />
                WebDAV
              </button>
            </>
          )}
        </div>
      </div>

      {target && (
        <div className="border-b bg-white px-6 py-2">
          <div className="flex items-center gap-2 rounded border px-2 py-1.5">
            <Search className="h-4 w-4 shrink-0 text-gray-400" />
            <input
              className="w-full text-sm outline-none"
              placeholder="Anhänge durchsuchen (Dateiname oder Absender)…"
              value={query}
              onChange={(e) => setQuery(e.target.value)}
            />
            {query && (
              <button
                onClick={() => setQuery('')}
                className="shrink-0 text-gray-400 hover:text-gray-700"
                title="Suche leeren"
              >
                <X className="h-4 w-4" />
              </button>
            )}
          </div>
        </div>
      )}

      {error && <div className="bg-red-50 px-6 py-2 text-sm text-red-700">{error}</div>}

      <div className="flex-1 overflow-y-auto px-6 py-4">
        {loading && <div className="text-gray-400">Lade…</div>}

        {!loading && !target && (
          <div className="flex h-full flex-col items-center justify-center text-center text-gray-500">
            <p className="mb-1 text-lg">Noch kein Ordner eingebunden.</p>
            <p className="mb-4 text-sm">
              Anhänge werden im gewählten Ziel nach Absender sortiert abgelegt.
            </p>
            <div className="flex gap-3">
              <button
                onClick={bindLocal}
                className="flex items-center gap-2 rounded border px-5 py-2.5 hover:bg-gray-50"
              >
                <HardDrive className="h-4 w-4" />
                Lokalen Ordner wählen
              </button>
              <button
                onClick={() => setShowWebdav(true)}
                className="flex items-center gap-2 rounded bg-brand px-5 py-2.5 text-white hover:bg-brand-dark"
              >
                <Cloud className="h-4 w-4" />
                WebDAV einbinden
              </button>
            </div>
          </div>
        )}

        {!loading && target && (listing?.groups.length ?? 0) === 0 && (
          <div className="text-gray-400">
            Noch keine Anhänge abgelegt. Öffne eine Mail mit Anhang und klicke „In Ordner ablegen".
          </div>
        )}

        {!loading && target && (listing?.groups.length ?? 0) > 0 && filteredGroups.length === 0 && (
          <div className="text-gray-400">Keine Treffer für „{query}".</div>
        )}

        {!loading &&
          filteredGroups.map((group) => (
            <div key={group.sender} className="mb-5">
              <div className="mb-1 flex items-center gap-2 text-sm font-semibold text-gray-700">
                <User className="h-4 w-4 text-gray-500" />
                <span>{group.sender}</span>
                <span className="text-xs font-normal text-gray-400">
                  ({group.files.length} {group.files.length === 1 ? 'Datei' : 'Dateien'})
                </span>
              </div>
              <div className="divide-y rounded border">
                {group.files.map((file) => (
                  <div
                    key={file.path}
                    className="flex items-center gap-3 px-3 py-1.5 text-sm hover:bg-gray-50"
                  >
                    <button
                      onClick={() =>
                        isPdf(file.name)
                          ? window.api.pdf.viewArchive(accountId, file.path)
                          : window.api.archive.open(accountId, file.path)
                      }
                      className="flex min-w-0 flex-1 items-center gap-1.5 truncate text-left text-brand hover:underline"
                      title={isPdf(file.name) ? 'Im PDF-Reader öffnen' : file.path}
                    >
                      {isPdf(file.name) ? (
                        <FileText className="h-4 w-4 shrink-0" />
                      ) : (
                        <File className="h-4 w-4 shrink-0" />
                      )}
                      <span className="truncate">{file.name}</span>
                    </button>
                    <span className="shrink-0 text-xs text-gray-400">{formatSize(file.size)}</span>
                    <span className="shrink-0 text-xs text-gray-400">
                      {new Date(file.modified).toLocaleDateString('de-DE')}
                    </span>
                    {isPdf(file.name) && (
                      <button
                        onClick={() => window.api.archive.open(accountId, file.path)}
                        className="shrink-0 rounded p-1 text-gray-500 hover:bg-gray-100"
                        title="Mit Standard-App öffnen"
                      >
                        <ExternalLink className="h-4 w-4" />
                      </button>
                    )}
                    <button
                      onClick={() => window.api.archive.reveal(accountId, file.path)}
                      className="shrink-0 rounded p-1 text-gray-500 hover:bg-gray-100"
                      title={isWebdav ? 'Im Browser öffnen' : 'Im Explorer anzeigen'}
                    >
                      {isWebdav ? <Globe className="h-4 w-4" /> : <FolderOpen className="h-4 w-4" />}
                    </button>
                  </div>
                ))}
              </div>
            </div>
          ))}
      </div>

      {showWebdav && (
        <WebDavSetup
          accountId={accountId}
          initial={target?.type === 'webdav' ? target.webdav : undefined}
          onClose={() => setShowWebdav(false)}
          onSaved={afterWebdavSaved}
        />
      )}
    </div>
  )
}
