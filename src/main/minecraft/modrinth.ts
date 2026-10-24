import fs from 'node:fs'
import fsp from 'node:fs/promises'
import path from 'node:path'
import { dirs } from '../core/paths'
import { fetchJson, downloadFile, runPool, formatBytes } from '../core/http'
import { ensureDir, safeFileName } from '../core/fsx'
import { consoleLog, emit } from '../core/bus'
import { TaskGroupHandle, TaskStepHandle } from '../core/tasks'
import { extractZipSafe } from '../core/zip'
import { getSettings } from '../core/settings'
import { translateModrinthMany, translateQueryToEnglish } from '../core/translate'
import { chineseNameFor } from '../core/modnames'
import { readInstance, createInstance, instancePath, cleanupInstallVersions } from './instances'
import { contentDir, gameDirOf, hasInstalledMod } from './content'
import { installFabricLike, installForgeLike, installVanilla, listLoaderVersions } from './installer'
import type { DownloadSource, LoaderKind } from '../../shared/types'
import type {
  ContentFilterKind,
  ContentKind,
  DownloadInstallOptions,
  Instance,
  ModrinthHit,
  ModrinthProject,
  ModrinthSearchResult,
  ModrinthVersion
} from '../../shared/types'

const API = 'https://api.modrinth.com/v2'
const UA = 'AuroraLauncher/1.0.0 (github.com/aurora-launcher)'

function headers(): Record<string, string> {
  return { 'User-Agent': UA, Accept: 'application/json' }
}

function modrinthSource(): DownloadSource {
  return getSettings().modrinthSource ?? 'auto'
}

export interface SearchParams {
  query?: string
  projectType?: ContentFilterKind
  mcVersion?: string
  loader?: string
  offset?: number
  limit?: number
  index?: 'relevance' | 'downloads' | 'follows' | 'newest' | 'updated'
  categories?: string[]
}

export async function searchModrinth(params: SearchParams): Promise<ModrinthSearchResult> {
  const facets: string[][] = []
  if (params.projectType) facets.push([`project_type:${params.projectType}`])
  if (params.mcVersion) facets.push([`versions:${params.mcVersion}`])
  if (params.loader) facets.push([`categories:${params.loader}`])
  if (params.categories?.length) facets.push(params.categories.map((c) => `categories:${c}`))
  const q = new URLSearchParams()
  const query = await translateQueryToEnglish(params.query)
  if (query) q.set('query', query)
  if (facets.length) q.set('facets', JSON.stringify(facets))
  q.set('limit', String(params.limit ?? 20))
  q.set('offset', String(params.offset ?? 0))
  q.set('index', params.index ?? 'relevance')
  const url = `${API}/search?${q.toString()}`
  const res = await fetchJson<ModrinthSearchResult>(url, { headers: headers(), timeout: 20_000, source: modrinthSource() })
  res.hits = res.hits.map((hit) => ({ ...hit, nameTranslation: chineseNameFor('modrinth', hit.slug) }))
  return res
}

export async function getModrinthTranslations(ids: string[]): Promise<Record<string, string>> {
  const translations = await translateModrinthMany(ids)
  return Object.fromEntries(translations)
}

export async function getProject(id: string): Promise<ModrinthProject> {
  // The project endpoint uses a different shape than search hits:
  // it returns `id` + `followers`, while search hits use `project_id` + `follows`.
  const raw = await fetchJson<Record<string, unknown>>(`${API}/project/${encodeURIComponent(id)}`, {
    headers: headers(),
    timeout: 20_000,
    source: modrinthSource()
  })
  const asString = (value: unknown, fallback = ''): string => (typeof value === 'string' ? value : fallback)
  const asNumber = (value: unknown): number => (typeof value === 'number' && Number.isFinite(value) ? value : 0)
  const categories = Array.isArray(raw.categories) ? (raw.categories as string[]) : []
  const versions = Array.isArray(raw.versions) ? (raw.versions as string[]) : []
  const gallery = Array.isArray(raw.gallery) ? (raw.gallery as ModrinthProject['gallery']) : []
  return {
    ...(raw as unknown as ModrinthProject),
    nameTranslation: chineseNameFor('modrinth', asString(raw.slug, id)),
    project_id: asString(raw.project_id, asString(raw.id, id)),
    id: asString(raw.id, id),
    slug: asString(raw.slug, id),
    title: asString(raw.title, id),
    description: asString(raw.description),
    body: asString(raw.body),
    categories,
    versions,
    gallery,
    downloads: asNumber(raw.downloads),
    follows: asNumber(raw.follows ?? raw.followers),
    followers: asNumber(raw.followers ?? raw.follows),
    author: asString(raw.author),
    project_type: asString(raw.project_type, 'mod'),
    date_modified: asString(raw.date_modified, asString(raw.updated)),
    client_side: asString(raw.client_side, 'unknown'),
    server_side: asString(raw.server_side, 'unknown'),
    updated: asString(raw.updated, asString(raw.date_modified)),
    published: asString(raw.published),
    icon_url: typeof raw.icon_url === 'string' ? raw.icon_url : undefined,
    color: typeof raw.color === 'number' ? raw.color : undefined
  }
}

async function fetchVersionJson<T>(url: string): Promise<T> {
  return fetchJson<T>(url, { headers: headers(), timeout: 20_000, source: modrinthSource() })
}

export async function getVersions(projectId: string, mcVersion?: string, loader?: string): Promise<ModrinthVersion[]> {
  const q = new URLSearchParams()
  if (mcVersion) q.set('game_versions', JSON.stringify([mcVersion]))
  if (loader) q.set('loaders', JSON.stringify([loader]))
  const url = `${API}/project/${encodeURIComponent(projectId)}/version${q.size ? `?${q.toString()}` : ''}`
  return fetchVersionJson<ModrinthVersion[]>(url)
}

export async function getVersion(versionId: string): Promise<ModrinthVersion> {
  return fetchVersionJson<ModrinthVersion>(`${API}/version/${encodeURIComponent(versionId)}`)
}

export async function featured(projectType: ContentFilterKind = 'mod'): Promise<ModrinthHit[]> {
  const res = await searchModrinth({ projectType, limit: 12, index: 'follows' })
  return res.hits
}

function loaderName(kind: LoaderKind): string | undefined {
  switch (kind) {
    case 'fabric':
      return 'fabric'
    case 'quilt':
      return 'quilt'
    case 'forge':
      return 'forge'
    case 'neoforge':
      return 'neoforge'
    default:
      return undefined
  }
}

function targetKindOf(projectType: string): ContentKind | 'modpack' {
  switch (projectType) {
    case 'resourcepack':
      return 'resourcepacks'
    case 'shader':
      return 'shaderpacks'
    case 'datapack':
      return 'datapacks'
    case 'modpack':
      return 'modpack'
    default:
      return 'mods'
  }
}

async function pickVersion(
  projectId: string,
  instance: { mcVersion: string; loader: LoaderKind },
  versionId?: string
): Promise<ModrinthVersion> {
  if (versionId) return getVersion(versionId)
  const loader = loaderName(instance.loader)
  let versions = await getVersions(projectId, instance.mcVersion, loader)
  if (!versions.length) versions = await getVersions(projectId, instance.mcVersion)
  if (!versions.length) versions = await getVersions(projectId)
  if (!versions.length) throw new Error('没有找到兼容的版本')
  return versions[0]
}

export async function installProject(opts: DownloadInstallOptions): Promise<{ installed: string[]; skipped: string[] }> {
  const instance = readInstance(opts.instanceId)
  if (!instance) throw new Error('实例不存在')
  const group = new TaskGroupHandle('安装 Modrinth 内容')
  const installed: string[] = []
  const skipped: string[] = []
  try {
    const project = await getProject(opts.projectId)
    const kind = targetKindOf(project.project_type)
    if (kind === 'modpack') {
      const result = await installModpack(opts.projectId, opts.versionId, undefined, group)
      installed.push(result.instanceId)
      group.finish('done')
      return { installed, skipped }
    }
    const version = await pickVersion(opts.projectId, instance, opts.versionId)
    const file = version.files.find((f) => f.primary) ?? version.files[0]
    if (!file) throw new Error('该版本没有可下载文件')
    const dir = contentDir(opts.instanceId, kind)
    await ensureDir(dir)
    const dest = path.join(dir, safeFileName(file.filename))
    const step = group.step(`下载 ${project.title}`, 1)
    step.start(file.size || 1, file.filename)
    if (fs.existsSync(dest) && fs.statSync(dest).size === file.size) {
      skipped.push(file.filename)
      step.done('已存在')
    } else {
      await downloadFile({
        url: file.url,
        dest,
        sha1: file.hashes?.sha1,
        size: file.size,
        source: modrinthSource(),
        signal: group.signal,
        retries: 2,
        onProgress: (received, total) =>
          step.progress(received, `${formatBytes(received)} / ${formatBytes(total || file.size)}`, total || file.size)
      })
      installed.push(file.filename)
      step.done(formatBytes(file.size))
    }
    consoleLog('download', 'info', `已安装 ${project.title} ${version.version_number} -> ${kind}`)
    if (opts.withDependencies !== false) {
      const visited = new Set<string>([opts.projectId])
      await installDependencies(version, instance, visited, installed, skipped, 0, group)
    }
    group.finish('done')
    return { installed, skipped }
  } catch (e) {
    group.fail(e)
    throw e
  }
}

/** Download one selected project version into an arbitrary folder (no instance). */
export async function installProjectToFolder(opts: { projectId: string; versionId: string; destDir: string }): Promise<{ installed: string[] }> {
  const project = await getProject(opts.projectId)
  const version = await getVersion(opts.versionId)
  const file = version.files.find((f) => f.primary) ?? version.files[0]
  if (!file) throw new Error('该版本没有可下载文件')
  const group = new TaskGroupHandle(`安装 ${project.title}`)
  const step = group.step(`下载 ${project.title}`, 1)
  try {
    await ensureDir(opts.destDir)
    const dest = path.join(opts.destDir, safeFileName(file.filename))
    const totalSize = file.size || 1
    step.start(totalSize, file.filename)
    await downloadFile({
      url: file.url,
      dest,
      sha1: file.hashes?.sha1,
      size: file.size,
      source: modrinthSource(),
      retries: 2,
      signal: group.signal,
      onProgress: (received, total) =>
        step.progress(received, `${formatBytes(received)} / ${formatBytes(total || totalSize)}`, total || totalSize)
    })
    step.done(formatBytes(file.size))
    group.finish('done')
    return { installed: [file.filename] }
  } catch (e) {
    group.fail(e)
    throw e
  }
}

async function installDependencies(
  version: ModrinthVersion,
  instance: Instance,
  visited: Set<string>,
  installed: string[],
  skipped: string[],
  depth: number,
  group: TaskGroupHandle
): Promise<void> {
  if (depth > 4) return
  for (const dep of version.dependencies ?? []) {
    if (dep.dependency_type !== 'required') continue
    const projectId = dep.project_id
    if (!projectId || visited.has(projectId)) continue
    visited.add(projectId)
    let step: TaskStepHandle | null = null
    try {
      const project = await getProject(projectId)
      if (project.project_type !== 'mod') continue
      const depVersion = await pickVersion(projectId, instance, dep.version_id)
      const file = depVersion.files.find((f) => f.primary) ?? depVersion.files[0]
      if (!file) continue
      if (hasInstalledMod(instance.id, { fileNames: [file.filename], ids: [projectId, project.slug], names: [project.title] })) {
        skipped.push(file.filename)
        continue
      }
      const dir = contentDir(instance.id, 'mods')
      await ensureDir(dir)
      const dest = path.join(dir, safeFileName(file.filename))
      step = group.step(`下载前置 ${project.title}`, 1)
      if (fs.existsSync(dest) && fs.statSync(dest).size === file.size) {
        step!.start(1, file.filename)
        step!.done('已存在')
        skipped.push(file.filename)
      } else {
        const totalSize = file.size || 1
        step!.start(totalSize, file.filename)
        await downloadFile({
          url: file.url,
          dest,
          sha1: file.hashes?.sha1,
          size: file.size,
          source: modrinthSource(),
          retries: 2,
          onProgress: (received, total) =>
            step!.progress(received, `${formatBytes(received)} / ${formatBytes(total || totalSize)}`, total || totalSize)
        })
        step!.done(formatBytes(file.size))
        installed.push(file.filename)
        consoleLog('download', 'info', `安装前置: ${project.title}`)
      }
      await installDependencies(depVersion, instance, visited, installed, skipped, depth + 1, group)
    } catch (e) {
      step?.done('跳过：下载失败')
      consoleLog('download', 'warn', `前置安装失败: ${e instanceof Error ? e.message : String(e)}`)
    }
  }
}

// ---------------------------------------------------------------------------
// Modpacks (.mrpack)
// ---------------------------------------------------------------------------

interface MrpackIndex {
  formatVersion: number
  game: string
  versionId: string
  name: string
  summary?: string
  files: {
    path: string
    hashes: { sha1?: string; sha512?: string }
    env?: { client?: string; server?: string }
    downloads: string[]
    fileSize: number
  }[]
  dependencies: Record<string, string>
}

export async function installModpack(
  projectId: string,
  versionId?: string,
  instanceName?: string,
  outerGroup?: TaskGroupHandle
): Promise<{ instanceId: string }> {
  const group = outerGroup ?? new TaskGroupHandle('安装整合包')
  try {
    const project = await getProject(projectId)
    let versions = await getVersions(projectId)
    if (versionId) versions = versions.filter((v) => v.id === versionId)
    if (!versions.length) throw new Error('没有可用的整合包版本')
    const version = versions[0]
    const file = version.files.find((f) => f.primary) ?? version.files[0]
    if (!file) throw new Error('整合包没有可下载文件')
    const tmpFile = path.join(dirs().cache, 'modpacks', safeFileName(file.filename))
    const step = group.step('下载整合包', 1)
    step.start(1, file.filename)
    await downloadFile({ url: file.url, dest: tmpFile, sha1: file.hashes?.sha1, size: file.size, source: modrinthSource(), signal: group.signal, retries: 2 })
    step.done(formatBytes(file.size))
    const extractDir = path.join(dirs().cache, 'modpacks', `${version.id}-extracted`)
    await fsp.rm(extractDir, { recursive: true, force: true })
    await ensureDir(extractDir)
    extractZipSafe(tmpFile, extractDir)
    const indexFile = path.join(extractDir, 'modrinth.index.json')
    if (!fs.existsSync(indexFile)) throw new Error('无效的整合包：缺少 modrinth.index.json')
    const index = JSON.parse(fs.readFileSync(indexFile, 'utf8')) as MrpackIndex
    const mcVersion = index.dependencies.minecraft
    const loaderKind = detectPackLoader(index.dependencies)
    const loaderVersion = loaderKind === 'fabric' ? index.dependencies['fabric-loader'] : loaderKind === 'quilt' ? index.dependencies['quilt-loader'] : loaderKind === 'forge' ? index.dependencies.forge : loaderKind === 'neoforge' ? index.dependencies.neoforge : undefined

    // find or install matching version
    const before = new Set(fs.readdirSync(dirs().versions))
    const versionIdResolved = await resolvePackVersion(mcVersion, loaderKind, loaderVersion, group)

    const name = instanceName || project.title || index.name
    const inst = await createInstance({
      name,
      versionId: versionIdResolved,
      mcVersion,
      loader: loaderKind,
      loaderVersion,
      icon: '📦',
      color: 'violet'
    })
    inst.notes = `由整合包 ${project.title} ${version.version_number} 安装`
    const { updateInstance } = await import('./instances')
    await updateInstance(inst.id, { notes: inst.notes })
    const created = fs.readdirSync(dirs().versions).filter((id) => !before.has(id))
    await cleanupInstallVersions(created, inst.versionId)

    // download files
    const files = index.files.filter((f) => f.env?.client !== 'unsupported')
    const dlStep = group.step('下载整合包文件', files.length)
    dlStep.start(files.length)
    let done = 0
    let bytes = 0
    await runPool(
      files,
      Math.max(4, Math.min(24, getSettings().maxConcurrency)),
      async (entry) => {
        const dest = path.join(inst.gameDir, entry.path)
        await ensureDir(path.dirname(dest))
        if (fs.existsSync(dest) && fs.statSync(dest).size === entry.fileSize) {
          done++
          return
        }
        let lastError: unknown = null
        for (const url of entry.downloads) {
          try {
            await downloadFile({ url, dest, sha1: entry.hashes.sha1, size: entry.fileSize, source: modrinthSource(), signal: group.signal, retries: 1 })
            lastError = null
            break
          } catch (e) {
            lastError = e
          }
        }
        if (lastError) throw lastError
        bytes += entry.fileSize
        done++
        dlStep.progress(done, `${done}/${files.length} · ${formatBytes(bytes)}`)
      },
      group.signal
    )
    dlStep.done()

    const overrideStep = group.step('应用整合包配置', 1)
    overrideStep.start(1)
    for (const folder of ['overrides', 'client-overrides']) {
      const src = path.join(extractDir, folder)
      if (fs.existsSync(src)) {
        await fsp.cp(src, inst.gameDir, { recursive: true, force: true })
      }
    }
    overrideStep.done()

    await fsp.rm(extractDir, { recursive: true, force: true }).catch(() => {})
    await fsp.rm(tmpFile, { force: true }).catch(() => {})
    group.finish('done')
    return { instanceId: inst.id }
  } catch (e) {
    group.fail(e)
    throw e
  }
}

export async function installLocalModpack(zipFile: string): Promise<{ instanceId: string }> {
  const group = new TaskGroupHandle('安装本地整合包')
  const extractDir = path.join(dirs().cache, 'modpacks', `local-${Date.now()}`)
  try {
    await fsp.rm(extractDir, { recursive: true, force: true })
    await ensureDir(extractDir)
    const step = group.step('解压整合包', 1)
    step.start(1, path.basename(zipFile))
    extractZipSafe(zipFile, extractDir)
    step.done()
    const indexFile = path.join(extractDir, 'modrinth.index.json')
    if (!fs.existsSync(indexFile)) throw new Error('无效的整合包：缺少 modrinth.index.json')
    const index = JSON.parse(fs.readFileSync(indexFile, 'utf8')) as MrpackIndex
    const mcVersion = index.dependencies.minecraft
    if (!mcVersion) throw new Error('整合包没有声明 Minecraft 版本')
    const loaderKind = detectPackLoader(index.dependencies)
    const loaderVersion =
      loaderKind === 'fabric'
        ? index.dependencies['fabric-loader']
        : loaderKind === 'quilt'
          ? index.dependencies['quilt-loader']
          : loaderKind === 'forge'
            ? index.dependencies.forge
            : loaderKind === 'neoforge'
              ? index.dependencies.neoforge
              : undefined
    const before = new Set(fs.readdirSync(dirs().versions))
    const versionIdResolved = await resolvePackVersion(mcVersion, loaderKind, loaderVersion, group)
    const name = index.name || path.basename(zipFile, path.extname(zipFile))
    const inst = await createInstance({
      name,
      versionId: versionIdResolved,
      mcVersion,
      loader: loaderKind,
      loaderVersion,
      icon: '📦',
      color: 'violet'
    })
    inst.notes = `由本地整合包 ${name}${index.versionId ? ' ' + index.versionId : ''} 安装`
    const { updateInstance } = await import('./instances')
    await updateInstance(inst.id, { notes: inst.notes })
    const created = fs.readdirSync(dirs().versions).filter((id) => !before.has(id))
    await cleanupInstallVersions(created, inst.versionId)

    const files = (index.files ?? []).filter((file) => file.env?.client !== 'unsupported')
    const dlStep = group.step('下载整合包文件', files.length)
    dlStep.start(files.length)
    let done = 0
    let bytes = 0
    await runPool(
      files,
      Math.max(4, Math.min(24, getSettings().maxConcurrency)),
      async (entry) => {
        const dest = path.join(inst.gameDir, entry.path)
        const rel = path.relative(inst.gameDir, dest)
        if (rel.startsWith('..') || path.isAbsolute(rel)) throw new Error(`非法整合包路径: ${entry.path}`)
        await ensureDir(path.dirname(dest))
        if (fs.existsSync(dest) && fs.statSync(dest).size === entry.fileSize) {
          done++
          dlStep.progress(done, `${done}/${files.length}`)
          return
        }
        let lastError: unknown = null
        for (const url of entry.downloads ?? []) {
          try {
            await downloadFile({ url, dest, sha1: entry.hashes?.sha1, size: entry.fileSize, source: modrinthSource(), signal: group.signal, retries: 1 })
            lastError = null
            break
          } catch (e) {
            lastError = e
          }
        }
        if (lastError) throw lastError
        bytes += entry.fileSize
        done++
        dlStep.progress(done, `${done}/${files.length} · ${formatBytes(bytes)}`)
      },
      group.signal
    )
    dlStep.done()

    const overrideStep = group.step('应用整合包配置', 1)
    overrideStep.start(1)
    for (const folder of ['overrides', 'client-overrides']) {
      const src = path.join(extractDir, folder)
      if (fs.existsSync(src)) await fsp.cp(src, inst.gameDir, { recursive: true, force: true })
    }
    overrideStep.done()
    group.finish('done')
    return { instanceId: inst.id }
  } catch (e) {
    group.fail(e)
    throw e
  } finally {
    await fsp.rm(extractDir, { recursive: true, force: true }).catch(() => {})
  }
}

function detectPackLoader(deps: Record<string, string>): LoaderKind {
  if (deps['fabric-loader']) return 'fabric'
  if (deps['quilt-loader']) return 'quilt'
  if (deps.neoforge) return 'neoforge'
  if (deps.forge) return 'forge'
  return 'vanilla'
}

export async function resolvePackVersion(
  mcVersion: string,
  loader: LoaderKind,
  loaderVersion: string | undefined,
  group: TaskGroupHandle
): Promise<string> {
  const { listInstances } = await import('./instances')
  const versions = await import('./version').then((m) => m.listInstalledVersions())
  const candidates = versions.filter((v) => (v.mcVersion === mcVersion || v.id === mcVersion) && (loader === 'vanilla' || v.loader === loader))
  if (loaderVersion) {
    const exact = candidates.find((v) => v.loaderVersion === loaderVersion)
    if (exact) return exact.id
  } else if (candidates.length) {
    return candidates[0].id
  }
  // install required loader
  if (loader === 'vanilla') {
    const id = await installVanilla(mcVersion, group)
    return id
  }
  if (loader === 'fabric' || loader === 'quilt') {
    const res = await installFabricLike(loader, mcVersion, loaderVersion, group)
    return res.versionId
  }
  if (loader === 'forge' || loader === 'neoforge') {
    const res = await installForgeLike(loader, mcVersion, loaderVersion, group)
    return res.versionId
  }
  return installVanilla(mcVersion, group)
}
