import fs from 'node:fs'
import path from 'node:path'
import { initSettings, updateSettings } from '../../src/main/core/settings'
import { ensureDirs, dirs, assetsIndexPath } from '../../src/main/core/paths'
import { TaskGroupHandle } from '../../src/main/core/tasks'
import { installVanilla } from '../../src/main/minecraft/installer'
import { loadVersionChain } from '../../src/main/minecraft/version'

function countObjects(): { files: number; bytes: number } {
  const root = path.join(dirs().assets, 'objects')
  let files = 0
  let bytes = 0
  const walk = (dir: string): void => {
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
      const full = path.join(dir, entry.name)
      if (entry.isDirectory()) walk(full)
      else {
        files++
        bytes += fs.statSync(full).size
      }
    }
  }
  if (fs.existsSync(root)) walk(root)
  return { files, bytes }
}

async function main(): Promise<void> {
  initSettings()
  ensureDirs()
  await updateSettings({ downloadAssets: true, maxConcurrency: 32 })

  console.log('\n=== modern assets (1.20.1) ===')
  const t0 = Date.now()
  await installVanilla('1.20.1', new TaskGroupHandle('assets 1.20.1'))
  const chain = await loadVersionChain('1.20.1')
  const indexFile = assetsIndexPath(chain.assetIndexId)
  console.log('index file exists:', fs.existsSync(indexFile), indexFile)
  const index = JSON.parse(fs.readFileSync(indexFile, 'utf8')) as { objects: Record<string, { hash: string; size: number }> }
  const expected = Object.keys(index.objects).length
  const counted = countObjects()
  console.log(`assets: ${counted.files} files / ${(counted.bytes / 1024 / 1024).toFixed(1)} MB, expected ${expected} objects, ${Math.round((Date.now() - t0) / 1000)}s`)
  if (counted.files < expected * 0.98) {
    console.log('FAIL modern assets incomplete')
    process.exitCode = 1
  } else {
    console.log('PASS modern assets complete')
  }

  const checkLegacy = async (versionId: string, label: string): Promise<void> => {
    const t = Date.now()
    await installVanilla(versionId, new TaskGroupHandle(`assets ${versionId}`))
    const chain = await loadVersionChain(versionId)
    const index = JSON.parse(fs.readFileSync(assetsIndexPath(chain.assetIndexId), 'utf8')) as {
      objects: Record<string, { hash: string }>
      virtual?: boolean
      map_to_resources?: boolean
    }
    const target = index.map_to_resources
      ? path.join(dirs().root, 'resources')
      : path.join(dirs().assets, 'virtual', chain.assetIndexId)
    let files = 0
    const walk = (dir: string): void => {
      if (!fs.existsSync(dir)) return
      for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
        const full = path.join(dir, entry.name)
        if (entry.isDirectory()) walk(full)
        else files++
      }
    }
    walk(target)
    console.log(
      `${label} ${versionId}: index=${chain.assetIndexId} virtual=${!!index.virtual} map_to_resources=${!!index.map_to_resources} objects=${Object.keys(index.objects).length} -> ${files} files (${Math.round((Date.now() - t) / 1000)}s)`
    )
    if (files > 100) console.log(`PASS ${label} assets materialized`)
    else {
      console.log(`FAIL ${label} assets missing`)
      process.exitCode = 1
    }
  }

  console.log('\n=== legacy assets ===')
  await checkLegacy('1.5.2', 'map_to_resources')
  await checkLegacy('1.6.4', 'virtual')
}

main().catch((e) => {
  console.error('ASSETS TEST FAILED:', e)
  process.exitCode = 1
})
