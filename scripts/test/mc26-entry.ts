import fs from 'node:fs'
import path from 'node:path'
import { initSettings } from '../../src/main/core/settings'
import { dirs, ensureDirs, setRootDir } from '../../src/main/core/paths'
import { loadVersionChain, listInstalledVersions } from '../../src/main/minecraft/version'
import { buildLaunchArgs } from '../../src/main/minecraft/launcher'
import { pickJava, scanJava } from '../../src/main/minecraft/java'
import { importExistingVersions, listInstances } from '../../src/main/minecraft/instances'
import { markBoot, markRendererReady, markShown, previousWindowFailed, startupLog, startupLogFile } from '../../src/main/core/startupLog'
import type { Account, Instance } from '../../src/shared/types'

const results: { name: string; ok: boolean; detail?: string }[] = []
function check(name: string, ok: boolean, detail?: string): void {
  results.push({ name, ok, detail })
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? ` — ${detail}` : ''}`)
  if (!ok) process.exitCode = 1
}

async function main(): Promise<void> {
  initSettings()
  const mcRoot = path.join(process.env.APPDATA ?? '', '.minecraft')
  console.log('real .minecraft:', mcRoot, 'exists:', fs.existsSync(mcRoot))

  console.log('\n=== 26.3 metadata / launch arguments (read only) ===')
  setRootDir(mcRoot)
  const installed = await listInstalledVersions()
  check('installed versions scanned', installed.length >= 2, installed.map((v) => `${v.id}(${v.loader})`).join(', '))

  const chain = await loadVersionChain('26.3')
  check('26.3 java requirement is 25', chain.javaMajor === 25, String(chain.javaMajor))
  check('26.3 default-user-jvm present', chain.defaultUserJvm.length > 0, `${chain.defaultUserJvm.length} entries`)
  check('26.3 main class', chain.mainClass === 'net.minecraft.client.main.Main', chain.mainClass)

  const javas = await scanJava(true)
  const epsilon = javas.find((j) => j.path.toLowerCase().includes('java-runtime-epsilon'))
  check('detected Java 25 runtime', !!epsilon && epsilon.major === 25, epsilon?.path)
  const picked = await pickJava(25)
  check('pickJava(25)', picked?.major === 25, picked?.path)

  const account: Account = {
    id: 'test',
    type: 'offline',
    username: 'TestPlayer',
    uuid: '0123456789abcdef0123456789abcdef',
    accessToken: '0',
    createdAt: Date.now()
  }
  const instance: Instance = {
    id: 'test-inst',
    name: '26.3',
    versionId: '26.3',
    mcVersion: '26.3',
    loader: 'vanilla',
    gameDir: path.join(mcRoot, 'versions', '26.3'),
    icon: '🧊',
    color: 'ocean',
    memory: 6144,
    createdAt: Date.now(),
    totalPlayTimeMs: 0,
    playCount: 0
  }
  const nativesDir = path.join(mcRoot, 'launcher', 'natives', 'test')
  const built = await buildLaunchArgs(instance, chain, account, picked!, { instanceId: instance.id }, instance.gameDir, nativesDir)
  const line = built.args.join(' ')
  check('default-user-jvm ZGC applied', built.args.includes('-XX:+UseZGC'))
  check('no conflicting G1 flags', !built.args.includes('-XX:+UseG1GC'))
  check('memory flag', built.args.includes('-Xmx6144M'))
  check('no unresolved placeholders', !line.includes('${'))
  check('classpath points into .minecraft/libraries', built.classpath.some((p) => p.startsWith(path.join(mcRoot, 'libraries'))))
  check('assets root', built.args.includes(path.join(mcRoot, 'assets')))
  check('game directory', built.args.includes(instance.gameDir))
  check('username args', built.args.includes('TestPlayer'))
  console.log('jvm sample:', built.args.filter((a) => a.startsWith('-XX') || a.startsWith('-Xm')).join(' '))
  console.log('game args sample:', built.args.slice(-18).join(' '))

  console.log('\n=== import .minecraft versions into a temporary root ===')
  const tempRoot = path.join(process.cwd(), '.sfx-test', 'import-home')
  fs.rmSync(tempRoot, { recursive: true, force: true })
  const tempVersion = path.join(tempRoot, 'versions', '26.3')
  fs.mkdirSync(tempVersion, { recursive: true })
  fs.copyFileSync(path.join(mcRoot, 'versions', '26.3', '26.3.json'), path.join(tempVersion, '26.3.json'))
  fs.mkdirSync(path.join(tempVersion, 'mods'), { recursive: true })
  setRootDir(tempRoot)
  ensureDirs()
  const count = await importExistingVersions()
  const imported = await listInstances()
  check('imported version count', count === 1, String(count))
  check('imported instance points at version folder', imported[0]?.imported === true && path.resolve(imported[0].gameDir) === path.resolve(tempVersion), imported[0]?.gameDir)
  const again = await importExistingVersions()
  check('import is idempotent', again === 0, String(again))
  fs.rmSync(tempRoot, { recursive: true, force: true })

  console.log('\n=== startup diagnostics ===')
  startupLog('test entry')
  markBoot('test')
  check('startup log file created', fs.existsSync(startupLogFile()), startupLogFile())
  check('previousWindowFailed before load', previousWindowFailed() === true)
  markShown()
  check('show alone is not enough', previousWindowFailed() === true)
  markRendererReady()
  check('previousWindowFailed after renderer ready', previousWindowFailed() === false)

  const failed = results.filter((r) => !r.ok)
  console.log(`\n${results.length - failed.length}/${results.length} checks passed`)
  if (failed.length) console.log('FAILED:', failed.map((f) => f.name).join(', '))
}

main().catch((e) => {
  console.error('MC26 TEST FAILED:', e)
  process.exitCode = 1
})
