import path from 'node:path'
import fs from 'node:fs'
import os from 'node:os'
import { app, session } from 'electron'
import { defaultRootDir, hasMinecraftData, rootDir, setRootDir, settingsDir, ensureDirs, dirs } from './paths'
import { emit } from './bus'
import { readJsonSync, writeJsonSync } from './fsx'
import type { Settings } from '../../shared/types'

const CONFIG_FILE = () => path.join(settingsDir(), 'config.json')

const DEFAULTS: Settings = {
  rootDir: '',
  language: 'zh-CN',
  theme: 'system',
  accent: 'aurora',
  downloadSource: 'auto',
  downloadSources: {},
  maxConcurrency: 24,
  downloadThreads: 128,
  proxyEnabled: false,
  proxyHost: '127.0.0.1',
  proxyPort: 7890,
  minimizeOnLaunch: false,
  showConsoleOnLaunch: true,
  keepLauncherOpen: true,
  closeBehavior: 'exit',
  defaultMemory: 0,
  autoMemory: true,
  jvmArgs: '',
  gameArgs: '',
  javaPath: '',
  msClientId: 'c36a9fb6-4f2a-41ff-90bd-ae7cc92031eb',
  enableStats: true,
  lastSelectedInstanceId: '',
  onboardingDone: true,
  checkUpdates: true,
  downloadAssets: true,
  downloadNatives: true,
  useAuthlibForOffline: false,
  autoImportVersions: true,
  firstLaunchChinese: true
}

let cache: Settings | null = null

export type DownloadTarget = 'vanilla' | 'forge' | 'neoforge' | 'fabric' | 'quilt'

export function downloadSourceFor(target: DownloadTarget): Settings['downloadSource'] {
  const settings = getSettings()
  return settings.downloadSources?.[target] ?? settings.downloadSource
}

export function getSettings(): Settings {
  if (cache) return cache
  const stored = readJsonSync<Partial<Settings>>(CONFIG_FILE(), {})
  cache = { ...DEFAULTS, ...stored }
  cache.downloadThreads = Math.max(1, Math.min(2048, Math.round(Number(cache.downloadThreads) || 128)))
  const detected = defaultRootDir()
  let migrated = false
  if (!cache.rootDir) {
    cache.rootDir = detected
    migrated = true
  } else if (path.resolve(cache.rootDir) !== path.resolve(detected)) {
    // Previous builds defaulted to an empty launcher-owned folder. If the user
    // already has a real .minecraft, switch to it automatically.
    const legacyOwned = !hasMinecraftData(cache.rootDir)
    if (legacyOwned && hasMinecraftData(detected)) {
      cache.rootDir = detected
      migrated = true
    }
  }
  // touch paths synchronously before returning
  setRootDir(cache.rootDir)
  try {
    ensureDirs()
    if (migrated) writeJsonSync(CONFIG_FILE(), cache)
  } catch (e) {
    console.error('Failed to create data directories', e)
  }
  return cache
}

export function initSettings(): Settings {
  const s = getSettings()
  setRootDir(s.rootDir)
  ensureDirs()
  applyProxy(s)
  return s
}

export async function updateSettings(patch: Partial<Settings>): Promise<Settings> {
  const current = getSettings()
  const next: Settings = { ...current, ...patch }
  if (patch.rootDir && path.resolve(patch.rootDir) !== path.resolve(current.rootDir)) {
    next.rootDir = path.resolve(patch.rootDir)
    setRootDir(next.rootDir)
    ensureDirs()
  }
  cache = next
  writeJsonSync(CONFIG_FILE(), next)
  if (patch.proxyEnabled !== undefined || patch.proxyHost !== undefined || patch.proxyPort !== undefined) {
    applyProxy(next)
  }
  emit('settings:changed', next)
  return next
}

export async function resetSettings(): Promise<Settings> {
  cache = { ...DEFAULTS, rootDir: defaultRootDir() }
  setRootDir(cache.rootDir)
  ensureDirs()
  writeJsonSync(CONFIG_FILE(), cache)
  applyProxy(cache)
  emit('settings:changed', cache)
  return cache
}

export function settingsFile(): string {
  return CONFIG_FILE()
}

export function applyProxy(s: Settings): void {
  try {
    if (s.proxyEnabled && s.proxyHost) {
      session.defaultSession.setProxy({ proxyRules: `http=${s.proxyHost}:${s.proxyPort};https=${s.proxyHost}:${s.proxyPort}` })
    } else {
      session.defaultSession.setProxy({ mode: 'direct' })
    }
  } catch (e) {
    console.warn('proxy setup failed', e)
  }
}

export function systemInfo(): Record<string, unknown> {
  const d = dirs()
  let totalMem = 0
  try {
    totalMem = Math.round(os.totalmem() / 1024 / 1024)
  } catch {}
  return {
    platform: process.platform,
    arch: process.arch,
    os: `${os.type()} ${os.release()}`,
    cpu: os.cpus()[0]?.model ?? 'Unknown CPU',
    cores: os.cpus().length,
    memoryMB: totalMem,
    freeMemoryMB: Math.round(os.freemem() / 1024 / 1024),
    appVersion: app.getVersion(),
    electron: process.versions.electron,
    chrome: process.versions.chrome,
    node: process.versions.node,
    rootDir: d.root,
    configPath: settingsFile()
  }
}

export function appVersion(): string {
  try {
    return app.getVersion()
  } catch {
    return '1.0.0'
  }
}

export function defaultSettings(): Settings {
  return { ...DEFAULTS }
}

export function isFirstRun(): boolean {
  return !fs.existsSync(CONFIG_FILE())
}

export function suggestedMemory(): number {
  const total = os.totalmem() / 1024 / 1024
  const half = Math.floor(total / 2)
  const capped = Math.min(8192, Math.max(2048, half))
  return Math.round(capped / 256) * 256
}

export function rootPath(): string {
  return rootDir()
}
