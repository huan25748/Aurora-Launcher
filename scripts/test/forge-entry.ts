import fs from 'node:fs'
import path from 'node:path'
import { initSettings, updateSettings, getSettings } from '../../src/main/core/settings'
import { ensureDirs, dirs } from '../../src/main/core/paths'
import { TaskGroupHandle } from '../../src/main/core/tasks'
import { installVanilla, installForgeLike, listLoaderVersions } from '../../src/main/minecraft/installer'
import { loadVersionChain } from '../../src/main/minecraft/version'
import { buildLaunchArgs } from '../../src/main/minecraft/launcher'
import { createInstance } from '../../src/main/minecraft/instances'
import { addOfflineAccount } from '../../src/main/minecraft/auth'
import { pickJava } from '../../src/main/minecraft/java'

async function main(): Promise<void> {
  initSettings()
  ensureDirs()
  await updateSettings({ downloadAssets: false, maxConcurrency: 24 })
  const kind = (process.argv[2] as 'forge' | 'neoforge') ?? 'forge'
  const mc = process.argv[3] ?? '1.20.1'
  console.log(`installing ${kind} for ${mc}; root=${dirs().root}`)
  const versions = await listLoaderVersions(kind, mc)
  console.log('loader versions:', versions.slice(0, 5).map((v) => v.version).join(', '))
  if (!versions.length) throw new Error('no loader versions')

  await installVanilla(mc, new TaskGroupHandle('vanilla'))
  const group = new TaskGroupHandle(`install ${kind}`)
  const t0 = Date.now()
  const result = await installForgeLike(kind, mc, versions[0].version, group)
  group.finish('done')
  console.log(`installed in ${Math.round((Date.now() - t0) / 1000)}s:`, result)

  const chain = await loadVersionChain(result.versionId)
  console.log('mainClass:', chain.mainClass)
  console.log('libraries:', chain.libraries.length, 'jvm args:', chain.jvmArgs.length, 'game args:', chain.gameArgs.length)
  console.log('java requirement:', chain.javaMajor)

  const inst = await createInstance({
    name: `test ${kind}`,
    versionId: result.versionId,
    mcVersion: mc,
    loader: kind,
    loaderVersion: result.loaderVersion
  })
  const account = await addOfflineAccount('ForgeTest')
  const java = (await pickJava(chain.javaMajor ?? 17))!
  console.log('using java:', java.path, java.major)
  const built = await buildLaunchArgs(inst, chain, account, java, { instanceId: inst.id }, inst.gameDir, path.join(dirs().natives, 'forge'))
  console.log('args count:', built.args.length)
  const leftovers = built.args.filter((a) => a.includes('${'))
  console.log('unresolved placeholders:', leftovers)
  console.log('check json exists:', fs.existsSync(path.join(dirs().versions, result.versionId, `${result.versionId}.json`)))
  console.log('version dir listing:', fs.readdirSync(path.join(dirs().versions, result.versionId)).join(', '))
  if (leftovers.length) process.exitCode = 1
}

main().catch((e) => {
  console.error('FORGE TEST FAILED:', e)
  process.exitCode = 1
})
