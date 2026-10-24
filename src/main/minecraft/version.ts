import fs from 'node:fs'
import fsp from 'node:fs/promises'
import path from 'node:path'
import os from 'node:os'
import { dirs, versionJsonPath, versionJarPath, versionDir, librariesDir, assetsIndexPath } from '../core/paths'
import { fetchJson, downloadFile, formatBytes } from '../core/http'
import { readJsonSync, writeJsonSync, exists, sha1File, dirSize, listFiles } from '../core/fsx'
import { consoleLog, emit } from '../core/bus'
import type { InstalledVersion, LoaderKind, McVersionSummary, VersionManifest } from '../../shared/types'

// ---------------------------------------------------------------------------
// Raw Mojang JSON types
// ---------------------------------------------------------------------------

export interface OsRule {
  name?: string
  arch?: string
  version?: string
  versionRange?: { min?: string; max?: string }
}

export interface Rule {
  action: 'allow' | 'disallow'
  os?: OsRule
  features?: Record<string, boolean>
}

export type ArgumentEntry = string | { rules?: Rule[]; value: string | string[] }

export interface DownloadInfo {
  path?: string
  url: string
  sha1?: string
  size?: number
}

export interface Library {
  name: string
  downloads?: { artifact?: DownloadInfo; classifiers?: Record<string, DownloadInfo> }
  natives?: Record<string, string>
  rules?: Rule[]
  extract?: { exclude?: string[] }
  url?: string
}

export interface VersionJson {
  id: string
  type?: string
  time?: string
  releaseTime?: string
  mainClass?: string
  inheritsFrom?: string
  minecraftArguments?: string
  arguments?: { game?: ArgumentEntry[]; jvm?: ArgumentEntry[]; 'default-user-jvm'?: ArgumentEntry[] }
  libraries?: Library[]
  downloads?: { client?: DownloadInfo; server?: DownloadInfo; client_mappings?: DownloadInfo; server_mappings?: DownloadInfo }
  assetIndex?: { id: string; url: string; sha1?: string; size?: number; totalSize?: number }
  assets?: string
  javaVersion?: { component: string; majorVersion: number }
  logging?: { client?: { argument: string; file: { id: string; url: string; sha1?: string; size?: number } } }
  complianceLevel?: number
  minimumLauncherVersion?: number
}

export interface AuroraMeta {
  loader: LoaderKind
  loaderVersion?: string
  mcVersion: string
  installedAt: number
  profileId: string
}

export interface ResolvedLibrary {
  lib: Library
  artifact?: DownloadInfo
  /** true when the artifact is a legacy native jar that must be extracted, not put on the classpath */
  extract: boolean
}

const MANIFEST_CACHE = () => path.join(dirs().cache, 'version_manifest.json')
const MANIFEST_TTL = 5 * 60 * 1000

const OS_NAME = process.platform === 'win32' ? 'windows' : process.platform === 'darwin' ? 'osx' : 'linux'

export function osName(): string {
  return OS_NAME
}

export function osArch(): string {
  if (process.arch === 'x64') return 'x86_64'
  if (process.arch === 'ia32') return 'x86'
  if (process.arch === 'arm64') return 'arm64'
  return process.arch
}

// ---------------------------------------------------------------------------
// Version manifest
// ---------------------------------------------------------------------------

interface CacheEnvelope {
  fetchedAt: number
  data: VersionManifest
}

export async function getVersionManifest(force = false): Promise<VersionManifest> {
  const cacheFile = MANIFEST_CACHE()
  if (!force) {
    const cached = readJsonSync<CacheEnvelope | null>(cacheFile, null)
    if (cached && Date.now() - cached.fetchedAt < MANIFEST_TTL) return cached.data
  }
  const raw = await fetchJson<{ latest: { release: string; snapshot: string }; versions: McVersionSummary[] }>(
    'https://piston-meta.mojang.com/mc/game/version_manifest_v2.json',
    { timeout: 20_000 }
  )
  const manifest: VersionManifest = {
    latest: raw.latest,
    versions: raw.versions.map((v) => ({
      id: v.id,
      type: v.type,
      releaseTime: v.releaseTime,
      url: v.url,
      sha1: v.sha1
    })),
    fetchedAt: Date.now()
  }
  writeJsonSync(cacheFile, { fetchedAt: Date.now(), data: manifest })
  return manifest
}

export async function getVersionSummary(versionId: string): Promise<McVersionSummary | null> {
  const manifest = await getVersionManifest()
  return manifest.versions.find((v) => v.id === versionId) ?? null
}

// ---------------------------------------------------------------------------
// Version JSON loading & inheritance
// ---------------------------------------------------------------------------

export function readLocalVersionJson(versionId: string): VersionJson | null {
  const file = versionJsonPath(versionId)
  if (!fs.existsSync(file)) return null
  return readJsonSync<VersionJson | null>(file, null)
}

export async function readRemoteVersionJson(versionId: string): Promise<VersionJson> {
  const summary = await getVersionSummary(versionId)
  if (!summary) throw new Error(`未找到版本 ${versionId}`)
  return fetchJson<VersionJson>(summary.url, { timeout: 20_000 })
}

/** Loads a version JSON from disk, or falls back to the official manifest. */
export async function loadVersionJson(versionId: string): Promise<VersionJson> {
  const local = readLocalVersionJson(versionId)
  if (local) return local
  const remote = await readRemoteVersionJson(versionId)
  return remote
}

export function readAuroraMeta(versionId: string): AuroraMeta | null {
  return readJsonSync<AuroraMeta | null>(path.join(versionDir(versionId), 'aurora.json'), null)
}

export function writeAuroraMeta(versionId: string, meta: AuroraMeta): void {
  writeJsonSync(path.join(versionDir(versionId), 'aurora.json'), meta)
}

export interface VersionChain {
  id: string
  chainIds: string[]
  json: VersionJson
  libraries: Library[]
  jvmArgs: ArgumentEntry[]
  gameArgs: ArgumentEntry[]
  defaultUserJvm: ArgumentEntry[]
  minecraftArguments?: string
  mainClass: string
  assetIndexId: string
  assetIndex?: { id: string; url: string; sha1?: string; size?: number }
  clientDownload?: DownloadInfo
  javaMajor?: number
  javaComponent?: string
  logging?: VersionJson['logging']
  releaseTime?: string
  type?: string
  inheritedJarId: string
}

function libKey(lib: Library): string {
  return lib.name
}

function libraryIdentity(name: string): string {
  const parts = name.split(':')
  const withoutVersion = [parts[0], parts[1], parts[3] ?? ''].join(':')
  return withoutVersion
}

/** Merge a full inheritance chain into one effective version description. */
export async function loadVersionChain(versionId: string): Promise<VersionChain> {
  const chainIds: string[] = []
  const jsons: VersionJson[] = []
  let current: string | undefined = versionId
  const seen = new Set<string>()
  while (current) {
    if (seen.has(current)) throw new Error(`版本继承出现循环: ${current}`)
    seen.add(current)
    const json = await loadVersionJson(current)
    jsons.push(json)
    chainIds.push(current)
    current = json.inheritsFrom
  }
  // jsons[0] = child, last = root parent
  const root = jsons[jsons.length - 1]
  const child = jsons[0]

  const libs: Library[] = []
  const indexByKey = new Map<string, number>()
  for (const json of [...jsons].reverse()) {
    for (const lib of json.libraries ?? []) {
      const key = libKey(lib)
      const existing = indexByKey.get(key)
      if (existing !== undefined) {
        libs[existing] = lib
      } else {
        indexByKey.set(key, libs.length)
        libs.push(lib)
      }
    }
  }

  const jvmArgs: ArgumentEntry[] = []
  const gameArgs: ArgumentEntry[] = []
  const defaultUserJvm: ArgumentEntry[] = []
  for (const json of [...jsons].reverse()) {
    if (json.arguments?.jvm?.length) jvmArgs.push(...json.arguments.jvm)
    if (json.arguments?.game?.length) gameArgs.push(...json.arguments.game)
    if (json.arguments?.['default-user-jvm']?.length) defaultUserJvm.push(...json.arguments['default-user-jvm'])
  }

  const minecraftArguments = child.minecraftArguments ?? root.minecraftArguments
  const mainClass = child.mainClass ?? root.mainClass ?? ''
  const assetIndexId = child.assetIndex?.id ?? root.assetIndex?.id ?? child.assets ?? root.assets ?? ''
  const clientDownload = findClientDownload(jsons)
  const javaVersion = child.javaVersion ?? findJavaVersion(jsons)

  return {
    id: versionId,
    chainIds,
    json: child,
    libraries: libs,
    jvmArgs,
    gameArgs,
    defaultUserJvm,
    minecraftArguments,
    mainClass,
    assetIndexId,
    assetIndex: child.assetIndex ?? root.assetIndex,
    clientDownload,
    javaMajor: javaVersion?.majorVersion,
    javaComponent: javaVersion?.component,
    logging: child.logging ?? findLogging(jsons),
    releaseTime: child.releaseTime ?? root.releaseTime,
    type: child.type ?? root.type,
    inheritedJarId: findJarId(jsons)
  }
}

function findClientDownload(jsons: VersionJson[]): DownloadInfo | undefined {
  for (const json of jsons) {
    if (json.downloads?.client?.url) return json.downloads.client
  }
  return undefined
}

function findJavaVersion(jsons: VersionJson[]): { component: string; majorVersion: number } | undefined {
  for (const json of jsons) {
    if (json.javaVersion?.majorVersion) return json.javaVersion
  }
  return undefined
}

function findLogging(jsons: VersionJson[]): VersionJson['logging'] {
  for (const json of jsons) if (json.logging?.client) return json.logging
  return undefined
}

function findJarId(jsons: VersionJson[]): string {
  for (const json of jsons) {
    if (json.downloads?.client?.url) return json.id
  }
  return jsons[jsons.length - 1].id
}

// ---------------------------------------------------------------------------
// Rules & libraries
// ---------------------------------------------------------------------------

export interface RuleContext {
  features: Record<string, boolean>
}

export function rulesAllow(rules: Rule[] | undefined, features: Record<string, boolean>): boolean {
  if (!rules || rules.length === 0) return true
  let allowed = false
  for (const rule of rules) {
    if (!ruleMatches(rule, features)) continue
    allowed = rule.action === 'allow'
  }
  return allowed
}

function ruleMatches(rule: Rule, features: Record<string, boolean>): boolean {
  if (rule.os) {
    if (rule.os.name && rule.os.name !== OS_NAME) return false
    if (rule.os.arch) {
      const arch = rule.os.arch
      if (arch === 'x86' && osArch() !== 'x86') return false
      if (arch === 'x86_64' && osArch() !== 'x86_64') return false
      if (arch === 'arm64' && osArch() !== 'arm64') return false
    }
    const systemVersion = systemVersionString()
    if (rule.os.version) {
      try {
        const re = new RegExp(rule.os.version)
        if (!re.test(systemVersion)) return false
      } catch {
        // invalid regex - ignore
      }
    }
    if (rule.os.versionRange) {
      const { min, max } = rule.os.versionRange
      if (min && compareDotted(systemVersion, min) < 0) return false
      if (max && compareDotted(systemVersion, max) > 0) return false
    }
  }
  if (rule.features) {
    for (const [key, expected] of Object.entries(rule.features)) {
      const actual = features[key] ?? false
      if (actual !== expected) return false
    }
  }
  return true
}

function systemVersionString(): string {
  const raw = process.getSystemVersion?.()
  if (raw) return raw
  try {
    return os.release()
  } catch {
    return ''
  }
}

function compareDotted(a: string, b: string): number {
  const pa = a.split(/[._+-]/).map((x) => parseInt(x, 10)).filter((x) => !Number.isNaN(x))
  const pb = b.split(/[._+-]/).map((x) => parseInt(x, 10)).filter((x) => !Number.isNaN(x))
  const len = Math.max(pa.length, pb.length)
  for (let i = 0; i < len; i++) {
    const x = pa[i] ?? 0
    const y = pb[i] ?? 0
    if (x !== y) return x - y
  }
  return 0
}

/** Newer snapshots omit logging.client.file.id; derive a stable file name. */
export function logConfigName(file: { id?: string; url?: string; sha1?: string }): string {
  if (file.id) return file.id
  if (file.url) {
    try {
      return path.basename(new URL(file.url).pathname)
    } catch {}
  }
  return `${file.sha1 ?? 'client'}-log4j.xml`
}

export function mavenToPath(name: string): string {
  const [coordsRaw, extRaw] = name.split('@')
  const coords = coordsRaw.split(':')
  if (coords.length < 3) return name
  const [group, artifact, version, classifier] = coords
  const ext = extRaw ?? 'jar'
  const file = classifier ? `${artifact}-${version}-${classifier}.${ext}` : `${artifact}-${version}.${ext}`
  return `${group.replace(/\./g, '/')}/${artifact}/${version}/${file}`
}

export function libraryArtifact(lib: Library): DownloadInfo | undefined {
  if (lib.downloads?.artifact) return lib.downloads.artifact
  if (lib.url) {
    const rel = mavenToPath(lib.name)
    return { path: rel, url: lib.url.replace(/\/?$/, '/') + rel }
  }
  return undefined
}

/**
 * Resolve the effective library list.
 *
 * - Modern versions list native jars as ordinary libraries (name ...:natives-windows):
 *   those stay on the classpath and LWJGL extracts them at runtime.
 * - Legacy versions declare a `natives` mapping: the base jar goes on the classpath and
 *   the classifier jar is extracted into the natives directory by the launcher.
 */
export function resolveLibraries(libs: Library[], features: Record<string, boolean>): ResolvedLibrary[] {
  const out: ResolvedLibrary[] = []
  const byIdentity = new Map<string, number>()

  const pushNormal = (lib: Library, artifact: DownloadInfo | undefined): void => {
    const identity = libraryIdentity(lib.name)
    const idx = byIdentity.get(identity)
    if (idx !== undefined) {
      out[idx] = { lib, artifact, extract: false }
      return
    }
    byIdentity.set(identity, out.length)
    out.push({ lib, artifact, extract: false })
  }

  for (const lib of libs) {
    if (!rulesAllow(lib.rules, features)) continue
    const artifact = libraryArtifact(lib)
    const legacyClassifier = lib.natives?.[OS_NAME]
    if (legacyClassifier) {
      pushNormal(lib, artifact)
      const classifier = legacyClassifier.replace('${arch}', process.arch === 'x64' ? '64' : process.arch === 'ia32' ? '32' : 'arm64')
      const dl = lib.downloads?.classifiers?.[classifier]
      if (dl) out.push({ lib, artifact: dl, extract: true })
      continue
    }
    pushNormal(lib, artifact)
  }
  return out
}

export function classpathLibraries(resolved: ResolvedLibrary[]): { path: string; url: string }[] {
  const out: { path: string; url: string }[] = []
  const seen = new Set<string>()
  for (const entry of resolved) {
    if (entry.extract) continue
    if (!entry.artifact?.path) continue
    if (seen.has(entry.artifact.path)) continue
    seen.add(entry.artifact.path)
    out.push({ path: entry.artifact.path, url: entry.artifact.url })
  }
  return out
}

// ---------------------------------------------------------------------------
// Installed versions
// ---------------------------------------------------------------------------

export async function listInstalledVersions(): Promise<InstalledVersion[]> {
  const dir = dirs().versions
  const entries = await listFiles(dir)
  const out: InstalledVersion[] = []
  for (const entry of entries) {
    if (!entry.isDirectory()) continue
    const id = entry.name
    const file = path.join(dir, id, `${id}.json`)
    if (!fs.existsSync(file)) continue
    const json = readJsonSync<VersionJson | null>(file, null)
    if (!json) continue
    const meta = readAuroraMeta(id)
    const detected = detectLoaderFromVersion(id, json)
    const jar = path.join(dir, id, `${id}.jar`)
    let sizeBytes = 0
    try {
      sizeBytes = (await fsp.stat(file)).size
      if (fs.existsSync(jar)) sizeBytes += (await fsp.stat(jar)).size
    } catch {}
    out.push({
      id,
      type: json.type ?? 'release',
      releaseTime: json.releaseTime,
      mcVersion: meta?.mcVersion ?? detected.mcVersion ?? json.inheritsFrom ?? json.id,
      loader: meta?.loader ?? detected.loader,
      loaderVersion: meta?.loaderVersion ?? detected.loaderVersion,
      mainClass: json.mainClass,
      hasJar: fs.existsSync(jar),
      sizeBytes
    })
  }
  return out.sort((a, b) => (b.releaseTime ?? '').localeCompare(a.releaseTime ?? ''))
}

export function detectLoaderFromId(id: string): LoaderKind {
  if (/^fabric-loader/i.test(id) || /fabric/i.test(id)) return 'fabric'
  if (/^quilt-loader/i.test(id) || /quilt/i.test(id)) return 'quilt'
  if (/neoforge/i.test(id)) return 'neoforge'
  if (/forge/i.test(id)) return 'forge'
  if (/optifine/i.test(id)) return 'optifine'
  return 'vanilla'
}

function readPclSetup(id: string): Record<string, string> | null {
  const file = path.join(dirs().versions, id, 'PCL', 'Setup.ini')
  try {
    if (!fs.existsSync(file)) return null
    const result: Record<string, string> = {}
    for (const rawLine of fs.readFileSync(file, 'utf8').split(/\r?\n/)) {
      const line = rawLine.replace(/^\uFEFF/, '')
      const index = line.indexOf(':')
      if (index <= 0) continue
      result[line.slice(0, index).trim()] = line.slice(index + 1).trim()
    }
    return result
  } catch {
    return null
  }
}

function versionFromLibrary(name: string): string | undefined {
  const parts = name.split(':')
  const raw = parts[parts.length - 1]
  if (!raw) return undefined
  const match = raw.match(/(\d+(?:\.\d+)+)$/)
  return match?.[1] ?? raw
}

/**
 * PCL integration packs often use a custom folder/version id (e.g. 剑与王国), so the
 * loader cannot be inferred from the folder name. Read PCL's Setup.ini first, then
 * the FML arguments/libraries in the merged JSON.
 */
export function detectLoaderFromVersion(id: string, json: VersionJson): { loader: LoaderKind; loaderVersion?: string; mcVersion?: string } {
  const pcl = readPclSetup(id)
  const args = (json.arguments?.game ?? []).filter((entry): entry is string => typeof entry === 'string')
  const argValue = (name: string): string | undefined => {
    const index = args.findIndex((entry) => entry.toLowerCase() === name.toLowerCase())
    return index >= 0 ? args[index + 1] : undefined
  }
  const libs = (json.libraries ?? []).map((lib) => lib.name)
  const findLib = (pattern: RegExp): string | undefined => libs.find((name) => pattern.test(name))
  const pclMc = pcl?.VersionVanillaName
  const argMc = argValue('--fml.mcVersion')
  const mcVersion = pclMc || argMc

  // PCL stores the exact loader and vanilla version in Setup.ini, including packs whose
  // folder name has no "forge"/"fabric" text.
  if (pcl) {
    if (pcl.VersionNeoForge) return { loader: 'neoforge', loaderVersion: pcl.VersionNeoForge, mcVersion }
    if (pcl.VersionForge) return { loader: 'forge', loaderVersion: pcl.VersionForge, mcVersion }
    if (pcl.VersionFabric) return { loader: 'fabric', loaderVersion: pcl.VersionFabric, mcVersion }
    if (pcl.VersionQuilt) return { loader: 'quilt', loaderVersion: pcl.VersionQuilt, mcVersion }
    if (pcl.VersionOptiFine) return { loader: 'optifine', loaderVersion: pcl.VersionOptiFine, mcVersion }
    const info = pcl.Info ?? ''
    const infoNeo = info.match(/NeoForge\s+([\d.]+)/i)
    if (infoNeo) return { loader: 'neoforge', loaderVersion: infoNeo[1], mcVersion }
    const infoForge = info.match(/Forge\s+([\d.]+)/i)
    if (infoForge) return { loader: 'forge', loaderVersion: infoForge[1], mcVersion }
    const infoFabric = info.match(/Fabric\s+([\d.]+)/i)
    if (infoFabric) return { loader: 'fabric', loaderVersion: infoFabric[1], mcVersion }
    const infoQuilt = info.match(/Quilt\s+([\d.]+)/i)
    if (infoQuilt) return { loader: 'quilt', loaderVersion: infoQuilt[1], mcVersion }
  }

  const neoLib = findLib(/^net\.neoforged:/i) ?? findLib(/^net\.neoforged\./i)
  if (neoLib) {
    return { loader: 'neoforge', loaderVersion: versionFromLibrary(neoLib), mcVersion }
  }
  const forgeLib = findLib(/^net\.minecraftforge:(fmlloader|forge|fmlearlydisplay):/i)
  if (forgeLib) {
    return { loader: 'forge', loaderVersion: versionFromLibrary(forgeLib), mcVersion }
  }
  const fabricLib = findLib(/^net\.fabricmc:fabric-loader/i)
  if (fabricLib) {
    return { loader: 'fabric', loaderVersion: versionFromLibrary(fabricLib), mcVersion }
  }
  const quiltLib = findLib(/^org\.quiltmc:quilt-loader/i)
  if (quiltLib) {
    return { loader: 'quilt', loaderVersion: versionFromLibrary(quiltLib), mcVersion }
  }
  const optifineLib = findLib(/^optifine:/i)
  if (optifineLib) {
    return { loader: 'optifine', loaderVersion: versionFromLibrary(optifineLib), mcVersion }
  }

  const neoArg = argValue('--fml.neoForgeVersion')
  if (neoArg) return { loader: 'neoforge', loaderVersion: neoArg, mcVersion }
  const forgeArg = argValue('--fml.forgeVersion')
  if (forgeArg) return { loader: 'forge', loaderVersion: forgeArg, mcVersion }

  const fromId = detectLoaderFromId(id)
  if (fromId !== 'vanilla') return { loader: fromId, mcVersion }
  if (json.inheritsFrom) {
    const inherited = detectLoaderFromId(json.inheritsFrom)
    if (inherited !== 'vanilla') return { loader: inherited, mcVersion }
  }
  // Main class alone cannot distinguish Forge from NeoForge, but it is a useful last hint.
  if (/cpw\.mods\.bootstraplauncher/i.test(json.mainClass ?? '') || /net\.minecraftforge/i.test(json.mainClass ?? '')) {
    return { loader: 'forge', mcVersion }
  }
  if (/net\.fabricmc/i.test(json.mainClass ?? '')) return { loader: 'fabric', mcVersion }
  if (/org\.quiltmc/i.test(json.mainClass ?? '')) return { loader: 'quilt', mcVersion }
  return { loader: 'vanilla', mcVersion }
}

export async function installedVersionSize(versionId: string): Promise<{ size: number; files: number }> {
  return dirSize(versionDir(versionId))
}

export async function javaRequirementOf(versionId: string): Promise<number> {
  try {
    const chain = await loadVersionChain(versionId)
    return chain.javaMajor ?? 8
  } catch {
    return 8
  }
}

// ---------------------------------------------------------------------------
// Asset index helpers
// ---------------------------------------------------------------------------

export interface AssetIndex {
  objects: Record<string, { hash: string; size: number }>
  virtual?: boolean
  map_to_resources?: boolean
}
