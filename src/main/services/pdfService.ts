import { app, BrowserWindow } from 'electron'
import { join } from 'path'
import { writeFileSync } from 'fs'
import { randomUUID } from 'crypto'

/**
 * Open a PDF in its own window using Chromium's built-in PDF viewer
 * (zoom, search, print — for free). The bytes are written to a temp file
 * because the viewer loads from a URL/file, not from memory.
 */
export function openPdf(content: Buffer, name: string): void {
  const safe = (name || 'dokument.pdf').replace(/[^\w.\- ]+/g, '_')
  const tmp = join(app.getPath('temp'), `nmc-${randomUUID()}-${safe}`)
  writeFileSync(tmp, content)

  const win = new BrowserWindow({
    width: 920,
    height: 1000,
    title: name || 'PDF',
    autoHideMenuBar: true,
    webPreferences: {
      // Required to enable Chromium's PDF viewer plugin.
      plugins: true
    }
  })
  win.loadFile(tmp)
}
