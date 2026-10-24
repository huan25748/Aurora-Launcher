import { createServer } from 'vite'
import { context } from 'esbuild'
import { spawn } from 'node:child_process'
import { resolve, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'
import electronPath from 'electron'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')

let ready = false
let restartTimer = null
let shuttingDown = false

const common = {
  bundle: true,
  platform: 'node',
  target: 'node20',
  format: 'cjs',
  sourcemap: true,
  external: ['electron'],
  logLevel: 'warning',
  plugins: [
    {
      name: 'electron-restart',
      setup(b) {
        b.onEnd((result) => {
          if (ready && result.errors.length === 0) restart()
        })
      }
    }
  ]
}

const ctxMain = await context({
  ...common,
  entryPoints: { index: resolve(root, 'src/main/index.ts') },
  outdir: resolve(root, 'dist/main')
})
const ctxPreload = await context({
  ...common,
  entryPoints: { index: resolve(root, 'src/preload/index.ts') },
  outdir: resolve(root, 'dist/preload')
})

await ctxMain.rebuild()
await ctxPreload.rebuild()

const server = await createServer({ configFile: resolve(root, 'vite.config.ts') })
await server.listen()
const url = server.resolvedUrls?.local?.[0] ?? 'http://localhost:5178/'
console.log(`\n  Aurora dev renderer: ${url}`)

await ctxMain.watch()
await ctxPreload.watch()
ready = true
restart()

let child = null

function restart() {
  if (shuttingDown) return
  clearTimeout(restartTimer)
  restartTimer = setTimeout(() => {
    if (child) {
      try {
        child.kill()
      } catch {}
    }
    const env = { ...process.env, APP_DEV_URL: url, AURORA_DEV: '1' }
    if (!process.env.AURORA_HOME) env.AURORA_HOME = resolve(root, '.aurora-home')
    child = spawn(electronPath, ['--no-sandbox', resolve(root, 'dist/main/index.js')], {
      stdio: 'inherit',
      env
    })
    child.on('exit', (code) => {
      if (!shuttingDown && code !== null && code !== 0) console.log(`[electron exited: ${code}]`)
    })
  }, 300)
}

async function shutdown(code = 0) {
  shuttingDown = true
  clearTimeout(restartTimer)
  if (child) {
    try {
      child.kill()
    } catch {}
  }
  await Promise.allSettled([ctxMain.dispose(), ctxPreload.dispose(), server.close()])
  process.exit(code)
}

process.on('SIGINT', () => shutdown(0))
process.on('SIGTERM', () => shutdown(0))
