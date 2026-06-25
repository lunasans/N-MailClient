import { useEffect, useState } from 'react'
import { Download, RefreshCw, X } from 'lucide-react'
import type { UpdateStatus } from '@shared/index'

/** Top banner that surfaces auto-update progress (available → downloading → ready). */
export default function UpdateBanner(): JSX.Element | null {
  const [status, setStatus] = useState<UpdateStatus | null>(null)
  const [dismissed, setDismissed] = useState(false)

  useEffect(() => {
    window.api.update.status().then((res) => {
      if (res.ok && res.data) setStatus(res.data)
    })
    const unsub = window.api.update.onStatus((s) => {
      setStatus(s)
      setDismissed(false)
    })
    return unsub
  }, [])

  if (!status || dismissed) return null
  if (status.state !== 'available' && status.state !== 'downloading' && status.state !== 'downloaded')
    return null

  return (
    <div className="flex items-center gap-3 border-b border-brand/30 bg-brand/10 px-4 py-2 text-sm">
      {status.state === 'available' && (
        <>
          <RefreshCw className="h-4 w-4 shrink-0 animate-spin text-brand" />
          <span>
            Neue Version <strong>v{status.version}</strong> verfügbar — wird heruntergeladen…
          </span>
        </>
      )}

      {status.state === 'downloading' && (
        <>
          <Download className="h-4 w-4 shrink-0 text-brand" />
          <span className="shrink-0">Update wird geladen…</span>
          <div className="h-2 max-w-xs flex-1 overflow-hidden rounded-full bg-white/60">
            <div className="h-full bg-brand transition-all" style={{ width: `${status.percent}%` }} />
          </div>
          <span className="shrink-0 tabular-nums text-gray-600">{status.percent}%</span>
        </>
      )}

      {status.state === 'downloaded' && (
        <>
          <Download className="h-4 w-4 shrink-0 text-brand" />
          <span>
            Update <strong>v{status.version}</strong> ist bereit.
          </span>
          <button
            onClick={() => window.api.update.install()}
            className="ml-auto rounded bg-brand px-3 py-1 text-xs font-medium text-white hover:bg-brand-dark"
          >
            Jetzt neu starten &amp; installieren
          </button>
          <button
            onClick={() => setDismissed(true)}
            className="rounded p-1 text-gray-500 hover:bg-white/60"
            title="Später"
          >
            <X className="h-4 w-4" />
          </button>
        </>
      )}
    </div>
  )
}
