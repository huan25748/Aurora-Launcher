import fs from 'node:fs'
import fsp from 'node:fs/promises'
import path from 'node:path'
import { dirs, instanceDir, instancesRoot, versionJarPath } from '../core/paths'
import { readJsonSync, writeJsonSync, ensureDir, dirSize, safeFileName, timestampName, uid, copyDir, removeDir, listFiles } from '../core/fsx'
import { loadVersionChain, readAuroraMeta, readLocalVersionJson, writeAuroraMeta, type VersionJson } from './version'
import { zipDirectory, extractZipSafe } from '../core/zip'
import { emit } from '../core/bus'
import type { Instance, LoaderKind } from '../../shared/types'

export const INSTANCE_FILE = 'instance.json'

const PALETTE = ['aurora', 'sunset', 'ocean', 'forest', 'candy', 'ember', 'violet', 'mint']

/** PCL keeps each isolated instance as a folder under versions/. */
function versionFolderName(name: string): string {
  const cleaned =
    safeFileName(name, 'instance')
      .replace(/\s+/g, '_')
      .replace(/[. ]+$/g, '')
      .slice(0, 60) || 'instance'
  let candidate = cleaned
  for (let i = 2; fs.existsSync(path.join(dirs().versions, candidate)); i++) candidate = `${cleaned}-${i}`
  return candidate
}

/**
 * Creates a self-contained, PCL-compatible version folder for a new instance:
 * <versions>/<name>/<name>.json is the fully merged profile (no inheritsFrom) and all
 * game files (mods/saves/...) live in the same folder. Intermediate vanilla/loader
 * version folders are no longer needed after creation.
 */
async function writeStandaloneVersion(
  sourceVersionId: string,
  folderId: string,
  folderDir: string,
  loader: LoaderKind,
  loaderVersion: string | undefined,
  mcVersion: string
): Promise<void> {
  const chain = await loadVersionChain(sourceVersionId)
  if (!chain.mainClass) throw new Error(`源版本 ${sourceVersionId} 尚未安装完成，无法创建实例`)

  // Vanilla/Fabric need the client jar; modern Forge/NeoForge load a patched module from
  // libraries and must NOT get the vanilla jar (it causes a module resolution conflict).
  const moduleLoader = /cpw\.mods\.bootstraplauncher/i.test(chain.mainClass)
  const ownJar = versionJarPath(sourceVersionId)
  const inheritedJar = versionJarPath(chain.inheritedJarId)
  const jarSource = fs.existsSync(ownJar) ? ownJar : !moduleLoader && fs.existsSync(inheritedJar) ? inheritedJar : ''
  const jarMatchesClient = !!jarSource && path.resolve(jarSource) === path.resolve(inheritedJar)
  if (jarSource) {
    const destination = path.join(folderDir, `${folderId}.jar`)
    try {
      fs.linkSync(jarSource, destination)
    } catch {
      fs.copyFileSync(jarSource, destination)
    }
  }

  const json: VersionJson = {
    id: folderId,
    type: chain.type ?? 'release',
    releaseTime: chain.releaseTime,
    mainClass: chain.mainClass,
    minecraftArguments: chain.minecraftArguments,
    arguments: {
      game: chain.gameArgs,
      jvm: chain.jvmArgs,
      ...(chain.defaultUserJvm.length ? { 'default-user-jvm': chain.defaultUserJvm } : {})
    },
    libraries: chain.libraries,
    assetIndex: chain.assetIndex,
    assets: chain.assetIndexId,
    logging: chain.logging,
    // Only advertise a client download when we actually copied the jar into the folder.
    downloads: jarMatchesClient && chain.clientDownload ? { client: chain.clientDownload } : undefined
  }
  if (chain.javaComponent) json.javaVersion = { component: chain.javaComponent, majorVersion: chain.javaMajor ?? 17 }
  writeJsonSync(path.join(folderDir, `${folderId}.json`), json)
  writeAuroraMeta(folderId, {
    loader,
    loaderVersion,
    mcVersion,
    installedAt: Date.now(),
    profileId: folderId
  })
}

const SUB_DIRS = [
  'mods',
  'resourcepacks',
  'shaderpacks',
  'saves',
  'screenshots',
  'logs',
  'crash-reports',
  'config',
  'datapacks',
  'backups'
]

export function instancePath(id: string): string {
  return instanceDir(id)
}

const PACK_ICON_CANDIDATES = [
  'PCL/Logo.png',
  'PCL/logo.png',
  'icon.png',
  'icon.jpg',
  'icon.jpeg',
  'icon.webp',
  'logo.png',
  'logo.jpg',
  'pack.png'
]

/** PCL and CurseForge-style packs often ship their own cover image. */
export function findPackIcon(gameDir: string): string | undefined {
  if (!gameDir) return undefined
  for (const rel of PACK_ICON_CANDIDATES) {
    const candidate = path.join(gameDir, rel)
    try {
      if (fs.statSync(candidate).isFile()) return candidate
    } catch {
      // continue
    }
  }
  return undefined
}

export function readInstance(id: string): Instance | null {
  const file = path.join(instanceDir(id), INSTANCE_FILE)
  const inst = readJsonSync<Instance | null>(file, null)
  if (!inst) return null
  if (inst.gameDir && !fs.existsSync(inst.gameDir)) inst.gameDir = instanceDir(id)
  inst.iconPath = findPackIcon(inst.gameDir)
  return inst
}

export function writeInstance(inst: Instance): void {
  ensureDir(inst.gameDir || instanceDir(inst.id))
  writeJsonSync(path.join(instanceDir(inst.id), INSTANCE_FILE), inst)
}

export async function listInstances(): Promise<Instance[]> {
  await ensureDir(instancesRoot())
  const entries = await listFiles(instancesRoot())
  const out: Instance[] = []
  for (const entry of entries) {
    if (!entry.isDirectory()) continue
    const inst = readInstance(entry.name)
    if (inst) out.push(inst)
  }
  return out.sort((a, b) => (b.lastPlayedAt ?? b.createdAt) - (a.lastPlayedAt ?? a.createdAt))
}

export async function getInstance(id: string): Promise<Instance | null> {
  return readInstance(id)
}

export interface CreateInstanceInput {
  name: string
  versionId: string
  mcVersion: string
  loader: string
  loaderVersion?: string
  icon?: string
  color?: string
  /** Reuse an existing game directory (imported .minecraft version). */
  gameDir?: string
  imported?: boolean
}

export async function createInstance(input: CreateInstanceInput): Promise<Instance> {
  const id = uid('inst-')
  const dir = instanceDir(id)
  const meta = readAuroraMeta(input.versionId)
  const name = input.name?.trim() || `${input.mcVersion} ${input.loader !== 'vanilla' ? input.loader : ''}`.trim()
  const loader = (input.loader as LoaderKind) ?? meta?.loader ?? 'vanilla'
  const loaderVersion = input.loaderVersion ?? meta?.loaderVersion
  await ensureDir(dir)

  const imported = !!input.imported || !!input.gameDir
  let gameDir: string
  let versionId = input.versionId
  let sourceVersionId: string | undefined
  let selfContained = false

  if (imported) {
    gameDir = path.resolve(input.gameDir ?? dir)
    await ensureDir(gameDir)
  } else {
    // New instances live in versions/<name>/ like PCL, with a merged version JSON so
    // both Aurora and PCL can see and launch the same folder.
    const folderId = versionFolderName(name)
    gameDir = path.join(dirs().versions, folderId)
    await ensureDir(gameDir)
    for (const sub of SUB_DIRS) await ensureDir(path.join(gameDir, sub))
    try {
      await writeStandaloneVersion(input.versionId, folderId, gameDir, loader, loaderVersion, input.mcVersion)
    } catch (e) {
      await removeDir(gameDir)
      throw e
    }
    versionId = folderId
    sourceVersionId = input.versionId
    selfContained = true
  }

  const now = Date.now()
  const inst: Instance = {
    id,
    name,
    versionId,
    sourceVersionId,
    selfContained: selfContained || undefined,
    mcVersion: input.mcVersion,
    loader,
    loaderVersion,
    gameDir,
    icon: input.icon ?? '🌌',
    color: input.color ?? PALETTE[Math.floor(Math.random() * PALETTE.length)],
    createdAt: now,
    totalPlayTimeMs: 0,
    playCount: 0,
    imported: imported || undefined
  }
  writeInstance(inst)
  if (!imported) writeJsonSync(path.join(gameDir, INSTANCE_FILE), inst)
  notify()
  return inst
}

export async function updateInstance(id: string, patch: Partial<Instance>): Promise<Instance> {
  const current = readInstance(id)
  if (!current) throw new Error('实例不存在')
  const next: Instance = { ...current, ...patch, id: current.id }
  if (patch.gameDir) {
    // game dir is managed by the launcher; ignore external changes to keep things consistent
    next.gameDir = current.gameDir
  }
  writeInstance(next)
  notify()
  return next
}

export async function removeInstance(id: string, deleteFiles: boolean): Promise<void> {
  const inst = readInstance(id)
  if (deleteFiles && inst) {
    const gameDir = path.resolve(inst.gameDir)
    const root = path.resolve(dirs().root)
    const versionsRoot = path.resolve(dirs().versions)
    const instancesRoot = path.resolve(dirs().instances)
    const inside = (rel: string): boolean => !!rel && !rel.startsWith('..') && !path.isAbsolute(rel)
    const isVersionFolder = inside(path.relative(versionsRoot, gameDir))
    const isManagedFolder = inside(path.relative(instancesRoot, gameDir))
    const shared = (await listInstances()).some((other) => other.id !== id && path.resolve(other.gameDir) === gameDir)
    const protectedPaths = [
      root,
      versionsRoot,
      instancesRoot,
      path.resolve(dirs().libraries),
      path.resolve(dirs().assets),
      path.resolve(dirs().runtime),
      path.resolve(dirs().launcher)
    ]
    const engineRoot = path.parse(gameDir).root
    const protectedPath = protectedPaths.includes(gameDir) || root.startsWith(gameDir + path.sep)
    // Imported instances own their whole game folder, including external PCL packs.
    // Delete it entirely, except when it is the game root/ancestor or a shared engine folder.
    // New-style instances only own versions/<name> and keep the shared-folder protection.
    const canDelete = gameDir !== engineRoot && !protectedPath
    if (canDelete && (inst.imported || (!shared && (isVersionFolder || isManagedFolder)))) {
      await removeDir(gameDir)
    } else if (inst.imported && gameDir === root) {
      // Legacy/imported instances can live in the shared .minecraft root. We must not
      // delete that root, but the matching version folder is still theirs to remove.
      const versionFolder = path.join(versionsRoot, inst.versionId)
      if (inside(path.relative(versionsRoot, versionFolder))) await removeDir(versionFolder)
    }
  }
  if (deleteFiles) await removeDir(instanceDir(id))
  else await fsp.rm(path.join(instanceDir(id), INSTANCE_FILE), { force: true })
  notify()
}

export async function copyInstance(id: string, name: string): Promise<Instance> {
  const source = readInstance(id)
  if (!source) throw new Error('实例不存在')
  const newId = uid('inst-')
  const folderName = versionFolderName(name?.trim() || `${source.name} 副本`)
  const target = path.join(dirs().versions, folderName)
  await ensureDir(target)
  // Copy the actual game directory so imported/shared instances become independent copies.
  // Never copy the whole .minecraft root (libraries/assets/versions) when the source used it.
  if (path.resolve(source.gameDir) === path.resolve(dirs().root)) {
    for (const sub of SUB_DIRS) {
      const from = path.join(source.gameDir, sub)
      if (fs.existsSync(from)) await copyDir(from, path.join(target, sub))
    }
    for (const file of ['options.txt', 'optionsof.txt', 'servers.dat']) {
      const from = path.join(source.gameDir, file)
      if (fs.existsSync(from)) await fsp.copyFile(from, path.join(target, file))
    }
  } else {
    await copyDir(source.gameDir, target)
  }
  await fsp.rm(path.join(target, INSTANCE_FILE), { force: true })
  const baseVersion = source.sourceVersionId || source.versionId
  const oldJson = path.join(target, `${source.versionId}.json`)
  const oldJar = path.join(target, `${source.versionId}.jar`)
  const newJar = path.join(target, `${folderName}.jar`)
  try {
    if (source.selfContained && fs.existsSync(oldJson)) {
      const json = readJsonSync<VersionJson | null>(oldJson, null)
      if (!json) throw new Error('实例版本 JSON 损坏')
      json.id = folderName
      writeJsonSync(path.join(target, `${folderName}.json`), json)
      await fsp.rm(oldJson, { force: true })
      if (fs.existsSync(oldJar) && oldJar !== newJar) {
        await fsp.rm(newJar, { force: true })
        await fsp.rename(oldJar, newJar)
      }
      writeAuroraMeta(folderName, {
        loader: source.loader,
        loaderVersion: source.loaderVersion,
        mcVersion: source.mcVersion,
        installedAt: Date.now(),
        profileId: folderName
      })
    } else {
      if (fs.existsSync(oldJson)) await fsp.rm(oldJson, { force: true })
      if (fs.existsSync(oldJar) && oldJar !== newJar) await fsp.rm(oldJar, { force: true })
      await writeStandaloneVersion(baseVersion, folderName, target, source.loader, source.loaderVersion, source.mcVersion)
    }
  } catch (e) {
    await removeDir(target)
    throw e
  }
  for (const sub of SUB_DIRS) await ensureDir(path.join(target, sub))
  const inst: Instance = {
    ...source,
    id: newId,
    name: name?.trim() || `${source.name} 副本`,
    versionId: folderName,
    sourceVersionId: baseVersion,
    selfContained: true,
    gameDir: target,
    imported: false,
    createdAt: Date.now(),
    lastPlayedAt: undefined,
    lastLaunchedAt: undefined,
    playCount: 0,
    totalPlayTimeMs: 0
  }
  await ensureDir(instanceDir(newId))
  writeInstance(inst)
  writeJsonSync(path.join(target, INSTANCE_FILE), inst)
  notify()
  return inst
}

/**
 * Removes vanilla/loader version folders that were created only as intermediates for a
 * new self-contained instance. Anything referenced by an existing instance is kept.
 */
export async function cleanupInstallVersions(createdIds: string[], keepVersionId: string): Promise<string[]> {
  const instances = await listInstances()
  const referenced = new Set<string>()
  for (const inst of instances) {
    referenced.add(inst.versionId)
    // Imported instances may use a version folder as their game directory.
    const rel = path.relative(path.resolve(dirs().versions), path.resolve(inst.gameDir))
    if (rel && !rel.startsWith('..') && !path.isAbsolute(rel)) referenced.add(rel.split(path.sep)[0])
    // Old cloned instances inherit their source folder; self-contained ones do not.
    if (!inst.selfContained && inst.sourceVersionId) referenced.add(inst.sourceVersionId)
  }
  const removed: string[] = []
  for (const id of new Set(createdIds)) {
    if (!id || id === keepVersionId || referenced.has(id)) continue
    const dir = path.join(dirs().versions, id)
    // Only remove real version folders, never the instance metadata folder.
    if (!fs.existsSync(path.join(dir, `${id}.json`))) continue
    await removeDir(dir)
    removed.push(id)
  }
  if (removed.length) notify()
  return removed
}

/** Detects game directories used by existing launchers (PCL isolates per version). */
function detectGameDir(versionId: string): string {
  const versionPath = path.join(dirs().versions, versionId)
  const versionMarkers = ['mods', 'saves', 'resourcepacks', 'shaderpacks', 'options.txt', 'logs', 'crash-reports']
  const rootMarkers = ['mods', 'saves', 'resourcepacks', 'options.txt']
  const isolated = versionMarkers.some((marker) => fs.existsSync(path.join(versionPath, marker)))
  const rootUsed = rootMarkers.some((marker) => fs.existsSync(path.join(dirs().root, marker)))
  if (isolated) return versionPath
  if (rootUsed) return dirs().root
  // PCL defaults to version isolation even before the first launch creates folders.
  return versionPath
}

const IMPORT_ICONS: Record<string, string> = {
  vanilla: '🧊',
  fabric: '🧵',
  quilt: '🧶',
  forge: '🔨',
  neoforge: '⚙️',
  optifine: '🎨'
}

/**
 * Imports every installed version found in the shared .minecraft as a ready-to-play
 * instance, pointing at the game directory the user already has.
 */
export async function importExistingVersions(includeLooseVersions = true): Promise<number> {
  const { listInstalledVersions } = await import('./version')
  const versions = await listInstalledVersions()
  const existing = await listInstances()
  const known = new Set(existing.map((inst) => inst.versionId))
  const versionById = new Map(versions.map((version) => [version.id, version]))
  // Older imports (especially PCL integration packs with custom folder names) may have
  // been stored as vanilla before the loader was detected from PCL/Setup.ini or FML args.
  for (const inst of existing) {
    if (!inst.imported) continue
    const version = versionById.get(inst.versionId)
    if (!version) continue
    const patch: Partial<Instance> = {}
    if (version.loader && version.loader !== inst.loader) patch.loader = version.loader
    if (version.loaderVersion && version.loaderVersion !== inst.loaderVersion) patch.loaderVersion = version.loaderVersion
    if (version.mcVersion && version.mcVersion !== inst.mcVersion) patch.mcVersion = version.mcVersion
    if (Object.keys(patch).length) await updateInstance(inst.id, patch)
  }
  let imported = 0
  for (const version of versions) {
    if (known.has(version.id)) continue
    // Aurora instance folders carry instance.json: restore the instance metadata instead
    // of treating the cloned version as a loose game version.
    const versionPath = path.join(dirs().versions, version.id)
    const savedFile = path.join(versionPath, INSTANCE_FILE)
    if (fs.existsSync(savedFile)) {
      const saved = readJsonSync<Partial<Instance> | null>(savedFile, null)
      try {
        await createInstance({
          name: saved?.name?.trim() || version.id,
          versionId: version.id,
          mcVersion: saved?.mcVersion || version.mcVersion || version.id,
          loader: saved?.loader || version.loader || 'vanilla',
          loaderVersion: saved?.loaderVersion ?? version.loaderVersion,
          icon: saved?.icon,
          color: saved?.color,
          gameDir: versionPath,
          imported: true
        })
        imported++
      } catch (e) {
        console.warn('failed to restore instance', version.id, e)
      }
      continue
    }
    // On normal startups only restore Aurora-owned instance folders; importing every
    // loose PCL/vanilla version again would create duplicate cards.
    if (!includeLooseVersions) continue
    const gameDir = detectGameDir(version.id)
    const loader = version.loader ?? 'vanilla'
    try {
      await createInstance({
        name: version.mcVersion && version.mcVersion !== version.id ? `${version.mcVersion} · ${version.id}` : version.id,
        versionId: version.id,
        mcVersion: version.mcVersion || version.id,
        loader,
        loaderVersion: version.loaderVersion,
        icon: IMPORT_ICONS[loader] ?? '🌌',
        color: PALETTE[Math.abs(hashString(version.id)) % PALETTE.length],
        gameDir,
        imported: true
      })
      imported++
    } catch (e) {
      console.warn('failed to import version', version.id, e)
    }
  }
  if (imported) notify()
  return imported
}

function hashString(value: string): number {
  let hash = 0
  for (let i = 0; i < value.length; i++) hash = (hash * 31 + value.charCodeAt(i)) | 0
  return hash
}

export async function exportInstance(id: string): Promise<{ path: string }> {
  const inst = readInstance(id)
  if (!inst) throw new Error('实例不存在')
  const exportDir = path.join(dirs().launcher, 'exports')
  await ensureDir(exportDir)
  const file = path.join(exportDir, `${safeFileName(inst.name)}-${timestampName('export')}.zip`)
  zipDirectory(inst.gameDir, file)
  return { path: file }
}

const MODPACK_DIRS = [
  'mods',
  'config',
  'defaultconfigs',
  'kubejs',
  'scripts',
  'resourcepacks',
  'shaderpacks',
  'datapacks'
]

/**
 * Export a shareable modpack archive: mods/config/resourcepacks plus a manifest,
 * without saves, screenshots, logs, assets, libraries or the game jar.
 */
export async function exportInstancePack(id: string): Promise<{ path: string; size: number; files: number }> {
  const inst = readInstance(id)
  if (!inst) throw new Error('实例不存在')
  const exportRoot = path.join(dirs().launcher, 'packs')
  await ensureDir(exportRoot)
  const temp = path.join(exportRoot, `.pack-${id}-${Date.now()}`)
  await ensureDir(temp)
  try {
    for (const sub of MODPACK_DIRS) {
      const from = path.join(inst.gameDir, sub)
      if (!fs.existsSync(from)) continue
      await copyDir(from, path.join(temp, sub))
    }
    for (const file of ['options.txt', 'optionsof.txt']) {
      const from = path.join(inst.gameDir, file)
      if (fs.existsSync(from)) await fsp.copyFile(from, path.join(temp, file))
    }

    const icon = findPackIcon(inst.gameDir)
    if (icon) {
      await ensureDir(path.join(temp, 'PCL'))
      await fsp.copyFile(icon, path.join(temp, 'icon.png'))
      await fsp.copyFile(icon, path.join(temp, 'PCL', 'Logo.png'))
    }

    const packInstance: Instance = {
      ...inst,
      id: id,
      gameDir: '',
      iconPath: undefined,
      imported: false,
      lastPlayedAt: undefined,
      lastLaunchedAt: undefined,
      totalPlayTimeMs: 0,
      playCount: 0
    }
    writeJsonSync(path.join(temp, INSTANCE_FILE), packInstance)
    writeJsonSync(path.join(temp, 'manifest.json'), {
      format: 'aurora-modpack-v1',
      name: inst.name,
      mcVersion: inst.mcVersion,
      loader: inst.loader,
      loaderVersion: inst.loaderVersion,
      exportedAt: new Date().toISOString(),
      launcher: 'Aurora Launcher'
    })

    const stamp = timestampName('modpack')
    const output = path.join(exportRoot, `${safeFileName(inst.name, 'modpack')}-${stamp}.zip`)
    zipDirectory(temp, output)
    const stats = await dirSize(temp)
    return { path: output, ...stats }
  } finally {
    await removeDir(temp)
  }
}

export async function importInstanceFromZip(zipFile: string): Promise<Instance> {
  const newId = uid('inst-')
  const target = instanceDir(newId)
  await ensureDir(target)
  extractZipSafe(zipFile, target)
  const existing = readJsonSync<Instance | null>(path.join(target, INSTANCE_FILE), null)
  let inst: Instance
  if (existing) {
    inst = {
      ...existing,
      id: newId,
      gameDir: target,
      name: `${existing.name}`,
      createdAt: existing.createdAt ?? Date.now()
    }
  } else {
    inst = {
      id: newId,
      name: path.basename(zipFile, path.extname(zipFile)),
      versionId: '',
      mcVersion: '',
      loader: 'vanilla',
      gameDir: target,
      icon: '📦',
      color: 'ocean',
      createdAt: Date.now(),
      totalPlayTimeMs: 0,
      playCount: 0
    }
  }
  for (const sub of SUB_DIRS) await ensureDir(path.join(target, sub))
  writeJsonSync(path.join(target, INSTANCE_FILE), inst)
  notify()
  return inst
}

export async function instanceSize(id: string): Promise<{ size: number; files: number }> {
  const inst = readInstance(id)
  return dirSize(inst?.gameDir ?? instanceDir(id))
}

export async function touchLaunch(id: string): Promise<Instance | null> {
  const inst = readInstance(id)
  if (!inst) return null
  inst.lastLaunchedAt = Date.now()
  inst.lastPlayedAt = Date.now()
  inst.playCount = (inst.playCount ?? 0) + 1
  writeInstance(inst)
  notify()
  return inst
}

export async function addPlayTime(id: string, ms: number): Promise<void> {
  const inst = readInstance(id)
  if (!inst) return
  inst.totalPlayTimeMs = (inst.totalPlayTimeMs ?? 0) + Math.max(0, ms)
  inst.lastPlayedAt = Date.now()
  writeInstance(inst)
  notify()
}

export function instanceSubDir(id: string, sub: string): string {
  const inst = readInstance(id)
  const dir = inst?.gameDir ?? instanceDir(id)
  return path.join(dir, sub)
}

export function notify(): void {
  void listInstances().then((list) => emit('instances:changed', list as unknown as Record<string, unknown>[]))
}

export function ensureInstanceFolders(id: string): void {
  const inst = readInstance(id)
  if (!inst) return
  for (const sub of SUB_DIRS) {
    try {
      fs.mkdirSync(path.join(inst.gameDir, sub), { recursive: true })
    } catch {}
  }
}

export function instanceMetaPath(id: string): string {
  return path.join(instanceDir(id), INSTANCE_FILE)
}
