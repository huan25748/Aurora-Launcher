import fs from 'node:fs'
import path from 'node:path'
import { spawnSync } from 'node:child_process'
import AdmZip from 'adm-zip'
import { initSettings, updateSettings, getSettings } from '../../src/main/core/settings'
import { ensureDirs, ensureMinecraftLayout, dirs } from '../../src/main/core/paths'
import { TaskGroupHandle } from '../../src/main/core/tasks'
import { installVanilla, installFabricLike, listLoaderVersions } from '../../src/main/minecraft/installer'
import { loadVersionChain, resolveLibraries, classpathLibraries } from '../../src/main/minecraft/version'
import { buildClasspath, buildLaunchArgs, extractNativesFor } from '../../src/main/minecraft/launcher'
import { cleanupInstallVersions, copyInstance, createInstance, listInstances, removeInstance } from '../../src/main/minecraft/instances'
import { offlineUuid, addOfflineAccount } from '../../src/main/minecraft/auth'
import { writeNbtFile, parseNbtFile, plain, nbt } from '../../src/main/core/nbt'
import { addServer, listServers } from '../../src/main/minecraft/servers'
import { readModMeta, listWorlds, listContent } from '../../src/main/minecraft/content'
import { analyzeInstanceCrash } from '../../src/main/minecraft/logs'
import { scanJava, findJavaComponent, pickJava } from '../../src/main/minecraft/java'
import { searchModrinth } from '../../src/main/minecraft/modrinth'

const results: { name: string; ok: boolean; detail?: string }[] = []
function check(name: string, ok: boolean, detail?: string): void {
  results.push({ name, ok, detail })
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? ` — ${detail}` : ''}`)
  if (!ok) process.exitCode = 1
}
function section(title: string): void {
  console.log(`\n=== ${title} ===`)
}

async function main(): Promise<void> {
  const home = process.env.AURORA_HOME!
  section('init')
  initSettings()
  const layout = path.join(home, 'layout-test')
  ensureMinecraftLayout(layout)
  check(
    'portable .minecraft layout created',
    ['versions', 'libraries', 'assets/indexes', 'mods', 'launcher_profiles.json'].every((entry) => fs.existsSync(path.join(layout, entry)))
  )
  ensureDirs()
  console.log('root:', dirs().root)
  check('settings initialized', !!getSettings().rootDir)

  section('version manifest + loader list')
  const manifest = await (await import('../../src/main/minecraft/version')).getVersionManifest(true)
  check('version manifest fetched', manifest.versions.length > 100, `${manifest.versions.length} versions, latest ${manifest.latest.release}`)
  const loaders = await listLoaderVersions('fabric', '1.20.1')
  check('fabric loader list', loaders.length > 0, `${loaders.length} versions`)

  section('vanilla install (1.20.1, assets skipped)')
  await updateSettings({ downloadAssets: false, maxConcurrency: 24 })
  const group = new TaskGroupHandle('e2e vanilla')
  const t0 = Date.now()
  await installVanilla('1.20.1', group)
  group.finish('done')
  const vdir = path.join(dirs().versions, '1.20.1')
  check('version json', fs.existsSync(path.join(vdir, '1.20.1.json')))
  check('client jar', fs.existsSync(path.join(vdir, '1.20.1.jar')), `${fs.statSync(path.join(vdir, '1.20.1.jar')).size} bytes`)
  const chain = await loadVersionChain('1.20.1')
  check('chain mainClass', chain.mainClass === 'net.minecraft.client.main.Main', chain.mainClass)
  const resolved = resolveLibraries(chain.libraries, {})
  const cp = classpathLibraries(resolved)
  check('classpath libraries resolved', cp.length > 10, `${cp.length} entries`)
  const libMissing = cp.filter((entry) => !fs.existsSync(path.join(dirs().libraries, entry.path)))
  check('all libraries downloaded', libMissing.length === 0, libMissing.slice(0, 3).map((x) => x.path).join(', '))
  check('install time under 5min', Date.now() - t0 < 300_000, `${Math.round((Date.now() - t0) / 1000)}s`)

  section('natives resolution')
  const modernNatives = cp.filter((entry) => /natives-windows/i.test(entry.path))
  check('modern native jars on classpath', modernNatives.length >= 5, `${modernNatives.length} native jars`)
  const syntheticResolved = resolveLibraries(
    [
      {
        name: 'org.lwjgl:lwjgl:2.9.4',
        natives: { windows: 'natives-windows' },
        downloads: {
          artifact: { path: 'org/lwjgl/lwjgl/2.9.4/lwjgl-2.9.4.jar', url: 'https://example.com/a.jar' },
          classifiers: { 'natives-windows': { path: 'org/lwjgl/lwjgl/2.9.4/lwjgl-2.9.4-natives-windows.jar', url: 'https://example.com/b.jar' } }
        }
      }
    ],
    {}
  )
  check('legacy natives mapping resolved', syntheticResolved.length === 2 && syntheticResolved[0].extract === false && syntheticResolved[1].extract === true)
  const nativesDir = path.join(dirs().natives, 'e2e')
  fs.rmSync(nativesDir, { recursive: true, force: true })
  fs.mkdirSync(nativesDir, { recursive: true })
  const jars = extractNativesFor(chain, {}, nativesDir)
  check('modern version needs no manual native extraction', jars.length === 0, `${jars.length} extracted`)

  section('launch arguments')
  const inst = await createInstance({ name: 'E2E 1.20.1', versionId: '1.20.1', mcVersion: '1.20.1', loader: 'vanilla' })
  check('instance created', fs.existsSync(path.join(inst.gameDir, 'mods')))
  check('instance lives under versions', path.dirname(inst.gameDir) === dirs().versions && inst.versionId === path.basename(inst.gameDir), inst.gameDir)
  const cloneFile = path.join(inst.gameDir, `${inst.versionId}.json`)
  const cloneJson = fs.existsSync(cloneFile) ? JSON.parse(fs.readFileSync(cloneFile, 'utf8')) : null
  check(
    'PCL-compatible standalone version json',
    !!cloneJson &&
      cloneJson.id === inst.versionId &&
      !cloneJson.inheritsFrom &&
      Array.isArray(cloneJson.libraries) &&
      cloneJson.libraries.length > 0 &&
      !!cloneJson.mainClass,
    cloneJson ? `${cloneJson.libraries?.length} libs, inherits=${cloneJson.inheritsFrom ?? 'none'}` : 'missing'
  )
  check('standalone instance has client jar', fs.existsSync(path.join(inst.gameDir, `${inst.versionId}.jar`)))
  check('instance metadata inside version folder', fs.existsSync(path.join(inst.gameDir, 'instance.json')))
  const cloneChain = await loadVersionChain(inst.versionId)
  check('clone version chain resolves', cloneChain.mainClass === chain.mainClass && !!cloneChain.clientDownload?.url)
  const inheritedForge = { ...cloneChain, mainClass: 'cpw.mods.bootstraplauncher.BootstrapLauncher', inheritedJarId: '1.20.1' } as never
  const inheritedForgeCp = buildClasspath(inheritedForge, {})
  check('modern Forge classpath omits inherited vanilla jar', !inheritedForgeCp.some((entry) => entry.endsWith(`${path.sep}1.20.1${path.sep}1.20.1.jar`)))
  const fullForge = { ...cloneChain, id: 'SyntheticForge', inheritedJarId: 'SyntheticForge', mainClass: 'cpw.mods.bootstraplauncher.BootstrapLauncher' } as never
  fs.mkdirSync(path.join(dirs().versions, 'SyntheticForge'), { recursive: true })
  fs.writeFileSync(path.join(dirs().versions, 'SyntheticForge', 'SyntheticForge.jar'), 'fake')
  check('self-contained Forge keeps its own jar', buildClasspath(fullForge, {}).some((entry) => entry.endsWith('SyntheticForge.jar')))
  for (const [id, marker] of [['TempVanilla', 'vanilla'], ['TempLoader', 'forge']] as const) {
    fs.mkdirSync(path.join(dirs().versions, id), { recursive: true })
    fs.writeFileSync(path.join(dirs().versions, id, `${id}.json`), JSON.stringify({ id, mainClass: marker }))
  }
  const removedTemp = await cleanupInstallVersions(['TempVanilla', 'TempLoader', inst.versionId], inst.versionId)
  check('intermediate version folders cleaned', removedTemp.length === 2 && !fs.existsSync(path.join(dirs().versions, 'TempVanilla')) && fs.existsSync(inst.gameDir))

  const account = await addOfflineAccount('E2ETester')
  check('offline uuid stable', account.uuid === offlineUuid('E2ETester') && account.uuid.length === 32, account.uuid)
  const fakeJava = { path: 'java', version: '17.0.15', major: 17, arch: 'x64', vendor: 'test', is64Bit: true, source: 'test' } as never
  const built = await buildLaunchArgs(inst, chain, account, fakeJava, { instanceId: inst.id }, inst.gameDir, nativesDir)
  const cloneBuilt = await buildLaunchArgs(inst, cloneChain, account, fakeJava, { instanceId: inst.id }, inst.gameDir, nativesDir)
  check('clone launch args resolve', cloneBuilt.args.length === built.args.length && !cloneBuilt.args.join(' ').includes('${'))
  const argLine = built.args.join(' ')
  check('main class in args', built.args.includes(chain.mainClass))
  check('classpath flag', built.args.some((a) => a === '-cp' || a.startsWith('-Djava.class.path')))
  check('username substitution', built.args.includes('E2ETester'))
  check('game dir substitution', built.args.includes(inst.gameDir))
  check('assets dir substitution', built.args.includes(dirs().assets))
  check('memory flag', built.args.some((a) => a.startsWith('-Xmx')))
  check('natives dir substitution', argLine.includes(nativesDir))
  check('version json untouched by args', !argLine.includes('${'))

  section('fabric install')
  const g2 = new TaskGroupHandle('e2e fabric')
  const fabric = await installFabricLike('fabric', '1.20.1', undefined, g2)
  g2.finish('done')
  check('fabric profile saved', fs.existsSync(path.join(dirs().versions, fabric.versionId, `${fabric.versionId}.json`)), fabric.versionId)
  const fchain = await loadVersionChain(fabric.versionId)
  check('fabric main class', fchain.mainClass.includes('KnotClient'), fchain.mainClass)
  const fResolved = resolveLibraries(fchain.libraries, {})
  const missingF = fResolved.filter((r) => r.artifact?.path && !fs.existsSync(path.join(dirs().libraries, r.artifact.path!)))
  check('fabric libs downloaded', missingF.length === 0, `${fResolved.length} libs`)
  const finst = await createInstance({ name: 'E2E Fabric', versionId: fabric.versionId, mcVersion: '1.20.1', loader: 'fabric', loaderVersion: fabric.loaderVersion })
  const fbuilt = await buildLaunchArgs(finst, fchain, account, fakeJava, { instanceId: finst.id }, finst.gameDir, nativesDir)
  check('fabric launch args', fbuilt.args.includes(fchain.mainClass) && fbuilt.args.includes('E2ETester'))

  section('nbt / servers.dat')
  const st = path.join(inst.gameDir, 'servers.dat')
  writeNbtFile(st, nbt.compound({ servers: nbt.list(10, [nbt.compound({ name: nbt.string('Hello'), ip: nbt.string('play.example.com') })]) }))
  const parsed = parseNbtFile(st)
  check('nbt roundtrip', !!parsed && parsed.type === 'compound' && JSON.stringify(plain(parsed)).includes('play.example.com'))
  await addServer(inst.id, { name: 'Test', ip: 'mc.example.net:25565' })
  const servers = await listServers(inst.id)
  check('servers.dat read/write', servers.some((s) => s.ip === 'mc.example.net:25565'), `${servers.length} servers`)

  section('mods metadata')
  const zip = new AdmZip()
  zip.addFile(
    'fabric.mod.json',
    Buffer.from(
      JSON.stringify({
        id: 'e2e_mod',
        name: 'E2E Mod',
        version: '1.2.3',
        description: 'Test mod',
        authors: ['Tester'],
        depends: { minecraft: '1.20.1', fabricloader: '>=0.15' }
      })
    )
  )
  const modFile = path.join(inst.gameDir, 'mods', 'e2e-mod-1.2.3.jar')
  zip.writeZip(modFile)
  const meta = readModMeta(modFile)
  check('fabric mod.json parsed', meta.id === 'e2e_mod' && meta.name === 'E2E Mod' && meta.version === '1.2.3', JSON.stringify(meta))
  const mods = await listContent(inst.id, 'mods')
  check('content list', mods.length === 1 && mods[0].enabled, `${mods.length} mods`)

  section('worlds metadata')
  const worldDir = path.join(inst.gameDir, 'saves', 'e2e-world')
  fs.mkdirSync(worldDir, { recursive: true })
  const level = nbt.compound({
    Data: nbt.compound({
      LevelName: nbt.string('E2E World'),
      LastPlayed: nbt.long(Date.now()),
      Hardcore: nbt.byte(0),
      allowCommands: nbt.byte(1),
      Version: nbt.compound({ Name: nbt.string('1.20.1') })
    })
  })
  writeNbtFile(path.join(worldDir, 'level.dat'), level)
  const worlds = await listWorlds(inst.id)
  check('world parsed', worlds.length === 1 && worlds[0].name === 'E2E World' && worlds[0].gameVersion === '1.20.1', JSON.stringify(worlds[0]?.name))

  section('crash analysis')
  const logsDir = path.join(inst.gameDir, 'logs')
  fs.mkdirSync(logsDir, { recursive: true })
  fs.writeFileSync(
    path.join(logsDir, 'latest.log'),
    '[12:00:00] [main/ERROR] java.lang.OutOfMemoryError: Java heap space\n\tat net.minecraft.client.Minecraft.run(Minecraft.java:1)\n'
  )
  const crash = analyzeInstanceCrash(inst.id)
  check('crash rule matched', crash?.cause === '内存不足', crash?.cause)

  section('java detection')
  const javas = await scanJava(true)
  check('java detected', javas.length > 0, javas.map((j) => `Java ${j.major} (${j.vendor})`).join(', '))
  const comp = await findJavaComponent(17)
  check('java runtime component', !!comp && comp.component === 'java-runtime-gamma', JSON.stringify(comp))
  const picked = await pickJava(17)
  check('pick java', !!picked, picked?.path)

  section('network api')
  const search = await searchModrinth({ query: 'sodium', projectType: 'mod', limit: 3 })
  check('modrinth search', search.hits.length > 0, `${search.total_hits} hits, first ${search.hits[0]?.title}`)

  section('java process')
  const javaExe = picked?.path ?? 'java'
  const outFile = path.join(home, 'java-version.txt')
  const fd = fs.openSync(outFile, 'w')
  const res = spawnSync(javaExe, ['-version'], { stdio: ['ignore', fd, fd], timeout: 30_000 })
  fs.closeSync(fd)
  const versionText = fs.readFileSync(outFile, 'utf8')
  check('java executable runs', res.status === 0, versionText.split('\n')[0])

  section('copy / delete instance')
  const copied = await copyInstance(inst.id, 'E2E Copy')
  check('copy lives under versions', path.dirname(copied.gameDir) === dirs().versions && copied.versionId === path.basename(copied.gameDir), copied.gameDir)
  check('copy has version json', fs.existsSync(path.join(copied.gameDir, `${copied.versionId}.json`)))
  const copiedDir = copied.gameDir
  await removeInstance(copied.id, true)
  check('delete removes version folder', !fs.existsSync(copiedDir))
  check('delete keeps source version', fs.existsSync(path.join(dirs().versions, '1.20.1')))

  section('instances listing')
  const list = await listInstances()
  check('instances listed', list.length >= 2, `${list.length} instances`)

  section('summary')
  const failed = results.filter((r) => !r.ok)
  console.log(`\n${results.length - failed.length}/${results.length} checks passed`)
  if (failed.length) {
    console.log('FAILED CHECKS:')
    for (const f of failed) console.log(` - ${f.name}: ${f.detail ?? ''}`)
    process.exitCode = 1
  }
}

main().catch((e) => {
  console.error('E2E fatal error:', e)
  process.exitCode = 1
})
