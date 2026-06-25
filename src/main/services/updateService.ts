import { app, BrowserWindow } from 'electron'
import electronUpdater from 'electron-updater'
import type { UpdateStatus } from '../types'

const { autoUpdater } = electronUpdater

let lastStatus: UpdateStatus | null = null

/** Push an update status to every open renderer window. */
function broadcast(status: UpdateStatus): void {
  lastStatus = status
  for (const win of BrowserWindow.getAllWindows()) {
    win.webContents.send('update:status', status)
  }
}

/** The most recent status (so a freshly-opened window can sync). */
export function getStatus(): UpdateStatus | null {
  return lastStatus
}

/** Wire the auto-updater events to the renderer. Call once after app ready. */
export function initUpdater(): void {
  // Download automatically so the UI can show progress; install on user request.
  autoUpdater.autoDownload = true
  autoUpdater.autoInstallOnAppQuit = true

  autoUpdater.on('checking-for-update', () => broadcast({ state: 'checking' }))
  autoUpdater.on('update-available', (info) =>
    broadcast({ state: 'available', version: info.version })
  )
  autoUpdater.on('update-not-available', () => broadcast({ state: 'none' }))
  autoUpdater.on('download-progress', (p) =>
    broadcast({ state: 'downloading', percent: Math.round(p.percent) })
  )
  autoUpdater.on('update-downloaded', (info) =>
    broadcast({ state: 'downloaded', version: info.version })
  )
  autoUpdater.on('error', (err) =>
    broadcast({ state: 'error', message: err?.message || 'Update fehlgeschlagen.' })
  )
}

/** Check for updates (no-op with a friendly status outside a packaged build). */
export async function checkForUpdates(): Promise<void> {
  if (!app.isPackaged) {
    broadcast({ state: 'error', message: 'Updates sind nur in der installierten App verfügbar.' })
    return
  }
  try {
    await autoUpdater.checkForUpdates()
  } catch (err) {
    broadcast({
      state: 'error',
      message: err instanceof Error ? err.message : 'Update-Prüfung fehlgeschlagen.'
    })
  }
}

/** Quit and install a previously downloaded update. */
export function quitAndInstall(): void {
  autoUpdater.quitAndInstall()
}
