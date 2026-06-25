import { app, BrowserWindow, shell } from 'electron'
import { join } from 'path'
import { initDb } from './services/db'
import { registerIpc } from './ipc'
import { getAccounts } from './services/accountStore'
import { startIdle, stopAllIdle } from './services/idleService'
import { checkForUpdates, initUpdater } from './services/updateService'

function createWindow(): void {
  const win = new BrowserWindow({
    width: 1280,
    height: 820,
    minWidth: 900,
    minHeight: 600,
    show: false,
    title: 'N-MailClient',
    autoHideMenuBar: true,
    webPreferences: {
      preload: join(__dirname, '../preload/index.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: false
    }
  })

  win.on('ready-to-show', () => win.show())

  // Open external links (e.g. from mail HTML) in the system browser, not the app.
  win.webContents.setWindowOpenHandler(({ url }) => {
    shell.openExternal(url)
    return { action: 'deny' }
  })

  const devUrl = process.env['ELECTRON_RENDERER_URL']
  if (devUrl) {
    win.loadURL(devUrl)
  } else {
    win.loadFile(join(__dirname, '../renderer/index.html'))
  }
}

app.whenReady().then(() => {
  initDb()
  registerIpc()
  createWindow()

  // Start live new-mail watching (IMAP IDLE) for every account.
  for (const acc of getAccounts()) startIdle(acc.id)

  // Auto-update from GitHub releases (packaged builds only); UI shows progress.
  initUpdater()
  if (app.isPackaged) {
    void checkForUpdates()
  }

  app.on('activate', () => {
    if (BrowserWindow.getAllWindows().length === 0) createWindow()
  })
})

app.on('before-quit', () => {
  stopAllIdle()
})

app.on('window-all-closed', () => {
  if (process.platform !== 'darwin') app.quit()
})
