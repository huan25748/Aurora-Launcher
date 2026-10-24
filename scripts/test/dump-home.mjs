import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { JSDOM } from 'jsdom'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..')
const bundle = fs.readFileSync(path.join(root, 'dist/renderer/assets/app.js'), 'utf8')
const mock = fs.readFileSync(path.join(root, 'scripts/preview/mock-aurora.js'), 'utf8')

const dom = new JSDOM('<!doctype html><body><div id="root"></div></body>', {
  url: 'http://localhost/#/',
  runScripts: 'outside-only',
  pretendToBeVisual: true
})
const w = dom.window
w.console.error = () => {}
w.matchMedia = () => ({ matches: false, addEventListener() {}, removeEventListener() {} })
w.requestAnimationFrame = (cb) => setTimeout(() => cb(16), 16)
w.ResizeObserver = class {
  observe() {}
  disconnect() {}
}
w.eval(mock)
w.eval(bundle)
await new Promise((r) => setTimeout(r, 1000))
const text = w.document.body.textContent.replace(/\s+/g, ' ').trim()
console.log(text.slice(0, 1400))
console.log('\n--- suspicious flags ---', /undefined|NaN|\[object Object\]/.test(text))
dom.window.close()
