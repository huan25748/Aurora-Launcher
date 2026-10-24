import fs from 'node:fs'
import fsp from 'node:fs/promises'
import path from 'node:path'
import { spawn } from 'node:child_process'
import { dirs, versionDir, versionJsonPath, versionJarPath, assetsIndexPath, objectPath } from '../core/paths'
import { fetchJson, fetchText, downloadFile, runPool, formatBytes } from '../core/http'
import { existsSync, readJsonSync, writeJsonSync, ensureDir, dirSize, timestampName } from '../core/fsx'
import { consoleLog } from '../core/bus'
import { readJsonEntry } from '../core/zip'
import { TaskGroupHandle, TaskStepHandle, CancelledError } from '../core/tasks'
import { getSettings, downloadSourceFor } from '../core/settings'
import {
  loadVersionChain,
  loadVersionJson,
  readLocalVersionJson,
  libraryArtifact,
  rulesAllow,
  resolveLibraries,
  classpathLibraries,
  getVersionManifest,
  writeAuroraMeta,
  readAuroraMeta,
  mavenToPath,
  logConfigName,
  type AssetIndex,
  type DownloadInfo,
  type Library,
  type VersionChain,
  type VersionJson
} from './version'
import { pickJava, scanJava } from './java'
import type { InstallLoaderRequest, LoaderKind, LoaderVersion } from '../../shared/types'

const WINDOWS_FEATURES = { has_custom_resolution: false, is_quick_play_singleplayer: false, is_quick_play_multiplayer: false, is_quick_play_realms: false }

// ---------------------------------------------------------------------------
// Vanilla installation
// ---------------------------------------------------------------------------

export interface ProgressLike {
  signal?: AbortSignal
}

export async function installVanilla(
  versionId: string,
  group: TaskGroupHandle,
  createInstance?: boolean,
  includeAssets = true
): Promise<string> {
  const manifest = await getVersionManifest()
  const summary = manifest.versions.find((v) => v.id === versionId)
  if (!summary) throw new Error(`未找到版本 ${versionId}`)
  const infoStep = group.step('获取版本信息', 1)
  infoStep.start(1, versionId)
  const versionJson = await fetchJson<VersionJson>(summary.url, { timeout: 30_000, signal: group.signal })
  await ensureDir(versionDir(versionId))
  writeJsonSync(versionJsonPath(versionId), versionJson)
  infoStep.done()

  const chain = await loadVersionChain(versionId)
  await downloadVersionFiles(chain, group, includeAssets)
  writeAuroraMeta(versionId, {
    loader: 'vanilla',
    mcVersion: versionId,
    installedAt: Date.now(),
    profileId: versionId
  })
  return versionId
}

/**
 * Makes sure the vanilla version can be used as a loader base.
 * Assets are NOT required to install a loader (they are fetched at launch), so a Forge
 * install no longer triggers a 600 MB asset download first.
 */
export async function ensureVanillaBase(versionId: string, group: TaskGroupHandle): Promise<void> {
  const jsonFile = versionJsonPath(versionId)
  const jarFile = versionJarPath(versionId)
  if (existsSync(jsonFile) && existsSync(jarFile)) {
    const chain = await loadVersionChain(versionId)
    const resolved = resolveLibraries(chain.libraries, WINDOWS_FEATURES)
    const missingLibs = resolved.filter((r) => r.artifact?.path && !existsSync(path.join(dirs().libraries, r.artifact.path)))
    if (!missingLibs.length) return
  }
  await installVanilla(versionId, group, false, false)
}

export async function downloadVersionFiles(chain: VersionChain, group: TaskGroupHandle, includeAssets = true): Promise<void> {
  const settings = getSettings()
  const vanillaSource = downloadSourceFor('vanilla')
  const libRoot = dirs().libraries

  // The client jar, libraries and logging config are independent; downloading
  // them together removes the serial client -> libraries stall.
  const clientTask = (async (): Promise<void> => {
    const jarStep = group.step('下载客户端', 1)
    if (chain.clientDownload?.url) {
      const jarId = chain.inheritedJarId
      const dest = versionJarPath(jarId)
      if (!existsSync(dest) || fs.statSync(dest).size !== (chain.clientDownload.size ?? -1)) {
        const jarSize = chain.clientDownload.size ?? 1
        jarStep.start(jarSize, path.basename(dest))
        await downloadFile({
          url: chain.clientDownload.url,
          dest,
          sha1: chain.clientDownload.sha1,
          size: chain.clientDownload.size,
          source: vanillaSource,
          signal: group.signal,
          onProgress: (received, total) =>
            jarStep.progress(
              Math.min(jarSize, received),
              `${formatBytes(received)} / ${formatBytes(total || jarSize)}`,
              total || jarSize
            )
        })
      }
      jarStep.done()
    } else {
      jarStep.start(1, '无独立客户端 jar')
      jarStep.done('跳过（继承版本）')
    }
  })()

  const libraryTask = (async (): Promise<void> => {
    const resolved = resolveLibraries(chain.libraries, WINDOWS_FEATURES)
    const libsToDownload = resolved.filter((r) => r.artifact?.path && r.artifact?.url)
    const libStep = group.step('下载依赖库', libsToDownload.length)
    libStep.start(libsToDownload.length)
    let libDone = 0
    const concurrency = Math.max(4, Math.min(32, settings.maxConcurrency))
    await runPool(
      libsToDownload,
      concurrency,
      async (entry) => {
        const artifact = entry.artifact!
        const dest = path.join(libRoot, artifact.path!)
        await downloadFile({
          url: artifact.url,
          dest,
          sha1: artifact.sha1,
          size: artifact.size,
          source: vanillaSource,
          signal: group.signal,
          retries: 2
        })
        libDone++
        libStep.progress(libDone, path.basename(artifact.path!))
      },
      group.signal
    )
    libStep.done(`${libsToDownload.length} 个依赖库`)
  })()

  const loggingTask = (async (): Promise<void> => {
    const logging = chain.logging?.client
    if (logging?.file?.url) {
      const logId = logConfigName(logging.file)
      const dest = path.join(dirs().assets, 'log_configs', logId)
      if (!existsSync(dest)) {
        const step = group.step('下载日志配置', 1)
        step.start(1, logId)
        await downloadFile({ url: logging.file.url, dest, sha1: logging.file.sha1, size: logging.file.size, source: vanillaSource, signal: group.signal })
        step.done()
      }
    }
  })()

  await Promise.all([clientTask, libraryTask, loggingTask])

  // assets (skipped when installing a loader: they are not needed until launch)
  if (includeAssets && settings.downloadAssets && chain.assetIndex?.url) {
    await downloadAssets(chain, group)
    // Legacy indexes need to be materialised on disk for the game to find them.
    const index = readAssetIndex(chain.assetIndexId)
    if (index && (index.virtual || index.map_to_resources)) {
      await materializeLegacyAssets(chain, index, group)
    }
  }
}

function readAssetIndex(assetId: string): AssetIndex | null {
  if (!assetId) return null
  return readJsonSync<AssetIndex | null>(assetsIndexPath(assetId), null)
}

async function downloadAssets(chain: VersionChain, group: TaskGroupHandle): Promise<void> {
  const assetSource = downloadSourceFor('vanilla')
  if (!chain.assetIndex?.url) return
  const indexFile = assetsIndexPath(chain.assetIndexId)
  const indexStep = group.step('获取资源索引', 1)
  indexStep.start(1, chain.assetIndexId)
  if (!existsSync(indexFile)) {
    await downloadFile({
      url: chain.assetIndex.url,
      dest: indexFile,
      sha1: chain.assetIndex.sha1,
      size: chain.assetIndex.size,
      source: assetSource,
      signal: group.signal
    })
  }
  const index = readJsonSync<AssetIndex | null>(indexFile, null)
  if (!index) throw new Error('资源索引解析失败')
  indexStep.done()
  const objects = Object.entries(index.objects ?? {})
  const step = group.step('下载游戏资源', objects.length)
  step.start(objects.length)
  let done = 0
  let bytes = 0
  const settings = getSettings()
  await runPool(
    objects,
    Math.max(8, Math.min(48, settings.maxConcurrency * 2)),
    async ([, obj]) => {
      const dest = objectPath(obj.hash)
      if (!existsSync(dest) || fs.statSync(dest).size !== obj.size) {
        await downloadFile({
          url: `https://resources.download.minecraft.net/${obj.hash.slice(0, 2)}/${obj.hash}`,
          dest,
          size: obj.size,
          source: assetSource,
          signal: group.signal,
          retries: 2
        })
        bytes += obj.size
      }
      done++
      if (done % 16 === 0 || done === objects.length) {
        step.progress(done, `${done}/${objects.length} · ${formatBytes(bytes)}`)
      }
    },
    group.signal
  )
  step.done(`${objects.length} 个资源文件`)
}

export async function materializeLegacyAssets(chain: VersionChain, index: AssetIndex, group: TaskGroupHandle): Promise<void> {
  const assetId = chain.assetIndexId
  const targetRoot = index.map_to_resources
    ? path.join(dirs().root, 'resources')
    : path.join(dirs().assets, 'virtual', assetId)
  const step = group.step('转换旧版资源', Object.keys(index.objects).length)
  step.start(Object.keys(index.objects).length)
  let done = 0
  for (const [name, obj] of Object.entries(index.objects)) {
    const source = objectPath(obj.hash)
    const target = path.join(targetRoot, name)
    if (!existsSync(target)) {
      await ensureDir(path.dirname(target))
      try {
        await fsp.copyFile(source, target)
      } catch {}
    }
    done++
    if (done % 32 === 0) step.progress(done, name)
  }
  step.done()
}

// ---------------------------------------------------------------------------
// Fabric / Quilt
// ---------------------------------------------------------------------------

export async function listLoaderVersions(kind: 'fabric' | 'quilt' | 'forge' | 'neoforge', mcVersion: string): Promise<LoaderVersion[]> {
  if (kind === 'fabric' || kind === 'quilt') {
    const source = downloadSourceFor(kind)
    const official = kind === 'fabric'
      ? `https://meta.fabricmc.net/v2/versions/loader/${encodeURIComponent(mcVersion)}`
      : `https://meta.quiltmc.org/v3/versions/loader/${encodeURIComponent(mcVersion)}`
    const mirror = kind === 'fabric'
      ? `https://bmclapi2.bangbang93.com/fabric-meta/v2/versions/loader/${encodeURIComponent(mcVersion)}`
      : `https://bmclapi2.bangbang93.com/quilt-meta/v3/versions/loader/${encodeURIComponent(mcVersion)}`
    const load = async (url: string) =>
      fetchJson<{ loader: { version: string; stable?: boolean } }[]>(url, { timeout: 20_000 })
    let list: { loader: { version: string; stable?: boolean } }[]
    if (source === 'mirror') list = await load(mirror)
    else {
      try {
        list = await load(official)
      } catch (error) {
        if (source === 'official') throw error
        list = await load(mirror)
      }
    }
    return list.map((e) => ({ version: e.loader.version, stable: e.loader.stable !== false, mcVersion }))
  }
  const source = downloadSourceFor(kind)
  const wantOfficial = source !== 'mirror'
  const wantMirror = source !== 'official'

  if (kind === 'forge') {
    let versions: string[] = []
    if (wantOfficial) {
      try {
        const xml = await fetchText('https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml', {
          timeout: 15_000,
          retries: 0,
          source: 'official'
        })
        const prefix = `${mcVersion}-`
        versions = [...xml.matchAll(/<version>([^<]+)<\/version>/g)]
          .map((m) => m[1])
          .filter((v) => v.startsWith(prefix))
          .map((v) => v.slice(prefix.length))
      } catch {}
    }
    if (!versions.length && wantMirror) {
      const list = await fetchJson<{ version: string; mcversion?: string }[]>(
        `https://bmclapi2.bangbang93.com/forge/minecraft/${encodeURIComponent(mcVersion)}`,
        { timeout: 20_000 }
      )
      versions = list.map((e) => e.version).filter(Boolean)
    }
    if (!versions.length) throw new Error(`没有找到适用于 ${mcVersion} 的 Forge 版本`)
    return [...new Set(versions)]
      .sort((a, b) => compareVersions(b, a))
      .map((v) => ({ version: v, stable: true, mcVersion }))
  }

  // neoforge
  const legacy = mcVersion === '1.20.1'
  const packageName = legacy ? 'forge' : 'neoforge'
  let all: string[] = []
  if (wantOfficial) {
    try {
      const detail = await fetchJson<{ files: { name: string; type: string }[] }>(
        `https://maven.neoforged.net/api/maven/details/releases/net/neoforged/${packageName}`,
        { timeout: 15_000, retries: 0, source: 'official' }
      )
      all = detail.files.filter((f) => f.type === 'DIRECTORY').map((f) => f.name)
    } catch {}
  }
  if (!all.length && wantMirror) {
    const detail = await fetchJson<{ files: { name: string; type: string }[] }>(
      `https://bmclapi2.bangbang93.com/neoforge/meta/api/maven/details/releases/net/neoforged/${packageName}`,
      { timeout: 20_000 }
    )
    all = detail.files.filter((f) => f.type === 'DIRECTORY').map((f) => f.name)
  }
  if (!all.length) throw new Error(`没有找到适用于 ${mcVersion} 的 NeoForge 版本`)
  let filtered = all
  if (legacy) {
    filtered = all.filter((v) => v.startsWith(`${mcVersion}-`) && /-47\./.test(v))
  } else {
    const m = mcVersion.match(/^1\.(\d+)(?:\.(\d+))?/)
    if (m) {
      const prefix = `${m[1]}.${m[2] ?? '0'}.`
      filtered = all.filter((v) => v.startsWith(prefix))
      if (!filtered.length) filtered = all.filter((v) => v.startsWith(`${m[1]}.`))
    }
  }
  return filtered
    .sort((a, b) => compareVersions(b, a))
    .map((v) => ({ version: v, stable: !/-(beta|alpha|rc)/i.test(v), mcVersion }))
}

export function compareVersions(a: string, b: string): number {
  const pa = a.split(/[.\-+]/).map((x) => (/^\d+$/.test(x) ? parseInt(x, 10) : x))
  const pb = b.split(/[.\-+]/).map((x) => (/^\d+$/.test(x) ? parseInt(x, 10) : x))
  for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
    const x = pa[i]
    const y = pb[i]
    if (x === undefined) return -1
    if (y === undefined) return 1
    if (typeof x === 'number' && typeof y === 'number') {
      if (x !== y) return x - y
    } else if (String(x) !== String(y)) {
      return String(x) > String(y) ? 1 : -1
    }
  }
  return 0
}

export async function installFabricLike(
  kind: 'fabric' | 'quilt',
  mcVersion: string,
  loaderVersion: string | undefined,
  group: TaskGroupHandle
): Promise<{ versionId: string; loaderVersion: string }> {
  await ensureVanillaBase(mcVersion, group)
  let picked: LoaderVersion | null = null
  if (loaderVersion) {
    const expectedId = `${kind}-loader-${loaderVersion}-${mcVersion}`
    if (existsSync(versionJsonPath(expectedId))) {
      const step = group.step('检查已安装版本', 1)
      step.start(1, expectedId)
      step.done('已安装，跳过重复安装')
      writeAuroraMeta(expectedId, {
        loader: kind,
        loaderVersion,
        mcVersion,
        installedAt: readAuroraMeta(expectedId)?.installedAt ?? Date.now(),
        profileId: expectedId
      })
      return { versionId: expectedId, loaderVersion }
    }
  }
  const listStep = group.step(`获取 ${kind === 'fabric' ? 'Fabric' : 'Quilt'} 版本列表`, 1)
  listStep.start(1, mcVersion)
  const versions = await listLoaderVersions(kind, mcVersion)
  listStep.done()
  if (!versions.length) throw new Error(`没有找到适用于 ${mcVersion} 的 ${kind} 版本`)
  if (loaderVersion) {
    picked = versions.find((v) => v.version === loaderVersion) ?? { version: loaderVersion, stable: true, mcVersion }
  } else {
    picked = versions.find((v) => v.stable) ?? versions[0]
  }
  const expectedId = `${kind}-loader-${picked.version}-${mcVersion}`
  if (existsSync(versionJsonPath(expectedId))) {
    const step = group.step('检查已安装版本', 1)
    step.start(1, expectedId)
    step.done('已安装，跳过重复安装')
    writeAuroraMeta(expectedId, {
      loader: kind,
      loaderVersion: picked.version,
      mcVersion,
      installedAt: readAuroraMeta(expectedId)?.installedAt ?? Date.now(),
      profileId: expectedId
    })
    return { versionId: expectedId, loaderVersion: picked.version }
  }
  const loaderSource = downloadSourceFor(kind)
  const base = kind === 'fabric' ? 'https://meta.fabricmc.net/v2/versions/loader' : 'https://meta.quiltmc.org/v3/versions/loader'
  const profileStep = group.step('获取加载器配置', 1)
  profileStep.start(1, picked.version)
  const profile = await fetchJson<VersionJson>(
    `${base}/${encodeURIComponent(mcVersion)}/${encodeURIComponent(picked.version)}/profile/json`,
    { timeout: 30_000, signal: group.signal, source: loaderSource === 'mirror' ? 'mirror' : loaderSource === 'official' ? 'official' : 'auto' }
  )
  profileStep.done()
  const profileId = profile.id
  await ensureDir(versionDir(profileId))
  writeJsonSync(versionJsonPath(profileId), profile)
  // download profile libraries
  const chain = await loadVersionChain(profileId)
  const resolved = resolveLibraries(chain.libraries, WINDOWS_FEATURES)
  const libs = resolved.filter((r) => r.artifact?.path && r.artifact?.url)
  const libStep = group.step('下载加载器依赖', libs.length)
  libStep.start(libs.length)
  let done = 0
  await runPool(
    libs,
    Math.max(4, Math.min(24, getSettings().maxConcurrency)),
    async (entry) => {
      const artifact = entry.artifact!
      await downloadFile({
        url: artifact.url,
        dest: path.join(dirs().libraries, artifact.path!),
        sha1: artifact.sha1,
        size: artifact.size,
        source: loaderSource === 'mirror' ? 'mirror' : loaderSource === 'official' ? 'official' : 'auto',
        signal: group.signal
      })
      done++
      libStep.progress(done, path.basename(artifact.path!))
    },
    group.signal
  )
  libStep.done()
  writeAuroraMeta(profileId, {
    loader: kind,
    loaderVersion: picked.version,
    mcVersion,
    installedAt: Date.now(),
    profileId
  })
  return { versionId: profileId, loaderVersion: picked.version }
}

// ---------------------------------------------------------------------------
// Forge / NeoForge (via official installer jar)
// ---------------------------------------------------------------------------

interface ForgeInstallProfile {
  libraries?: Library[]
  processors?: { jar?: string; classpath?: string[] }[]
  data?: Record<string, { client?: string; server?: string }>
}

/**
 * PCL-style warm-up: the official Forge installer downloads libraries one by one, which
 * dominates a cold install. Parse the installer's own metadata and fetch every library
 * (plus the Mojang mappings it needs) in parallel first; the installer then only validates
 * local files and runs its processors.
 *
 * Important: do NOT deduplicate by group:artifact here. The installer's processors need
 * several versions of the same artifact (asm 9.2 + 9.6, srgutils 0.4.3/0.4.9/0.5.6);
 * deduplicating leaves them missing and the installer downloads them sequentially.
 */
async function prefetchForgeInstaller(installerFile: string, mcVersion: string, group: TaskGroupHandle, sourceMode: 'auto' | 'mirror' | 'official' | 'prefer-official'): Promise<void> {
  const profile = readJsonEntry<ForgeInstallProfile>(installerFile, ['install_profile.json'])
  const version = readJsonEntry<VersionJson>(installerFile, ['version.json'])
  if (!profile && !version) return
  const libraries = [...(profile?.libraries ?? []), ...(version?.libraries ?? [])]
  const artifacts = new Map<string, DownloadInfo>()
  for (const lib of libraries) {
    if (!rulesAllow(lib.rules, WINDOWS_FEATURES)) continue
    const artifact = libraryArtifact(lib)
    if (artifact?.path && artifact.url) artifacts.set(artifact.path, artifact)
  }
  // Processor classpaths occasionally reference coordinates absent from the JSON lists.
  for (const processor of profile?.processors ?? []) {
    for (const coord of [processor.jar, ...(processor.classpath ?? [])]) {
      if (!coord) continue
      const relative = mavenToPath(coord)
      if (!relative || artifacts.has(relative)) continue
      artifacts.set(relative, { path: relative, url: `https://maven.minecraftforge.net/${relative}` })
    }
  }

  const downloads = [...artifacts.values()]
  const step = group.step('预下载 Forge 依赖', downloads.length + 1)
  step.start(downloads.length + 1, `${downloads.length} 个依赖`)
  let done = 0
  let failed = 0
  try {
    const concurrency = Math.max(8, Math.min(32, getSettings().maxConcurrency))
    await runPool(
      downloads,
      concurrency,
      async (artifact) => {
        try {
          await downloadFile({
            url: artifact.url!,
            dest: path.join(dirs().libraries, artifact.path!),
            sha1: artifact.sha1,
            size: artifact.size,
            source: sourceMode,
            signal: group.signal,
            retries: 2
          })
          done++
        } catch (e) {
          if (e instanceof CancelledError) throw e
          failed++
          consoleLog('launcher', 'warn', `预下载失败（安装器会重试）：${artifact.path} ${e instanceof Error ? e.message : ''}`)
        }
        step.progress(done + failed, path.basename(artifact.path!))
      },
      group.signal
    )

    const mappingsCoord = profile?.data?.MOJMAPS?.client
    if (mappingsCoord) {
      try {
        const relative = mavenToPath(mappingsCoord.replace(/^\[|\]$/g, ''))
        const chain = await loadVersionChain(mcVersion)
        const mappings = chain.json.downloads?.client_mappings
        if (relative && mappings?.url) {
          const dest = path.join(dirs().libraries, relative)
          const needsDownload = !existsSync(dest) || (mappings.size != null && fs.statSync(dest).size !== mappings.size)
          if (needsDownload) {
            await downloadFile({
              url: mappings.url,
              dest,
              sha1: mappings.sha1,
              size: mappings.size,
              source: sourceMode,
              signal: group.signal,
              retries: 2
            })
          }
        }
      } catch (e) {
        if (e instanceof CancelledError) throw e
        consoleLog('launcher', 'warn', `预下载 Mappings 失败，安装器将自行处理：${e instanceof Error ? e.message : String(e)}`)
      }
    }
    step.progress(downloads.length + 1, '完成')
    step.done(failed ? `${downloads.length - failed}/${downloads.length} 个依赖，其余交给安装器` : `${downloads.length} 个依赖`)
  } catch (e) {
    if (e instanceof CancelledError) throw e
    consoleLog('launcher', 'warn', `预下载 Forge 依赖失败，安装器将继续自行下载：${e instanceof Error ? e.message : String(e)}`)
    step.done('部分失败，交给安装器')
  }
}

/**
 * Returns an already installed loader profile for this exact MC + loader version.
 * Re-running the official Forge installer is expensive (it downloads libraries and runs
 * dozens of processor steps), so reuse a complete existing profile whenever possible.
 */
async function findInstalledLoader(kind: LoaderKind, mcVersion: string, loaderVersion: string): Promise<string | null> {
  const versionsDir = dirs().versions
  let ids: string[] = []
  try {
    ids = fs
      .readdirSync(versionsDir, { withFileTypes: true })
      .filter((entry) => entry.isDirectory())
      .map((entry) => entry.name)
  } catch {
    return null
  }

  for (const id of ids) {
    const meta = readAuroraMeta(id)
    if (meta?.loader === kind && meta.loaderVersion === loaderVersion && meta.mcVersion === mcVersion) return id
  }

  const groupNeedle =
    kind === 'forge' ? 'net.minecraftforge' : kind === 'neoforge' ? 'net.neoforged' : kind === 'fabric' ? 'net.fabricmc' : 'org.quiltmc'
  for (const id of ids) {
    if (!id.includes(loaderVersion)) continue
    const json = readLocalVersionJson(id)
    if (!json) continue
    if (json.inheritsFrom && json.inheritsFrom !== mcVersion) continue
    const librariesJson = JSON.stringify(json.libraries ?? [])
    if (!librariesJson.includes(groupNeedle)) continue
    if (kind === 'forge' && librariesJson.includes('net.neoforged')) continue
    if (kind === 'neoforge' && !librariesJson.includes('net.neoforged')) continue
    const resolved = resolveLibraries((json.libraries ?? []) as Library[], WINDOWS_FEATURES)
    const artifacts = resolved.filter((entry) => entry.artifact?.path)
    if (!artifacts.length) continue
    const missing = artifacts.filter((entry) => !existsSync(path.join(dirs().libraries, entry.artifact!.path!)))
    if (missing.length / artifacts.length < 0.2) return id
  }
  return null
}

export async function installForgeLike(
  kind: 'forge' | 'neoforge',
  mcVersion: string,
  loaderVersion: string | undefined,
  group: TaskGroupHandle
): Promise<{ versionId: string; loaderVersion: string }> {
  const reuseInstalled = async (version: string): Promise<{ versionId: string; loaderVersion: string } | null> => {
    const existing = await findInstalledLoader(kind, mcVersion, version)
    if (!existing) return null
    const reuseStep = group.step('检查已安装版本', 1)
    reuseStep.start(1, existing)
    reuseStep.done('已安装，跳过重复安装')
    consoleLog('launcher', 'info', `${kind === 'forge' ? 'Forge' : 'NeoForge'} ${version} 已安装，直接复用 ${existing}`)
    writeAuroraMeta(existing, {
      loader: kind,
      loaderVersion: version,
      mcVersion,
      installedAt: readAuroraMeta(existing)?.installedAt ?? Date.now(),
      profileId: existing
    })
    return { versionId: existing, loaderVersion: version }
  }

  // With an explicit version we can skip the network entirely when it is installed.
  if (loaderVersion) {
    const reused = await reuseInstalled(loaderVersion)
    if (reused) return reused
  }

  const listStep = group.step(`获取 ${kind === 'forge' ? 'Forge' : 'NeoForge'} 版本列表`, 1)
  listStep.start(1, mcVersion)
  const versions = await listLoaderVersions(kind, mcVersion)
  listStep.done()
  if (!versions.length) throw new Error(`没有找到适用于 ${mcVersion} 的 ${kind} 版本`)
  const picked = loaderVersion
    ? versions.find((v) => v.version === loaderVersion) ?? { version: loaderVersion, stable: true, mcVersion }
    : versions[0]

  const reusedAfterList = await reuseInstalled(picked.version)
  if (reusedAfterList) return reusedAfterList

  const legacyNeoForge = kind === 'neoforge' && mcVersion === '1.20.1'
  const packageName = kind === 'forge' ? 'forge' : legacyNeoForge ? 'forge' : 'neoforge'
  const artifact = kind === 'forge' ? `forge-${mcVersion}-${picked.version}-installer.jar` : `${packageName}-${picked.version}-installer.jar`
  const rel =
    kind === 'forge'
      ? `net/minecraftforge/forge/${mcVersion}-${picked.version}/${artifact}`
      : `net/neoforged/${packageName}/${picked.version}/${artifact}`
  const installerUrl =
    kind === 'forge'
      ? `https://maven.minecraftforge.net/${rel}`
      : `https://maven.neoforged.net/releases/${rel}`
  const installerFile = path.join(dirs().cache, 'installers', artifact)
  // The vanilla base (client + libraries) and the loader installer do not
  // depend on each other; fetch them together.
  const basePromise = ensureVanillaBase(mcVersion, group)
  const dlStep = group.step('下载安装器', 1)
  dlStep.start(1, artifact)
  const installerPromise = downloadFile({
    url: installerUrl,
    dest: installerFile,
    source: downloadSourceFor(kind),
    signal: group.signal,
    retries: 2,
    onProgress: (received, total) =>
      dlStep.progress(received, `${formatBytes(received)} / ${formatBytes(total || received || 1)}`, total || 1)
  })
  await Promise.all([basePromise, installerPromise])
  dlStep.done()

  // Fetch the installer's libraries in parallel (mirror-aware) before running it.
  const prefetchStart = Date.now()
  await prefetchForgeInstaller(installerFile, mcVersion, group, downloadSourceFor(kind))
  consoleLog('launcher', 'info', `Forge 依赖预下载用时 ${((Date.now() - prefetchStart) / 1000).toFixed(1)}s`)

  // pick a suitable java to run the installer
  const java = await pickJava(mcVersionJavaMajor(mcVersion))
  if (!java) throw new Error('未找到可用的 Java，请先在设置中下载或选择 Java')

  // Forge/NeoForge installers refuse to run without a launcher_profiles.json.
  const profilesFile = path.join(dirs().root, 'launcher_profiles.json')
  if (!existsSync(profilesFile)) {
    writeJsonSync(profilesFile, { profiles: {}, settings: {}, version: 3 })
  }

  const before = new Set(fs.readdirSync(dirs().versions))
  const runStep = group.step('运行安装器', 1)
  runStep.start(1, `${packageName} ${picked.version}`)
  // The official installer downloads its own libraries and occasionally drops a TLS
  // connection. Files it already validated are reused, so retrying is cheap and usually
  // finishes the job. Give it three chances before surfacing the error.
  let output = ''
  let lastError: unknown = null
  for (let attempt = 1; attempt <= 3; attempt++) {
    const sourceMode = downloadSourceFor(kind)
    // In auto mode use the mirror first: the official Forge/NeoForge maven is
    // frequently slow or unreachable from mainland China. Official mode keeps
    // official first and only retries through the mirror.
    const useMirror = sourceMode === 'mirror' || (sourceMode === 'auto' ? attempt !== 2 : attempt > 1)
    const attemptStarted = Date.now()
    let lastLogged = 0
    const heartbeat = setInterval(() => {
      const elapsed = Math.round((Date.now() - attemptStarted) / 1000)
      runStep.progress(0, `${useMirror ? '镜像' : '官方源'} · 第 ${attempt} 次尝试 · 已等待 ${elapsed}s`)
      if (elapsed - lastLogged >= 30) {
        lastLogged = elapsed
        consoleLog('launcher', 'info', `安装器第 ${attempt} 次尝试已等待 ${elapsed}s，暂时没有输出；超过 90s 无输出会自动切换下载源。`)
      }
    }, 5_000)
    try {
      runStep.progress(0, `${useMirror ? '镜像' : '官方源'} · 第 ${attempt} 次尝试`)
      output = await runInstaller(java.path, installerFile, group, useMirror ? 'https://bmclapi2.bangbang93.com/maven/' : undefined, (line) => {
        if (/Downloading|Extracting|Injecting|Task:|Building Processors/i.test(line)) runStep.progress(0, line.trim().slice(0, 150))
      })
      lastError = null
      break
    } catch (e) {
      if (e instanceof CancelledError) throw e
      lastError = e
      consoleLog('launcher', 'warn', `安装器第 ${attempt} 次运行失败：${e instanceof Error ? e.message : String(e)}`)
      if (attempt < 3) await new Promise((r) => setTimeout(r, 2000))
    } finally {
      clearInterval(heartbeat)
    }
  }

  if (lastError) throw lastError
  runStep.done('安装器完成')
  const after = fs.readdirSync(dirs().versions).filter((id) => !before.has(id) && !/installed$/.test(id))
  let created = after.find((id) => {
    const json = readJsonSync<VersionJson | null>(path.join(dirs().versions, id, `${id}.json`), null)
    return !!json && (json.inheritsFrom === mcVersion || id.includes(picked.version))
  })
  if (!created) {
    // fall back to known naming conventions
    const guesses = [
      `${mcVersion}-forge-${picked.version}`,
      `${mcVersion}-neoforge-${picked.version}`,
      `neoforge-${picked.version}`,
      `${mcVersion}-forge-${picked.version.split('-')[0]}`
    ]
    created = guesses.find((g) => existsSync(path.join(dirs().versions, g, `${g}.json`)))
  }
  if (!created) {
    throw new Error(`安装器未能生成版本配置。\n${output.slice(-1500)}`)
  }
  writeAuroraMeta(created, {
    loader: kind,
    loaderVersion: picked.version,
    mcVersion,
    installedAt: Date.now(),
    profileId: created
  })
  await fsp.rm(installerFile, { force: true }).catch(() => {})
  await fsp.rm(path.join(dirs().root, `${path.basename(installerFile)}.log`), { force: true }).catch(() => {})
  return { versionId: created, loaderVersion: picked.version }
}

function mcVersionJavaMajor(mcVersion: string): number {
  const m = mcVersion.match(/^1\.(\d+)(?:\.(\d+))?/)
  // New-style version ids (e.g. "26.3") after the 2025 versioning change.
  if (!m) return 25
  const minor = parseInt(m[1], 10)
  const patch = parseInt(m[2] ?? '0', 10)
  if (minor >= 21) return 21
  if (minor === 20 && patch >= 5) return 21
  if (minor >= 18) return 17
  if (minor === 17) return 16
  return 8
}

function runInstaller(
  javaPath: string,
  jar: string,
  group: TaskGroupHandle,
  mirror?: string,
  onLine?: (line: string) => void
): Promise<string> {
  return new Promise((resolve, reject) => {
    const args = ['-Djava.awt.headless=true', '-jar', jar, '--installClient', dirs().root]
    if (mirror) args.push('--mirror', mirror)
    consoleLog('launcher', 'info', `运行安装器: ${path.basename(javaPath)} ${args.join(' ')}`)
    // Redirect stdout/stderr to a file instead of pipes: this works on every platform
    // (including restricted environments where anonymous pipes are unavailable) and we
    // poll the file to stream progress into the UI.
    const logDir = path.join(dirs().cache, 'installers')
    fs.mkdirSync(logDir, { recursive: true })
    const logFile = path.join(logDir, `install-${Date.now()}.log`)
    const fd = fs.openSync(logFile, 'w')
    const child = spawn(javaPath, args, {
      cwd: dirs().root,
      windowsHide: true,
      stdio: ['ignore', fd, fd]
    })
    fs.closeSync(fd)
    let emitted = 0
    let settled = false
    const startedAt = Date.now()
    let lastOutputAt = startedAt
    const flush = (): void => {
      try {
        const size = fs.statSync(logFile).size
        if (size <= emitted) return
        const handle = fs.openSync(logFile, 'r')
        const buffer = Buffer.alloc(size - emitted)
        try {
          fs.readSync(handle, buffer, 0, buffer.length, emitted)
        } finally {
          fs.closeSync(handle)
        }
        emitted = size
        lastOutputAt = Date.now()
        for (const line of buffer.toString('utf8').split(/\r?\n/)) {
          const text = line.trim()
          if (!text) continue
          // The Forge installer emits tens of thousands of class-level lines
          // ("Splitting: ...", "Patching ...", renamed class names). Forwarding all of
          // them to the renderer starves the event loop and noticeably slows the install;
          // keep only stage lines and errors.
          const keep = /^(Extracting json|Downloading libraries|Building Processors|Task:|Downloading library from|Injecting profile|Successfully installed)/i.test(text) ||
            /(Exception|Error|Failed|WARN)/i.test(text)
          if (keep) {
            consoleLog('download', 'info', line)
            onLine?.(line)
          }
        }
      } catch {}
    }
    const finish = (error: Error | null, output = ''): void => {
      if (settled) return
      settled = true
      clearInterval(timer)
      clearInterval(watch)
      group.signal?.removeEventListener('abort', cancel)
      if (error) reject(error)
      else resolve(output)
    }
    const timer = setInterval(flush, 400)
    const watch = setInterval(() => {
      if (settled) return
      const now = Date.now()
      const idle = now - lastOutputAt
      if (idle > 90_000) {
        try {
          child.kill()
        } catch {}
        finish(new Error(`安装器已经 ${Math.round(idle / 1000)} 秒没有输出，已停止。将自动切换镜像/下载源重试。`))
      } else if (now - startedAt > 8 * 60_000) {
        try {
          child.kill()
        } catch {}
        finish(new Error('安装器运行超过 8 分钟，已停止。请检查网络或切换下载源后重试。'))
      }
    }, 5_000)
    const cancel = (): void => {
      try {
        child.kill()
      } catch {}
      finish(new CancelledError())
    }
    group.signal?.addEventListener('abort', cancel, { once: true })
    child.on('error', (err) => finish(err))
    child.on('close', (code) => {
      if (settled) return
      flush()
      let output = ''
      try {
        output = fs.readFileSync(logFile, 'utf8')
      } catch {}
      if (code === 0) {
        try {
          fs.rmSync(logFile, { force: true })
        } catch {}
        finish(null, output)
      } else {
        finish(new Error(`安装器退出码 ${code}\n${output.slice(-3000)}`))
      }
    })
  })
}

// ---------------------------------------------------------------------------
// Verified launch helper: ensure a version is fully installed before launching
// ---------------------------------------------------------------------------

export async function ensureInstalled(versionId: string, group: TaskGroupHandle): Promise<void> {
  const chain = await loadVersionChain(versionId)
  const settings = getSettings()
  const libRoot = dirs().libraries
  const resolved = resolveLibraries(chain.libraries, WINDOWS_FEATURES)
  const missingLibs = resolved.filter((r) => r.artifact?.path && r.artifact?.url && !existsSync(path.join(libRoot, r.artifact.path)))
  if (missingLibs.length) await downloadVersionFiles(chain, group)
  else if (chain.clientDownload?.url && !existsSync(versionJarPath(chain.inheritedJarId))) await downloadVersionFiles(chain, group)
  else if (settings.downloadAssets && chain.assetIndex?.url && !existsSync(assetsIndexPath(chain.assetIndexId))) {
    await downloadAssets(chain, group)
  }
}
