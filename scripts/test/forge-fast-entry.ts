import fs from 'node:fs'
import path from 'node:path'
import { initSettings, updateSettings } from '../../src/main/core/settings'
import { dirs, ensureDirs, versionJsonPath, versionJarPath } from '../../src/main/core/paths'
import { TaskGroupHandle } from '../../src/main/core/tasks'
import { installForgeLike } from '../../src/main/minecraft/installer'

const results: { name: string; ok: boolean; detail?: string }[] = []
function check(name: string, ok: boolean, detail?: string): void {
  results.push({ name, ok, detail })
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? ` — ${detail}` : ''}`)
  if (!ok) process.exitCode = 1
}

async function main(): Promise<void> {
  initSettings()
  ensureDirs()
  await updateSettings({ downloadAssets: true, downloadSource: 'mirror' })

  const mc = '1.20.1'
  const forge = '47.4.28'
  const forgeId = `${mc}-forge-${forge}`

  // Minimal vanilla base: json + jar is all a loader install requires.
  fs.mkdirSync(path.dirname(versionJsonPath(mc)), { recursive: true })
  fs.writeFileSync(
    versionJsonPath(mc),
    JSON.stringify({
      id: mc,
      mainClass: 'net.minecraft.client.main.Main',
      libraries: [],
      assetIndex: { id: 'fake-assets', url: 'https://example.invalid/assets.json' }
    })
  )
  fs.writeFileSync(versionJarPath(mc), Buffer.from('fake'))

  // Existing Forge profile with one existing library (simulates a completed install).
  const libPath = `net/minecraftforge/forge/${mc}-${forge}/forge-${mc}-${forge}-universal.jar`
  const libFile = path.join(dirs().libraries, libPath)
  fs.mkdirSync(path.dirname(libFile), { recursive: true })
  fs.writeFileSync(libFile, Buffer.from('fake-forge-lib'))
  fs.mkdirSync(path.dirname(versionJsonPath(forgeId)), { recursive: true })
  fs.writeFileSync(
    versionJsonPath(forgeId),
    JSON.stringify({
      id: forgeId,
      inheritsFrom: mc,
      mainClass: 'cpw.mods.bootstraplauncher.BootstrapLauncher',
      libraries: [
        {
          name: `net.minecraftforge:forge:${mc}-${forge}:universal`,
          downloads: { artifact: { path: libPath, url: 'https://example.invalid/forge.jar', size: 13 } }
        }
      ]
    })
  )

  console.log('\n=== Forge fast path (already installed) ===')
  const t0 = Date.now()
  const result = await installForgeLike('forge', mc, forge, new TaskGroupHandle('forge fast'))
  const elapsed = Date.now() - t0
  check('returns existing version', result.versionId === forgeId, result.versionId)
  check('does not run installer', elapsed < 2000, `${elapsed} ms`)
  check('does not download assets for loader install', !fs.existsSync(path.join(dirs().assets, 'indexes', 'fake-assets.json')))
  check('writes aurora meta', fs.existsSync(path.join(dirs().versions, forgeId, 'aurora.json')))
  check('installer log untouched', !fs.existsSync(path.join(dirs().cache, 'installers', 'install.log')))

  console.log('\n=== Forge fast path (meta path) ===')
  const t1 = Date.now()
  const second = await installForgeLike('forge', mc, forge, new TaskGroupHandle('forge fast 2'))
  check('second call reuses profile', second.versionId === forgeId && Date.now() - t1 < 1500, `${Date.now() - t1} ms`)

  const failed = results.filter((r) => !r.ok)
  console.log(`\n${results.length - failed.length}/${results.length} checks passed`)
}

main().catch((e) => {
  console.error('FORGE FAST TEST FAILED:', e)
  process.exitCode = 1
})
