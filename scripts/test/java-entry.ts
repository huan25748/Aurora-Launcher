import fs from 'node:fs'
import path from 'node:path'
import { spawnSync } from 'node:child_process'
import { initSettings } from '../../src/main/core/settings'
import { ensureDirs, dirs } from '../../src/main/core/paths'
import { TaskGroupHandle } from '../../src/main/core/tasks'
import { downloadJava, scanJava } from '../../src/main/minecraft/java'

async function main(): Promise<void> {
  initSettings()
  ensureDirs()
  const major = Number(process.argv[2] ?? 17)
  console.log('downloading Java', major, 'to', dirs().runtime)
  const group = new TaskGroupHandle(`java ${major}`)
  const t0 = Date.now()
  const info = await downloadJava(major, group)
  group.finish('done')
  console.log('installed:', info, 'in', Math.round((Date.now() - t0) / 1000), 's')
  if (!fs.existsSync(info.path)) throw new Error('java executable missing')
  const outFile = path.join(dirs().cache, 'java-version.txt')
  const fd = fs.openSync(outFile, 'w')
  const res = spawnSync(info.path, ['-version'], { stdio: ['ignore', fd, fd], timeout: 30_000 })
  fs.closeSync(fd)
  console.log('version output:', fs.readFileSync(outFile, 'utf8').split('\n').slice(0, 3).join(' | '))
  console.log('exit status:', res.status)
  const scanned = await scanJava(true)
  console.log('scanned managed runtimes:', scanned.filter((j) => j.managed).map((j) => `${j.major}:${j.path}`))
  if (res.status !== 0) process.exitCode = 1
}

main().catch((e) => {
  console.error('JAVA TEST FAILED:', e)
  process.exitCode = 1
})
