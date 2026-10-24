// Builds a portable Windows folder: release/AuroraLauncher/AuroraLauncher.exe
// No external tooling (electron-builder) is required; it reuses the installed Electron runtime.
import { spawnSync } from 'node:child_process'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const releaseDir = path.join(root, 'release', 'AuroraLauncher')
const electronDir = path.join(root, 'node_modules', 'electron', 'dist')

console.log('[package] building bundles…')
const build = spawnSync(process.execPath, [path.join(root, 'scripts', 'build-local.mjs')], { stdio: 'inherit', cwd: root })
if (build.status !== 0) process.exit(build.status ?? 1)

if (!fs.existsSync(electronDir)) {
  console.error('Electron runtime not found. Run: npm install (electron) first.')
  process.exit(1)
}

console.log('[package] preparing', releaseDir)
fs.rmSync(path.join(root, 'release'), { recursive: true, force: true })
fs.mkdirSync(releaseDir, { recursive: true })

for (const entry of fs.readdirSync(electronDir)) {
  fs.cpSync(path.join(electronDir, entry), path.join(releaseDir, entry), { recursive: true })
}

// Chromium ships ~40 MB of locale paks; the launcher only needs Chinese and English.
const localesDir = path.join(releaseDir, 'locales')
if (fs.existsSync(localesDir)) {
  for (const entry of fs.readdirSync(localesDir)) {
    if (!['en-US.pak', 'zh-CN.pak'].includes(entry)) fs.rmSync(path.join(localesDir, entry), { force: true })
  }
}

const exeSource = path.join(releaseDir, 'electron.exe')
const exeTarget = path.join(releaseDir, 'AuroraLauncher.exe')
if (fs.existsSync(exeSource)) fs.renameSync(exeSource, exeTarget)

const sourcePkg = JSON.parse(fs.readFileSync(path.join(root, 'package.json'), 'utf8'))
const rcedit = path.join(root, 'resources', 'rcedit-x64.exe')
if (fs.existsSync(rcedit)) {
  const result = spawnSync(rcedit, [
    exeTarget,
    '--set-version-string', 'FileDescription', 'Aurora Launcher',
    '--set-version-string', 'ProductName', 'Aurora Launcher',
    '--set-version-string', 'CompanyName', 'Aurora Launcher',
    '--set-version-string', 'InternalName', 'AuroraLauncher',
    '--set-version-string', 'OriginalFilename', 'AuroraLauncher.exe',
    '--set-version-string', 'LegalCopyright', 'Aurora Launcher',
    '--set-file-version', sourcePkg.version,
    '--set-product-version', sourcePkg.version,
    '--set-icon', path.join(root, 'resources', 'icon.ico')
  ], { stdio: 'inherit' })
  if (result.status !== 0) console.warn('[package] rcedit failed; executable metadata unchanged')
}

const appDir = path.join(releaseDir, 'resources', 'app')
fs.mkdirSync(appDir, { recursive: true })
fs.cpSync(path.join(root, 'dist'), path.join(appDir, 'dist'), { recursive: true })
for (const entry of fs.readdirSync(path.join(appDir, 'dist'), { recursive: true })) {
  if (String(entry).endsWith('.map')) fs.rmSync(path.join(appDir, 'dist', String(entry)), { force: true })
}
for (const extra of ['test', 'preview']) {
  fs.rmSync(path.join(appDir, 'dist', extra), { recursive: true, force: true })
}
fs.mkdirSync(path.join(appDir, 'resources'), { recursive: true })
fs.copyFileSync(path.join(root, 'resources', 'icon.png'), path.join(appDir, 'resources', 'icon.png'))
fs.copyFileSync(path.join(root, 'resources', 'icon.png'), path.join(releaseDir, 'resources', 'icon.png'))
fs.writeFileSync(
  path.join(appDir, 'package.json'),
  JSON.stringify(
    {
      name: 'aurora-launcher',
      productName: 'Aurora Launcher',
      version: sourcePkg.version,
      description: 'Aurora Launcher - a modern Minecraft launcher',
      main: 'dist/main/index.js',
      author: 'Aurora'
    },
    null,
    2
  )
)
fs.writeFileSync(
  path.join(releaseDir, '使用说明.txt'),
  [
    'Aurora Launcher 便携版',
    '',
    '双击 AuroraLauncher.exe 启动。',
    '游戏数据默认保存在 %APPDATA%\\AuroraLauncher\\minecraft，可在启动器设置中修改。',
    '首次使用请在「下载」页面安装游戏版本，并在「账户」页面添加账户。',
    '',
    '如果需要迁移，直接复制整个 AuroraLauncher 文件夹即可。'
  ].join('\r\n')
)

function dirSize(dir) {
  let size = 0
  const walk = (d) => {
    for (const entry of fs.readdirSync(d, { withFileTypes: true })) {
      const full = path.join(d, entry.name)
      if (entry.isDirectory()) walk(full)
      else size += fs.statSync(full).size
    }
  }
  walk(dir)
  return size
}

const size = dirSize(releaseDir)
console.log(`[package] done: ${path.join(releaseDir, 'AuroraLauncher.exe')}`)
console.log(`[package] portable folder size: ${(size / 1024 / 1024).toFixed(1)} MB`)
