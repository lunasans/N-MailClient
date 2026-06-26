import { BrowserWindow, dialog } from 'electron'
import { readFileSync, writeFileSync } from 'fs'
import { exportData, importData } from './db'
import { getAccounts } from './accountStore'
import { startIdle, stopIdle } from './idleService'
import type { SaveResult } from '../types'

// Backup / restore of the app configuration (accounts, labels, calendar, PGP
// keys) plus the renderer's preferences. Secrets remain safeStorage-encrypted in
// the file, so a backup restores fully only on the same OS user/machine (DPAPI).
// On another machine the accounts import but passwords must be re-entered.

const MAGIC = 'n-mailclient'

interface BackupFile {
  app: typeof MAGIC
  kind: 'settings-backup'
  version: number
  exportedAt: string
  data: ReturnType<typeof exportData>
  prefs: Record<string, string>
}

/** Write a backup file (config + renderer prefs) via a save dialog. */
export async function exportSettings(prefs: Record<string, string>): Promise<SaveResult> {
  const win = BrowserWindow.getFocusedWindow() ?? undefined
  const stamp = new Date().toISOString().slice(0, 10)
  const res = await dialog.showSaveDialog(win!, {
    title: 'Einstellungen exportieren',
    defaultPath: `n-mailclient-backup-${stamp}.json`,
    filters: [{ name: 'JSON', extensions: ['json'] }]
  })
  if (res.canceled || !res.filePath) return { canceled: true }
  const payload: BackupFile = {
    app: MAGIC,
    kind: 'settings-backup',
    version: 1,
    exportedAt: new Date().toISOString(),
    data: exportData(),
    prefs
  }
  writeFileSync(res.filePath, JSON.stringify(payload, null, 2), 'utf-8')
  return { canceled: false, path: res.filePath }
}

/** Read a backup file, restore the config, and return the prefs for the renderer. */
export async function importSettings(): Promise<{
  canceled: boolean
  prefs?: Record<string, string>
}> {
  const win = BrowserWindow.getFocusedWindow() ?? undefined
  const res = await dialog.showOpenDialog(win!, {
    title: 'Einstellungen importieren',
    filters: [{ name: 'JSON', extensions: ['json'] }],
    properties: ['openFile']
  })
  if (res.canceled || !res.filePaths[0]) return { canceled: true }

  const parsed = JSON.parse(readFileSync(res.filePaths[0], 'utf-8')) as Partial<BackupFile>
  if (parsed.app !== MAGIC || parsed.kind !== 'settings-backup' || !parsed.data) {
    throw new Error('Keine gültige N-MailClient-Sicherung.')
  }

  // Stop watchers for the old accounts, swap config, then start for the new set.
  for (const acc of getAccounts()) stopIdle(acc.id)
  importData(parsed.data)
  for (const acc of getAccounts()) startIdle(acc.id)

  return { canceled: false, prefs: parsed.prefs ?? {} }
}
