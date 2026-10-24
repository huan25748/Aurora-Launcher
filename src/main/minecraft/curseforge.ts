import fs from 'node:fs'
import fsp from 'node:fs/promises'
import path from 'node:path'
import { fetchJson, downloadFile, runPool, formatBytes } from '../core/http'
import { dirs } from '../core/paths'
import { consoleLog } from '../core/bus'
import { TaskGroupHandle } from '../core/tasks'
import { ensureDir, existsSync, safeFileName } from '../core/fsx'
import { readJsonEntry, extractZipSafe } from '../core/zip'
import { createInstance, getInstance, cleanupInstallVersions } from './instances'
import { hasInstalledMod } from './content'
import { chineseNameFor } from '../core/modnames'
import { translateCurseForgeMany, translateQueryToEnglish } from '../core/translate'
import { listInstalledVersions } from './version'
import { installVanilla, installFabricLike, installForgeLike } from './installer'
import type {
  ContentFilterKind,
  CurseForgeCategory,
  CurseForgeFile,
  CurseForgeHit,
  CurseForgeInstallOptions,
  CurseForgePackInstallOptions,
  CurseForgeProject,
  CurseForgeSearchResult,
  Instance
} from '../../shared/types'

const API = 'https://mod.mcimirror.top/curseforge/v1'

const CLASS_IDS: Record<ContentFilterKind, number> = {
  mod: 6,
  resourcepack: 12,
  shader: 6552,
  datapack: 6,
  modpack: 4471
}

const LOADER_IDS: Record<string, number> = {
  forge: 1,
  fabric: 4,
  quilt: 5,
  neoforge: 6
}

function headers(): Record<string, string> {
  return { 'User-Agent': 'AuroraLauncher/1.0.0 (Electron)', Accept: 'application/json' }
}

function mapHit(raw: Record<string, any>): CurseForgeHit {
  const classId = Number(raw.classId ?? 6)
  const projectType: ContentFilterKind =
    classId === 4471 ? 'modpack' : classId === 12 ? 'resourcepack' : classId === 6552 ? 'shader' : 'mod'
  return {
    id: Number(raw.id),
    name: String(raw.name ?? ''),
    slug: String(raw.slug ?? ''),
    summary: String(raw.summary ?? ''),
    logo: raw.logo?.thumbnailUrl ?? raw.logo?.url ?? undefined,
    downloads: Number(raw.downloadCount ?? 0),
    categories: Array.isArray(raw.categories)
      ? raw.categories.map((c: any) => String(c.name ?? '')).filter(Boolean)
      : [],
    authors: Array.isArray(raw.authors) ? raw.authors.map((a: any) => String(a.name ?? '')).filter(Boolean) : [],
    classId,
    projectType,
    dateModified: String(raw.dateModified ?? raw.dateCreated ?? '')
  }
}

function mapFile(raw: Record<string, any>): CurseForgeFile {
  return {
    id: Number(raw.id),
    modId: Number(raw.modId),
    displayName: String(raw.displayName ?? raw.fileName ?? ''),
    fileName: String(raw.fileName ?? ''),
    releaseType: Number(raw.releaseType ?? 1),
    fileDate: String(raw.fileDate ?? ''),
    fileLength: Number(raw.fileLength ?? 0),
    downloadCount: Number(raw.downloadCount ?? 0),
    gameVersions: Array.isArray(raw.gameVersions) ? raw.gameVersions.map(String) : [],
    downloadUrl: typeof raw.downloadUrl === 'string' ? raw.downloadUrl : null,
    hashes: Array.isArray(raw.hashes)
      ? raw.hashes.map((h: any) => ({ algo: Number(h.algo ?? 0), value: String(h.value ?? '') })).filter((h) => h.value)
      : [],
    dependencies: Array.isArray(raw.dependencies)
      ? raw.dependencies.map((d: any) => ({ modId: Number(d.modId), relationType: Number(d.relationType ?? 0) }))
      : []
  }
}

export async function searchCurseForge(opts: {
  query?: string
  categoryId?: number
  projectType: ContentFilterKind
  mcVersion?: string
  loader?: string
  offset?: number
  limit?: number
  sort?: 'relevance' | 'downloads' | 'newest' | 'updated'
}): Promise<CurseForgeSearchResult> {
  const params = new URLSearchParams({
    gameId: '432',
    classId: String(CLASS_IDS[opts.projectType] ?? 6),
    index: String(opts.offset ?? 0),
    pageSize: String(Math.max(1, Math.min(50, opts.limit ?? 20))),
    sortOrder: 'desc'
  })
  const sortField = opts.sort === 'newest' ? 10 : opts.sort === 'updated' ? 3 : opts.sort === 'relevance' ? 2 : 6
  params.set('sortField', String(sortField))
  if (opts.categoryId) params.set('categoryId', String(opts.categoryId))
  const query = await translateQueryToEnglish(opts.query)
  if (query) params.set('searchFilter', query)
  if (opts.mcVersion) params.set('gameVersion', opts.mcVersion)
  if (opts.loader && (opts.projectType === 'mod' || opts.projectType === 'modpack')) {
    const loaderId = LOADER_IDS[opts.loader]
    if (loaderId) params.set('modLoaderType', String(loaderId))
  }
  const raw = await fetchJson<{ data?: Record<string, any>[]; pagination?: { totalCount?: number } }>(
    `${API}/mods/search?${params}`,
    { headers: headers(), timeout: 20_000 }
  )
  return {
    hits: (raw.data ?? []).map((entry) => {
      const hit = mapHit(entry)
      return { ...hit, nameTranslation: chineseNameFor('curseforge', hit.slug) }
    }),
    total: Number(raw.pagination?.totalCount ?? 0),
    offset: opts.offset ?? 0,
    limit: opts.limit ?? 20
  }
}

export async function getCurseForgeTranslations(ids: number[]): Promise<Record<string, string>> {
  const translations = await translateCurseForgeMany(ids)
  return Object.fromEntries([...translations].map(([id, text]) => [String(id), text]))
}

export async function getCurseForgeCategories(projectType: ContentFilterKind): Promise<CurseForgeCategory[]> {
  const raw = await fetchJson<{ data?: { id: number; name: string }[] }>(
    `${API}/categories?gameId=432&classId=${CLASS_IDS[projectType] ?? 6}`,
    { headers: headers(), timeout: 15_000 }
  )
  return (raw.data ?? []).map((category) => ({ id: Number(category.id), name: String(category.name) }))
}

export async function getCurseForgeMod(modId: number): Promise<CurseForgeProject> {
  const raw = await fetchJson<{ data: Record<string, any> }>(`${API}/mods/${modId}`, {
    headers: headers(),
    timeout: 20_000
  })
  const hit = mapHit(raw.data)
  return {
    ...hit,
    nameTranslation: chineseNameFor('curseforge', hit.slug),
    description: String(raw.data.description ?? raw.data.summary ?? ''),
    screenshots: Array.isArray(raw.data.screenshots)
      ? raw.data.screenshots.map((s: any) => String(s.url ?? s.thumbnailUrl ?? '')).filter(Boolean)
      : [],
    latestFiles: Array.isArray(raw.data.latestFiles) ? raw.data.latestFiles.map(mapFile) : [],
    links: raw.data.links
  }
}

export async function getCurseForgeFiles(modId: number, mcVersion?: string, loader?: string): Promise<CurseForgeFile[]> {
  const params = new URLSearchParams({ pageSize: '50' })
  if (mcVersion) params.set('gameVersion', mcVersion)
  if (loader) {
    const loaderId = LOADER_IDS[loader]
    if (loaderId) params.set('modLoaderType', String(loaderId))
  }
  const raw = await fetchJson<{ data?: Record<string, any>[] }>(`${API}/mods/${modId}/files?${params}`, {
    headers: headers(),
    timeout: 20_000
  })
  return (raw.data ?? []).map(mapFile).sort((a, b) => b.id - a.id)
}

async function getCurseForgeFile(modId: number, fileId: number): Promise<CurseForgeFile> {
  const raw = await fetchJson<{ data: Record<string, any> }>(`${API}/mods/${modId}/files/${fileId}`, {
    headers: headers(),
    timeout: 20_000
  })
  return mapFile(raw.data)
}

async function resolveDownloadUrl(file: CurseForgeFile): Promise<string> {
  if (file.downloadUrl) return file.downloadUrl
  const raw = await fetchJson<{ data?: string | null }>(`${API}/mods/${file.modId}/files/${file.id}/download-url`, {
    headers: headers(),
    timeout: 20_000
  })
  if (!raw.data) throw new Error(`「${file.displayName}」的作者未开放第三方下载`)
  return raw.data
}

function sha1Of(file: CurseForgeFile): string | undefined {
  return file.hashes.find((hash) => hash.algo === 1)?.value
}

function contentDirectory(instance: Instance, classId: number): string {
  if (classId === 12) return path.join(instance.gameDir, 'resourcepacks')
  if (classId === 6552) return path.join(instance.gameDir, 'shaderpacks')
  return path.join(instance.gameDir, 'mods')
}

async function firstCompatibleFile(modId: number, mcVersion: string, loader: string): Promise<CurseForgeFile | null> {
  const files = await getCurseForgeFiles(modId, mcVersion, loader)
  return files.find((file) => file.downloadUrl || file.hashes.length) ?? null
}

export async function installCurseForgeFile(
  opts: CurseForgeInstallOptions
): Promise<{ installed: string[]; skipped: string[] }> {
  const instance = await getInstance(opts.instanceId)
  if (!instance) throw new Error('目标实例不存在')
  const mod = await getCurseForgeMod(opts.modId)
  const root = await getCurseForgeFile(opts.modId, opts.fileId)
  const group = new TaskGroupHandle(`安装 CurseForge · ${mod.name}`)
  const installed: string[] = []
  const skipped: string[] = []
  const visited = new Set<string>()

  const installOne = async (file: CurseForgeFile, classId: number, depth = 0): Promise<void> => {
    const key = `${file.modId}:${file.id}`
    if (visited.has(key) || depth > 8) return
    visited.add(key)
    if (classId === 6) {
      const meta = await getCurseForgeMod(file.modId).catch(() => null)
      if (hasInstalledMod(instance.id, { fileNames: [file.fileName], ids: [String(file.modId), meta?.slug ?? ''], names: [meta?.name ?? ''] })) {
        skipped.push(file.fileName)
        return
      }
    }
    const dir = contentDirectory(instance, classId)
    await ensureDir(dir)
    const fileName = safeFileName(file.fileName || `${file.modId}-${file.id}.jar`)
    const dest = path.join(dir, fileName)
    const step = group.step(`下载 ${file.displayName || fileName}`, 1)
    const totalSize = file.fileLength || 1
    step.start(totalSize, fileName)
    const url = await resolveDownloadUrl(file)
    await downloadFile({
      url,
      dest,
      sha1: sha1Of(file),
      size: file.fileLength || undefined,
      signal: group.signal,
      retries: 2,
      onProgress: (received, total) =>
        step.progress(received, `${formatBytes(received)} / ${formatBytes(total || totalSize)}`, total || totalSize)
    })
    step.done()
    installed.push(fileName)
    for (const dep of file.dependencies ?? []) {
      if (dep.relationType !== 3 || !dep.modId) continue
      const depFile = await firstCompatibleFile(dep.modId, instance.mcVersion, instance.loader)
      if (!depFile) {
        skipped.push(`依赖 ${dep.modId}`)
        continue
      }
      await installOne(depFile, classId, depth + 1)
    }
  }

  try {
    await installOne(root, mod.classId)
    group.finish('done')
    return { installed, skipped }
  } catch (e) {
    group.fail(e)
    throw e
  }
}

/** Download one selected CurseForge file into an arbitrary folder (no instance). */
export async function installCurseForgeFileToFolder(opts: { modId: number; fileId: number; destDir: string }): Promise<{ installed: string[] }> {
  const mod = await getCurseForgeMod(opts.modId)
  const file = await getCurseForgeFile(opts.modId, opts.fileId)
  const group = new TaskGroupHandle(`安装 ${mod.name}`)
  const step = group.step(`下载 ${mod.name}`, 1)
  try {
    await ensureDir(opts.destDir)
    const dest = path.join(opts.destDir, safeFileName(file.fileName))
    const totalSize = file.fileLength || 1
    step.start(totalSize, file.fileName)
    await downloadFile({
      url: await resolveDownloadUrl(file),
      dest,
      sha1: sha1Of(file),
      size: file.fileLength || undefined,
      signal: group.signal,
      retries: 2,
      onProgress: (received, total) =>
        step.progress(received, `${formatBytes(received)} / ${formatBytes(total || totalSize)}`, total || totalSize)
    })
    step.done(formatBytes(file.fileLength))
    group.finish('done')
    return { installed: [file.fileName] }
  } catch (e) {
    group.fail(e)
    throw e
  }
}

interface CurseForgeManifest {
  minecraft?: { version?: string; modLoaders?: { id: string; primary?: boolean }[] }
  name?: string
  version?: string
  author?: string
  files?: { projectID: number; fileID: number; required?: boolean }[]
  overrides?: string
}

export async function installCurseForgePack(opts: CurseForgePackInstallOptions): Promise<{ instanceId: string }> {
  const mod = await getCurseForgeMod(opts.modId)
  const files = await getCurseForgeFiles(opts.modId)
  const target = opts.fileId ? files.find((file) => file.id === opts.fileId) : files[0]
  if (!target) throw new Error('没有可用的整合包文件')
  const group = new TaskGroupHandle(`安装整合包 · ${mod.name}`)
  const before = (await listInstalledVersions()).map((version) => version.id)
  const zipFile = path.join(
    dirs().cache,
    'curseforge',
    safeFileName(target.fileName || `${opts.modId}-${target.id}.zip`)
  )
  const extractDir = path.join(dirs().cache, 'curseforge', `extract-${target.id}-${Date.now()}`)
  try {
    const dl = group.step('下载整合包', 1)
    const totalSize = target.fileLength || 1
    dl.start(totalSize, target.fileName)
    await downloadFile({
      url: await resolveDownloadUrl(target),
      dest: zipFile,
      sha1: sha1Of(target),
      size: target.fileLength || undefined,
      signal: group.signal,
      retries: 2,
      onProgress: (received, total) =>
        dl.progress(received, `${formatBytes(received)} / ${formatBytes(total || totalSize)}`, total || totalSize)
    })
    dl.done()
    const manifest = readJsonEntry<CurseForgeManifest>(zipFile, ['manifest.json'])
    if (!manifest?.minecraft?.version) throw new Error('整合包缺少 manifest.json')
    const mcVersion = manifest.minecraft.version
    const primary = manifest.minecraft.modLoaders?.find((loader) => loader.primary) ?? manifest.minecraft.modLoaders?.[0]
    const rawLoader = primary?.id ?? ''
    const [loaderName, ...loaderParts] = rawLoader.split('-')
    const loader = ['forge', 'neoforge', 'fabric', 'quilt'].includes(loaderName) ? loaderName : 'vanilla'
    let loaderVersion = loaderParts.join('-') || undefined
    if (loaderVersion?.startsWith(`${mcVersion}-`)) loaderVersion = loaderVersion.slice(mcVersion.length + 1)
    const base = group.step('安装游戏基础', 1)
    base.start(1, `${mcVersion} ${loader}`)
    let installedVersion: string
    if (loader === 'fabric' || loader === 'quilt') {
      installedVersion = (await installFabricLike(loader, mcVersion, loaderVersion, group)).versionId
    } else if (loader === 'forge' || loader === 'neoforge') {
      installedVersion = (await installForgeLike(loader, mcVersion, loaderVersion, group)).versionId
    } else {
      installedVersion = await installVanilla(mcVersion, group, false, false)
    }
    base.done()
    const inst = await createInstance({
      name: opts.instanceName?.trim() || manifest.name || mod.name,
      versionId: installedVersion,
      mcVersion,
      loader,
      loaderVersion,
      icon: '📦',
      color: 'forest'
    })
    await cleanupInstallVersions(before, inst.versionId)

    const fileList = (manifest.files ?? []).filter((file) => file.required !== false)
    const modsDir = path.join(inst.gameDir, 'mods')
    await ensureDir(modsDir)
    const dlStep = group.step('下载整合包模组', fileList.length)
    dlStep.start(fileList.length)
    let done = 0
    await runPool(
      fileList,
      8,
      async (entry) => {
        try {
          const file = await getCurseForgeFile(entry.projectID, entry.fileID)
          await downloadFile({
            url: await resolveDownloadUrl(file),
            dest: path.join(modsDir, safeFileName(file.fileName)),
            sha1: sha1Of(file),
            size: file.fileLength || undefined,
            signal: group.signal,
            retries: 2
          })
        } catch (e) {
          consoleLog('launcher', 'warn', `整合包文件 ${entry.projectID}/${entry.fileID} 下载失败: ${String(e)}`)
        }
        done++
        dlStep.progress(done, `${done}/${fileList.length}`)
      },
      group.signal
    )
    dlStep.done()

    await fsp.rm(extractDir, { recursive: true, force: true })
    await fsp.mkdir(extractDir, { recursive: true })
    extractZipSafe(zipFile, extractDir)
    for (const folder of [manifest.overrides || 'overrides', 'client-overrides']) {
      const src = path.join(extractDir, folder)
      if (existsSync(src)) await fsp.cp(src, inst.gameDir, { recursive: true, force: true })
    }
    group.finish('done')
    return { instanceId: inst.id }
  } catch (e) {
    group.fail(e)
    throw e
  } finally {
    await fsp.rm(extractDir, { recursive: true, force: true }).catch(() => {})
    await fsp.rm(zipFile, { force: true }).catch(() => {})
  }
}
