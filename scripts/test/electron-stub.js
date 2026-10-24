// Minimal electron API stub so the main-process services can run under plain Node
// for end-to-end tests inside restricted environments.
const path = require('path')
const os = require('os')

const base = process.env.AURORA_HOME || path.join(os.tmpdir(), 'aurora-home')

const noop = () => {}
const resolved = () => Promise.resolve()

module.exports = {
  app: {
    getPath: (name) => (name === 'userData' ? base : name === 'appData' ? base : name === 'home' ? os.homedir() : base),
    getAppPath: () => base,
    getVersion: () => '1.0.0',
    setName: noop,
    setAppUserModelId: noop,
    whenReady: resolved,
    on: noop,
    once: noop,
    quit: noop,
    exit: noop,
    relaunch: noop,
    requestSingleInstanceLock: () => true,
    setPath: noop,
    isPackaged: false
  },
  net: {
    fetch: (input, init) => globalThis.fetch(input, init)
  },
  session: {
    defaultSession: { setProxy: noop, clearCache: resolved }
  },
  BrowserWindow: class BrowserWindow {
    static getAllWindows() {
      return []
    }
    static fromWebContents() {
      return null
    }
  },
  ipcMain: { handle: noop, on: noop },
  dialog: { showOpenDialog: async () => ({ canceled: true, filePaths: [] }) },
  shell: { openExternal: resolved, openPath: resolved, showItemInFolder: noop },
  Tray: class Tray {},
  Menu: { buildFromTemplate: () => ({}), setApplicationMenu: noop },
  nativeImage: { createFromPath: () => ({}) },
  protocol: { handle: noop, registerSchemesAsPrivileged: noop }
}
