import fs from 'node:fs'
import path from 'node:path'
import os from 'node:os'
import { dirs } from '../core/paths'
import { fetchJson, runPool } from '../core/http'
import { existsSync, readJsonSync, writeJsonSync, listFiles } from '../core/fsx'
import { getSettings } from '../core/settings'
import { consoleLog } from '../core/bus'
import { TaskGroupHandle } from '../core/tasks'
import type { JavaInfo } from '../../shared/types'

const JAVA_ALL_URL =
  'https://launchermeta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json'

const JAVA_ALL_CACHE = () => path.join(dirs().cache, 'java_runtime_all.json')

interface RuntimeComponentEntry {
  availability?: { group: number; progress: number }
  manifest: { sha1: string; size: number; url: string }
  version: { name: string; released?: string }
}

type RuntimeAll = Record<string, Record<string, RuntimeComponentEntry[]>>

interface RuntimeManifest {
  files: Record<
    string,
    | { type: 'directory' }
    | { type: 'file' | 'link'; executable?: boolean; target?: string; downloads?: { raw?: { url: string; sha1: string; size: number }; lzma?: unknown } }
  >
}

let cachedJava: JavaInfo[] | null = null
let cachedAt = 0

function platformKey(): string {
  if (process.platform === 'win32') {
    if (process.arch === 'x64') return 'windows-x64'
    if (process.arch === 'ia32') return 'windows-x86'
    if (process.arch === 'arm64') return 'windows-arm64'
    return 'windows-x64'
  }
  if (process.platform === 'darwin') return process.arch === 'arm64' ? 'mac-os-arm64' : 'mac-os'
  if (process.arch === 'ia32') return 'linux-i386'
  return 'linux'
}

function javaExe(dir: string): string {
  return path.join(dir, 'bin', process.platform === 'win32' ? 'java.exe' : 'java')
}

function parseRelease(dir: string): { version?: string; arch?: string; vendor?: string } | null {
  const release = path.join(dir, 'release')
  if (!existsSync(release)) return null
  try {
    const text = fs.readFileSync(release, 'utf8')
    const get = (key: string): string | undefined => {
      const m = text.match(new RegExp(`${key}="?([^"\\r\\n]+)"?`))
      return m?.[1]
    }
    return {
      version: get('JAVA_VERSION'),
      arch: get('OS_ARCH'),
      vendor: get('IMPLEMENTOR') ?? get('JAVA_VENDOR')
    }
  } catch {
    return null
  }
}

function majorOf(version: string | undefined): number {
  if (!version) return 0
  const m = version.match(/^(\d+)(?:\.(\d+))?/)
  if (!m) return 0
  const first = parseInt(m[1], 10)
  if (first === 1 && m[2]) return parseInt(m[2], 10)
  return first
}

export function javaInfoFromDir(dir: string, source = 'auto'): JavaInfo | null {
  const exe = javaExe(dir)
  if (!existsSync(exe)) return null
  const release = parseRelease(dir)
  const managedMeta = readJsonSync<{ component?: string; major?: number; version?: string } | null>(
    path.join(dir, 'aurora.json'),
    null
  )
  const folder = path.basename(dir).toLowerCase()
  const componentMajor: Record<string, number> = {
    'jre-legacy': 8,
    'java-runtime-alpha': 16,
    'java-runtime-beta': 17,
    'java-runtime-gamma': 17,
    'java-runtime-gamma-snapshot': 17,
    'java-runtime-delta': 21,
    'java-runtime-epsilon': 25
  }
  const version = release?.version ?? managedMeta?.version ?? ''
  const major = managedMeta?.major ?? (majorOf(version) || componentMajor[folder] || 0)
  let arch: JavaInfo['arch'] = 'unknown'
  const rawArch = (release?.arch ?? '').toLowerCase()
  if (rawArch.includes('64') || rawArch === 'amd64' || rawArch === 'x86_64') arch = 'x64'
  else if (rawArch.includes('arm64') || rawArch.includes('aarch64')) arch = 'arm64'
  else if (rawArch.includes('86') || rawArch === 'i386') arch = 'x86'
  const bits = process.arch === 'x64' ? 64 : 32
  return {
    path: exe,
    version: version || (major ? String(major) : '未知'),
    major: major || 0,
    arch,
    vendor: release?.vendor ?? inferVendor(dir),
    is64Bit: release ? arch === 'x64' || arch === 'arm64' : bits === 64,
    source,
    managed: source === 'managed' || !!managedMeta
  }
}

function inferVendor(dir: string): string {
  const p = dir.toLowerCase()
  if (p.includes('temurin') || p.includes('adoptium')) return 'Eclipse Temurin'
  if (p.includes('corretto') || p.includes('amazon')) return 'Amazon Corretto'
  if (p.includes('zulu') || p.includes('azul')) return 'Azul Zulu'
  if (p.includes('graal')) return 'GraalVM'
  if (p.includes('microsoft')) return 'Microsoft OpenJDK'
  if (p.includes('bellsoft') || p.includes('liberica')) return 'BellSoft Liberica'
  if (p.includes('jbr') || p.includes('jetbrains')) return 'JetBrains Runtime'
  if (p.includes('semeru') || p.includes('ibm')) return 'IBM Semeru'
  return 'Java'
}

function addCandidate(map: Map<string, JavaInfo>, dir: string | undefined, source: string): void {
  if (!dir) return
  try {
    if (!existsSync(dir)) return
    const key = path.resolve(dir).toLowerCase()
    if (map.has(key)) return
    const info = javaInfoFromDir(dir, source)
    if (info) map.set(key, info)
  } catch {}
}

function pushDirCandidates(map: Map<string, JavaInfo>, base: string, source: string, depth = 2): void {
  if (!existsSync(base)) return
  const visit = (dir: string, level: number): void => {
    if (level < 0) return
    let entries: fs.Dirent[]
    try {
      entries = fs.readdirSync(dir, { withFileTypes: true })
    } catch {
      return
    }
    if (existsSync(javaExe(dir))) {
      addCandidate(map, dir, source)
      return
    }
    for (const e of entries) {
      if (!e.isDirectory()) continue
      visit(path.join(dir, e.name), level - 1)
    }
  }
  visit(base, depth)
}

export async function scanJava(force = false): Promise<JavaInfo[]> {
  if (!force && cachedJava && Date.now() - cachedAt < 30_000) return cachedJava
  const map = new Map<string, JavaInfo>()

  // managed runtimes
  const runtimeRoot = dirs().runtime
  if (existsSync(runtimeRoot)) {
    for (const e of fs.readdirSync(runtimeRoot, { withFileTypes: true })) {
      if (e.isDirectory()) addCandidate(map, path.join(runtimeRoot, e.name), 'managed')
    }
  }

  addCandidate(map, process.env.JAVA_HOME, 'JAVA_HOME')

  // PATH
  const pathEnv = process.env.PATH ?? ''
  for (const segment of pathEnv.split(path.delimiter)) {
    if (!segment) continue
    const exe = path.join(segment, process.platform === 'win32' ? 'java.exe' : 'java')
    if (existsSync(exe)) addCandidate(map, path.dirname(segment), 'PATH')
  }

  const roots: { dir: string; depth: number }[] = []
  if (process.platform === 'win32') {
    const pf = process.env.ProgramFiles ?? 'C:\\Program Files'
    const pf86 = process.env['ProgramFiles(x86)'] ?? 'C:\\Program Files (x86)'
    const localAppData = process.env.LOCALAPPDATA ?? ''
    roots.push(
      { dir: path.join(pf, 'Java'), depth: 1 },
      { dir: path.join(pf, 'Eclipse Adoptium'), depth: 1 },
      { dir: path.join(pf, 'Eclipse Foundation'), depth: 1 },
      { dir: path.join(pf, 'Microsoft'), depth: 1 },
      { dir: path.join(pf, 'Zulu'), depth: 1 },
      { dir: path.join(pf, 'BellSoft'), depth: 1 },
      { dir: path.join(pf, 'Amazon Corretto'), depth: 1 },
      { dir: path.join(pf, 'Semeru'), depth: 1 },
      { dir: path.join(pf, 'JetBrains'), depth: 2 },
      { dir: path.join(pf86, 'Java'), depth: 1 },
      { dir: path.join(localAppData, 'Programs', 'Eclipse Adoptium'), depth: 1 },
      { dir: path.join(os.homedir(), '.jdks'), depth: 1 },
      { dir: path.join(os.homedir(), 'scoop', 'apps'), depth: 2 }
    )
  } else if (process.platform === 'darwin') {
    roots.push({ dir: '/Library/Java/JavaVirtualMachines', depth: 2 }, { dir: '/System/Library/Java/JavaVirtualMachines', depth: 2 })
  } else {
    roots.push({ dir: '/usr/lib/jvm', depth: 1 }, { dir: '/usr/java', depth: 1 }, { dir: path.join(os.homedir(), '.jdks'), depth: 1 })
  }
  for (const r of roots) pushDirCandidates(map, r.dir, 'scan', r.depth)

  const list = [...map.values()].sort((a, b) => b.major - a.major || a.path.localeCompare(b.path))
  cachedJava = list
  cachedAt = Date.now()
  return list
}

export function invalidateJavaCache(): void {
  cachedJava = null
  cachedAt = 0
}

export async function pickJava(requiredMajor: number, preferredPath?: string): Promise<JavaInfo | null> {
  const settings = getSettings()
  if (preferredPath && existsSync(preferredPath)) {
    const info = javaInfoFromDir(path.dirname(path.dirname(preferredPath)), 'instance')
    if (info) return info
    if (existsSync(preferredPath)) {
      return { path: preferredPath, version: '', major: requiredMajor, arch: 'x64', vendor: '自定义', is64Bit: true, source: 'instance' }
    }
  }
  if (settings.javaPath && existsSync(settings.javaPath)) {
    const info = javaInfoFromDir(path.dirname(path.dirname(settings.javaPath)), 'settings')
    if (info) return info
  }
  const list = await scanJava()
  if (!list.length) return null
  const exact = list.filter((j) => j.major === requiredMajor)
  if (exact.length) {
    // Prefer a runtime bundled with Minecraft (matches the version JSON component).
    const managed = exact.find((j) => j.managed)
    return managed ?? exact[0]
  }
  // Java is generally backwards compatible; prefer the closest version >= required.
  const compatible = list
    .filter((j) => j.major >= requiredMajor)
    .sort((a, b) => a.major - b.major || (a.managed === b.managed ? 0 : a.managed ? -1 : 1))
  if (compatible.length) return compatible[0]
  // Old game versions can run on newer Java, but 1.16.5 and below break on 17+.
  return list.sort((a, b) => b.major - a.major)[0]
}

export async function getJavaRuntimeAll(force = false): Promise<RuntimeAll> {
  const cacheFile = JAVA_ALL_CACHE()
  if (!force) {
    const cached = readJsonSync<{ at: number; data: RuntimeAll } | null>(cacheFile, null)
    if (cached && Date.now() - cached.at < 60 * 60 * 1000) return cached.data
  }
  const data = await fetchJson<RuntimeAll>(JAVA_ALL_URL, { timeout: 20_000 })
  writeJsonSync(cacheFile, { at: Date.now(), data })
  return data
}

export interface JavaComponent {
  component: string
  major: number
  version: string
  manifestUrl: string
}

export async function findJavaComponent(major: number): Promise<JavaComponent | null> {
  const all = await getJavaRuntimeAll()
  const platform = all[platformKey()]
  if (!platform) return null
  const entries: JavaComponent[] = []
  for (const [component, list] of Object.entries(platform)) {
    if (!Array.isArray(list) || !list.length) continue
    const entry = list[0]
    const version = entry.version.name
    entries.push({ component, major: majorOf(version), version, manifestUrl: entry.manifest.url })
  }
  if (!entries.length) return null
  // Prefer the component Mojang itself uses for this Java major version.
  const preferred: Record<number, string> = {
    8: 'jre-legacy',
    16: 'java-runtime-alpha',
    17: 'java-runtime-gamma',
    21: 'java-runtime-delta',
    25: 'java-runtime-epsilon'
  }
  const preferredEntry = entries.find((e) => e.component === preferred[major])
  if (preferredEntry) return preferredEntry
  const exact = entries.filter((e) => e.major === major).sort((a, b) => b.version.localeCompare(a.version))[0]
  if (exact) return exact
  const newer = entries.filter((e) => e.major >= major).sort((a, b) => a.major - b.major || a.version.localeCompare(b.version))[0]
  return newer ?? entries.sort((a, b) => b.major - a.major)[0]
}

export async function downloadJava(major: number, group?: TaskGroupHandle): Promise<JavaInfo> {
  const component = await findJavaComponent(major)
  if (!component) throw new Error(`未找到适用于 Java ${major} 的运行时`)
  const targetDir = path.join(dirs().runtime, component.component)
  const manifestStep = group?.step(`获取 Java ${component.major} 运行时信息`, 1)
  manifestStep?.start(1, component.component)
  const manifest = await fetchJson<RuntimeManifest>(component.manifestUrl, { timeout: 30_000 })
  manifestStep?.done()
  const files = Object.entries(manifest.files)
  const downloadStep = group?.step(`下载 Java 运行时 (${component.version})`, files.length)
  downloadStep?.start(files.length)
  let done = 0
  const fileItems = files.filter(([, v]) => v.type === 'file' && v.downloads?.raw?.url)
  await runPool(
    fileItems,
    Math.max(4, Math.min(16, getSettings().maxConcurrency)),
    async ([rel, info]) => {
      if (info.type !== 'file' || !info.downloads?.raw) return
      const raw = info.downloads.raw
      const dest = path.join(targetDir, rel)
      if (existsSync(dest) && fs.statSync(dest).size === raw.size) {
        // skip existing
      } else {
        const { downloadFile } = await import('../core/http')
        await downloadFile({ url: raw.url, dest, sha1: raw.sha1, size: raw.size, signal: group?.signal, retries: 2 })
      }
      done++
      downloadStep?.progress(done, rel)
    },
    group?.signal
  )
  // create symlinks / launcher entries (not needed on Windows)
  for (const [rel, info] of files) {
    if (info.type === 'directory') fs.mkdirSync(path.join(targetDir, rel), { recursive: true })
  }
  writeJsonSync(path.join(targetDir, 'aurora.json'), {
    component: component.component,
    major: component.major,
    version: component.version,
    installedAt: Date.now()
  })
  downloadStep?.done()
  invalidateJavaCache()
  consoleLog('launcher', 'info', `Java 运行时已安装: ${component.component} ${component.version}`)
  return {
    path: javaExe(targetDir),
    version: component.version,
    major: component.major,
    arch: process.arch === 'x64' ? 'x64' : 'arm64',
    vendor: 'Mojang',
    is64Bit: true,
    source: 'managed',
    managed: true
  }
}

export async function deleteJava(javaPath: string): Promise<void> {
  const dir = path.dirname(path.dirname(javaPath))
  const managedMeta = readJsonSync<{ component?: string } | null>(path.join(dir, 'aurora.json'), null)
  if (!managedMeta || !dir.startsWith(dirs().runtime)) throw new Error('只能删除由 Aurora 下载的 Java')
  fs.rmSync(dir, { recursive: true, force: true })
  invalidateJavaCache()
}

/** Scans a folder for Java installations to populate the settings picker. */
export async function listJava(force = false): Promise<JavaInfo[]> {
  return scanJava(force)
}

export async function javaVersionText(javaPath: string): Promise<string> {
  const dir = path.dirname(path.dirname(javaPath))
  const release = parseRelease(dir)
  return release?.version ?? ''
}
