// Sandbox-friendly build: invokes the esbuild CLI binary directly and bundles the
// renderer without Vite. Useful where the esbuild JS service cannot spawn.
import { execFileSync } from 'node:child_process'
import { resolve, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'
import fs from 'node:fs'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const esbuild = resolve(root, 'node_modules/@esbuild/win32-x64/esbuild.exe')

function run(args) {
  execFileSync(esbuild, args, { stdio: 'inherit', cwd: root })
}

fs.rmSync(resolve(root, 'dist'), { recursive: true, force: true })

run([
  'src/main/index.ts',
  '--bundle',
  '--platform=node',
  '--target=node20',
  '--format=cjs',
  '--external:electron',
  '--outfile=dist/main/index.js',
  '--log-level=warning'
])

run([
  'src/preload/index.ts',
  '--bundle',
  '--platform=node',
  '--target=node20',
  '--format=cjs',
  '--external:electron',
  '--outfile=dist/preload/index.js',
  '--log-level=warning'
])

run([
  'src/renderer/src/main.tsx',
  '--bundle',
  '--loader:.tsx=tsx',
  '--loader:.ts=ts',
  '--loader:.css=css',
  '--loader:.svg=file',
  '--loader:.png=file',
  '--loader:.woff2=file',
  '--jsx=automatic',
  '--define:process.env.NODE_ENV="production"',
  '--target=chrome120',
  '--alias:@shared=./src/shared',
  '--alias:@renderer=./src/renderer/src',
  '--minify',
  '--public-path=./assets/',
  '--outfile=dist/renderer/assets/app.js',
  '--log-level=warning'
])

const cssPath = resolve(root, 'dist/renderer/assets/app.css')
const hasCss = fs.existsSync(cssPath)
const html = `<!doctype html>
<html lang="zh-CN">
  <head>
    <meta charset="UTF-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <meta http-equiv="Content-Security-Policy" content="default-src 'self' file:; img-src 'self' data: https: aurora-file: file: blob:; style-src 'self' 'unsafe-inline' file:; script-src 'self' 'unsafe-inline' file:; connect-src 'self' https: ws: http: file:; font-src 'self' data:;" />
    <title>Aurora Launcher</title>
    ${hasCss ? '<link rel="stylesheet" href="./assets/app.css" />' : ''}
  </head>
  <body>
    <div id="root"></div>
    <script src="./assets/app.js"></script>
  </body>
</html>
`
fs.writeFileSync(resolve(root, 'dist/renderer/index.html'), html)
console.log('local build complete -> dist/')
