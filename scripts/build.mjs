import { build as viteBuild } from 'vite'
import { build as esbuild } from 'esbuild'
import { rmSync } from 'node:fs'
import { resolve, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')

export async function buildMain() {
  rmSync(resolve(root, 'dist/main'), { recursive: true, force: true })
  await esbuild({
    entryPoints: {
      index: resolve(root, 'src/main/index.ts')
    },
    outdir: resolve(root, 'dist/main'),
    bundle: true,
    platform: 'node',
    target: 'node20',
    format: 'cjs',
    sourcemap: true,
    external: ['electron'],
    logLevel: 'info'
  })
}

export async function buildPreload() {
  rmSync(resolve(root, 'dist/preload'), { recursive: true, force: true })
  await esbuild({
    entryPoints: {
      index: resolve(root, 'src/preload/index.ts')
    },
    outdir: resolve(root, 'dist/preload'),
    bundle: true,
    platform: 'node',
    target: 'node20',
    format: 'cjs',
    sourcemap: true,
    external: ['electron'],
    logLevel: 'info'
  })
}

export async function buildRenderer() {
  await viteBuild({ configFile: resolve(root, 'vite.config.ts') })
}

if (process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1])) {
  await buildMain()
  await buildPreload()
  await buildRenderer()
  console.log('\nBuild complete -> dist/')
}
