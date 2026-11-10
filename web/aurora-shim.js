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
  let dropOverlay = null
  const updateDropOverlay = (active) => {
    if (!active) {
      if (dropOverlay) { dropOverlay.remove(); dropOverlay = null }
      return
    }
    if (dropOverlay) return
    dropOverlay = document.createElement('div')
    dropOverlay.id = 'aurora-drop-overlay'
    dropOverlay.style.cssText = 'position:fixed;inset:0;z-index:2147483000;display:flex;flex-direction:column;align-items:center;justify-content:center;gap:10px;background:rgba(8,10,14,.62);backdrop-filter:blur(3px);color:#f4f7fb;pointer-events:none;font-family:inherit;'
    const icon = document.createElement('div')
    icon.style.cssText = 'width:56px;height:56px;border-radius:16px;border:2px dashed rgba(255,255,255,.55);display:flex;align-items:center;justify-content:center;font-size:26px;'
    icon.textContent = '📦'
    const title = document.createElement('div')
    title.textContent = '松开安装整合包'
    title.style.cssText = 'font-size:24px;font-weight:700;letter-spacing:1px;'
    const hint = document.createElement('div')
    hint.textContent = '支持 .zip / .mrpack / CurseForge 整合包'
    hint.style.cssText = 'font-size:13px;color:rgba(230,238,250,.75);'
    dropOverlay.appendChild(icon)
    dropOverlay.appendChild(title)
    dropOverlay.appendChild(hint)
    document.body.appendChild(dropOverlay)
  }
  const pending = new Map()
  let nextInvokeId = 1
  if (window.chrome && window.chrome.webview) {
    window.chrome.webview.addEventListener('message', (event) => {
      const message = event.data
      if (!message) return
      if (message.type === 'event') {
        if (message.name === 'drag-state') { updateDropOverlay(Boolean(message.payload && message.payload.active)); return }
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
    // No frontend-side timeout: long operations (asset downloads, installers,
    // first launch) legitimately run for minutes and a rejected promise here
    // only produces a misleading "backend timeout" toast while the backend
    // keeps working.
    pending.set(id, {
      resolve: (value) => resolve(value),
      reject: (error) => reject(error)
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
      setOnlineMode: async (id, enabled) => { try { return await invoke('instances:online-mode', id, enabled) } catch { return false } },
      remove: async (id, deleteFiles) => { try { return await invoke('instances:remove', id, deleteFiles) } catch { return null } },
      copy: async (id, name) => { try { return await invoke('instances:copy', id, name) } catch { return null } },
      size: async (id) => { try { return await invoke('instances:size', id) } catch { return { size: 0 } } },
      openFolder: async (id, sub) => { try { return await invoke('instances:open-folder', id, sub) } catch { return null } },
      export: async (id) => { try { return await invoke('instances:export', id) } catch { return { path: '' } } },
      exportPack: async (options) => { try { return await invoke('instances:export-pack', options) } catch { return { path: '', size: 0, files: 0 } } },
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
    mcmodEnglish: async (query) => { try { return await invoke('mcmod:english', query) } catch { return '' } },
    skin: {
      install: (instanceId) => invoke('skin:install', instanceId)
    },
    mcmod: {
      search: async (query, alt) => { try { return await invoke('mcmod:search', query, alt) } catch { return '' } }
    },
    modrinth: {
      search: async (params) => { try { if (params && params.query && /[\u4e00-\u9fff]/.test(params.query)) { const en = await invoke('mcmod:english', params.query).catch(() => ''); if (en) params = { ...params, query: en } } return await invoke('modrinth:search', params) } catch { return emptySearch } },
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
    memory: {
      optimize: async () => { try { return await invoke('memory:optimize') } catch { return { freed: 0 } } }
    },

    storage: {
      stats: async () => { try { return await invoke('storage:stats') } catch { return { total: 0 } } },
      clean: async () => { try { return await invoke('storage:clean') } catch { return 0 } },
      prune: async () => { try { return await invoke('storage:prune') } catch { return 0 } }
    },
    modnames: { translate: async (names) => { try { return await invoke('modnames:translate', names) } catch { return {} } } },
    mods: {
      dependencies: async (input) => { try { return await invoke('mods:dependencies', input) } catch { return [] } },
    },
    curseforge: {
      search: async (params) => { try { if (params && params.query && /[\u4e00-\u9fff]/.test(params.query)) { const en = await invoke('mcmod:english', params.query).catch(() => ''); if (en) params = { ...params, query: en } } return await invoke('curseforge:search', params) } catch { return emptySearch } },
      categories: async (type) => { try { return await invoke('curseforge:categories', type) } catch { return [] } },
      project: async (id) => { try { return await invoke('curseforge:project', id) } catch { return null } },
      files: async (id, mcVersion, loader) => { try { return await invoke('curseforge:files', id, mcVersion, loader) } catch { return [] } },
      fileCount: async (id) => { try { return await invoke('curseforge:file-count', id) } catch { return 0 } },
      filesPage: async (id, index, pageSize) => { try { return await invoke('curseforge:files-page', id, index, pageSize) } catch { return [] } },
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
    terracotta: {
      install: async (force) => { try { return await invoke('terracotta:install', force === true) } catch (error) { return { ok: false, error: String(error) } } },
      start: async () => { try { return await invoke('terracotta:start') } catch (error) { return { ok: false, error: String(error) } } },
      state: async () => { try { return await invoke('terracotta:state') } catch { return { state: 'not-running' } } },
      host: async (player) => { try { return await invoke('terracotta:host', player) } catch (error) { return { ok: false, error: String(error) } } },
      join: async (room, player) => { try { return await invoke('terracotta:join', room, player) } catch (error) { return { ok: false, error: String(error) } } },
      reset: async () => { try { return await invoke('terracotta:reset') } catch { return { ok: false } } },
      stop: async () => { try { return await invoke('terracotta:stop') } catch { return { ok: false } } }
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
      onLogs: (cb) => on('logs', cb),
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

  const openExportPack = (instance) => {
    if (!instance || !instance.id) return
    const overlay = document.createElement('div')
    overlay.style.cssText = 'position:fixed;inset:0;z-index:2147483000;background:rgba(0,0,0,.45);display:flex;align-items:center;justify-content:center;padding:24px'
    const card = document.createElement('div')
    card.style.cssText = 'width:560px;max-height:86vh;overflow:auto;background:var(--panel);color:var(--text);border:1px solid var(--line);border-radius:12px;padding:20px;font:13px/1.5 system-ui,Segoe UI,sans-serif;box-shadow:0 24px 60px rgba(0,0,0,.45)'
    card.innerHTML = ''
    const title = document.createElement('div')
    title.textContent = '导出整合包'
    title.style.cssText = 'font-size:16px;font-weight:600;margin-bottom:14px'
    card.appendChild(title)
    const label = (text) => {
      const el = document.createElement('div')
      el.textContent = text
      el.style.cssText = 'font-size:12px;color:var(--muted);margin:12px 0 4px'
      card.appendChild(el)
      return el
    }
    const input = (placeholder, value) => {
      const el = document.createElement('input')
      el.className = 'input'
      el.placeholder = placeholder
      el.value = value || ''
      el.style.cssText = 'width:100%;box-sizing:border-box;padding:7px 9px;border-radius:7px;border:1px solid var(--line);background:var(--bg);color:var(--text);outline:none'
      card.appendChild(el)
      return el
    }
    label('导出格式')
    const formats = [['mrpack', 'Modrinth 整合包 (.mrpack)'], ['zip', 'Zip 压缩包 (.zip，内容与 mrpack 相同)']]
    const formatInputs = []
    formats.forEach(([value, text], index) => {
      const row = document.createElement('label')
      row.style.cssText = 'display:flex;align-items:center;gap:8px;padding:6px 2px;cursor:pointer'
      const radio = document.createElement('input')
      radio.type = 'radio'
      radio.name = 'aurora-export-format'
      radio.value = value
      radio.checked = index === 0
      formatInputs.push(radio)
      row.appendChild(radio)
      const span = document.createElement('span')
      span.textContent = text
      row.appendChild(span)
      card.appendChild(row)
    })
    label('包名称')
    const nameInput = input('实例名称', instance.name || '')
    label('包版本号')
    const versionInput = input('1.0.0', '1.0.0')
    label('作者（可留空）')
    const authorInput = input('作者', '')
    label('简介（可留空）')
    const summaryInput = input('简介', '')
    label('导出到')
    const folderRow = document.createElement('div')
    folderRow.style.cssText = 'display:flex;gap:8px;align-items:center'
    const folderInput = document.createElement('input')
    folderInput.className = 'input'
    folderInput.placeholder = '默认：.aurora/packs'
    folderInput.style.cssText = 'flex:1;box-sizing:border-box;padding:7px 9px;border-radius:7px;border:1px solid var(--line);background:var(--bg);color:var(--text);outline:none'
    const choose = document.createElement('button')
    choose.className = 'btn'
    choose.textContent = '选择文件夹'
    choose.style.cssText = 'padding:7px 12px;border-radius:7px;border:1px solid var(--line);background:var(--panel-hover);color:var(--text);cursor:pointer'
    choose.addEventListener('click', async () => {
      const picked = await api.app.pickDirectory('选择导出文件夹', folderInput.value)
      const path = picked && picked.paths && picked.paths.length ? picked.paths[0] : ''
      if (path) folderInput.value = path
    })
    const openButton = document.createElement('button')
    openButton.className = 'btn'
    openButton.textContent = '打开'
    openButton.style.cssText = choose.style.cssText
    openButton.addEventListener('click', () => { if (folderInput.value) api.app.openPath(folderInput.value) })
    folderRow.appendChild(folderInput)
    folderRow.appendChild(choose)
    folderRow.appendChild(openButton)
    card.appendChild(folderRow)
    const status = document.createElement('div')
    status.style.cssText = 'margin-top:12px;font-size:12px;color:var(--muted);min-height:18px;word-break:break-all'
    card.appendChild(status)
    const actions = document.createElement('div')
    actions.style.cssText = 'display:flex;justify-content:flex-end;gap:8px;margin-top:16px'
    const cancel = document.createElement('button')
    cancel.textContent = '取消'
    cancel.style.cssText = 'padding:8px 16px;border-radius:7px;border:1px solid var(--line);background:transparent;color:var(--text);cursor:pointer'
    const confirm = document.createElement('button')
    confirm.textContent = '开始导出'
    confirm.style.cssText = 'padding:8px 18px;border-radius:7px;border:none;background:var(--acc1);color:#1a1a1a;font-weight:600;cursor:pointer'
    actions.appendChild(cancel)
    actions.appendChild(confirm)
    card.appendChild(actions)
    cancel.addEventListener('click', () => overlay.remove())
    overlay.addEventListener('click', (event) => { if (event.target === overlay) overlay.remove() })
    confirm.addEventListener('click', async () => {
      const format = (formatInputs.find((item) => item.checked) || formatInputs[0]).value
      confirm.disabled = true
      confirm.style.opacity = '.6'
      status.textContent = '正在导出…'
      try {
        const result = await api.instances.exportPack({
          id: instance.id,
          format,
          name: nameInput.value.trim() || instance.name || '',
          version: versionInput.value.trim() || '1.0.0',
          author: authorInput.value.trim(),
          summary: summaryInput.value.trim(),
          outputDir: folderInput.value.trim()
        })
        status.textContent = result && result.path ? '已导出到 ' + result.path : '导出完成'
        confirm.textContent = '完成'
        confirm.disabled = false
        confirm.style.opacity = '1'
        confirm.onclick = () => overlay.remove()
      } catch (error) {
        status.textContent = '导出失败：' + (error && error.message ? error.message : error)
        confirm.disabled = false
        confirm.style.opacity = '1'
      }
    })
    overlay.appendChild(card)
    document.body.appendChild(overlay)
  }

  window.aurora = api
  window.auroraPacks = { openExport: openExportPack }
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
      // Refresh before showing so status text does not visibly change after mount.
      await refresh()
      panel.style.display = 'block'
      panel.classList.remove('page-enter')
      void panel.offsetWidth
      panel.classList.add('page-enter')
      if (refreshTimer) clearInterval(refreshTimer)
      refreshTimer = setInterval(() => void refresh(true), 3500)
    }
    const closePanel = () => {
      if (!panel) return
      panel.style.display = 'none'
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

    window.__etToggleOnlineMode = async (checked) => {
      try {
        const settings = await api.settings.get();
        const list = await api.instances.list();
        const instance = (list || []).find((item) => item.id === settings?.lastSelectedInstanceId) || (list || [])[0];
        if (!instance) return;
        await api.instances.setOnlineMode(instance.id, !checked);
      } catch {}
    };
    const createPanel = () => {
      panel = document.createElement('div')
      panel.className = 'page-enter'
      panel.id = 'aurora-easytier-panel'
      panel.style.cssText = 'display:none;padding:0 2px 0 0;width:100%;height:auto;min-height:calc(100vh - 96px);overflow:visible'
      panel.innerHTML = `
        <div class="modal wide" role="dialog" aria-label="EasyTier 联机" style="width:100%;max-width:none;max-height:none;overflow:visible;box-shadow:none;border:none;background:transparent">
          <div class="page-head">
            <div class="titles"><h1>联机</h1><p>EasyTier 虚拟局域网</p></div>
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
              <div style="margin-top:10px;display:flex;align-items:center;gap:8px;color:var(--text-soft);font-size:12px">
                <input type="checkbox" id="et-offline" onchange="window.__etToggleOnlineMode && window.__etToggleOnlineMode(this.checked)" />
                <label for="et-offline">关闭正版验证（允许离线玩家加入）</label>
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
      if (!window.__auroraEasyTierBound) {
        window.__auroraEasyTierBound = true
        const enhanceTerracotta = () => {
    const panel = document.getElementById('aurora-easytier-panel')
    if (!panel || panel.dataset.terracottaReady) return
    panel.dataset.terracottaReady = '1'
    const body = panel.querySelector('.modal-body')
    const head = panel.querySelector('.page-head')
    if (!body || !head) return
    const etWrap = document.createElement('div')
    etWrap.id = 'online-easytier'
    while (body.firstChild) etWrap.appendChild(body.firstChild)
    body.appendChild(etWrap)
    const subtitle = head.querySelector('.titles p')
    if (subtitle) subtitle.id = 'online-tab-subtitle'
    const actions = document.createElement('div')
    actions.className = 'page-actions'
    actions.style.cssText = 'display:flex;gap:8px'
    const etTab = document.createElement('button')
    etTab.className = 'btn btn-sm btn-primary'
    etTab.id = 'online-tab-et'
    etTab.textContent = 'EasyTier'
    const tcTab = document.createElement('button')
    tcTab.className = 'btn btn-sm btn-soft'
    tcTab.id = 'online-tab-tc'
    tcTab.textContent = '陶瓦联机'
    actions.append(etTab, tcTab)
    head.appendChild(actions)

    const tc = document.createElement('div')
    tc.id = 'online-tc'
    tc.style.display = 'none'
    tc.innerHTML = [
      '<div class="card card-pad" style="margin-bottom:14px">',
      '  <div style="display:flex;align-items:center;gap:10px;flex-wrap:wrap">',
      '    <span class="badge" id="tc-state">未启动</span>',
      '    <span id="tc-version" style="color:var(--muted);font-size:12px"></span>',
      '    <button class="btn btn-sm" id="tc-install">安装 / 更新</button>',
      '    <button class="btn btn-sm btn-soft" id="tc-stop">退出联机</button>',
      '  </div>',
      '  <div class="sub" id="tc-hint" style="margin-top:8px">陶瓦联机与 HMCL 使用同一套房间码，两边可以互相加入。</div>',
      '</div>',
      '<div class="card card-pad" style="margin-bottom:14px">',
      '  <div style="display:flex;align-items:center;gap:10px;flex-wrap:wrap">',
      '    <button class="btn btn-sm btn-primary" id="tc-host">创建房间</button>',
      '    <input class="input" id="tc-room" placeholder="房间邀请码（如 U/XXXX-XXXX-XXXX-XXXX）" style="flex:1;min-width:220px" />',
      '    <button class="btn btn-sm btn-soft" id="tc-join">加入房间</button>',
      '  </div>',
      '  <div id="tc-room-box" style="display:none;margin-top:12px;align-items:center;gap:10px;flex-wrap:wrap">',
      '    <span style="color:var(--muted);font-size:12px">邀请码</span>',
      '    <code id="tc-room-code" style="font-family:Microsoft YaHei UI, Segoe UI, system-ui, sans-serif;font-size:17px;font-weight:700;letter-spacing:1.5px"></code>',
      '    <button class="btn btn-sm btn-soft" id="tc-copy">复制</button>',
      '  </div>',
      '  <div id="tc-url-box" style="display:none;margin-top:10px;color:var(--muted);font-size:12px"></div>',
      '  <div id="tc-members" class="sub" style="margin-top:10px"></div>',
      '  <div style="margin-top:12px"><button class="btn btn-sm btn-soft" id="tc-reset">返回待机</button></div>',
      '</div>',
      '<div class="card card-pad"><div class="sub">房主需要先在游戏里对局域网开放，陶瓦会扫描到端口；加入方在多人游戏列表里双击「陶瓦联机大厅」即可进入。</div><div class="sub" style="margin-top:6px;color:var(--faint);font-size:11px">陶瓦联机 Terracotta · by burningtnt · AGPL-3.0</div></div>'
    ].join('')
    body.appendChild(tc)

    let timer = null
    let busy = false
    const el = (id) => document.getElementById(id)
    const stateNames = {
      'not-running': '未启动', waiting: '待机', 'host-scanning': '正在扫描本机 Minecraft 端口',
      'host-starting': '正在创建房间', 'host-ok': '房间已就绪',
      'guest-connecting': '正在连接房主', 'guest-starting': '正在加入房间', 'guest-ok': '已加入房间',
      exception: '联机异常'
    }
    const exceptionNames = { 0: '无法连接房主', 1: '与房主的连接中断', 2: '加入方 EasyTier 崩溃', 3: '房主 EasyTier 崩溃', 4: '房主端 Minecraft 服务器失联', 5: '脚手架协议错误' }
    const render = (state) => {
      if (!state) return
      el('tc-state').textContent = stateNames[state.state] || state.state
      if (state.state === 'host-ok' && state.room) {
        el('tc-room-box').style.display = 'flex'
        el('tc-room-code').textContent = state.room
      } else if (state.state === 'host-starting' && state.room) {
        el('tc-room-box').style.display = 'flex'
        el('tc-room-code').textContent = state.room
      } else {
        el('tc-room-box').style.display = 'none'
      }
      if (state.state === 'guest-ok' && state.url) {
        el('tc-url-box').style.display = 'block'
        el('tc-url-box').textContent = '备用直连地址：' + state.url
      } else {
        el('tc-url-box').style.display = 'none'
      }
      const members = Array.isArray(state.profiles) ? state.profiles : []
      const box = el('tc-members')
      if (!members.length) {
        box.textContent = ''
      } else {
        box.innerHTML = '<div style="font-weight:600;margin-bottom:6px">房间成员（' + members.length + '）</div>' +
          members.map((m) => '<div style="display:flex;align-items:center;gap:8px;padding:6px 0;border-top:1px solid var(--line)">' +
            '<span style="width:22px;height:22px;border-radius:50%;display:inline-flex;align-items:center;justify-content:center;background:var(--panel);font-size:11px;font-weight:700">' +
            String((m.name || '?').slice(0, 1)).toUpperCase() + '</span>' +
            '<span style="flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap">' + String(m.name || '匿名') + '</span>' +
            '<span class="badge" style="font-size:11px">' + (m.kind === 'HOST' ? '房主' : m.kind === 'LOCAL' ? '本机' : '玩家') + '</span>' +
            '</div>').join('')
      }
      if (state.state === 'exception') {
        el('tc-hint').textContent = exceptionNames[state.type] || '联机异常，请返回待机后重试。'
      } else if (state.state === 'host-scanning') {
        el('tc-hint').textContent = '请在游戏内「对局域网开放」，陶瓦扫描到端口后会自动创建房间。'
      } else {
        el('tc-hint').textContent = '陶瓦联机与 HMCL 使用同一套房间码，两边可以互相加入。'
      }
    }
    const poll = async () => {
      if (timer === null) return
      try { render(await api.terracotta.state()) } catch {}
    }
    const ensure = async () => {
      el('tc-hint').textContent = '正在准备陶瓦联机…'
      const installed = await api.terracotta.install(false)
      if (!installed || installed.ok !== true) {
        el('tc-hint').textContent = installed?.error || '陶瓦联机安装失败'
        return false
      }
      const started = await api.terracotta.start()
      if (!started || started.ok !== true) {
        el('tc-hint').textContent = started?.error || '陶瓦联机启动失败'
        return false
      }
      return true
    }
    const run = async (action) => {
      if (busy) return
      busy = true
      try {
        if (!(await ensure())) return
        await action()
        await poll()
      } finally { busy = false }
    }
    etTab.onclick = () => {
      etTab.className = 'btn btn-sm btn-primary'
      tcTab.className = 'btn btn-sm btn-soft'
      etWrap.style.display = ''
      tc.style.display = 'none'
      if (subtitle) subtitle.textContent = 'EasyTier 虚拟局域网'
      if (timer) { clearInterval(timer); timer = null }
    }
    tcTab.onclick = () => {
      tcTab.className = 'btn btn-sm btn-primary'
      etTab.className = 'btn btn-sm btn-soft'
      etWrap.style.display = 'none'
      tc.style.display = ''
      if (subtitle) subtitle.textContent = '陶瓦联机（可与 HMCL 互通）'
      void run(async () => {})
      if (!timer) timer = setInterval(poll, 800)
    }
    el('tc-install').onclick = () => void run(async () => {
      el('tc-hint').textContent = '正在检查并下载陶瓦联机，进度显示在控制台…'
      const result = await api.terracotta.install(false)
      el('tc-hint').textContent = result?.ok
        ? '陶瓦联机已就绪，正在启动…'
        : (result?.error || '安装失败')
    })
    el('tc-host').onclick = () => void run(async () => {
      const account = await api.accounts.active()
      const player = account?.name || account?.username || '玩家'
      const result = await api.terracotta.host(player)
      if (result && result.ok !== true) el('tc-hint').textContent = result.error || '创建房间失败'
    })
    el('tc-join').onclick = () => void run(async () => {
      const room = el('tc-room').value.trim()
      if (!room) { el('tc-hint').textContent = '请先粘贴房间邀请码'; return }
      const account = await api.accounts.active()
      const player = account?.name || account?.username || '玩家'
      const result = await api.terracotta.join(room, player)
      if (result && result.ok !== true) el('tc-hint').textContent = '邀请码无效或当前不可加入'
    })
    el('tc-copy').onclick = () => {
      const code = el('tc-room-code').textContent || ''
      if (!code) return
      void navigator.clipboard?.writeText(code)
      el('tc-copy').textContent = '已复制'
      setTimeout(() => { el('tc-copy').textContent = '复制' }, 1500)
    }
    el('tc-reset').onclick = () => void api.terracotta.reset().then(poll)
    el('tc-stop').onclick = () => void api.terracotta.stop().then(() => { render({ state: 'not-running' }) })
  }
  window.addEventListener('aurora-easytier-open', () => { void openPanel(); setTimeout(enhanceTerracotta, 30) })
        window.addEventListener('aurora-easytier-close', () => closePanel())
      }
      if (!sidebar.dataset.etNavBound) {
        sidebar.dataset.etNavBound = '1'
        sidebar.addEventListener('click', (event) => {
          if (!(event.target instanceof Element)) return
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
      .aurora-select-menu{position:fixed;z-index:1000;min-width:0;max-height:300px;overflow:auto;padding:5px;border:1px solid var(--line);border-radius:10px;background:var(--panel-solid);box-shadow:0 12px 32px -18px rgba(0,0,0,.55)}
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
      if ((option.textContent || '').includes('没有兼容当前实例的版本')) continue
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
    const width = Math.max(rect.width, 120)
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
  document.addEventListener('click', (event) => {
    const link = event.target instanceof Element ? event.target.closest('a[href]') : null
    if (!link) return
    const href = link.getAttribute('href') || ''
    if (!/^https?:/i.test(href)) return
    event.preventDefault()
    event.stopPropagation()
    void api.app.openExternal(href)
  }, true)
})()