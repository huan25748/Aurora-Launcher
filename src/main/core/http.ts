import { net } from 'electron'
import fs from 'node:fs'
import fsp from 'node:fs/promises'
import path from 'node:path'
import crypto from 'node:crypto'
import { Readable } from 'node:stream'
import { pipeline } from 'node:stream/promises'
import type { DownloadSource, EndpointTestResult } from '../../shared/types'
import { getSettings } from './settings'
import { CancelledError } from './tasks'
import { sha1File, sha1FileSync } from './fsx'

const MIRROR_RULES: { re: RegExp; to: string }[] = [
  { re: /^https:\/\/piston-meta\.mojang\.com\//, to: 'https://bmclapi2.bangbang93.com/' },
  { re: /^https:\/\/piston-data\.mojang\.com\//, to: 'https://bmclapi2.bangbang93.com/' },
  { re: /^https:\/\/launchermeta\.mojang\.com\//, to: 'https://bmclapi2.bangbang93.com/' },
  { re: /^https:\/\/launcher\.mojang\.com\//, to: 'https://bmclapi2.bangbang93.com/' },
  { re: /^https:\/\/resources\.download\.minecraft\.net\//, to: 'https://bmclapi2.bangbang93.com/assets/' },
  { re: /^https:\/\/libraries\.minecraft\.net\//, to: 'https://bmclapi2.bangbang93.com/libraries/' },
  { re: /^https:\/\/meta\.fabricmc\.net\//, to: 'https://bmclapi2.bangbang93.com/fabric-meta/' },
  { re: /^https:\/\/meta\.quiltmc\.org\//, to: 'https://bmclapi2.bangbang93.com/quilt-meta/' },
  { re: /^https:\/\/maven\.fabricmc\.net\//, to: 'https://bmclapi2.bangbang93.com/maven/' },
  { re: /^https:\/\/maven\.quiltmc\.org\//, to: 'https://bmclapi2.bangbang93.com/maven/' },
  { re: /^https:\/\/maven\.minecraftforge\.net\//, to: 'https://bmclapi2.bangbang93.com/maven/' },
  { re: /^https:\/\/files\.minecraftforge\.net\/maven\//, to: 'https://bmclapi2.bangbang93.com/maven/' },
  { re: /^https:\/\/maven\.neoforged\.net\/releases\//, to: 'https://bmclapi2.bangbang93.com/maven/' },
  { re: /^https:\/\/maven\.neoforged\.net\/api\//, to: 'https://bmclapi2.bangbang93.com/neoforge/meta/api/' },
  { re: /^https:\/\/api\.modrinth\.com\//, to: 'https://mod.mcimirror.top/modrinth/' },
  { re: /^https:\/\/cdn\.modrinth\.com\//, to: 'https://mod.mcimirror.top/' }
]

export function mirrorUrl(url: string): string {
  // BMCLAPI mirrors the version manifest and asset index, but not every
  // per-version /v1/packages/*.json document (version JSON). Mirroring those
  // only produces 404s, so leave them on the official host.
  if (/^https:\/\/piston-meta\.mojang\.com\/v1\/packages\//i.test(url)) return url
  for (const rule of MIRROR_RULES) {
    if (rule.re.test(url)) return url.replace(rule.re, rule.to)
  }
  return url
}

export function sourceOf(): DownloadSource {
  try {
    return getSettings().downloadSource
  } catch {
    return 'auto'
  }
}

/** Probe the actual download hosts without applying mirror rewriting. */
export async function testDownloadEndpoints(): Promise<EndpointTestResult[]> {
  const endpoints: { id: string; label: string; url: string; range?: boolean }[] = [
    {
      id: 'official',
      label: 'Mojang 官方',
      url: 'https://piston-meta.mojang.com/mc/game/version_manifest_v2.json'
    },
    {
      id: 'bmclapi',
      label: 'BMCLAPI 游戏',
      url: 'https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json'
    },
    {
      id: 'forge-official',
      label: 'Forge 官方',
      url: 'https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml'
    },
    {
      id: 'neoforge-official',
      label: 'NeoForge 官方',
      url: 'https://maven.neoforged.net/api/maven/details/releases/net/neoforged/neoforge'
    },
    {
      id: 'fabric-official',
      label: 'Fabric 官方',
      url: 'https://meta.fabricmc.net/v2/versions/loader/1.20.1'
    },
    {
      id: 'forge',
      label: 'BMCLAPI Forge',
      url: 'https://bmclapi2.bangbang93.com/forge/minecraft'
    },
    {
      id: 'neoforge',
      label: 'BMCLAPI NeoForge',
      url: 'https://bmclapi2.bangbang93.com/neoforge/meta/api/maven/details/releases/net/neoforged/neoforge'
    },
    {
      id: 'fabric',
      label: 'BMCLAPI Fabric',
      url: 'https://bmclapi2.bangbang93.com/fabric-meta/v2/versions/loader/1.20.1'
    },
    {
      id: 'quilt',
      label: 'Quilt 官方',
      url: 'https://meta.quiltmc.org/v3/versions/loader/1.20.1'
    },
    {
      id: 'mcim',
      label: 'MCIM Modrinth API',
      url: 'https://mod.mcimirror.top/modrinth/v2/search?query=sodium&limit=1'
    },
    {
      id: 'modrinth-cdn',
      label: 'Modrinth CDN',
      url: 'https://cdn.modrinth.com/data/AANobbMI/versions/g6ju16VJ/sodium-fabric-0.9.3-beta.1%2Bmc26.3.jar',
      range: true
    }
  ]
  return Promise.all(
    endpoints.map(async (endpoint) => {
      const started = Date.now()
      try {
        const res = await net.fetch(endpoint.url, {
          headers: {
            'User-Agent': userAgent(),
            Accept: 'application/json',
            ...(endpoint.range ? { Range: 'bytes=0-0' } : {})
          },
          signal: AbortSignal.timeout(8_000)
        } as RequestInit)
        const ok = res.ok && (!endpoint.range || res.status === 206)
        await res.body?.cancel().catch(() => {})
        return {
          id: endpoint.id,
          label: endpoint.label,
          url: endpoint.url,
          ok,
          ms: Date.now() - started,
          status: res.status
        }
      } catch (e) {
        return {
          id: endpoint.id,
          label: endpoint.label,
          url: endpoint.url,
          ok: false,
          ms: Date.now() - started,
          error: e instanceof Error ? e.message : String(e)
        }
      }
    })
  )
}

/** Ordered list of URLs to try for a logical resource. */
export function candidates(url: string, source: DownloadSource = sourceOf()): string[] {
  const mirrored = mirrorUrl(url)
  const list: string[] = []
  if (source === 'official') list.push(url)
  else if (source === 'mirror') list.push(mirrored, url)
  else if (source === 'auto' && /^https:\/\/api\.modrinth\.com\//i.test(url)) {
    // Modrinth metadata is cached by MCIM; files still use the official CDN.
    list.push(mirrored, url)
  } else {
    // auto / prefer-official: official first, mirror as the fallback. This
    // matches PCL's default and avoids stalling on an unhealthy BMCLAPI path.
    list.push(url, ...(mirrored !== url ? [mirrored] : []))
  }
  return [...new Set(list)]
}

export interface FetchOptions {
  method?: string
  headers?: Record<string, string>
  body?: string | Buffer | URLSearchParams
  timeout?: number
  retries?: number
  source?: DownloadSource
  signal?: AbortSignal
  redirect?: 'follow' | 'manual' | 'error'
}

function combineSignals(signal: AbortSignal | undefined, timeout: number): AbortSignal {
  const timeoutSignal = AbortSignal.timeout(timeout)
  if (!signal) return timeoutSignal
  return AbortSignal.any([signal, timeoutSignal])
}

export async function httpFetchRaw(url: string, opts: FetchOptions = {}): Promise<Response> {
  const timeout = opts.timeout ?? 30_000
  const retries = opts.retries ?? 2
  const list = candidates(url, opts.source)
  let lastError: unknown = null
  for (let attempt = 0; attempt <= retries; attempt++) {
    // On retries, try the previously-skipped source first (usually official after a slow mirror).
    const ordered = attempt === 0 || list.length < 2 ? list : [...list].reverse()
    for (const candidate of ordered) {
      try {
        if (process.env.AURORA_DEBUG) console.log(`[http] attempt ${attempt} ${candidate}`)
        const res = await net.fetch(candidate, {
          method: opts.method ?? 'GET',
          headers: opts.headers,
          body: opts.body as never,
          signal: combineSignals(opts.signal, timeout),
          redirect: opts.redirect ?? 'follow'
        } as RequestInit)
        if (res.ok || res.status === 304) return res
        lastError = new Error(`HTTP ${res.status} ${res.statusText} - ${candidate}`)
        // don't retry on client errors except rate limiting
        if (res.status >= 400 && res.status < 500 && res.status !== 429) {
          if (candidate === list[list.length - 1]) throw lastError
        }
      } catch (e) {
        if (opts.signal?.aborted) throw new CancelledError()
        lastError = e
      }
    }
    if (attempt < retries) await sleep(400 * (attempt + 1))
  }
  throw lastError instanceof Error ? lastError : new Error(String(lastError))
}

export async function fetchJson<T>(url: string, opts: FetchOptions = {}): Promise<T> {
  const res = await httpFetchRaw(url, {
    ...opts,
    headers: { Accept: 'application/json', 'User-Agent': userAgent(), ...(opts.headers ?? {}) }
  })
  const text = await res.text()
  try {
    return JSON.parse(text) as T
  } catch {
    throw new Error(`JSON 解析失败 (${url}): ${text.slice(0, 120)}`)
  }
}

export async function fetchText(url: string, opts: FetchOptions = {}): Promise<string> {
  const res = await httpFetchRaw(url, {
    ...opts,
    headers: { 'User-Agent': userAgent(), ...(opts.headers ?? {}) }
  })
  return res.text()
}

export async function fetchBuffer(url: string, opts: FetchOptions = {}): Promise<Buffer> {
  const res = await httpFetchRaw(url, { ...opts, headers: { 'User-Agent': userAgent(), ...(opts.headers ?? {}) } })
  return Buffer.from(await res.arrayBuffer())
}

export interface DownloadOptions {
  url: string
  dest: string
  sha1?: string
  size?: number
  signal?: AbortSignal
  onProgress?: (received: number, total: number) => void
  source?: DownloadSource
  retries?: number
  skipIfExists?: boolean
  /** Number of parallel range connections for large files. Defaults to 4. */
  connections?: number
}

function userAgent(): string {
  return 'AuroraLauncher/1.0.0 (Electron)'
}

export function fileLooksValid(file: string, sha1?: string, size?: number): boolean {
  if (!fs.existsSync(file)) return false
  const st = fs.statSync(file)
  if (st.size === 0) return false
  if (size) return st.size === size
  if (sha1) {
    try {
      return sha1FileSync(file) === sha1
    } catch {
      return false
    }
  }
  return true
}

const MULTIPART_MIN_SIZE = 1_200_000
const DEFAULT_DOWNLOAD_THREADS = 128
// Opening hundreds of sockets to one CDN is counter-productive; the setting
// controls global concurrency, while a single file uses a small range pool.
const MAX_SEGMENTS_PER_FILE = 16
let officialRangeHealthy: boolean | undefined
let activeSegments = 0
const segmentWaiters: Array<() => void> = []

function downloadThreads(): number {
  try {
    return Math.max(1, Math.min(2048, Math.round(Number(getSettings().downloadThreads) || DEFAULT_DOWNLOAD_THREADS)))
  } catch {
    return DEFAULT_DOWNLOAD_THREADS
  }
}

async function acquireSegment(signal?: AbortSignal): Promise<void> {
  if (signal?.aborted) throw new CancelledError()
  if (activeSegments < downloadThreads()) {
    activeSegments++
    return
  }
  await new Promise<void>((resolve) => segmentWaiters.push(resolve))
  activeSegments++
  if (signal?.aborted) {
    releaseSegment()
    throw new CancelledError()
  }
}

function releaseSegment(): void {
  activeSegments = Math.max(0, activeSegments - 1)
  segmentWaiters.shift()?.()
}

function parseContentRange(value: string | null): { start: number; total: number } | null {
  const match = /^bytes\s+(\d+)-(\d+)\/(\d+)$/.exec(value ?? '')
  if (!match) return null
  return { start: Number(match[1]), total: Number(match[3]) }
}

async function probeRange(url: string, signal?: AbortSignal): Promise<number | null> {
  const res = await net.fetch(url, {
    headers: { Range: 'bytes=0-0', 'User-Agent': userAgent() },
    signal: combineSignals(signal, 20_000)
  } as RequestInit)
  const range = parseContentRange(res.headers.get('content-range'))
  try {
    await res.body?.cancel()
  } catch {}
  if (res.status !== 206 || !range) return null
  return range.total
}

async function probeRangeSpeed(url: string, signal?: AbortSignal): Promise<boolean> {
  const sample = 128 * 1024
  const started = Date.now()
  try {
    const res = await net.fetch(url, {
      headers: { Range: `bytes=0-${sample - 1}`, 'User-Agent': userAgent() },
      signal: combineSignals(signal, 2_500)
    } as RequestInit)
    if (res.status !== 206 || !res.body) {
      await res.body?.cancel().catch(() => {})
      return false
    }
    let received = 0
    for await (const chunk of Readable.fromWeb(res.body as never)) {
      received += (chunk as Buffer).length
    }
    const ok = received >= sample * 0.9 && received / Math.max(0.001, (Date.now() - started) / 1000) > 48 * 1024
    if (process.env.AURORA_DEBUG) console.log(`[http] range probe ${ok ? 'fast' : 'slow'} ${received}B in ${Date.now() - started}ms`)
    return ok
  } catch (e) {
    if (process.env.AURORA_DEBUG) console.log(`[http] range probe failed after ${Date.now() - started}ms: ${e instanceof Error ? e.message : String(e)}`)
    return false
  }
}

async function downloadRange(
  url: string,
  file: string,
  start: number,
  end: number,
  signal: AbortSignal | undefined,
  onChunk: (size: number) => void
): Promise<void> {
  const res = await net.fetch(url, {
    headers: { Range: `bytes=${start}-${end}`, 'User-Agent': userAgent() },
    signal: combineSignals(signal, 90_000)
  } as RequestInit)
  if (res.status !== 206) {
    await res.body?.cancel().catch(() => {})
    throw new Error(`HTTP ${res.status} 不支持分段下载`)
  }
  const range = parseContentRange(res.headers.get('content-range'))
  if (range?.start !== start) {
    await res.body?.cancel().catch(() => {})
    throw new Error('分段响应范围不匹配')
  }
  if (!res.body) throw new Error('空响应')
  const nodeStream = Readable.fromWeb(res.body as never)
  nodeStream.on('data', (chunk: Buffer) => onChunk(chunk.length))
  await pipeline(nodeStream, fs.createWriteStream(file))
}

async function downloadMultipart(
  url: string,
  part: string,
  opts: DownloadOptions,
  total: number,
  connections: number,
  deadline?: AbortSignal
): Promise<boolean> {
  const count = Math.max(1, Math.min(MAX_SEGMENTS_PER_FILE, connections, Math.ceil(total / 256_000)))
  if (count < 2) return false
  const chunkSize = Math.ceil(total / count)
  const segments = Array.from({ length: count }, (_, index) => {
    const start = index * chunkSize
    const end = index === count - 1 ? total - 1 : Math.min(total - 1, start + chunkSize - 1)
    return { start, end, file: `${part}.${index}` }
  })
  const written = new Array<number>(count).fill(0)
  const failController = new AbortController()
  let received = 0
  let lastEmit = 0
  const emit = (force = false): void => {
    const now = Date.now()
    if (force || now - lastEmit > 120) {
      lastEmit = now
      opts.onProgress?.(Math.min(total, received), total)
    }
  }
  try {
    await Promise.all(
      segments.map(async (segment, index) => {
        for (let attempt = 0; ; attempt++) {
          if (opts.signal?.aborted) throw new CancelledError()
          await acquireSegment(opts.signal)
          if (deadline?.aborted || failController.signal.aborted) {
            releaseSegment()
            throw new Error(deadline?.aborted ? '分段下载超时' : '其他分段下载失败')
          }
          try {
            const base = opts.signal ?? new AbortController().signal
            const rangeSignal = deadline
              ? AbortSignal.any([base, deadline, failController.signal])
              : AbortSignal.any([base, failController.signal])
            await downloadRange(url, segment.file, segment.start, segment.end, rangeSignal, (size) => {
              written[index] += size
              received += size
              emit()
            })
            const expected = segment.end - segment.start + 1
            if (written[index] !== expected) throw new Error('分段大小不匹配')
            return
          } catch (e) {
            if (opts.signal?.aborted) throw new CancelledError()
            if (deadline?.aborted) throw new Error('分段下载超时')
            if (failController.signal.aborted) throw new Error('其他分段下载失败')
            received -= written[index]
            written[index] = 0
            await fsp.rm(segment.file, { force: true }).catch(() => {})
            if (attempt >= 2) {
              failController.abort()
              throw e
            }
            await sleep(300 * (attempt + 1))
          } finally {
            releaseSegment()
          }
        }
      })
    )
    const merged = fs.createWriteStream(part)
    merged.setMaxListeners(0)
    for (const segment of segments) {
      await pipeline(fs.createReadStream(segment.file), merged, { end: false })
    }
    await new Promise<void>((resolve, reject) => {
      merged.once('error', reject)
      merged.end(resolve)
    })
    if (fs.statSync(part).size !== total) throw new Error('合并后的文件大小不匹配')
    opts.onProgress?.(total, total)
    return true
  } finally {
    failController.abort()
    await Promise.all(segments.map((segment) => fsp.rm(segment.file, { force: true }).catch(() => {})))
  }
}

async function finalizeDownload(part: string, dest: string, opts: DownloadOptions): Promise<void> {
  if (opts.sha1) {
    const got = await sha1File(part)
    if (got.toLowerCase() !== opts.sha1.toLowerCase()) {
      await fsp.rm(part, { force: true })
      throw new Error(`SHA1 校验失败: ${path.basename(dest)}`)
    }
  }
  await fsp.rm(dest, { force: true })
  await fsp.rename(part, dest)
}

const MULTIPART_HOST_BLOCKLIST = /^https:\/\/mod\.mcimirror\.top\//i

export async function downloadFile(opts: DownloadOptions): Promise<void> {
  const { dest, signal } = opts
  const retries = opts.retries ?? 2
  if (opts.skipIfExists !== false && fileLooksValid(dest, opts.sha1, opts.size)) {
    opts.onProgress?.(opts.size ?? fs.statSync(dest).size, opts.size ?? fs.statSync(dest).size)
    return
  }
  await fsp.mkdir(path.dirname(dest), { recursive: true })
  const list = candidates(opts.url, opts.source)

  // Large files: prefer parallel ranges from the un-throttled official host.
  // MCIM single-stream is fast but heavily throttles range requests, so it is
  // deliberately excluded from the range path and remains the fallback.
  if ((opts.size ?? 0) >= MULTIPART_MIN_SIZE || opts.size === undefined) {
    // Modrinth CDN is the special case where the untouched official host is
    // fastest for ranges; everything else should prefer the configured mirror.
    const preferred = /^https:\/\/cdn\.modrinth\.com\//i.test(opts.url) ? [opts.url, ...list] : [...list, opts.url]
    const multipartOrder = preferred.filter(
      (url, index, all) => !MULTIPART_HOST_BLOCKLIST.test(url) && all.indexOf(url) === index
    )
    for (const candidate of multipartOrder) {
      if (signal?.aborted) throw new CancelledError()
      const part = `${dest}.part`
      try {
        const total = opts.size && opts.size >= MULTIPART_MIN_SIZE ? opts.size : await probeRange(candidate, signal)
        if (!total || total < MULTIPART_MIN_SIZE) continue
        // Official Modrinth CDN can briefly throttle range requests. Probe once
        // per session, otherwise fall straight through to the single-stream mirror.
        if (/^https:\/\/cdn\.modrinth\.com\//i.test(candidate)) {
          if (officialRangeHealthy === undefined) {
            officialRangeHealthy = await probeRangeSpeed(candidate, signal)
          }
          if (!officialRangeHealthy) continue
        }
        const connections = Math.max(1, Math.min(MAX_SEGMENTS_PER_FILE, opts.connections ?? downloadThreads()))
        const deadline = new AbortController()
        const timer = setTimeout(
          () => deadline.abort(),
          Math.min(120_000, Math.max(8_000, (total / (1024 * 1024)) * 1_500))
        )
        let completed = false
        const t0 = Date.now()
        try {
          completed = await downloadMultipart(candidate, part, opts, total, connections, deadline.signal)
        } finally {
          clearTimeout(timer)
          if (process.env.AURORA_DEBUG) console.log(`[http] multipart ${completed ? 'ok' : 'failed'} in ${Date.now() - t0}ms ${candidate}`)
        }
        if (completed) {
          await finalizeDownload(part, dest, opts)
          return
        }
      } catch (e) {
        if (signal?.aborted) throw new CancelledError()
        if (/^https:\/\/cdn\.modrinth\.com\//i.test(candidate)) officialRangeHealthy = false
        if (process.env.AURORA_DEBUG) {
          console.log(`[http] multipart failed ${candidate}: ${e instanceof Error ? e.message : String(e)}`)
        }
      } finally {
        await fsp.rm(part, { force: true }).catch(() => {})
      }
    }
  }

  let lastError: unknown = null
  for (let attempt = 0; attempt <= retries; attempt++) {
    for (const candidate of list) {
      const part = `${dest}.part`
      try {
        await downloadOnce(candidate, part, opts)
        await finalizeDownload(part, dest, opts)
        return
      } catch (e) {
        if (signal?.aborted) {
          await fsp.rm(part, { force: true })
          throw new CancelledError()
        }
        lastError = e
        await fsp.rm(part, { force: true }).catch(() => {})
      }
    }
    if (attempt < retries) await sleep(500 * (attempt + 1))
  }
  throw new Error(`下载失败 ${path.basename(dest)}: ${lastError instanceof Error ? lastError.message : String(lastError)}`)
}

async function downloadOnce(url: string, part: string, opts: DownloadOptions): Promise<void> {
  const res = await net.fetch(url, {
    headers: { 'User-Agent': userAgent() },
    signal: combineSignals(opts.signal, 60_000)
  } as RequestInit)
  if (!res.ok) throw new Error(`HTTP ${res.status} ${url}`)
  const total = Number(res.headers.get('content-length') ?? opts.size ?? 0)
  let received = 0
  let lastEmit = 0
  const body = res.body
  if (!body) throw new Error('空响应')
  const nodeStream = Readable.fromWeb(body as never)
  const out = fs.createWriteStream(part)
  nodeStream.on('data', (chunk: Buffer) => {
    received += chunk.length
    const now = Date.now()
    if (now - lastEmit > 120) {
      lastEmit = now
      opts.onProgress?.(received, total || opts.size || received)
    }
  })
  await pipeline(nodeStream, out)
  opts.onProgress?.(received, total || received)
  if (opts.size && received !== opts.size) {
    // size mismatch is only fatal when no sha1 (sha1 validates stronger)
    if (!opts.sha1) throw new Error(`文件大小不匹配: 期望 ${opts.size}, 实际 ${received}`)
  }
}

export async function runPool<T>(
  items: T[],
  concurrency: number,
  worker: (item: T, index: number) => Promise<void>,
  signal?: AbortSignal
): Promise<void> {
  let next = 0
  let firstError: unknown = null
  const size = Math.max(1, Math.min(concurrency, items.length))
  const run = async (): Promise<void> => {
    while (true) {
      if (signal?.aborted) throw new CancelledError()
      if (firstError) return
      const index = next++
      if (index >= items.length) return
      try {
        await worker(items[index], index)
      } catch (e) {
        if (!firstError) firstError = e
        return
      }
    }
  }
  await Promise.all(Array.from({ length: size }, run))
  if (firstError) throw firstError
  if (signal?.aborted) throw new CancelledError()
}

export function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms))
}

export function formatBytes(n: number): string {
  if (!n || n < 0) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let i = 0
  let v = n
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024
    i++
  }
  return `${v.toFixed(v >= 100 || i === 0 ? 0 : 1)} ${units[i]}`
}

export function randomToken(): string {
  return crypto.randomBytes(16).toString('hex')
}
