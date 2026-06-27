import { app, BrowserWindow, Menu, nativeImage, shell, Tray } from 'electron'
import { join } from 'path'
import { initDb } from './services/db'
import { registerIpc } from './ipc'
import { getAccounts } from './services/accountStore'
import { startIdle, stopAllIdle } from './services/idleService'
import { checkForUpdates, initUpdater } from './services/updateService'

let mainWindow: BrowserWindow | null = null
let tray: Tray | null = null
let isQuitting = false
const startHidden = process.argv.includes('--hidden')

function iconPath(): string {
  return app.isPackaged
    ? join(process.resourcesPath, 'icon.png')
    : join(__dirname, '../../build/icon.png')
}

function createWindow(): void {
  const win = new BrowserWindow({
    width: 1280,
    height: 820,
    minWidth: 900,
    minHeight: 600,
    show: false,
    title: 'N-MailClient',
    icon: iconPath(),
    autoHideMenuBar: true,
    webPreferences: {
      preload: join(__dirname, '../preload/index.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: false
    }
  })
  mainWindow = win

  // Show on first paint, unless launched hidden (e.g. silent autostart to tray).
  win.on('ready-to-show', () => {
    if (!startHidden) win.show()
  })

  // Closing the window hides it to the tray; quitting happens via the tray menu.
  win.on('close', (e) => {
    if (!isQuitting) {
      e.preventDefault()
      win.hide()
    }
  })

  win.on('closed', () => {
    mainWindow = null
  })

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

function showMainWindow(): void {
  if (!mainWindow) {
    createWindow()
    return
  }
  if (mainWindow.isMinimized()) mainWindow.restore()
  mainWindow.show()
  mainWindow.focus()
}

function createTray(): void {
  let image = nativeImage.createFromPath(iconPath())
  if (!image.isEmpty()) image = image.resize({ width: 16, height: 16 })
  tray = new Tray(image)
  tray.setToolTip('N-MailClient')
  tray.setContextMenu(
    Menu.buildFromTemplate([
      { label: 'Öffnen', click: showMainWindow },
      { type: 'separator' },
      {
        label: 'Beenden',
        click: () => {
          isQuitting = true
          app.quit()
        }
      }
    ])
  )
  tray.on('click', showMainWindow)
}

app.whenReady().then(() => {
  initDb()
  registerIpc()
  createWindow()
  createTray()

  // Start live new-mail watching (IMAP IDLE) for every account.
  for (const acc of getAccounts()) startIdle(acc.id)

  // Auto-update from GitHub releases (packaged builds only); UI shows progress.
  initUpdater()
  if (app.isPackaged) {
    void checkForUpdates()
  }

  app.on('activate', () => {
    showMainWindow()
  })
})

app.on('before-quit', () => {
  isQuitting = true
  stopAllIdle()
})

// With a tray icon the app keeps running when the window is hidden/closed;
// it only quits via the tray's "Beenden" (which sets isQuitting).
app.on('window-all-closed', () => {
  if (process.platform !== 'darwin' && isQuitting) app.quit()
})
