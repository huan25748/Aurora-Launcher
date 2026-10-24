import { app, BrowserWindow, Menu, Tray, dialog, nativeImage, net, protocol, screen, shell } from 'electron'
import path from 'node:path'
import fs from 'node:fs'
import { pathToFileURL } from 'node:url'
import { bindWindow, emit, windowRef } from './core/bus'
import { initSettings, getSettings, updateSettings } from './core/settings'
import { dirs, ensureDirs, ensureMinecraftLayout, portableRootDir, rootDir, settingsDir } from './core/paths'
import { registerIpc } from './ipc'
import { isRunning } from './minecraft/launcher'
import { importExistingVersions, listInstances } from './minecraft/instances'
import { markBoot, markRendererReady, markShown, previousWindowFailed, startupLog, startupLogFile } from './core/startupLog'

const APP_NAME = 'Aurora Launcher'
const DEV_URL = process.env.APP_DEV_URL
const IS_DEV = !!DEV_URL

app.setName(APP_NAME)
app.setAppUserModelId('cc.aurora.launcher')
startupLog(
  `boot version=${app.getVersion()} electron=${process.versions.electron} node=${process.versions.node} packaged=${app.isPackaged} single=${process.env.AURORA_SINGLE_FILE ?? '0'}`
)
// Configuration and Chromium state live next to this launcher copy, so every folder is
// independent and never inherits another installation's settings or recent instances.
try {
  const userData = settingsDir()
  app.setPath('userData', userData)
  startupLog(`userData=${userData}`)
} catch (e) {
  startupLog('WARN failed to set userData path', e)
}

// If the previous run never managed to show a window, assume a GPU/Driver issue and
// start in software rendering mode. Users can also force this with disable-gpu.txt.
const exeDir = process.env.AURORA_EXE_DIR || path.dirname(process.execPath)
const forceSoftware = process.env.AURORA_DISABLE_GPU === '1' || fs.existsSync(path.join(exeDir, 'disable-gpu.txt'))
if (forceSoftware || previousWindowFailed()) {
  app.disableHardwareAcceleration()
  startupLog(`hardware acceleration disabled (${forceSoftware ? 'requested' : 'previous window failure'})`)
}
markBoot('main')

process.on('uncaughtException', (error) => {
  startupLog('ERROR uncaught exception in main process', error)
  try {
    dialog.showErrorBox('Aurora Launcher 发生错误', `${error.message}\n\n日志：${startupLogFile()}`)
  } catch {}
})
process.on('unhandledRejection', (reason) => {
  startupLog('ERROR unhandled promise rejection in main process', reason)
})

protocol.registerSchemesAsPrivileged([
  {
    scheme: 'aurora-file',
    privileges: { standard: true, secure: true, supportFetchAPI: true, stream: true, bypassCSP: true }
  }
])

let mainWindow: BrowserWindow | null = null
let tray: Tray | null = null
let quitting = false

function iconPath(): string | undefined {
  const candidates = [
    path.join(__dirname, '../../resources/icon.png'),
    path.join(process.resourcesPath ?? '', 'icon.png'),
    path.join(app.getAppPath(), 'resources/icon.png')
  ]
  for (const p of candidates) if (p && fs.existsSync(p)) return p
  return undefined
}

function windowVisibleOnSomeDisplay(win: BrowserWindow): boolean {
  try {
    const b = win.getBounds()
    return screen.getAllDisplays().some((display) => {
      const area = display.workArea
      return b.x < area.x + area.width && b.x + b.width > area.x && b.y < area.y + area.height && b.y + b.height > area.y
    })
  } catch {
    return true
  }
}

function revealWindow(win: BrowserWindow, reason: string): void {
  if (win.isDestroyed()) return
  if (!windowVisibleOnSomeDisplay(win)) {
    startupLog('window was off-screen; recentering')
    win.center()
  }
  if (win.isMinimized()) win.restore()
  if (!win.isVisible()) {
    startupLog(`show window (${reason})`)
    win.show()
  }
  try {
    win.focus()
  } catch {}
  markShown()
}

function createWindow(): void {
  const settings = getSettings()
  let bounds = settings.windowBounds
  if (bounds && bounds.x !== undefined && bounds.y !== undefined) {
    const width = bounds.width ?? 1400
    const height = bounds.height ?? 880
    const onScreen = screen.getAllDisplays().some((display) => {
      const area = display.workArea
      return bounds!.x! < area.x + area.width && bounds!.x! + width > area.x && bounds!.y! < area.y + area.height && bounds!.y! + height > area.y
    })
    if (!onScreen) {
      startupLog('saved window position is off-screen; using default position')
      bounds = undefined
    }
  }
  const preloadPath = path.join(__dirname, '../preload/index.js')
  const rendererPath = path.join(__dirname, '../renderer/index.html')
  startupLog(`createWindow preload=${preloadPath} exists=${fs.existsSync(preloadPath)}`)
  if (!IS_DEV) startupLog(`renderer=${rendererPath} exists=${fs.existsSync(rendererPath)}`)

  mainWindow = new BrowserWindow({
    width: bounds?.width ?? 1400,
    height: bounds?.height ?? 880,
    x: bounds?.x,
    y: bounds?.y,
    minWidth: 1080,
    minHeight: 700,
    show: false,
    frame: false,
    roundedCorners: true,
    backgroundColor: '#080b12',
    title: APP_NAME,
    icon: iconPath(),
    webPreferences: {
      preload: preloadPath,
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: false,
      webSecurity: true,
      spellcheck: false,
      backgroundThrottling: true
    }
  })
  bindWindow(mainWindow)
  const win = mainWindow

  let fallbackTimer: NodeJS.Timeout | null = null
  const clearFallback = (): void => {
    if (fallbackTimer) {
      clearTimeout(fallbackTimer)
      fallbackTimer = null
    }
  }
  // Never leave the user with an invisible process: show the window even if the
  // renderer never emits ready-to-show (GPU issues, broken GPU driver, ...).
  fallbackTimer = setTimeout(() => {
    if (!win.isDestroyed() && !win.isVisible()) revealWindow(win, 'fallback-timer')
  }, 3000)

  win.once('ready-to-show', () => {
    clearFallback()
    revealWindow(win, 'ready-to-show')
    if (IS_DEV && process.env.AURORA_DEVTOOLS) win.webContents.openDevTools({ mode: 'detach' })
  })
  win.webContents.once('did-finish-load', () => {
    clearFallback()
    startupLog('did-finish-load')
    markRendererReady()
    revealWindow(win, 'did-finish-load')
  })
  win.webContents.on('did-fail-load', (_event, errorCode, errorDescription, validatedURL, isMainFrame) => {
    if (!isMainFrame) return
    startupLog(`ERROR did-fail-load code=${errorCode} ${errorDescription} url=${validatedURL}`)
    revealWindow(win, 'did-fail-load')
    try {
      dialog.showErrorBox(
        'Aurora Launcher 界面加载失败',
        `${errorDescription} (${errorCode})\n${validatedURL}\n\n日志：${startupLogFile()}`
      )
    } catch {}
  })
  win.webContents.on('render-process-gone', (_event, details) => {
    startupLog('ERROR render process gone', JSON.stringify(details))
    revealWindow(win, 'render-process-gone')
    try {
      dialog.showErrorBox('Aurora Launcher 渲染进程异常退出', `原因：${details.reason}\n\n日志：${startupLogFile()}`)
    } catch {}
  })
  win.webContents.on('console-message', (_event, level, message, line, sourceId) => {
    if (level >= 3) startupLog(`renderer ERROR: ${message} (${sourceId}:${line})`)
  })
  win.on('unresponsive', () => startupLog('WARN main window unresponsive'))

  win.on('maximize', () => emit('window:maximized', true))
  win.on('unmaximize', () => emit('window:maximized', false))

  win.on('close', (event) => {
    if (quitting) return
    const s = getSettings()
    saveBounds()
    if (s.closeBehavior === 'tray') {
      event.preventDefault()
      win.hide()
      return
    }
  })

  win.on('closed', () => {
    clearFallback()
    mainWindow = null
    bindWindow(null)
  })

  win.webContents.setWindowOpenHandler(({ url }) => {
    if (/^https?:/i.test(url)) void shell.openExternal(url)
    return { action: 'deny' }
  })

  win.webContents.on('will-navigate', (event, url) => {
    const allowed = IS_DEV ? url.startsWith(DEV_URL ?? '') : false
    if (!allowed) {
      event.preventDefault()
      if (/^https?:/i.test(url)) void shell.openExternal(url)
    }
  })

  const load = IS_DEV ? win.loadURL(DEV_URL!) : win.loadFile(rendererPath)
  load
    .then(() => {
      startupLog('load resolved')
      setTimeout(() => {
        if (!win.isDestroyed() && !win.isVisible()) revealWindow(win, 'load-resolved')
      }, 400)
    })
    .catch((error) => {
      startupLog('ERROR load failed', error)
      revealWindow(win, 'load-failed')
      try {
        dialog.showErrorBox(
          'Aurora Launcher 无法加载界面',
          `${error instanceof Error ? error.message : String(error)}\n\n日志：${startupLogFile()}`
        )
      } catch {}
    })

  win.on('resize', debounce(saveBounds, 500))
  win.on('move', debounce(saveBounds, 700))
}

function saveBounds(): void {
  if (!mainWindow || mainWindow.isDestroyed()) return
  if (mainWindow.isMinimized() || mainWindow.isMaximized()) return
  const b = mainWindow.getBounds()
  void updateSettings({ windowBounds: { width: b.width, height: b.height, x: b.x, y: b.y } })
}

function debounce<T extends (...args: never[]) => void>(fn: T, ms: number): T {
  let timer: NodeJS.Timeout | null = null
  return ((...args: never[]) => {
    if (timer) clearTimeout(timer)
    timer = setTimeout(() => fn(...args), ms)
  }) as T
}

function setupTray(): void {
  const ico = iconPath()
  if (!ico) return
  try {
    tray = new Tray(nativeImage.createFromPath(ico))
    tray.setToolTip(APP_NAME)
    const menu = Menu.buildFromTemplate([
      { label: '显示主界面', click: () => showWindow() },
      { type: 'separator' },
      {
        label: '退出',
        click: () => {
          quitting = true
          app.quit()
        }
      }
    ])
    tray.setContextMenu(menu)
    tray.on('double-click', () => showWindow())
  } catch (e) {
    console.warn('tray setup failed', e)
  }
}

function showWindow(): void {
  if (!mainWindow || mainWindow.isDestroyed()) {
    createWindow()
    return
  }
  revealWindow(mainWindow, 'showWindow')
}

function registerProtocol(): void {
  protocol.handle('aurora-file', async (request) => {
    try {
      const url = new URL(request.url)
      let filePath = decodeURIComponent(url.pathname)
      if (process.platform === 'win32' && filePath.startsWith('/')) filePath = filePath.slice(1)
      const resolved = path.resolve(filePath)
      const allowedRoots = [path.resolve(rootDir()), path.resolve(settingsDir())]
      const inside = allowedRoots.some((root) => {
        const rel = path.relative(root, resolved)
        return rel === '' || (!rel.startsWith('..') && !path.isAbsolute(rel))
      })
      if (!inside) {
        return new Response('Forbidden', { status: 403 })
      }
      if (!fs.existsSync(resolved)) return new Response('Not Found', { status: 404 })
      return await net.fetch(pathToFileURL(resolved).toString())
    } catch {
      return new Response('Bad Request', { status: 400 })
    }
  })
}

const gotLock = app.requestSingleInstanceLock()
if (!gotLock) {
  app.quit()
} else {
  app.on('second-instance', () => showWindow())

  app.whenReady().then(() => {
    startupLog('app ready')
    try {
      const portable = portableRootDir()
      ensureMinecraftLayout(portable)
      startupLog(`portable .minecraft ready=${portable}`)
    } catch (e) {
      startupLog('WARN failed to create portable .minecraft', e)
    }
    try {
      const settings = initSettings()
      ensureDirs()
      startupLog(`settings loaded root=${settings.rootDir}`)
      registerProtocol()
      registerIpc()
      startupLog('ipc registered')
      createWindow()
      startupLog('window created')
      setupTray()
      if (settings.autoImportVersions) {
        void (async () => {
          try {
            const instances = await listInstances()
            // Always restore Aurora instance folders whose metadata disappeared; import
            // loose PCL/vanilla versions only when the list is empty.
            const count = await importExistingVersions(instances.length === 0)
            if (count) startupLog(`auto imported/restored ${count} existing versions`)
          } catch (e) {
            startupLog('WARN auto import failed', e)
          }
        })()
      }
    } catch (e) {
      startupLog('ERROR startup failed', e)
      const message = e instanceof Error ? `${e.message}\n\n${e.stack ?? ''}` : String(e)
      try {
        dialog.showErrorBox('Aurora Launcher 启动失败', `${message}\n\n日志：${startupLogFile()}`)
      } catch {}
      app.quit()
    }

    app.on('activate', () => {
      if (BrowserWindow.getAllWindows().length === 0) createWindow()
      else showWindow()
    })
  })

  app.on('before-quit', () => {
    quitting = true
    saveBounds()
    if (isRunning()) {
      // game keeps running on launcher exit
    }
  })

  app.on('window-all-closed', () => {
    if (process.platform !== 'darwin') {
      if (getSettings().closeBehavior === 'tray' && tray) return
      app.quit()
    }
  })
}
