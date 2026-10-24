import fs from 'node:fs'
import path from 'node:path'
import { defaultRootDir, ensureMinecraftLayout, portableDataDir, portableRootDir, settingsDir } from '../../src/main/core/paths'

delete process.env.AURORA_HOME
const dir = path.resolve('.portable-test')
process.env.AURORA_EXE_DIR = dir
const data = settingsDir()
const root = portableRootDir()
ensureMinecraftLayout(root)
const checks: [string, boolean, string][] = [
  ['config is per launcher folder', data === path.join(dir, '.aurora'), data],
  ['portable data helper agrees', portableDataDir() === data, portableDataDir()],
  ['game root is local .minecraft', root === path.join(dir, '.minecraft'), root],
  ['default root uses local folder', defaultRootDir() === root, defaultRootDir()],
  ['layout directories exist', ['versions', 'libraries', 'assets/indexes', 'mods', 'launcher_profiles.json'].every((x) => fs.existsSync(path.join(root, x))), root],
  ['config not in AppData', !data.toLowerCase().includes('appdata'), data]
]
for (const [name, ok, detail] of checks) console.log(`${ok ? 'PASS' : 'FAIL'}  ${name} — ${detail}`)
if (checks.some(([, ok]) => !ok)) process.exitCode = 1
fs.rmSync(dir, { recursive: true, force: true })
