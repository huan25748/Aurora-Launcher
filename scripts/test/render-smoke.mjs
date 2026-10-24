// Renders the bundled renderer inside jsdom with the mock IPC bridge.
// Catches runtime crashes in components that static type checking cannot see.
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { JSDOM } from 'jsdom'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..')
const bundle = fs.readFileSync(path.join(root, 'dist/renderer/assets/app.js'), 'utf8')
const mock = fs.readFileSync(path.join(root, 'scripts/preview/mock-aurora.js'), 'utf8')

const pages = ['', 'instances', 'download', 'accounts', 'settings', 'about', 'instance/inst-demo', 'project/AANobbMI']

let failed = false

for (const page of pages) {
  const url = `http://localhost/${page ? `#/${page}` : ''}`
  const dom = new JSDOM('<!doctype html><html><head></head><body><div id="root"></div></body></html>', {
    url,
    runScripts: 'outside-only',
    pretendToBeVisual: true
  })
  const { window } = dom
  const errors = []
  window.console.error = (...args) => {
    const text = args.map((a) => (a instanceof Error ? `${a.name}: ${a.message}` : String(a))).join(' ')
    if (text.includes('not wrapped in act') || text.includes('Warning:')) return
    errors.push(text)
  }
  window.addEventListener('error', (e) => errors.push(`window.error: ${e.message}`))
  window.addEventListener('unhandledrejection', (e) => errors.push(`unhandledrejection: ${e.reason}`))
  window.matchMedia = () => ({ matches: false, addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {} })
  window.requestAnimationFrame = (cb) => setTimeout(() => cb(Date.now()), 16)
  window.cancelAnimationFrame = (id) => clearTimeout(id)
  window.ResizeObserver = class {
    observe() {}
    unobserve() {}
    disconnect() {}
  }
  window.HTMLElement.prototype.scrollTo = () => {}
  window.navigator.clipboard = { writeText: async () => {} }

  try {
    window.eval(mock)
    window.eval(bundle)
  } catch (e) {
    errors.push(`eval: ${e instanceof Error ? e.stack : String(e)}`)
  }

  await new Promise((r) => setTimeout(r, 900))

  const html = window.document.body.innerHTML
  const text = window.document.body.textContent ?? ''
  const meaningful = text.replace(/\s+/g, '').length
  const ok = errors.length === 0 && html.length > 200 && meaningful > 30
  if (!ok) failed = true
  console.log(`${ok ? 'PASS' : 'FAIL'}  /#/${page || 'home'}  (html=${html.length}B, text=${meaningful} chars)`)
  for (const err of errors.slice(0, 6)) console.log(`   ! ${err.slice(0, 400)}`)
  dom.window.close()
}

console.log(failed ? '\nrender smoke test FAILED' : '\nrender smoke test passed')
process.exit(failed ? 1 : 0)
