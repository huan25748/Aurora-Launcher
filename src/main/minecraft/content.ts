import fs from 'node:fs'
import fsp from 'node:fs/promises'
import path from 'node:path'
import { dirs } from '../core/paths'
import { ensureDir, listFiles, safeFileName, sha1Text, timestampName } from '../core/fsx'
import { readJsonEntry, readEntryText, readEntryBuffer, zipDirectory } from '../core/zip'
import { parseNbtFile, plain, tagString, tagNumber, type NbtTag } from '../core/nbt'
import { readInstance } from './instances'
import type { ContentItem, ContentKind, ModMeta, ScreenshotItem, WorldItem } from '../../shared/types'

const metaCache = new Map<string, { key: string; meta: ModMeta }>()

export function gameDirOf(instanceId: string): string {
  const inst = readInstance(instanceId)
  if (!inst) throw new Error('实例不存在')
  return inst.gameDir
}

export function contentDir(instanceId: string, kind: ContentKind): string {
  const base = gameDirOf(instanceId)
  const map: Record<ContentKind, string> = {
    mods: 'mods',
    resourcepacks: 'resourcepacks',
    shaderpacks: 'shaderpacks',
    saves: 'saves',
    screenshots: 'screenshots',
    datapacks: 'datapacks'
  }
  return path.join(base, map[kind] ?? kind)
}

function flattenText(value: unknown): string {
  if (!value) return ''
  if (typeof value === 'string') return value
  if (Array.isArray(value)) return value.map(flattenText).join('')
  if (typeof value === 'object') {
    const obj = value as Record<string, unknown>
    return `${obj.text ?? ''}${obj.extra ? flattenText(obj.extra) : ''}`
  }
  return String(value)
}

function parseTomlMods(text: string): ModMeta {
  const meta: ModMeta = {}
  const get = (key: string): string | undefined => {
    const m = text.match(new RegExp(`^\\s*${key}\\s*=\\s*"([^"]*)"`, 'm'))
    return m?.[1]
  }
  meta.id = get('modId')
  meta.name = get('displayName') ?? meta.id
  meta.version = get('version')
  meta.description = get('description')
  const authors = get('authors')
  if (authors) meta.authors = authors.split(',').map((s) => s.trim())
  const logo = get('logoFile') ?? get('logoFileSmall')
  if (logo) meta.icon = logo
  meta.loader = 'forge'
  return meta
}

export function readModMeta(file: string): ModMeta {
  const stat = fs.statSync(file)
  const key = `${stat.size}:${stat.mtimeMs}`
  const cached = metaCache.get(file)
  if (cached && cached.key === key) return cached.meta
  const meta: ModMeta = { fileName: path.basename(file) }
  try {
    const fabric = readJsonEntry<Record<string, unknown>>(file, ['fabric.mod.json'])
    if (fabric) {
      meta.id = String(fabric.id ?? '')
      meta.name = String(fabric.name ?? meta.id)
      meta.version = String(fabric.version ?? '')
      meta.description = flattenText(fabric.description)
      const authors = fabric.authors
      if (Array.isArray(authors)) {
        meta.authors = authors.map((a) => (typeof a === 'string' ? a : String((a as { name?: string }).name ?? '')))
      }
      const depends = fabric.depends as Record<string, string> | undefined
      if (depends) {
        meta.dependencies = Object.entries(depends)
          .filter(([k]) => !['minecraft', 'fabricloader', 'fabric-api', 'java'].includes(k))
          .map(([k, v]) => ({ id: k, version: String(v) }))
        const mc = depends.minecraft
        if (mc) meta.mcVersions = [String(mc)]
      }
      meta.loader = 'fabric'
      const icon = fabric.icon
      if (typeof icon === 'string') meta.icon = icon
    }
    if (!meta.id) {
      const quilt = readJsonEntry<Record<string, unknown>>(file, ['quilt.mod.json'])
      if (quilt) {
        const loader = (quilt.quilt_loader ?? {}) as Record<string, unknown>
        meta.id = String(loader.id ?? '')
        meta.name = meta.id
        meta.version = String(loader.version ?? '')
        const md = (loader.metadata ?? {}) as Record<string, unknown>
        if (md.name) meta.name = String(md.name)
        if (md.description) meta.description = flattenText(md.description)
        if (typeof md.icon === 'string') meta.icon = md.icon
        meta.loader = 'quilt'
      }
    }
    if (!meta.id) {
      const toml = readEntryText(file, ['META-INF/neoforge.mods.toml', 'META-INF/mods.toml'])
      if (toml) {
        const parsed = parseTomlMods(toml)
        Object.assign(meta, parsed)
        meta.loader = toml.includes('neoforge') ? 'neoforge' : 'forge'
      }
    }
    if (!meta.id) {
      const legacy = readJsonEntry<Record<string, unknown>[]>(file, ['mcmod.info'])
      if (Array.isArray(legacy) && legacy.length) {
        const first = legacy[0]
        meta.id = String(first.modid ?? '')
        meta.name = String(first.name ?? meta.id)
        meta.version = String(first.version ?? '')
        meta.description = flattenText(first.description)
        meta.loader = 'forge'
      }
    }
  } catch {
    // unreadable jar: fall back to filename only
  }
  if (!meta.name) {
    meta.name = path.basename(file).replace(/\.jar(\.disabled)?$/i, '')
  }
  metaCache.set(file, { key, meta })
  return meta
}

/** Check mods/ for an already installed project before downloading a dependency. */
export function hasInstalledMod(instanceId: string, opts: { fileNames?: string[]; ids?: string[]; names?: string[] }): boolean {
  try {
    const dir = contentDir(instanceId, 'mods')
    if (!fs.existsSync(dir)) return false
    const exact = new Set((opts.fileNames ?? []).map((name) => name.toLowerCase()))
    const ids = new Set((opts.ids ?? []).map((id) => id.toLowerCase()).filter(Boolean))
    const names = new Set((opts.names ?? []).map((name) => name.toLowerCase()).filter(Boolean))
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
      if (!entry.isFile()) continue
      const fileName = entry.name
      if (!/.jar(.disabled)?$/i.test(fileName)) continue
      if (exact.has(fileName.toLowerCase())) return true
      const full = path.join(dir, fileName)
      try {
        if (fs.statSync(full).size <= 0) continue
        const meta = readModMeta(full)
        if (meta.id && ids.has(meta.id.toLowerCase())) return true
        if (meta.name && names.has(meta.name.toLowerCase())) return true
      } catch {}
    }
  } catch {}
  return false
}

function modIconPath(file: string, meta: ModMeta): string | undefined {
  const candidates = new Set<string>()
  if (meta.icon && !/^(https?:)?\/\//.test(meta.icon)) candidates.add(meta.icon)
  if (meta.id) {
    if (meta.icon) candidates.add(`assets/${meta.id}/${meta.icon}`)
    for (const name of ['icon.png', 'logo.png', 'icon.jpg', 'logo.jpg']) {
      candidates.add(`assets/${meta.id}/${name}`)
      candidates.add(name)
    }
  }
  if (!candidates.size) return undefined
  try {
    for (const candidate of candidates) {
      const buf = readEntryBuffer(file, [candidate])
      if (!buf) continue
      const cacheDir = path.join(dirs().cache, 'icons')
      fs.mkdirSync(cacheDir, { recursive: true })
      const dest = path.join(cacheDir, `${sha1Text(`${file}:${candidate}`)}${path.extname(candidate) || '.png'}`)
      if (!fs.existsSync(dest)) fs.writeFileSync(dest, buf)
      return dest
    }
    return undefined
  } catch {
    return undefined
  }
}

export async function listContent(instanceId: string, kind: ContentKind): Promise<ContentItem[]> {
  const dir = contentDir(instanceId, kind)
  await ensureDir(dir)
  const entries = await listFiles(dir)
  const items: ContentItem[] = []
  for (const entry of entries) {
    if (entry.isDirectory()) continue
    const name = entry.name
    if (name.endsWith('.disabled') || /\.jar\.disabled$/.test(name) || /\.zip\.disabled$/.test(name)) {
      // handled below via extension parsing
    }
    const full = path.join(dir, name)
    let enabled = true
    let fileName = name
    if (/\.disabled$/i.test(name)) {
      enabled = false
      fileName = name.replace(/\.disabled$/i, '')
    }
    if (kind === 'mods' && !/\.jar$/i.test(fileName)) continue
    if (kind === 'resourcepacks' && !/\.zip$/i.test(fileName)) continue
    if (kind === 'shaderpacks' && !/\.zip$/i.test(fileName)) continue
    if (kind === 'datapacks' && !/\.zip$/i.test(fileName)) continue
    let stat: fs.Stats
    try {
      stat = fs.statSync(full)
    } catch {
      continue
    }
    let meta: ModMeta | undefined
    let icon: string | undefined
    let iconData: string | undefined
    if (kind === 'mods') {
      try {
        meta = readModMeta(full)
        icon = modIconPath(full, meta)
      if (icon) {
        try {
          const ext = path.extname(icon).toLowerCase()
          const mime = ext === '.jpg' || ext === '.jpeg' ? 'image/jpeg' : ext === '.webp' ? 'image/webp' : 'image/png'
          iconData = `data:${mime};base64,${fs.readFileSync(icon).toString('base64')}`
        } catch {}
      }
      } catch {}
    }
    if (kind === 'resourcepacks') {
      meta = readPackMeta(full)
      if (meta?.icon) icon = meta.icon
    }
    items.push({
      name: meta?.name || fileName,
      fileName,
      path: full,
      enabled,
      size: stat.size,
      mtime: stat.mtimeMs,
      kind,
      meta,
      icon,
      iconData
    })
  }
  return items.sort((a, b) => a.name.localeCompare(b.name))
}

function readPackMeta(file: string): ModMeta | undefined {
  const mcmeta = readJsonEntry<{ pack?: { description?: unknown; pack_format?: number } }>(file, ['pack.mcmeta'])
  const meta: ModMeta = { name: path.basename(file, '.zip') }
  if (mcmeta?.pack) {
    meta.description = flattenText(mcmeta.pack.description)
    meta.version = String(mcmeta.pack.pack_format ?? '')
  }
  const png = readEntryBuffer(file, ['pack.png'])
  if (png) {
    const cacheDir = path.join(dirs().cache, 'icons')
    fs.mkdirSync(cacheDir, { recursive: true })
    const dest = path.join(cacheDir, `${sha1Text(file)}.png`)
    try {
      if (!fs.existsSync(dest)) fs.writeFileSync(dest, png)
      meta.icon = dest
    } catch {}
  }
  return meta
}

export async function toggleContent(instanceId: string, kind: ContentKind, fileName: string): Promise<void> {
  const dir = contentDir(instanceId, kind)
  const enabledPath = path.join(dir, fileName)
  const disabledPath = `${enabledPath}.disabled`
  if (fs.existsSync(enabledPath)) {
    await fsp.rename(enabledPath, disabledPath)
  } else if (fs.existsSync(disabledPath)) {
    await fsp.rename(disabledPath, enabledPath)
  } else {
    throw new Error('文件不存在')
  }
}

export async function removeContent(instanceId: string, kind: ContentKind, fileName: string): Promise<void> {
  const dir = contentDir(instanceId, kind)
  const candidates = [path.join(dir, fileName), path.join(dir, `${fileName}.disabled`)]
  for (const file of candidates) {
    if (fs.existsSync(file)) {
      await fsp.rm(file, { recursive: true, force: true })
      return
    }
  }
  throw new Error('文件不存在')
}

export async function importContentFiles(instanceId: string, kind: ContentKind, files: string[]): Promise<number> {
  const dir = contentDir(instanceId, kind)
  await ensureDir(dir)
  let count = 0
  for (const file of files) {
    try {
      const stat = await fsp.stat(file)
      if (!stat.isFile()) continue
      const base = safeFileName(path.basename(file))
      let target = path.join(dir, base)
      let i = 1
      while (fs.existsSync(target)) {
        const ext = path.extname(base)
        target = path.join(dir, `${path.basename(base, ext)} (${i++})${ext}`)
      }
      await fsp.copyFile(file, target)
      count++
    } catch {}
  }
  return count
}

// ---------------------------------------------------------------------------
// Worlds
// ---------------------------------------------------------------------------

function worldFromFolder(dir: string, folderName: string): WorldItem | null {
  const levelDat = path.join(dir, 'level.dat')
  if (!fs.existsSync(levelDat)) return null
  let name = folderName
  let lastPlayed: number | undefined
  let gameVersion: string | undefined
  let icon: string | undefined
  let hardcore = false
  let cheats = false
  try {
    const tag = parseNbtFile(levelDat)
    if (tag && tag.type === 'compound') {
      const data = tag.value.Data
      if (data && data.type === 'compound') {
        const d = data.value
        name = tagString(d.LevelName, folderName)
        const lp = tagNumber(d.LastPlayed, 0)
        if (lp) lastPlayed = lp
        if (d.Version && d.Version.type === 'compound') {
          gameVersion = tagString(d.Version.value.Name, '')
        }
        const iconTag = d.icon
        if (iconTag && iconTag.type === 'string' && iconTag.value.length > 100) {
          icon = `data:image/png;base64,${iconTag.value.replace(/^data:image\/png;base64,/, '')}`
        }
        hardcore = tagNumber(d.Hardcore, 0) === 1
        cheats = tagNumber(d.allowCommands, 0) === 1
      }
    }
  } catch {}
  let size = 0
  try {
    const walk = (p: string): void => {
      for (const e of fs.readdirSync(p, { withFileTypes: true })) {
        const full = path.join(p, e.name)
        if (e.isDirectory()) walk(full)
        else size += fs.statSync(full).size
      }
    }
    walk(dir)
  } catch {}
  return {
    name,
    folderName,
    path: dir,
    size,
    mtime: fs.statSync(dir).mtimeMs,
    lastPlayed,
    gameVersion,
    icon,
    hardcore,
    cheats
  }
}

export async function listWorlds(instanceId: string): Promise<WorldItem[]> {
  const dir = contentDir(instanceId, 'saves')
  await ensureDir(dir)
  const entries = await listFiles(dir)
  const worlds: WorldItem[] = []
  for (const entry of entries) {
    if (!entry.isDirectory()) continue
    try {
      const world = worldFromFolder(path.join(dir, entry.name), entry.name)
      if (world) worlds.push(world)
    } catch {}
  }
  return worlds.sort((a, b) => (b.lastPlayed ?? b.mtime) - (a.lastPlayed ?? a.mtime))
}

export async function backupWorld(instanceId: string, folderName: string): Promise<{ path: string }> {
  const source = path.join(contentDir(instanceId, 'saves'), folderName)
  if (!fs.existsSync(source)) throw new Error('存档不存在')
  const backupDir = path.join(gameDirOf(instanceId), 'backups')
  await ensureDir(backupDir)
  const file = path.join(backupDir, `${safeFileName(folderName)}-${timestampName('backup')}.zip`)
  zipDirectory(source, file)
  return { path: file }
}

export async function deleteWorld(instanceId: string, folderName: string): Promise<void> {
  const target = path.join(contentDir(instanceId, 'saves'), folderName)
  if (!fs.existsSync(target)) throw new Error('存档不存在')
  await fsp.rm(target, { recursive: true, force: true })
}

// ---------------------------------------------------------------------------
// Screenshots
// ---------------------------------------------------------------------------

export async function listScreenshots(instanceId: string): Promise<ScreenshotItem[]> {
  const dir = contentDir(instanceId, 'screenshots')
  await ensureDir(dir)
  const items: ScreenshotItem[] = []
  for (const entry of await listFiles(dir)) {
    if (!entry.isFile()) continue
    if (!/\.(png|jpe?g|webp)$/i.test(entry.name)) continue
    const full = path.join(dir, entry.name)
    try {
      const stat = fs.statSync(full)
      items.push({ fileName: entry.name, path: full, size: stat.size, mtime: stat.mtimeMs })
    } catch {}
  }
  return items.sort((a, b) => b.mtime - a.mtime)
}

export function flattenTextValue(value: unknown): string {
  return flattenText(value)
}
