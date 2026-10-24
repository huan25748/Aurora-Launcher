import { spawn } from 'node:child_process'
import { resolve, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'
import electronPath from 'electron'
import { buildMain, buildPreload, buildRenderer } from './build.mjs'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
await buildMain()
await buildPreload()
await buildRenderer()
const env = { ...process.env, AURORA_DEV: '0' }
if (!process.env.AURORA_HOME) env.AURORA_HOME = resolve(root, '.aurora-home')
const child = spawn(electronPath, ['--no-sandbox', resolve(root, 'dist/main/index.js')], { stdio: 'inherit', env })
child.on('exit', (code) => process.exit(code ?? 0))
