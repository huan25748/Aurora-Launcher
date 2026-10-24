import { app } from 'electron'
import path from 'node:path'
import fs from 'node:fs'

let settingsDirCache: string | null = null
let rootDirCache: string | null = null

function launcherDir(): string {
  return process.env.AURORA_EXE_DIR || (app.isPackaged ? path.dirname(process.execPath) : process.cwd())
}

/** Per-launcher data folder: <launcher folder>/.aurora (never shared). */
export function portableDataDir(): string {
  return path.join(launcherDir(), '.aurora')
}

function shortHash(value: string): string {
  let hash = 2166136261
  for (let i = 0; i < value.length; i++) {
    hash ^= value.charCodeAt(i)
    hash = Math.imul(hash, 16777619)
  }
  return (hash >>> 0).toString(16)
}

function canWriteDirectory(dir: string): boolean {
  try {
    fs.mkdirSync(dir, { recursive: true })
    const probe = path.join(dir, `.write-test-${process.pid}`)
    fs.writeFileSync(probe, 'ok')
    fs.unlinkSync(probe)
    return true
  } catch {
    return false
  }
}

/**
 * Launcher configuration/state lives next to the launcher, so each copy is fully
 * independent. A new folder starts with an empty config and an empty .minecraft.
 * If the launcher folder is read-only, use a per-copy hashed folder under AppData.
 */
export function settingsDir(): string {
  if (settingsDirCache) return settingsDirCache
  if (process.env.AURORA_HOME) {
    settingsDirCache = path.resolve(process.env.AURORA_HOME)
  } else {
    const appData = process.env.APPDATA || path.join(process.env.USERPROFILE ?? '', 'AppData', 'Roaming')
    const localAppData = process.env.LOCALAPPDATA || path.join(process.env.USERPROFILE ?? '', 'AppData', 'Local')
    const suffix = shortHash(launcherDir())
    const candidates = [
      portableDataDir(),
      path.join(appData, 'AuroraLauncher', `data-${suffix}`),
      path.join(localAppData, 'AuroraLauncher', `data-${suffix}`)
    ]
    settingsDirCache = candidates.find(canWriteDirectory) ?? portableDataDir()
  }
  fs.mkdirSync(settingsDirCache, { recursive: true })
  return settingsDirCache
}

function isDirectory(target: string): boolean {
  try {
    return fs.statSync(target).isDirectory()
  } catch {
    return false
  }
}

/**
 * Prefer the Minecraft folder the user already has instead of an isolated
 * launcher-owned folder:
 *   1. .minecraft next to the launcher executable (portable / single-file)
 *   2. %APPDATA%\.minecraft (official launcher / PCL / most users)
 *   3. %USERPROFILE%\.minecraft
 * Falls back to the standard %APPDATA%\.minecraft location when none exist.
 */
export function defaultRootDir(): string {
  if (process.env.AURORA_HOME) return path.join(settingsDir(), 'minecraft')
  const exeDir = process.env.AURORA_EXE_DIR || path.dirname(process.execPath)
  const home = app.getPath('home')
  const appData = process.env.APPDATA || path.join(home, 'AppData', 'Roaming')
  const candidates =
    process.platform === 'win32'
      ? [path.join(exeDir, '.minecraft'), path.join(appData, '.minecraft'), path.join(home, '.minecraft')]
      : [path.join(home, '.minecraft'), path.join(exeDir, '.minecraft')]
  for (const candidate of candidates) {
    if (isDirectory(candidate)) return candidate
  }
  return process.platform === 'win32' ? path.join(appData, '.minecraft') : path.join(home, '.minecraft')
}

export function hasMinecraftData(dir: string): boolean {
  if (!isDirectory(dir)) return false
  return ['versions', 'assets', 'libraries'].some((name) => isDirectory(path.join(dir, name)))
}

export function setRootDir(dir: string): void {
  rootDirCache = dir
}

export function rootDir(): string {
  if (!rootDirCache) rootDirCache = defaultRootDir()
  return rootDirCache
}

export interface DataDirs {
  root: string
  versions: string
  libraries: string
  assets: string
  instances: string
  runtime: string
  launcher: string
  cache: string
  logs: string
  authlib: string
  natives: string
}

export function dirs(): DataDirs {
  const root = rootDir()
  const launcher = path.join(root, 'launcher')
  return {
    root,
    versions: path.join(root, 'versions'),
    libraries: path.join(root, 'libraries'),
    assets: path.join(root, 'assets'),
    instances: path.join(instancesRoot()),
    runtime: path.join(root, 'runtime'),
    launcher,
    cache: path.join(launcher, 'cache'),
    logs: path.join(launcher, 'logs'),
    authlib: path.join(launcher, 'authlib'),
    natives: path.join(launcher, 'natives')
  }
}

/** Instances live next to the game root so moving the root moves everything. */
export function instancesRoot(): string {
  return path.join(rootDir(), 'instances')
}

export function instanceDir(id: string): string {
  return path.join(instancesRoot(), id)
}

/** The portable game root: <launcher folder>/.minecraft (single-file aware). */
export function portableRootDir(): string {
  if (process.env.AURORA_HOME) return path.join(settingsDir(), 'minecraft')
  return path.join(launcherDir(), '.minecraft')
}

/**
 * Creates the standard .minecraft layout so a fresh folder is immediately usable.
 * Existing files are never overwritten.
 */
export function ensureMinecraftLayout(root: string): void {
  fs.mkdirSync(root, { recursive: true })
  for (const relative of [
    'versions',
    'libraries',
    'assets',
    'assets/indexes',
    'assets/objects',
    'runtime',
    'logs',
    'crash-reports',
    'saves',
    'resourcepacks',
    'shaderpacks',
    'mods',
    'config',
    'datapacks',
    'instances',
    'launcher',
    'launcher/cache',
    'launcher/logs',
    'launcher/natives',
    'launcher/authlib'
  ]) {
    fs.mkdirSync(path.join(root, relative), { recursive: true })
  }
  const profiles = path.join(root, 'launcher_profiles.json')
  if (!fs.existsSync(profiles)) {
    fs.writeFileSync(profiles, JSON.stringify({ profiles: {}, settings: {}, version: 3 }, null, 2))
  }
}

export function ensureDirs(): void {
  ensureMinecraftLayout(rootDir())
}

export function versionDir(versionId: string): string {
  return path.join(dirs().versions, versionId)
}

export function versionJsonPath(versionId: string): string {
  return path.join(versionDir(versionId), `${versionId}.json`)
}

export function versionJarPath(versionId: string): string {
  return path.join(versionDir(versionId), `${versionId}.jar`)
}

export function librariesDir(): string {
  return dirs().libraries
}

export function assetsIndexPath(assetId: string): string {
  return path.join(dirs().assets, 'indexes', `${assetId}.json`)
}

export function objectPath(hash: string): string {
  return path.join(dirs().assets, 'objects', hash.slice(0, 2), hash)
}

export function authlibJarPath(version: string): string {
  return path.join(dirs().authlib, `authlib-injector-${version}.jar`)
}

export function safeName(name: string): string {
  return name.replace(/[\\/:*?"<>|\r\n\t]/g, '_').trim() || 'instance'
}
