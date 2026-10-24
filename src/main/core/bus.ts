import { BrowserWindow } from 'electron'
import type { LogLine, Settings } from '../../shared/types'

type Payload = unknown

let mainWindow: BrowserWindow | null = null

export function bindWindow(win: BrowserWindow | null): void {
  mainWindow = win
}

export function windowRef(): BrowserWindow | null {
  return mainWindow
}

export function emit(channel: string, payload?: Payload): void {
  if (!mainWindow || mainWindow.isDestroyed()) return
  try {
    mainWindow.webContents.send(channel, payload)
  } catch {
    // renderer may be reloading
  }
}

let consoleLines: LogLine[] = []
const MAX_CONSOLE_LINES = 2000
const MAX_CONSOLE_TEXT = 4000

export function consoleLog(source: LogLine['source'], level: LogLine['level'], text: string): void {
  // Strip ANSI colour sequences emitted by Minecraft / Forge.
  const clean = text.replace(/\u001b\[[0-9;?]*[A-Za-z]/g, '').slice(0, MAX_CONSOLE_TEXT)
  const line: LogLine = { ts: Date.now(), source, level, text: clean }
  consoleLines.push(line)
  if (consoleLines.length > MAX_CONSOLE_LINES) consoleLines.splice(0, consoleLines.length - MAX_CONSOLE_LINES)
  emit('log:line', line)
  const prefix = source === 'game' ? '[game]' : source === 'download' ? '[dl]' : source === 'auth' ? '[auth]' : '[app]'
  const fn = level === 'error' ? console.error : level === 'warn' ? console.warn : console.log
  fn(prefix, text)
}

export function consoleBuffer(): LogLine[] {
  return consoleLines.slice(-600)
}

export function clearConsole(): void {
  consoleLines = []
}

export function toast(type: 'info' | 'success' | 'error' | 'warning', message: string, title?: string): void {
  emit('toast', { type, message, title })
}

export function settingsChanged(settings: Settings): void {
  emit('settings:changed', settings)
}
