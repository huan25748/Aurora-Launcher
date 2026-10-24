// Bundles and runs the Node-level end-to-end suites.
// Usage: node scripts/test/run-all.mjs [e2e|services|java|assets|forge|all]
import { spawnSync } from 'node:child_process'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..')
const esbuild = path.join(root, 'node_modules', '@esbuild', 'win32-x64', 'esbuild.exe')
const outDir = path.join(root, 'dist', 'test')
const home = path.join(root, '.e2e-home')
fs.mkdirSync(outDir, { recursive: true })
fs.mkdirSync(home, { recursive: true })

const suites = {
  e2e: 'e2e-entry.ts',
  services: 'services-entry.ts',
  java: 'java-entry.ts',
  assets: 'assets-entry.ts',
  forge: 'forge-entry.ts',
  mc26: 'mc26-entry.ts',
  'forge-fast': 'forge-fast-entry.ts',
  portable: 'portable-entry.ts'
}

const arg = process.argv[2] ?? 'e2e'
const selected = arg === 'all' ? ['e2e', 'services'] : [arg]
let failed = false

for (const name of selected) {
  const entry = suites[name]
  if (!entry) {
    console.error(`unknown suite: ${name}`)
    process.exit(1)
  }
  const out = path.join(outDir, `${name}.cjs`)
  console.log(`\n===== bundling ${name} =====`)
  const bundle = spawnSync(
    esbuild,
    [
      path.join(root, 'scripts', 'test', entry),
      '--bundle',
      '--platform=node',
      '--format=cjs',
      '--target=node20',
      '--alias:electron=' + path.join(root, 'scripts', 'test', 'electron-stub.js'),
      '--outfile=' + out,
      '--log-level=warning'
    ],
    { stdio: 'inherit', cwd: root }
  )
  if (bundle.status !== 0) {
    failed = true
    continue
  }
  console.log(`\n===== running ${name} =====`)
  const run = spawnSync(process.execPath, [out], {
    stdio: 'inherit',
    cwd: root,
    env: { ...process.env, AURORA_HOME: home }
  })
  if (run.status !== 0) failed = true
}

process.exit(failed ? 1 : 0)
