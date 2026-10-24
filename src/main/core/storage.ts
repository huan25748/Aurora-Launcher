import fs from 'node:fs'
import fsp from 'node:fs/promises'
import path from 'node:path'
import { dirs } from './paths'

export interface StorageStats {
  root: string
  total: number
  assets: number
  libraries: number
  versions: number
  instances: number
  cache: number
  packs: number
  natives: number
  runtime: number
  mods: number
  installerArtifacts: number
}

async function dirSize(dir: string): Promise<number> {
  let total = 0
  let entries: fs.Dirent[]
  try {
    entries = await fsp.readdir(dir, { withFileTypes: true })
  } catch {
    return 0
  }
  for (const entry of entries) {
    const full = path.join(dir, entry.name)
    if (entry.isDirectory()) total += await dirSize(full)
    else {
      try {
        total += (await fsp.stat(full)).size
      } catch {}
    }
  }
  return total
}

function installerArtifacts(): string[] {
  try {
    return fs
      .readdirSync(dirs().root)
      .filter((name) => /^(forge|neoforge|fabric|quilt).*installer.*\.(jar|log)$/i.test(name) || /.*-installer\.jar\.log$/i.test(name))
      .map((name) => path.join(dirs().root, name))
  } catch {
    return []
  }
}

export async function storageStats(): Promise<StorageStats> {
  const root = dirs().root
  const assets = await dirSize(dirs().assets)
  const libraries = await dirSize(dirs().libraries)
  const versions = await dirSize(dirs().versions)
  const instances = await dirSize(dirs().instances)
  const cache = await dirSize(dirs().cache)
  const packs = await dirSize(path.join(root, 'launcher', 'packs'))
  const natives = await dirSize(dirs().natives)
  const runtime = await dirSize(dirs().runtime)
  const mods = await dirSize(path.join(root, 'mods'))
  const installer = installerArtifacts()
  let installerSize = 0
  for (const file of installer) {
    try {
      installerSize += (await fsp.stat(file)).size
    } catch {}
  }
  return {
    root,
    total: assets + libraries + versions + instances + cache + packs + natives + runtime + mods + installerSize,
    assets,
    libraries,
    versions,
    instances,
    cache,
    packs,
    natives,
    runtime,
    mods,
    installerArtifacts: installerSize
  }
}

export async function cleanStorage(): Promise<{ freed: number }> {
  const root = dirs().root
  const targets = [
    dirs().cache,
    path.join(root, 'launcher', 'packs'),
    path.join(root, 'launcher', 'logs'),
    dirs().natives,
    ...installerArtifacts()
  ]
  let freed = 0
  for (const target of targets) {
    try {
      const stat = await fsp.stat(target)
      freed += stat.isDirectory() ? await dirSize(target) : stat.size
      await fsp.rm(target, { recursive: true, force: true })
    } catch {}
  }
  return { freed }
}

function coordToPath(name: string): string | undefined {
  const [coord, ext = 'jar'] = name.split('@')
  const parts = coord.split(':')
  if (parts.length < 3) return undefined
  const group = parts[0].replace(/\./g, '/')
  const artifact = parts[1]
  const version = parts[2]
  const classifier = parts[3]
  const file = `${artifact}-${version}${classifier ? `-${classifier}` : ''}.${ext}`
  return `${group}/${artifact}/${version}/${file}`
}

async function collectReferences(): Promise<{ libraries: Set<string>; assets: Set<string> | null }> {
  const libs = new Set<string>()
  const assetIds = new Set<string>()
  const versionsRoot = dirs().versions
  let entries: fs.Dirent[]
  try {
    entries = await fsp.readdir(versionsRoot, { withFileTypes: true })
  } catch {
    return { libraries: libs, assets: null }
  }
  let jsonCount = 0
  for (const entry of entries) {
    if (!entry.isDirectory()) continue
    const dir = path.join(versionsRoot, entry.name)
    let files: string[]
    try {
      files = fs.readdirSync(dir).filter((name) => name.endsWith('.json') && name !== 'instance.json')
    } catch {
      continue
    }
    for (const name of files) {
      try {
        const json = JSON.parse(await fsp.readFile(path.join(dir, name), 'utf8')) as Record<string, any>
        if (!Array.isArray(json.libraries) && !json.assetIndex) continue
        jsonCount++
        for (const library of json.libraries ?? []) {
          if (typeof library.name === 'string') {
            const p = coordToPath(library.name)
            if (p) libs.add(p.replace(/\\/g, '/'))
          }
          const downloads = library.downloads ?? {}
          if (downloads.artifact?.path) libs.add(String(downloads.artifact.path).replace(/\\/g, '/'))
          for (const value of Object.values(downloads.classifiers ?? {})) {
            const p = (value as any)?.path
            if (p) libs.add(String(p).replace(/\\/g, '/'))
          }
        }
        if (json.assetIndex?.id) assetIds.add(String(json.assetIndex.id))
      } catch {
        // Unreadable version JSON: keep conservative and don't prune assets based on it.
      }
    }
  }
  if (!jsonCount || !libs.size) return { libraries: libs, assets: null }
  if (!assetIds.size) return { libraries: libs, assets: null }
  const assets = new Set<string>()
  for (const id of assetIds) {
    try {
      const index = JSON.parse(await fsp.readFile(path.join(dirs().assets, 'indexes', `${id}.json`), 'utf8')) as {
        objects?: Record<string, { hash: string }>
      }
      for (const value of Object.values(index.objects ?? {})) if (value?.hash) assets.add(value.hash)
    } catch {
      return { libraries: libs, assets: null }
    }
  }
  return { libraries: libs, assets }
}

async function removeUnreferenced(root: string, keep: (rel: string) => boolean): Promise<number> {
  let freed = 0
  async function walk(dir: string): Promise<void> {
    let entries: fs.Dirent[]
    try {
      entries = await fsp.readdir(dir, { withFileTypes: true })
    } catch {
      return
    }
    for (const entry of entries) {
      const full = path.join(dir, entry.name)
      if (entry.isDirectory()) await walk(full)
      else if (!keep(path.relative(root, full).replace(/\\/g, '/'))) {
        try {
          const size = (await fsp.stat(full)).size
          await fsp.rm(full, { force: true })
          freed += size
        } catch {}
      }
    }
  }
  await walk(root)
  return freed
}

export async function pruneUnused(): Promise<{ freed: number; libraries: number; assets: number }> {
  const refs = await collectReferences()
  const libs = refs.libraries.size ? await removeUnreferenced(dirs().libraries, (rel) => refs.libraries.has(rel)) : 0
  let assets = 0
  if (refs.assets) {
    assets = await removeUnreferenced(path.join(dirs().assets, 'objects'), (rel) => {
      const hash = rel.split('/').pop() ?? ''
      return refs.assets!.has(hash)
    })
  }
  return { freed: libs + assets, libraries: libs, assets }
}
