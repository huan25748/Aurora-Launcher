// Builds a headless preview bundle of the renderer with a mocked IPC bridge.
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const outDir = path.join(root, 'dist/preview')
fs.mkdirSync(outDir, { recursive: true })
fs.copyFileSync(path.join(root, 'scripts/preview/mock-aurora.js'), path.join(outDir, 'mock-aurora.js'))

const html = `<!doctype html>
<html lang="zh-CN">
  <head>
    <meta charset="UTF-8" />
    <base href="../renderer/" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <meta http-equiv="Content-Security-Policy" content="default-src 'self'; img-src 'self' data: https:; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; connect-src 'self' https:; font-src 'self' data:;" />
    <title>Aurora Launcher Preview</title>
    <link rel="stylesheet" href="/renderer/assets/app.css" />
  </head>
  <body>
    <div id="root"></div>
    <script src="/preview/mock-aurora.js"></script>
    <script src="/renderer/assets/app.js"></script>
  </body>
</html>
`
fs.writeFileSync(path.join(outDir, 'index.html'), html)
console.log('preview -> dist/preview/index.html')
