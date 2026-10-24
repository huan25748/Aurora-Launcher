import { contextBridge, ipcRenderer } from 'electron'
import type { AuroraApi } from '../shared/api'

function invoke<T>(channel: string, ...args: unknown[]): Promise<T> {
  return ipcRenderer.invoke(channel, ...args) as Promise<T>
}

function subscribe<T>(channel: string, cb: (payload: T) => void): () => void {
  const listener = (_e: Electron.IpcRendererEvent, payload: T) => cb(payload)
  ipcRenderer.on(channel, listener)
  return () => ipcRenderer.removeListener(channel, listener)
}

const api: AuroraApi = {
  app: {
    info: () => invoke('app:info'),
    openExternal: (url) => invoke('app:open-external', url),
    openPath: (target) => invoke('app:open-path', target),
    showItem: (target) => invoke('app:show-item', target),
    pickDirectory: (title, defaultPath) => invoke('app:pick-directory', title, defaultPath),
    pickFile: (filters, multi) => invoke('app:pick-file', filters, multi),
    relaunch: () => invoke('app:relaunch'),
    quit: () => invoke('app:quit'),
    window: {
      minimize: () => invoke('window:minimize'),
      toggleMaximize: () => invoke('window:toggle-maximize'),
      close: () => invoke('window:close'),
      isMaximized: () => invoke('window:is-maximized'),
      setTitle: (title) => invoke('window:set-title', title)
    }
  },
  network: {
    test: () => invoke('network:test')
  },
  settings: {
    get: () => invoke('settings:get'),
    update: (patch) => invoke('settings:update', patch),
    reset: () => invoke('settings:reset'),
    pickRoot: () => invoke('settings:pick-root'),
    usePortableRoot: () => invoke('settings:use-portable-root'),
    systemInfo: () => invoke('settings:system-info')
  },
  versions: {
    manifest: (force) => invoke('versions:manifest', force),
    installed: () => invoke('versions:installed'),
    delete: (versionId) => invoke('versions:delete', versionId),
    loaders: (kind, mcVersion) => invoke('versions:loaders', kind, mcVersion),
    javaRequirement: (versionId) => invoke('versions:java-requirement', versionId),
    loaderSize: (versionId) => invoke('versions:loader-size', versionId)
  },
  install: {
    vanilla: (versionId, createInstance, instanceName) => invoke('install:vanilla', versionId, createInstance, instanceName),
    loader: (req) => invoke('install:loader', req)
  },
  launch: {
    start: (opts) => invoke('launch:start', opts),
    stop: () => invoke('launch:stop'),
    state: () => invoke('launch:state')
  },
  instances: {
    list: () => invoke('instances:list'),
    get: (id) => invoke('instances:get', id),
    create: (input) => invoke('instances:create', input),
    update: (id, patch) => invoke('instances:update', id, patch),
    remove: (id, deleteFiles) => invoke('instances:remove', id, deleteFiles),
    copy: (id, name) => invoke('instances:copy', id, name),
    export: (id) => invoke('instances:export', id),
    exportPack: (id) => invoke('instances:export-pack', id),
    import: () => invoke('instances:import'),
    importExisting: () => invoke('instances:import-existing'),
    openFolder: (id, sub) => invoke('instances:open-folder', id, sub),
    size: (id) => invoke('instances:size', id)
  },
  content: {
    list: (instanceId, kind) => invoke('content:list', instanceId, kind),
    worlds: (instanceId) => invoke('content:worlds', instanceId),
    screenshots: (instanceId) => invoke('content:screenshots', instanceId),
    toggle: (instanceId, kind, file) => invoke('content:toggle', instanceId, kind, file),
    remove: (instanceId, kind, file) => invoke('content:remove', instanceId, kind, file),
    import: (instanceId, kind) => invoke('content:import', instanceId, kind),
    backupWorld: (instanceId, folder) => invoke('content:backup-world', instanceId, folder),
    deleteWorld: (instanceId, folder) => invoke('content:delete-world', instanceId, folder)
  },
  modrinth: {
    search: (params) => invoke('modrinth:search', params),
    project: (id) => invoke('modrinth:project', id),
    versions: (projectId, mcVersion, loader) => invoke('modrinth:versions', projectId, mcVersion, loader),
    translations: (ids) => invoke('modrinth:translations', ids),
    install: (opts) => invoke('modrinth:install', opts),
    installToFolder: (opts) => invoke('modrinth:install-folder', opts),
    installPack: (projectId, instanceName, versionId) => invoke('modrinth:install-pack', projectId, instanceName, versionId),
    featured: (type) => invoke('modrinth:featured', type)
  },
  updates: {
    check: (force?: boolean) => invoke('updates:check', force)
  },
  storage: {
    stats: () => invoke('storage:stats'),
    clean: () => invoke('storage:clean'),
    prune: () => invoke('storage:prune')
  },
  modnames: {
    translate: (names) => invoke('modnames:translate', names)
  },
  curseforge: {
    search: (params) => invoke('curseforge:search', params),
    categories: (type) => invoke('curseforge:categories', type),
    project: (id) => invoke('curseforge:project', id),
    files: (id, mcVersion, loader) => invoke('curseforge:files', id, mcVersion, loader),
    translations: (ids) => invoke('curseforge:translations', ids),
    install: (opts) => invoke('curseforge:install', opts),
    installToFolder: (opts) => invoke('curseforge:install-folder', opts),
    installPack: (opts) => invoke('curseforge:install-pack', opts)
  },
  java: {
    list: () => invoke('java:list'),
    scan: () => invoke('java:scan'),
    download: (major) => invoke('java:download', major),
    delete: (p) => invoke('java:delete', p)
  },
  accounts: {
    list: () => invoke('accounts:list'),
    active: () => invoke('accounts:active'),
    setActive: (id) => invoke('accounts:set-active', id),
    addOffline: (username) => invoke('accounts:add-offline', username),
    startMicrosoft: () => invoke('accounts:start-microsoft'),
    pollMicrosoft: (flowId) => invoke('accounts:poll-microsoft', flowId),
    cancelMicrosoft: (flowId) => invoke('accounts:cancel-microsoft', flowId),
    addAuthlib: (server, username, password) => invoke('accounts:add-authlib', server, username, password),
    remove: (id) => invoke('accounts:remove', id),
    refresh: (id) => invoke('accounts:refresh', id)
  },
  servers: {
    list: (instanceId) => invoke('servers:list', instanceId),
    add: (instanceId, entry) => invoke('servers:add', instanceId, entry),
    remove: (instanceId, ip) => invoke('servers:remove', instanceId, ip),
    ping: (ip) => invoke('servers:ping', ip)
  },
  logs: {
    files: (instanceId) => invoke('logs:files', instanceId),
    read: (p, maxBytes) => invoke('logs:read', p, maxBytes),
    latest: (instanceId) => invoke('logs:latest', instanceId),
    analyze: (instanceId) => invoke('logs:analyze', instanceId),
    clearConsole: () => invoke('logs:clear-console')
  },
  news: {
    list: (force) => invoke('news:list', force)
  },
  events: {
    onProgress: (cb) => subscribe('progress:update', cb),
    onLog: (cb) => subscribe('log:line', cb),
    onGameState: (cb) => subscribe('game:state', cb),
    onSettings: (cb) => subscribe('settings:changed', cb),
    onAccounts: (cb) => subscribe('accounts:changed', cb),
    onInstances: (cb) => subscribe('instances:changed', cb),
    onToast: (cb) => subscribe('toast', cb),
    onConsoleToggle: (cb) => subscribe('console:toggle', cb),
    onMaximized: (cb) => subscribe('window:maximized', cb)
  }
}

contextBridge.exposeInMainWorld('aurora', api)
