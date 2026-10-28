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
    const timeoutMs = (channel.startsWith('install:') || channel === 'easytier:install' || channel === 'updates:download'
      || channel === 'curseforge:install-pack' || channel === 'modrinth:install-pack'
      || channel === 'curseforge:install' || channel === 'modrinth:install') ? 0 : 60000
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
    version: boot.version || '1.2.0',
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
    fabricapi: {
      versions: async (mcVersion) => { try { return await invoke('fabricapi:versions', mcVersion) } catch { return [] } }
    },
    updates: {
      check: async (force) => { try { return await invoke('updates:check', force) } catch { return { currentVersion: '1.1.0', latestVersion: '1.1.0', hasUpdate: false, releaseUrl: '', title: '', notes: '' } } },
      downloadAndApply: async (url, version) => { return await invoke('updates:download', url, version) }
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
      refresh: async (id) => { try { return await invoke('accounts:refresh', id) } catch { return null } },
      skin: async (payload) => { return await invoke('accounts:skin', payload) },
      uploadSkin: async (payload) => { return await invoke('accounts:upload-skin', payload) },
      authlibProfiles: async (id) => { return await invoke('accounts:authlib-profiles', id) },
      switchProfile: async (id, profileId) => { return await invoke('accounts:authlib-switch', id, profileId) },
      capes: async (id) => { return await invoke('accounts:capes', id) },
      setCape: async (id, capeId) => { return await invoke('accounts:set-cape', id, capeId) }
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
    easytier: {
      status: async () => { try { return await invoke('easytier:status') } catch { return null } },
      latency: async (endpoint) => { try { return await invoke('easytier:latency', endpoint) } catch { return { ok: false } } },
      install: async (force) => { try { return await invoke('easytier:install', force === true) } catch (error) { return { ok: false, error: String(error) } } },
      save: async (config) => { try { return await invoke('easytier:save', config) } catch { return null } },
      start: async (config) => { try { return await invoke('easytier:start', config) } catch (error) { return { ok: false, error: String(error) } } },
      stop: async () => { try { return await invoke('easytier:stop') } catch { return { ok: false } } },
      peers: async () => { try { return await invoke('easytier:peers') } catch { return '' } },
      lanScan: async () => { try { return await invoke('easytier:lan-scan') } catch (error) { return { ok: false, error: String(error) } } },
      firewall: async () => { try { return await invoke('easytier:firewall') } catch (error) { return { ok: false, error: String(error) } } },
      createInvite: async (input) => { try { return await invoke('easytier:create-invite', input) } catch (error) { return { ok: false, error: String(error) } } },
      joinInvite: async (code) => { try { return await invoke('easytier:join-invite', code) } catch (error) { return { ok: false, error: String(error) } } }
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

  // EasyTier multiplayer panel. It is injected into the existing sidebar so the
  // feature uses the launcher's native styles without changing the React bundle.
  try {
    const et = api.easytier
    let panel = null
    let modal = null
    let refreshTimer = null
    let latest = null
    let hiddenSiblings = []
    let previousActive = null

    const escapeHtml = (value) => String(value ?? '').replace(/[&<>"']/g, (ch) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[ch]))
    const setText = (el, text) => { if (el) el.textContent = text }
    const parsePeers = (text) => {
      const rows = []
      for (const raw of String(text || '').split('\n')) {
        const line = raw.trim()
        if (!line.startsWith('|') || line.includes('---')) continue
        // Keep empty cells: the public relay row has no IPv4, and filtering
        // empties shifted hostname/cost/latency into the wrong columns.
        const cells = line.split('|').map((cell) => cell.trim())
        if (cells[0] === '') cells.shift()
        if (cells[cells.length - 1] === '') cells.pop()
        if (cells.length < 4) continue
        if (/^ipv4$/i.test(cells[0]) || /hostname/i.test(cells[1] || '')) continue
        rows.push({
          ipv4: (cells[0] || '').replace(/\/\d+$/, ''),
          hostname: (cells[1] || '').trim(),
          cost: (cells[2] || '').trim(),
          latency: (cells[3] || '').trim()
        })
      }
      return rows
    }
    const renderPeers = (text) => {
      const container = document.getElementById('et-peers')
      if (!container) return
      const rows = parsePeers(text)
      if (rows.length === 0) {
        container.innerHTML = '<div style="padding:14px;border:1px dashed var(--line);border-radius:10px;color:var(--muted);font-size:12px">暂无房间成员，等待朋友加入…</div>'
        return
      }
      container.innerHTML = rows.map((row) => {
        const local = row.cost.toLowerCase() === 'local'
        const relay = /^PublicServer/i.test(row.hostname)
        const name = local ? '本机' : relay ? '公共服务器' : row.hostname
        const badge = local ? '本机' : relay ? '中继' : '玩家'
        const badgeColor = local ? 'var(--muted)' : relay ? 'var(--faint)' : 'var(--acc1)'
        const latency = row.latency && row.latency !== '-' ? `${escapeHtml(row.latency)} ms` : '—'
        return `<div style="display:flex;align-items:center;gap:12px;padding:10px 12px;border:1px solid var(--line);border-radius:10px;background:var(--panel)">
          <div style="width:30px;height:30px;border-radius:9px;display:grid;place-items:center;background:var(--panel-hover);color:${badgeColor};font-size:12px;font-weight:700;flex:none">${escapeHtml(name.slice(0, 1).toUpperCase())}</div>
          <div style="flex:1;min-width:0">
            <div style="display:flex;align-items:center;gap:8px;min-width:0">
              <span style="font-size:12.5px;font-weight:600;white-space:nowrap;overflow:hidden;text-overflow:ellipsis">${escapeHtml(name)}</span>
              <span style="font-size:10px;padding:1px 6px;border-radius:99px;border:1px solid var(--line);color:${badgeColor}">${badge}</span>
            </div>
            <div style="margin-top:2px;font-family:var(--font-mono);font-size:11px;color:var(--muted)">${escapeHtml(row.ipv4)}</div>
          </div>
          <div style="text-align:right;flex:none">
            <div style="font-size:12px;font-family:var(--font-mono);color:${local || relay ? 'var(--muted)' : 'var(--text)'}">${latency}</div>
            <div style="margin-top:2px;font-size:10px;color:var(--faint)">${escapeHtml(row.cost)}</div>
          </div>
        </div>`
      }).join('')
    }

    const openPanel = async () => {
      if (!panel) return
      const scroll = document.querySelector('.page-scroll')
      if (scroll) {
        if (panel.parentElement !== scroll) scroll.appendChild(panel)
        hiddenSiblings = Array.from(scroll.children).filter((child) => child !== panel)
        hiddenSiblings.forEach((child) => { child.style.display = 'none' })
      }
      panel.style.display = 'block'
      previousActive = document.querySelector('.sidebar .nav-item.active:not(#aurora-easytier-nav)')
      previousActive?.classList.remove('active')
      document.getElementById('aurora-easytier-nav')?.classList.add('active')
      await refresh()
      if (refreshTimer) clearInterval(refreshTimer)
      refreshTimer = setInterval(() => void refresh(true), 3500)
    }
    const closePanel = () => {
      if (!panel) return
      panel.style.display = 'none'
      document.getElementById('aurora-easytier-nav')?.classList.remove('active')
      if (previousActive && document.contains(previousActive)) previousActive.classList.add('active')
      previousActive = null
      hiddenSiblings.forEach((child) => { child.style.display = '' })
      hiddenSiblings = []
      if (refreshTimer) clearInterval(refreshTimer)
      refreshTimer = null
    }

    const refresh = async (quiet) => {
      const status = await et.status()
      if (!status) return
      latest = status
      const installed = status.installed === true
      const running = status.running === true
      const installButton = document.getElementById('et-install')
      const startButton = document.getElementById('et-start')
      const stopButton = document.getElementById('et-stop')
      const badge = document.getElementById('et-state')
      if (installButton) {
        installButton.textContent = installed ? '重新安装 EasyTier' : '安装 EasyTier'
        installButton.dataset.installed = installed ? '1' : '0'
      }
      if (startButton) startButton.disabled = !installed || running
      if (stopButton) stopButton.disabled = !running
      if (badge) {
        badge.textContent = running ? '已连接' : installed ? '未连接' : '未安装'
        badge.className = 'badge ' + (running ? 'ok' : installed ? 'warn' : '')
      }
      setText(document.getElementById('et-version'), installed ? `EasyTier ${status.version || ''}` : '尚未安装 EasyTier 运行文件')
      setText(document.getElementById('et-log'), status.log || '')
      // Do not restore the room name automatically: join mode must stay empty.
      const select = document.getElementById('et-server')
      if (select) {
        const servers = Array.isArray(status.servers) ? status.servers : []
        let current = select.value || status.server || ''
        if (!servers.some((server) => server.endpoint === current)) current = servers[0]?.endpoint || ''
        select.value = current
        const labelFor = (server) => server?.builtin
          ? (server.label || `${server.contributor || server.name} · ${server.info || ''} · ${server.requirements || ''}`)
          : '自定义服务器'
        const currentServer = servers.find((server) => server.endpoint === current)
        setText(document.getElementById('et-server-label'), labelFor(currentServer) || '选择公共服务器')
        const menu = document.getElementById('et-server-menu')
        if (menu) {
          menu.innerHTML = servers.map((server) => {
            const active = server.endpoint === current
            return `<button type="button" class="et-server-option" data-endpoint="${escapeHtml(server.endpoint)}" style="width:100%;display:flex;align-items:center;gap:8px;padding:9px 10px;border-radius:7px;text-align:left;font-size:12px;color:var(--text-soft);background:${active ? 'var(--panel-hover)' : 'transparent'}">
              <span style="flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap">${escapeHtml(labelFor(server))}</span>
              ${active ? '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="var(--acc1)" stroke-width="2.4"><path d="m5 12 4 4L19 6"/></svg>' : ''}
            </button>`
          }).join('')
          menu.querySelectorAll('.et-server-option').forEach((option) => {
            option.onmouseenter = () => { if (option.dataset.endpoint !== select.value) option.style.background = 'var(--panel-hover)' }
            option.onmouseleave = () => { if (option.dataset.endpoint !== select.value) option.style.background = 'transparent' }
            option.onclick = () => {
              select.value = option.dataset.endpoint || ''
              const picked = servers.find((server) => server.endpoint === select.value)
              setText(document.getElementById('et-server-label'), labelFor(picked) || '选择公共服务器')
              menu.style.display = 'none'
            }
          })
        }
      }
      if (!quiet && running) {
        const peers = await et.peers()
        renderPeers(peers)
      }
    }

    const createPanel = () => {
      panel = document.createElement('div')
      panel.className = 'page-enter'
      panel.style.cssText = 'display:none;padding:0;width:100%;height:100%;overflow:auto'
      panel.innerHTML = `
        <div class="modal wide" role="dialog" aria-label="EasyTier 联机">
          <div class="modal-head">
            <div><h3>联机</h3><p>EasyTier 虚拟局域网</p></div>
          </div>
          <div class="modal-body">
            <div class="card card-pad" style="margin-bottom:14px">
              <div style="display:flex;align-items:center;gap:10px;flex-wrap:wrap">
                <span class="badge" id="et-state">未安装</span>
                <span id="et-version" style="color:var(--muted);font-size:12px"></span>
                <span style="flex:1"></span>
                <button class="btn btn-ghost btn-sm" id="et-install">安装 EasyTier</button>
              </div>
              <div style="margin-top:10px;color:var(--warning);font-size:11px;line-height:1.6">
                下载或解压 EasyTier 时可能被杀毒软件误报，请先暂时关闭杀毒软件或加入信任。
              </div>
              <div id="et-progress-wrap" style="display:none;margin-top:12px">
                <div class="progress-track"><div class="progress-fill" id="et-progress-fill" style="width:0%"></div></div>
                <div id="et-progress-text" style="margin-top:6px;color:var(--muted);font-size:11.5px"></div>
              </div>
            </div>
            <div class="card card-pad" style="margin-bottom:14px">
              <div style="display:flex;align-items:center;gap:10px;flex-wrap:wrap">
                <label style="font-size:12.5px;font-weight:600;color:var(--text-soft)">公共服务器</label>
                <div id="et-server-wrap" style="position:relative;flex:1;min-width:240px">
                  <input type="hidden" id="et-server" />
                  <button type="button" id="et-server-button" class="select" style="width:100%;display:flex;align-items:center;justify-content:space-between;gap:10px;text-align:left;background-image:none;appearance:none;padding-right:13px">
                    <span id="et-server-label" style="overflow:hidden;text-overflow:ellipsis;white-space:nowrap"></span>
                    <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" style="flex:none;opacity:.65"><path d="m6 9 6 6 6-6"/></svg>
                  </button>
                  <div id="et-server-menu" style="display:none;position:absolute;left:0;right:0;top:calc(100% + 6px);z-index:40;padding:5px;border:1px solid var(--line);border-radius:10px;background:var(--panel-solid);box-shadow:0 12px 32px -18px rgba(0,0,0,.55);max-height:280px;overflow:auto"></div>
                </div>
                <button class="btn btn-soft btn-sm" id="et-ping">延迟检测</button>
                <span id="et-latency" style="font-size:12px;color:var(--muted);min-width:64px"></span>
              </div>
              <div class="input-row" style="margin-top:10px">
                <input class="input" id="et-custom" placeholder="自定义服务器，例如 tcp://1.2.3.4:11010" />
                <button class="btn btn-soft btn-sm" id="et-add-custom">添加</button>
              </div>
              <div style="margin-top:10px;color:var(--muted);font-size:11px;line-height:1.6">
                来源为 EasyTier 公共服务器。若对 EasyTier 节点造成太大影响，届时将关闭公共服务器的服务。
              </div>
            </div>
            <div class="card card-pad" style="margin-bottom:14px">
              <div style="display:flex;gap:6px;margin-bottom:12px">
                <button class="btn btn-sm" id="et-mode-create">创建房间</button>
                <button class="btn btn-ghost btn-sm" id="et-mode-join">加入房间</button>
              </div>
              <div class="input-row" style="margin-bottom:10px">
                <input class="input" id="et-name" placeholder="房间名称（创建时自动生成）" />
                <input class="input" id="et-invite" placeholder="粘贴邀请码" style="display:none" />
              </div>
              <div class="input-row" id="et-invite-box" style="margin-bottom:10px;display:none">
                <input class="input" id="et-invite-value" placeholder="邀请码" readonly />
                <button class="btn btn-soft btn-sm" id="et-invite-copy">复制邀请码</button>
              </div>
              <div style="display:flex;gap:8px;flex-wrap:wrap">
                <button class="btn btn-primary btn-sm" id="et-start">创建房间</button>
                <button class="btn btn-danger btn-sm" id="et-stop" disabled>断开</button>
                <button class="btn btn-ghost btn-sm" id="et-refresh">刷新</button>
              </div>
              <div style="margin-top:12px;color:var(--muted);font-size:11.5px;line-height:1.6" id="et-hint">
                创建房间后把房间名称和密码发给朋友，双方使用同一组公共服务器即可进入同一个虚拟局域网。Windows 首次使用可能需要允许 TUN 驱动或管理员权限。
              </div>
            </div>
            <div class="card card-pad" style="margin-bottom:14px">
              <div style="display:flex;align-items:center;gap:8px;margin-bottom:10px"><div style="font-weight:650;font-size:13px">房主连接地址</div></div>
              <div class="input-row" style="margin-bottom:10px">
                <input class="input" id="et-address" placeholder="虚拟 IP:局域网端口" readonly />
                <button class="btn btn-soft btn-sm" id="et-scan">扫描局域网</button>
                <button class="btn btn-primary btn-sm" id="et-copy" disabled>复制地址</button>
                <button class="btn btn-ghost btn-sm" id="et-firewall">修复防火墙</button>
              </div>
              <div id="et-scan-status" style="color:var(--muted);font-size:11.5px;line-height:1.6">进入世界并点击“对局域网开放”后，点击扫描。</div>
            </div>
            <div class="card card-pad">
              <div style="display:flex;align-items:center;gap:8px;margin-bottom:8px"><div style="font-weight:650;font-size:13px">房间成员</div><button class="btn btn-ghost btn-xs" id="et-peer-refresh">刷新</button></div>
              <div id="et-peers" style="display:flex;flex-direction:column;gap:8px;max-height:240px;overflow:auto"></div>
              <pre id="et-log" style="margin:10px 0 0;max-height:120px;overflow:auto;white-space:pre-wrap;font-family:var(--font-mono);font-size:10.5px;color:var(--faint)"></pre>
            </div>
          </div>
        </div>`
      document.body.appendChild(panel)
      modal = panel.querySelector('.modal')
      if (modal) modal.style.cssText = 'width:100%;max-width:none;max-height:none;border:0;box-shadow:none;background:transparent;padding:0;margin:0'
      const serverButton = document.getElementById('et-server-button')
      const serverMenu = document.getElementById('et-server-menu')
      if (serverButton && serverMenu) {
        serverButton.addEventListener('click', (event) => {
          event.stopPropagation()
          serverMenu.style.display = serverMenu.style.display === 'block' ? 'none' : 'block'
        })
        document.addEventListener('click', (event) => {
          const wrap = document.getElementById('et-server-wrap')
          if (wrap && event.target instanceof Node && !wrap.contains(event.target)) serverMenu.style.display = 'none'
        })
      }
      const closeButton = document.getElementById('et-close')
      if (closeButton) closeButton.onclick = closePanel
      const doneButton = document.getElementById('et-done')
      if (doneButton) doneButton.onclick = closePanel
      document.getElementById('et-install').onclick = async () => {
        const button = document.getElementById('et-install')
        const wrap = document.getElementById('et-progress-wrap')
        const fill = document.getElementById('et-progress-fill')
        const label = document.getElementById('et-progress-text')
        button.disabled = true
        button.textContent = '正在下载…'
        if (wrap) wrap.style.display = 'block'
        const timer = setInterval(async () => {
          const status = await et.status()
          if (!status) return
          const progress = Math.max(0, Math.min(100, Number(status.installProgress) || 0))
          if (fill) fill.style.width = progress + '%'
          if (label) label.textContent = `${status.installDetail || '正在安装 EasyTier'}（${Math.round(progress)}%）`
        }, 500)
        try {
          const result = await et.install(button.dataset.installed === '1')
          if (!result || result.ok !== true) alert(result?.error || 'EasyTier 安装失败')
          await refresh()
        } finally {
          clearInterval(timer)
          button.disabled = false
          button.textContent = '重新安装 EasyTier'
          setTimeout(() => { if (wrap) wrap.style.display = 'none' }, 1800)
        }
      }
      document.getElementById('et-ping').onclick = async () => {
        const endpoint = document.getElementById('et-server').value
        setText(document.getElementById('et-latency'), '检测中…')
        const result = await et.latency(endpoint)
        setText(document.getElementById('et-latency'), result?.ok ? `${result.latency} ms` : '不可达')
      }
      document.getElementById('et-add-custom').onclick = async () => {
        const input = document.getElementById('et-custom')
        const endpoint = input.value.trim()
        if (!endpoint) return
        const servers = latest?.servers ?? []
        const custom = servers.filter((server) => server.builtin !== true).map((server) => ({ endpoint: server.endpoint }))
        custom.push({ endpoint })
        await et.save({ customServers: custom, server: endpoint })
        input.value = ''
        await refresh()
      }
      let roomMode = 'create'
      const generateRoom = () => 'aurora-' + Math.random().toString(36).slice(2, 10)
      const setMode = (mode) => {
        roomMode = mode
        const name = document.getElementById('et-name')
        const invite = document.getElementById('et-invite')
        const box = document.getElementById('et-invite-box')
        const create = document.getElementById('et-mode-create')
        const join = document.getElementById('et-mode-join')
        const start = document.getElementById('et-start')
        const hint = document.getElementById('et-hint')
        if (mode === 'create') {
          if (!name.value || name.value.startsWith('aurora-')) name.value = generateRoom()
          name.style.display = ''
          invite.style.display = 'none'
          box.style.display = 'none'
          create.className = 'btn btn-sm'
          join.className = 'btn btn-ghost btn-sm'
          start.textContent = '创建房间'
          hint.textContent = '创建房间后会生成邀请码，把邀请码发给朋友即可，不需要再单独发送房间名和密码。'
        } else {
          name.style.display = 'none'
          invite.style.display = ''
          box.style.display = 'none'
          invite.value = ''
          create.className = 'btn btn-ghost btn-sm'
          join.className = 'btn btn-sm'
          start.textContent = '验证并加入'
          hint.textContent = '粘贴朋友发来的邀请码。加入前会先验证房主是否在线、邀请码当前是否可用。'
        }
      }
      document.getElementById('et-invite-copy').onclick = async () => {
        const value = document.getElementById('et-invite-value').value
        if (!value) return
        try { await navigator.clipboard.writeText(value) } catch {
          const input = document.getElementById('et-invite-value')
          input.select()
          document.execCommand('copy')
        }
        const button = document.getElementById('et-invite-copy')
        button.textContent = '已复制'
        setTimeout(() => { button.textContent = '复制邀请码' }, 1500)
      }
      document.getElementById('et-scan').onclick = async () => {
        const status = document.getElementById('et-scan-status')
        const input = document.getElementById('et-address')
        const copy = document.getElementById('et-copy')
        if (status) status.textContent = '正在扫描游戏局域网端口…'
        const result = await et.lanScan()
        if (result?.ok) {
          input.value = result.address
          copy.disabled = false
          if (status) status.textContent = `已发现端口 ${result.port}，虚拟 IP ${result.ip || '未识别'}`
        } else {
          input.value = ''
          copy.disabled = true
          if (status) status.textContent = result?.error || '没有发现局域网端口'
        }
      }
      document.getElementById('et-firewall').onclick = async () => {
        const button = document.getElementById('et-firewall')
        const status = document.getElementById('et-scan-status')
        button.disabled = true
        button.textContent = '等待 UAC…'
        try {
          const result = await et.firewall()
          if (status) status.textContent = result?.ok ? '已添加 Java 和 EasyTier 防火墙规则' : (result?.error || '防火墙规则添加失败')
        } finally {
          button.disabled = false
          button.textContent = '修复防火墙'
        }
      }
      document.getElementById('et-copy').onclick = async () => {
        const input = document.getElementById('et-address')
        const button = document.getElementById('et-copy')
        if (!input.value) return
        try {
          await navigator.clipboard.writeText(input.value)
          button.textContent = '已复制'
          setTimeout(() => { button.textContent = '复制地址' }, 1500)
        } catch {
          input.select()
          document.execCommand('copy')
        }
      }
      document.getElementById('et-mode-create').onclick = () => setMode('create')
      document.getElementById('et-mode-join').onclick = () => setMode('join')
      document.getElementById('et-peer-refresh').onclick = async () => {
        const peers = await et.peers()
        renderPeers(peers)
      }
      setMode('create')
      document.getElementById('et-start').onclick = async () => {
        const hint = document.getElementById('et-hint')
        if (roomMode === 'join') {
          const code = document.getElementById('et-invite').value.trim()
          if (!code) { alert('请粘贴邀请码'); return }
          hint.textContent = '正在验证邀请码并尝试加入房间…'
          const result = await et.joinInvite(code)
          if (!result || result.ok !== true) {
            alert(result?.error || '邀请码当前不可用')
            hint.textContent = '邀请码验证失败，请确认房主已创建房间并保持在线。'
          } else {
            const box = document.getElementById('et-invite-box')
            box.style.display = 'flex'
            document.getElementById('et-invite-value').value = code
            hint.textContent = result.warning || ('验证通过，已加入房间：' + (result.networkName || ''))
          }
          await refresh()
          return
        }
        if (!document.getElementById('et-name').value.trim())
          document.getElementById('et-name').value = generateRoom()
        const invite = await et.createInvite({
          networkName: document.getElementById('et-name').value.trim(),
          server: document.getElementById('et-server').value
        })
        if (!invite || invite.ok !== true) { alert(invite?.error || '生成邀请码失败'); return }
        const result = await et.start({
          networkName: invite.networkName,
          networkSecret: invite.networkSecret,
          server: document.getElementById('et-server').value
        })
        if (!result || result.ok !== true) { alert(result?.error || '启动失败'); return }
        document.getElementById('et-name').value = invite.networkName
        const box = document.getElementById('et-invite-box')
        box.style.display = 'flex'
        document.getElementById('et-invite-value').value = invite.code
        hint.textContent = '房间已创建，复制邀请码发给朋友即可。'
        await refresh()
      }
      document.getElementById('et-stop').onclick = async () => { await et.stop(); await refresh() }
      document.getElementById('et-refresh').onclick = () => void refresh()
    }

    const mount = () => {
      const sidebar = document.querySelector('.sidebar')
      if (!sidebar) return
      if (!panel) createPanel()
      if (!document.getElementById('aurora-easytier-nav')) {
        const button = document.createElement('button')
        button.id = 'aurora-easytier-nav'
        button.className = 'nav-item'
        button.innerHTML = '<span style="width:19px;height:19px;display:grid;place-items:center"><svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="5" r="2.4"/><circle cx="5" cy="19" r="2.4"/><circle cx="19" cy="19" r="2.4"/><path d="M12 7.4v4.1M12 11.5 6.5 16.8M12 11.5l5.5 5.3"/></svg></span><span>联机</span>'
        button.addEventListener('click', () => void openPanel())
        const otherSection = sidebar.querySelectorAll('.nav-section-label')[1]
        if (otherSection) sidebar.insertBefore(button, otherSection)
        else sidebar.insertBefore(button, sidebar.querySelector('.sidebar-spacer, .sidebar-foot') || null)
      }
      if (!sidebar.dataset.etNavBound) {
        sidebar.dataset.etNavBound = '1'
        sidebar.addEventListener('click', (event) => {
          if (!(event.target instanceof Element)) return
          if (event.target.closest('#aurora-easytier-nav')) return
          closePanel()
        })
      }
    }

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', mount, { once: true })
    else mount()
    const observer = new MutationObserver(mount)
    if (document.body) observer.observe(document.body, { childList: true, subtree: true })
    setTimeout(mount, 300)
    setInterval(mount, 1200)
  } catch (error) {
    console.error('EasyTier panel failed', error)
  }
})()

// Consistent dropdown menu for every native <select class="select">.
(function () {
  const styleId = 'aurora-select-style'
  if (!document.getElementById(styleId)) {
    const style = document.createElement('style')
    style.id = styleId
    style.textContent = `
      select.select{appearance:none!important;-webkit-appearance:none!important;background-image:url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='12' height='12' viewBox='0 0 24 24' fill='none' stroke='%23999' stroke-width='2'%3E%3Cpath d='m6 9 6 6 6-6'/%3E%3C/svg%3E")!important;background-position:right 13px center!important;background-repeat:no-repeat!important;background-size:12px!important;padding-right:34px!important;pointer-events:none!important}
      .aurora-select-menu{position:fixed;z-index:1000;min-width:160px;max-height:300px;overflow:auto;padding:5px;border:1px solid var(--line);border-radius:10px;background:var(--panel-solid);box-shadow:0 12px 32px -18px rgba(0,0,0,.55)}
      .aurora-select-option{display:flex;align-items:center;justify-content:space-between;gap:8px;width:100%;padding:9px 10px;border-radius:7px;text-align:left;font-size:12px;color:var(--text-soft);background:transparent;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
      .aurora-select-option:hover{background:var(--panel-hover);color:var(--text)}
      .aurora-select-option.active{background:var(--panel-hover);color:var(--text)}
    `
    document.head.appendChild(style)
  }

  let menu = null
  let current = null
  const close = () => {
    if (menu) menu.remove()
    menu = null
    current = null
  }
  const open = (select) => {
    close()
    const options = Array.from(select.options)
    if (!options.length) return
    current = select
    menu = document.createElement('div')
    menu.className = 'aurora-select-menu'
    for (const option of options) {
      if (option.disabled) continue
      const row = document.createElement('button')
      row.type = 'button'
      row.className = 'aurora-select-option' + (option.value === select.value ? ' active' : '')
      row.textContent = option.textContent || option.label || option.value
      row.addEventListener('click', (event) => {
        event.preventDefault()
        event.stopPropagation()
        select.value = option.value
        select.dispatchEvent(new Event('input', { bubbles: true }))
        select.dispatchEvent(new Event('change', { bubbles: true }))
        close()
      })
      menu.appendChild(row)
    }
    document.body.appendChild(menu)
    const rect = select.getBoundingClientRect()
    const width = Math.max(rect.width, 160)
    menu.style.width = Math.min(width, window.innerWidth - 16) + 'px'
    menu.style.left = Math.min(Math.max(8, rect.left), window.innerWidth - width - 8) + 'px'
    const height = Math.min(menu.scrollHeight, 300)
    menu.style.top = (rect.bottom + height + 8 > window.innerHeight ? Math.max(8, rect.top - height - 6) : rect.bottom + 6) + 'px'
  }

  document.addEventListener('pointerdown', (event) => {
    if (!(event.target instanceof Element)) return
    const select = event.target.closest('select.select')
    if (!select) return
    event.preventDefault()
    event.stopPropagation()
    open(select)
  }, true)

  document.addEventListener('mousedown', (event) => {
    if (event.target instanceof Element && event.target.closest('select.select')) event.preventDefault()
  }, true)
  document.addEventListener('pointerdown', (event) => {
    if (!menu) return
    if (event.target instanceof Node && menu.contains(event.target)) return
    close()
  }, true)
  document.addEventListener('keydown', (event) => {
    if (event.key === 'Escape') close()
  })
  window.addEventListener('click', (event) => {
    if (event.target instanceof Element && event.target.closest('select.select')) event.preventDefault()
  }, true)
  document.addEventListener('pointerdown', (event) => {
    if (event.target instanceof Element && event.target.closest('select.select')) return
    const x = event.clientX, y = event.clientY
    for (const select of document.querySelectorAll('select.select')) {
      const rect = select.getBoundingClientRect()
      if (rect.width <= 0 || rect.height <= 0) continue
      if (x < rect.left || x > rect.right || y < rect.top || y > rect.bottom) continue
      event.preventDefault()
      event.stopPropagation()
      open(select)
      break
    }
  }, true)
  window.addEventListener('resize', close)
  document.addEventListener('scroll', (event) => {
    if (menu && event.target instanceof Node && menu.contains(event.target)) return
    close()
  }, true)
})()