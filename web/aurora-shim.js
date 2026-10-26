(function () {
  'use strict'
  const listeners = Object.create(null)
  const on = (name, cb) => {
    ;(listeners[name] || (listeners[name] = [])).push(cb)
    return () => {
      const list = listeners[name]
      if (!list) return
      const index = list.indexOf(cb)
      if (index >= 0) list.splice(index, 1)
    }
  }
  const post = (payload) => {
    if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage(payload)
  }
  const pending = new Map()
  let nextInvokeId = 1
  if (window.chrome && window.chrome.webview) {
    window.chrome.webview.addEventListener('message', (event) => {
      const message = event.data
      if (!message) return
      if (message.type === 'event') {
        ;(listeners[message.name] || []).forEach((cb) => { try { cb(message.payload) } catch (error) { console.error(error) } })
        return
      }
      if (message.type !== 'resolve' && message.type !== 'reject') return
      const waiter = pending.get(message.id)
      if (!waiter) return
      pending.delete(message.id)
      if (message.type === 'resolve') waiter.resolve(message.result)
      else waiter.reject(new Error(message.error || '操作失败'))
    })
  }
  const invoke = (channel, ...args) => new Promise((resolve, reject) => {
    if (!window.chrome || !window.chrome.webview) {
      reject(new Error('WebView2 桥接不可用'))
      return
    }
    const id = 'rpc-' + nextInvokeId++
    const timeoutMs = channel.startsWith('install:') ? 0 : 60000
    const timer = timeoutMs > 0 ? setTimeout(() => {
      pending.delete(id)
      reject(new Error('后端响应超时：' + channel))
    }, timeoutMs) : null
    pending.set(id, {
      resolve: (value) => { if (timer) clearTimeout(timer); resolve(value) },
      reject: (error) => { if (timer) clearTimeout(timer); reject(error) }
    })
    window.chrome.webview.postMessage({ type: 'invoke', id, channel, args })
  })
  const boot = window.__AURORA_BOOT__ || {}
  const settingDefaults = {
    rootDir: boot.rootDir || 'D:\\Minecraft\\.minecraft',
    language: 'zh-CN',
    theme: 'system',
    accent: 'aurora',
    downloadSource: 'auto',
    modrinthSource: 'mirror',
    optifineSource: 'auto',
    downloadSources: {},
    maxConcurrency: 24,
    downloadThreads: 64,
    proxyEnabled: false,
    proxyHost: '127.0.0.1',
    proxyPort: 7890,
    minimizeOnLaunch: false,
    showConsoleOnLaunch: true,
    keepLauncherOpen: true,
    closeBehavior: 'exit',
    defaultMemory: 0,
    autoMemory: true,
    jvmArgs: '',
    gameArgs: '',
    javaPath: '',
    msClientId: 'c36a9fb6-4f2a-41ff-90bd-ae7cc92031eb',
    enableStats: true,
    lastSelectedInstanceId: '',
    onboardingDone: true,
    checkUpdates: true,
    downloadAssets: true,
    downloadNatives: true,
    useAuthlibForOffline: false,
    autoImportVersions: true,
    firstLaunchChinese: true
  }
  let settings = Object.assign({}, settingDefaults, JSON.parse(localStorage.getItem('aurora.settings') || '{}'))
  const root = boot.rootDir || settingDefaults.rootDir
  const bootInstances = Array.isArray(boot.instances) ? boot.instances : []
  const appInfo = {
    version: boot.version || '1.1.0',
    electron: '',
    chrome: navigator.userAgent,
    node: '',
    platform: navigator.platform,
    arch: 'x64',
    rootDir: root,
    versionsDir: boot.versionsDir || root + '\\versions',
    assetsDir: boot.assetsDir || root + '\\assets',
    librariesDir: boot.librariesDir || root + '\\libraries',
    instancesDir: boot.instancesDir || root + '\\instances',
    totalInstances: bootInstances.length,
    totalVersions: bootInstances.length,
    totalPlayTimeMs: 0
  }
  const emptySearch = { hits: [], offset: 0, limit: 20, total_hits: 0, total: 0 }
  const noop = async () => {}
  const empty = async () => []
  const api = {
    app: {
      info: async () => { try { return await invoke('app:info') } catch { return appInfo } },
      openExternal: async (url) => post({ type: 'openExternal', url }),
      openPath: async (target) => post({ type: 'openPath', path: target }),
      showItem: async (target) => post({ type: 'showItem', path: target }),
      pickDirectory: async (title, defaultPath) => { try { return await invoke('app:pick-directory', title, defaultPath) } catch { return { canceled: true, paths: [] } } },
      pickFile: async (filters, multi) => { try { return await invoke('app:pick-file', filters, multi) } catch { return { canceled: true, paths: [] } } },
      relaunch: async () => post({ type: 'window', action: 'relaunch' }),
      quit: async () => post({ type: 'window', action: 'close' }),
      window: {
        minimize: async () => post({ type: 'window', action: 'minimize' }),
        toggleMaximize: async () => post({ type: 'window', action: 'maximize' }),
        close: async () => post({ type: 'window', action: 'close' }),
        isMaximized: async () => false,
        setTitle: async (title) => post({ type: 'window', action: 'title', title })
      }
    },
    network: { test: async () => { try { return await invoke('network:test') } catch { return [] } } },
    settings: {
      get: async () => { try { return await invoke('settings:get') } catch { return settings } },
      update: async (patch) => {
        try {
          settings = await invoke('settings:update', patch)
        } catch {
          settings = Object.assign({}, settings, patch)
        }
        localStorage.setItem('aurora.settings', JSON.stringify(settings))
        return settings
      },
      reset: async () => {
        try { settings = await invoke('settings:reset') } catch { settings = Object.assign({}, settingDefaults) }
        localStorage.removeItem('aurora.settings')
        return settings
      },
      pickRoot: async () => { try { return await invoke('settings:pick-root') } catch { return settings } },
      usePortableRoot: async () => { try { return await invoke('settings:use-portable-root') } catch { return settings } },
      systemInfo: async () => { try { return await invoke('settings:system-info') } catch { return { platform: 'win32', arch: 'x64', os: 'Windows', cpu: 'Unknown CPU', memoryMB: 0, totalMemoryMB: 0 } } }
    },
    versions: {
      manifest: async (force) => { try { return await invoke('versions:manifest', !!force) } catch { return { latest: { release: '', snapshot: '' }, versions: [], fetchedAt: Date.now() } } },
      installed: async () => { try { return await invoke('versions:installed') } catch { return [] } },
      delete: async (versionId) => { try { return await invoke('versions:delete', versionId) } catch { return null } },
      loaders: async (kind, mcVersion) => { try { return await invoke('versions:loaders', kind, mcVersion) } catch { return [] } },
      javaRequirement: async (versionId) => { try { return await invoke('versions:java-requirement', versionId) } catch { return 21 } },
      loaderSize: async (versionId) => { try { return await invoke('versions:loader-size', versionId) } catch { return { size: 0, files: 0 } } }
    },
    install: {
      vanilla: async (versionId, createInstance, instanceName) => { try { return await invoke('install:vanilla', versionId, createInstance !== false, instanceName) } catch (error) { throw error } },
      loader: async (request) => { return await invoke('install:loader', request) }
    },
    launch: {
      start: async (opts) => { try { return await invoke('launch:start', opts) } catch (error) { return { ok: false, error: String(error.message || error) } } },
      stop: async () => { try { return await invoke('launch:stop') } catch { return null } },
      state: async () => { try { return await invoke('launch:state') } catch { return { state: 'idle' } } }
    },
    instances: {
      list: async () => { try { return await invoke('instances:list') } catch { return bootInstances } },
      get: async (id) => { try { return await invoke('instances:get', id) } catch { return bootInstances.find((item) => item.id === id) || null } },
      create: async (input) => { try { return await invoke('instances:create', input) } catch { return null } },
      update: async (id, patch) => { try { return await invoke('instances:update', id, patch) } catch { return null } },
      remove: async (id, deleteFiles) => { try { return await invoke('instances:remove', id, deleteFiles) } catch { return null } },
      copy: async (id, name) => { try { return await invoke('instances:copy', id, name) } catch { return null } },
      size: async (id) => { try { return await invoke('instances:size', id) } catch { return { size: 0 } } },
      openFolder: async (id, sub) => { try { return await invoke('instances:open-folder', id, sub) } catch { return null } },
      export: async (id) => { try { return await invoke('instances:export', id) } catch { return { path: '' } } },
      exportPack: async (id) => { try { return await invoke('instances:export-pack', id) } catch { return { path: '', size: 0, files: 0 } } },
      import: async () => { try { return await invoke('instances:import') } catch { return null } },
      importExisting: async () => { try { return await invoke('instances:import-existing') } catch { return 0 } }
    },
    content: {
      list: async (instanceId, kind) => { try { return await invoke('content:list', instanceId, kind) } catch { return [] } },
      worlds: async (instanceId) => { try { return await invoke('content:worlds', instanceId) } catch { return [] } },
      screenshots: async (instanceId) => { try { return await invoke('content:screenshots', instanceId) } catch { return [] } },
      toggle: async (instanceId, kind, file) => { try { return await invoke('content:toggle', instanceId, kind, file) } catch { return null } },
      remove: async (instanceId, kind, file) => { try { return await invoke('content:remove', instanceId, kind, file) } catch { return null } },
      import: async (instanceId, kind) => { try { return await invoke('content:import', instanceId, kind) } catch { return 0 } },
      backupWorld: async (instanceId, folder) => { try { return await invoke('content:backup-world', instanceId, folder) } catch { return { path: '' } } },
      deleteWorld: async (instanceId, folder) => { try { return await invoke('content:delete-world', instanceId, folder) } catch { return null } }
    },
    modrinth: {
      search: async (params) => { try { return await invoke('modrinth:search', params) } catch { return emptySearch } },
      project: async (id) => { try { return await invoke('modrinth:project', id) } catch { return null } },
      versions: async (projectId, mcVersion, loader) => { try { return await invoke('modrinth:versions', projectId, mcVersion, loader) } catch { return [] } },
      translations: async () => ({}),
      install: async (opts) => { return await invoke('modrinth:install', opts) },
      installToFolder: async (opts) => { return await invoke('modrinth:install-folder', opts) },
      installPack: async (projectId, instanceName, versionId) => { return await invoke('modrinth:install-pack', projectId, instanceName, versionId) },
      featured: async (type) => { try { return await invoke('modrinth:featured', type) } catch { return emptySearch } }
    },
    updates: {
      check: async (force) => { try { return await invoke('updates:check', force) } catch { return { currentVersion: '1.1.0', latestVersion: '1.1.0', hasUpdate: false, releaseUrl: '', title: '', notes: '' } } }
    },
    storage: {
      stats: async () => { try { return await invoke('storage:stats') } catch { return { total: 0 } } },
      clean: async () => { try { return await invoke('storage:clean') } catch { return 0 } },
      prune: async () => { try { return await invoke('storage:prune') } catch { return 0 } }
    },
    modnames: { translate: async (names) => { try { return await invoke('modnames:translate', names) } catch { return {} } } },
    curseforge: {
      search: async (params) => { try { return await invoke('curseforge:search', params) } catch { return emptySearch } },
      categories: async (type) => { try { return await invoke('curseforge:categories', type) } catch { return [] } },
      project: async (id) => { try { return await invoke('curseforge:project', id) } catch { return null } },
      files: async (id, mcVersion, loader) => { try { return await invoke('curseforge:files', id, mcVersion, loader) } catch { return [] } },
      translations: async () => ({}),
      install: async (opts) => { return await invoke('curseforge:install', opts) },
      installToFolder: async (opts) => { return await invoke('curseforge:install-folder', opts) },
      installPack: async (opts) => { return await invoke('curseforge:install-pack', opts) }
    },
    java: {
      list: async () => { try { return await invoke('java:list') } catch { return [] } },
      scan: async () => { try { return await invoke('java:scan') } catch { return [] } },
      download: async (major) => { return await invoke('java:download', major) },
      delete: async (path) => { try { return await invoke('java:delete', path) } catch { return null } }
    },
    accounts: {
      list: async () => { try { return await invoke('accounts:list') } catch { return [] } },
      active: async () => { try { return await invoke('accounts:active') } catch { return null } },
      setActive: async (id) => { try { return await invoke('accounts:set-active', id) } catch { return null } },
      addOffline: async (username) => { try { return await invoke('accounts:add-offline', username) } catch { return null } },
      startMicrosoft: async () => { return await invoke('accounts:start-microsoft') },
      pollMicrosoft: async (flowId) => { try { return await invoke('accounts:poll-microsoft', flowId) } catch (error) { return { status: 'error', message: String(error.message || error) } } },
      cancelMicrosoft: async (flowId) => { try { return await invoke('accounts:cancel-microsoft', flowId) } catch { return null } },
      addAuthlib: async (server, username, password) => { return await invoke('accounts:add-authlib', server, username, password) },
      remove: async (id) => { try { return await invoke('accounts:remove', id) } catch { return null } },
      refresh: async (id) => { try { return await invoke('accounts:refresh', id) } catch { return null } }
    },
    servers: {
      list: async (instanceId) => { try { return await invoke('servers:list', instanceId) } catch { return [] } },
      add: async (instanceId, entry) => { try { return await invoke('servers:add', instanceId, entry) } catch { return [] } },
      remove: async (instanceId, ip) => { try { return await invoke('servers:remove', instanceId, ip) } catch { return [] } },
      ping: async (ip) => { try { return await invoke('servers:ping', ip) } catch { return { online: false } } }
    },
    logs: {
      files: async (instanceId) => { try { return await invoke('logs:files', instanceId) } catch { return [] } },
      read: async (path, maxBytes) => { try { return await invoke('logs:read', path, maxBytes) } catch { return '' } },
      latest: async (instanceId) => { try { return await invoke('logs:latest', instanceId) } catch { return [] } },
      analyze: async (instanceId) => { try { return await invoke('logs:analyze', instanceId) } catch { return null } },
      clearConsole: noop
    },
    news: {
      article: async (url) => { try { return await invoke('news:article', url) } catch { return '' } },
      list: async (force) => {
        let items
        try { items = await invoke('news:list', force === true) } catch { items = [] }
        return Array.isArray(items) ? items : []
      }
    },
    events: {
      onProgress: (cb) => on('progress', cb),
      onLog: (cb) => on('log', cb),
      onGameState: (cb) => on('game', cb),
      onSettings: (cb) => on('settings', cb),
      onAccounts: (cb) => on('accounts', cb),
      onInstances: (cb) => on('instances', cb),
      onToast: (cb) => on('toast', cb),
      onConsoleToggle: (cb) => on('console', cb),
      onMaximized: (cb) => on('maximized', cb)
    }
  }
  const dragStyle = document.createElement('style')
  dragStyle.textContent = 'html,body{overflow:hidden!important}.titlebar{-webkit-app-region:no-drag!important}.titlebar .brand,.titlebar-actions{-webkit-app-region:no-drag!important}'
  if (document.head) document.head.appendChild(dragStyle)
  else document.addEventListener('DOMContentLoaded', () => document.head.appendChild(dragStyle))
  document.addEventListener('mousedown', (event) => {
    if (event.button !== 0) return
    const target = event.target
    if (!(target instanceof Element)) return
    if (!target.closest('.titlebar')) return
    if (target.closest('button, input, select, textarea, a')) return
    post({ type: 'window', action: 'drag' })
  })
  document.addEventListener('dblclick', (event) => {
    const target = event.target
    if (!(target instanceof Element)) return
    if (!target.closest('.titlebar')) return
    if (target.closest('button, input, select, textarea, a')) return
    post({ type: 'window', action: 'maximize' })
  })
  window.aurora = api
  try {
    const resizeLayer = document.createElement('div')
    resizeLayer.id = 'aurora-resize-layer'
    resizeLayer.style.cssText = 'position:fixed;inset:0;pointer-events:none;z-index:2147483647'
    const guards = [
      ['left:0;top:0;bottom:0;width:8px', 'ew-resize', 10],
      ['right:0;top:0;bottom:0;width:8px', 'ew-resize', 11],
      ['left:0;right:0;top:0;height:8px', 'ns-resize', 12],
      ['left:0;right:0;bottom:0;height:8px', 'ns-resize', 15],
      ['left:0;top:0;width:12px;height:12px', 'nwse-resize', 13],
      ['right:0;top:0;width:12px;height:12px', 'nesw-resize', 14],
      ['left:0;bottom:0;width:12px;height:12px', 'nesw-resize', 16],
      ['right:0;bottom:0;width:12px;height:12px', 'nwse-resize', 17]
    ]
    for (const [css, cursor, hit] of guards) {
      const guard = document.createElement('div')
      guard.style.cssText = `position:fixed;${css};pointer-events:auto;background:transparent;cursor:${cursor}`
      let dragging = false
      guard.addEventListener('pointerdown', (event) => {
        if (event.button !== 0) return
        event.preventDefault()
        dragging = true
        guard.setPointerCapture(event.pointerId)
        post({ type: 'window', action: 'resize-start', hit, x: event.screenX, y: event.screenY })
      })
      guard.addEventListener('pointermove', (event) => {
        if (!dragging) return
        post({ type: 'window', action: 'resize-move', x: event.screenX, y: event.screenY })
      })
      const stop = (event) => {
        if (!dragging) return
        dragging = false
        try { guard.releasePointerCapture(event.pointerId) } catch {}
        post({ type: 'window', action: 'resize-end' })
      }
      guard.addEventListener('pointerup', stop)
      guard.addEventListener('pointercancel', stop)
      resizeLayer.appendChild(guard)
    }
    const attach = () => {
      const parent = document.body || document.documentElement
      if (parent) parent.appendChild(resizeLayer)
    }
    if (document.body) attach()
    else document.addEventListener('DOMContentLoaded', attach, { once: true })
  } catch (error) {
    console.error(error)
  }
})()
