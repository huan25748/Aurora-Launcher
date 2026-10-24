import fs from 'node:fs'
import path from 'node:path'
import { settingsDir } from './paths'

function launcherDataDir(): string {
  return settingsDir()
}

let logFileCache: string | null = null
let stateFileCache: string | null = null

export function startupLogFile(): string {
  if (!logFileCache) logFileCache = path.join(launcherDataDir(), 'startup.log')
  return logFileCache
}

function stateFile(): string {
  if (!stateFileCache) stateFileCache = path.join(launcherDataDir(), 'window-state.json')
  return stateFileCache
}

function ensureDir(): void {
  try {
    fs.mkdirSync(launcherDataDir(), { recursive: true })
  } catch {}
}

export function startupLog(message: string, error?: unknown): void {
  const detail = error
    ? ` :: ${error instanceof Error ? `${error.name}: ${error.message}\n${error.stack ?? ''}` : String(error)}`
    : ''
  try {
    ensureDir()
    fs.appendFileSync(startupLogFile(), `[${new Date().toISOString()}] ${message}${detail}\n`)
  } catch {}
  if (message.startsWith('ERROR') || error) console.error(`[Aurora] ${message}`, error ?? '')
  else console.log(`[Aurora] ${message}`)
}

interface WindowState {
  bootAt: number
  shownAt?: number
  rendererReadyAt?: number
  view?: string
}

function readState(): WindowState | null {
  try {
    return JSON.parse(fs.readFileSync(stateFile(), 'utf8')) as WindowState
  } catch {
    return null
  }
}

function writeState(state: WindowState): void {
  try {
    ensureDir()
    fs.writeFileSync(stateFile(), JSON.stringify(state))
  } catch {}
}

/** True when the previous run started but never managed to show its window. */
export function previousWindowFailed(): boolean {
  const state = readState()
  return !!state && !!state.bootAt && !state.rendererReadyAt
}

export function markBoot(view?: string): void {
  writeState({ bootAt: Date.now(), view })
}

export function markShown(): void {
  const state = readState()
  writeState({ bootAt: state?.bootAt ?? Date.now(), shownAt: Date.now(), rendererReadyAt: state?.rendererReadyAt, view: state?.view })
}

export function markRendererReady(): void {
  const state = readState()
  writeState({
    bootAt: state?.bootAt ?? Date.now(),
    shownAt: state?.shownAt,
    rendererReadyAt: Date.now(),
    view: state?.view
  })
}

export function lastKnownState(): WindowState | null {
  return readState()
}
