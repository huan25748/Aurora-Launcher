import { app, dialog, ipcMain, shell, BrowserWindow } from 'electron'
import fs from 'node:fs'
import path from 'node:path'
import { dirs, ensureMinecraftLayout, portableRootDir, rootDir, settingsDir } from './core/paths'
import { getSettings, updateSettings, resetSettings, systemInfo, suggestedMemory } from './core/settings'
import { TaskGroupHandle, snapshot, cancelGroup, isCancelledError } from './core/tasks'
import { consoleLog, emit, toast, windowRef } from './core/bus'
import { translateModNames } from './core/modnames'
import { storageStats, cleanStorage, pruneUnused } from './core/storage'
import { readEntryText } from './core/zip'
import { checkForUpdates } from './core/updates'
import { getVersionManifest, listInstalledVersions, javaRequirementOf, installedVersionSize } from './minecraft/version'
import { installVanilla, installFabricLike, installForgeLike, listLoaderVersions } from './minecraft/installer'
import { cleanupInstallVersions, createInstance, listInstances, getInstance, updateInstance, removeInstance, copyInstance, exportInstance, exportInstancePack, importInstanceFromZip, importExistingVersions, instanceSize, instancePath, readInstance } from './minecraft/instances'
import { listContent, listWorlds, listScreenshots, toggleContent, removeContent, importContentFiles, backupWorld, deleteWorld, contentDir, gameDirOf } from './minecraft/content'
import { searchModrinth, getProject, getVersions, getModrinthTranslations, installProject, installProjectToFolder, installModpack, installLocalModpack, featured } from './minecraft/modrinth'
import { searchCurseForge, getCurseForgeMod, getCurseForgeFiles, getCurseForgeCategories, getCurseForgeTranslations, installCurseForgeFile, installCurseForgeFileToFolder, installCurseForgePack } from './minecraft/curseforge'
import { listJava, downloadJava, deleteJava, scanJava } from './minecraft/java'
import {
  listAccounts,
  activeAccount,
  setActiveAccount,
  addOfflineAccount,
  startMicrosoftLogin,
  pollMicrosoftLogin,
  cancelMicrosoftLogin,
  addAuthlibAccount,
  removeAccount,
  refreshAccount,
  refreshStoredAvatars
} from './minecraft/auth'
import { launchGame, stopGame, isRunning } from './minecraft/launcher'
import { getGameState } from './minecraft/gameState'
import { listServers, addServer, removeServer, pingServer } from './minecraft/servers'
import { listLogFiles, readLogFile, latestConsole, clearConsoleBuffer, analyzeInstanceCrash } from './minecraft/logs'
import { getNews } from './minecraft/news'
import { testDownloadEndpoints } from './core/http'
import type { AppInfo, ContentKind, InstallLoaderRequest, LaunchOptions, Settings } from '../shared/types'

type Handler = (...args: any[]) => Promise<unknown> | unknown

function handle(channel: string, fn: Handler): void {
  ipcMain.handle(channel, async (_event, ...args) => {
    try {
      return await fn(...args)
    } catch (e) {
      if (isCancelledError(e)) throw new Error('操作已取消')
      const message = e instanceof Error ? e.message : String(e)
      consoleLog('launcher', 'error', `${channel}: ${message}`)
      throw new Error(message)
    }
  })
}

function validPath(target: string): string {
  if (!target || typeof target !== 'string') throw new Error('非法路径')
  return path.resolve(target)
}

async function openContentFolder(instanceId: string, kind: ContentKind): Promise<void> {
  const dir = contentDir(instanceId, kind)
  fs.mkdirSync(dir, { recursive: true })
  await shell.openPath(dir)
}

function assertInInstances(target: string): void {
  const resolved = path.resolve(target)
  if (!resolved.startsWith(path.resolve(dirs().instances))) throw new Error('非法路径')
}

export function registerIpc(): void {
  // -------------------------------------------------------------------------
  // app
  // -------------------------------------------------------------------------
  handle('app:info', async (): Promise<AppInfo> => {
    const instances = await listInstances()
    const versions = await listInstalledVersions()
    return {
      version: app.getVersion(),
      electron: process.versions.electron,
      chrome: process.versions.chrome,
      node: process.versions.node,
      platform: process.platform,
      arch: process.arch,
      rootDir: dirs().root,
      versionsDir: dirs().versions,
      assetsDir: dirs().assets,
      librariesDir: dirs().libraries,
      instancesDir: dirs().instances,
      totalInstances: instances.length,
      totalVersions: versions.length,
      totalPlayTimeMs: instances.reduce((sum, i) => sum + (i.totalPlayTimeMs ?? 0), 0)
    }
  })
  handle('app:open-external', async (url: string) => {
    if (!/^(https?:|minecraft:)/i.test(url)) throw new Error('不支持的链接')
    await shell.openExternal(url)
  })
  handle('app:open-path', async (target: string) => {
    const p = validPath(target)
    if (!fs.existsSync(p)) throw new Error('路径不存在')
    const err = await shell.openPath(p)
    if (err) throw new Error(err)
  })
  handle('app:show-item', async (target: string) => {
    const p = validPath(target)
    if (!fs.existsSync(p)) throw new Error('路径不存在')
    shell.showItemInFolder(p)
  })
  handle('app:pick-directory', async (title?: string, defaultPath?: string) => {
    const win = windowRef()
    const result = win
      ? await dialog.showOpenDialog(win, { title: title ?? '选择文件夹', defaultPath, properties: ['openDirectory', 'createDirectory'] })
      : await dialog.showOpenDialog({ title: title ?? '选择文件夹', properties: ['openDirectory', 'createDirectory'] })
    return { canceled: result.canceled, paths: result.filePaths }
  })
  handle('app:pick-file', async (filters?: { name: string; extensions: string[] }[], multi?: boolean) => {
    const win = windowRef()
    const options: Electron.OpenDialogOptions = {
      title: '选择文件',
      properties: multi ? ['openFile', 'multiSelections'] : ['openFile'],
      filters: filters ?? [{ name: '所有文件', extensions: ['*'] }]
    }
    const result = win ? await dialog.showOpenDialog(win, options) : await dialog.showOpenDialog(options)
    return { canceled: result.canceled, paths: result.filePaths }
  })
  handle('app:relaunch', () => {
    app.relaunch()
    app.exit(0)
  })
  handle('app:quit', () => app.quit())

  // window
  handle('window:minimize', () => windowRef()?.minimize())
  handle('window:toggle-maximize', () => {
    const win = windowRef()
    if (!win) return false
    if (win.isMaximized()) win.unmaximize()
    else win.maximize()
    return win.isMaximized()
  })
  handle('window:close', () => windowRef()?.close())
  handle('window:is-maximized', () => windowRef()?.isMaximized() ?? false)
  handle('window:set-title', (title: string) => windowRef()?.setTitle(title))

  // -------------------------------------------------------------------------
  // settings
  // -------------------------------------------------------------------------
  handle('settings:get', () => getSettings())
  handle('settings:update', async (patch: Partial<Settings>) => {
    const next = await updateSettings(patch)
    return next
  })
  handle('settings:reset', () => resetSettings())
  handle('updates:check', (force?: boolean) => checkForUpdates(!!force))
  handle('storage:stats', () => storageStats())
  handle('storage:clean', () => cleanStorage())
  handle('storage:prune', () => pruneUnused())
  handle('modnames:translate', (queries: Parameters<typeof translateModNames>[0]) => translateModNames(queries))
  handle('network:test', () => testDownloadEndpoints())
  async function switchGameRoot(root: string): Promise<void> {
    ensureMinecraftLayout(root)
    await updateSettings({ rootDir: root, lastSelectedInstanceId: '' })
    try {
      const imported = await importExistingVersions(true)
      if (imported) consoleLog('launcher', 'info', `已从新目录导入 ${imported} 个版本`)
    } catch (e) {
      consoleLog('launcher', 'warn', `导入新目录版本失败: ${e instanceof Error ? e.message : String(e)}`)
    }
  }
  handle('settings:pick-root', async () => {
    const win = windowRef()
    const result = win
      ? await dialog.showOpenDialog(win, { title: '切换游戏文件夹', properties: ['openDirectory', 'createDirectory'] })
      : await dialog.showOpenDialog({ title: '切换游戏文件夹', properties: ['openDirectory', 'createDirectory'] })
    if (result.canceled || !result.filePaths[0]) return getSettings()
    await switchGameRoot(result.filePaths[0])
    return getSettings()
  })
  handle('settings:use-portable-root', async () => {
    await switchGameRoot(portableRootDir())
    return getSettings()
  })
  handle('settings:system-info', () => ({ ...systemInfo(), suggestedMemory: suggestedMemory() }))

  // -------------------------------------------------------------------------
  // versions
  // -------------------------------------------------------------------------
  handle('versions:manifest', (force?: boolean) => getVersionManifest(!!force))
  handle('versions:installed', () => listInstalledVersions())
  handle('versions:delete', async (versionId: string) => {
    if (!/^[\w.\-+]+$/.test(versionId)) throw new Error('非法版本名')
    const dir = path.join(dirs().versions, versionId)
    if (!fs.existsSync(dir)) throw new Error('版本不存在')
    fs.rmSync(dir, { recursive: true, force: true })
  })
  handle('versions:loaders', (kind: 'fabric' | 'quilt' | 'forge' | 'neoforge', mcVersion: string) => listLoaderVersions(kind, mcVersion))
  handle('versions:java-requirement', (versionId: string) => javaRequirementOf(versionId))
  handle('versions:loader-size', (versionId: string) => installedVersionSize(versionId))

  // -------------------------------------------------------------------------
  // install
  // -------------------------------------------------------------------------
  function snapshotVersionFolders(): Set<string> {
    try {
      return new Set(fs.readdirSync(dirs().versions, { withFileTypes: true }).filter((entry) => entry.isDirectory()).map((entry) => entry.name))
    } catch {
      return new Set()
    }
  }

  async function cleanupIntermediates(before: Set<string>, keepVersionId: string): Promise<void> {
    try {
      const created = fs
        .readdirSync(dirs().versions, { withFileTypes: true })
        .filter((entry) => entry.isDirectory() && !before.has(entry.name))
        .map((entry) => entry.name)
      const removed = await cleanupInstallVersions(created, keepVersionId)
      if (removed.length) consoleLog('launcher', 'info', `已清理临时版本目录: ${removed.join(', ')}`)
    } catch (e) {
      consoleLog('launcher', 'warn', `清理临时版本目录失败: ${e instanceof Error ? e.message : String(e)}`)
    }
  }

  handle('install:vanilla', async (versionId: string, createNewInstance?: boolean, instanceName?: string) => {
    const group = new TaskGroupHandle(`安装 Minecraft ${versionId}`)
    const before = snapshotVersionFolders()
    try {
      await installVanilla(versionId, group)
      let instanceId: string | undefined
      if (createNewInstance !== false) {
        const inst = await createInstance({
          name: instanceName?.trim() || versionId,
          versionId,
          mcVersion: versionId,
          loader: 'vanilla',
          loaderVersion: undefined,
          icon: '🧊',
          color: 'ocean'
        })
        instanceId = inst.id
        await cleanupIntermediates(before, inst.versionId)
        await updateSettings({ lastSelectedInstanceId: inst.id })
      }
      group.finish('done')
      return { instanceId, versionId }
    } catch (e) {
      group.fail(e)
      throw e
    }
  })

  handle('install:loader', async (req: InstallLoaderRequest) => {
    const label = `${req.loader} ${req.loaderVersion ?? ''} (${req.mcVersion})`
    const group = new TaskGroupHandle(`安装 ${label}`)
    const before = snapshotVersionFolders()
    try {
      let result: { versionId: string; loaderVersion: string }
      if (req.loader === 'fabric' || req.loader === 'quilt') {
        result = await installFabricLike(req.loader, req.mcVersion, req.loaderVersion, group)
      } else {
        result = await installForgeLike(req.loader, req.mcVersion, req.loaderVersion, group)
      }
      let instanceId: string | undefined
      if (req.createInstance !== false) {
        const inst = await createInstance({
          name: req.instanceName?.trim() || `${req.mcVersion} ${req.loader}${result.loaderVersion ? ` ${result.loaderVersion}` : ''}`,
          versionId: result.versionId,
          mcVersion: req.mcVersion,
          loader: req.loader,
          loaderVersion: result.loaderVersion,
          icon: req.loader === 'fabric' ? '🧵' : req.loader === 'quilt' ? '🧶' : req.loader === 'neoforge' ? '⚙️' : '🔨',
          color: 'violet'
        })
        instanceId = inst.id
        await cleanupIntermediates(before, inst.versionId)
        await updateSettings({ lastSelectedInstanceId: inst.id })
      }
      group.finish('done')
      return { instanceId, versionId: result.versionId }
    } catch (e) {
      group.fail(e)
      throw e
    }
  })

  // -------------------------------------------------------------------------
  // launch
  // -------------------------------------------------------------------------
  handle('launch:start', async (opts: LaunchOptions) => {
    if (isRunning()) return { ok: false, error: '已有游戏正在运行' }
    const group = new TaskGroupHandle('启动游戏')
    try {
      const result = await launchGame(opts.instanceId, opts, group)
      group.finish('done')
      if (result.ok && getSettings().minimizeOnLaunch) windowRef()?.minimize()
      return result
    } catch (e) {
      group.fail(e)
      const message = e instanceof Error ? e.message : String(e)
      return { ok: false, error: message }
    }
  })
  handle('launch:stop', () => stopGame())
  handle('launch:state', () => getGameState())

  // -------------------------------------------------------------------------
  // instances
  // -------------------------------------------------------------------------
  handle('instances:list', () => listInstances())
  handle('instances:get', (id: string) => getInstance(id))
  handle('instances:create', (input: { name: string; versionId: string; mcVersion: string; loader: string; loaderVersion?: string; icon?: string; color?: string }) =>
    createInstance(input)
  )
  handle('instances:update', (id: string, patch: Partial<Settings>) => updateInstance(id, patch as never))
  handle('instances:remove', async (id: string, deleteFiles: boolean) => {
    await removeInstance(id, deleteFiles)
  })
  handle('instances:copy', (id: string, name: string) => copyInstance(id, name))
  handle('instances:export', (id: string) => exportInstance(id))
  handle('instances:export-pack', (id: string) => exportInstancePack(id))
  handle('instances:import', async () => {
    const win = windowRef()
    const result = win
      ? await dialog.showOpenDialog(win, { title: '导入整合包或实例压缩包', filters: [{ name: '整合包 / 实例压缩包', extensions: ['zip', 'mrpack'] }], properties: ['openFile'] })
      : await dialog.showOpenDialog({ title: '导入整合包或实例压缩包', properties: ['openFile'] })
    if (result.canceled || !result.filePaths[0]) return null
    const zipFile = result.filePaths[0]
    const indexText = readEntryText(zipFile, ['modrinth.index.json'])
    if (indexText) {
      const installed = await installLocalModpack(zipFile)
      return readInstance(installed.instanceId)
    }
    return importInstanceFromZip(zipFile)
  })
  handle('instances:open-folder', async (id: string, sub?: string) => {
    const inst = readInstance(id)
    const base = inst?.gameDir ?? instancePath(id)
    const target = sub ? path.join(base, sub) : base
    const rel = path.relative(path.resolve(base), path.resolve(target))
    if (rel.startsWith('..') || path.isAbsolute(rel)) throw new Error('非法路径')
    fs.mkdirSync(target, { recursive: true })
    await shell.openPath(target)
  })
  handle('instances:size', (id: string) => instanceSize(id))
  handle('instances:import-existing', () => importExistingVersions())

  // -------------------------------------------------------------------------
  // content
  // -------------------------------------------------------------------------
  handle('content:list', (instanceId: string, kind: ContentKind) => listContent(instanceId, kind))
  handle('content:worlds', (instanceId: string) => listWorlds(instanceId))
  handle('content:screenshots', (instanceId: string) => listScreenshots(instanceId))
  handle('content:toggle', (instanceId: string, kind: ContentKind, fileName: string) => toggleContent(instanceId, kind, fileName))
  handle('content:remove', (instanceId: string, kind: ContentKind, fileName: string) => removeContent(instanceId, kind, fileName))
  handle('content:import', async (instanceId: string, kind: ContentKind) => {
    const win = windowRef()
    const filters = kind === 'mods' ? [{ name: '模组', extensions: ['jar'] }] : [{ name: '压缩包', extensions: ['zip'] }]
    const result = win
      ? await dialog.showOpenDialog(win, { title: '选择文件', properties: ['openFile', 'multiSelections'], filters })
      : await dialog.showOpenDialog({ title: '选择文件', properties: ['openFile', 'multiSelections'], filters })
    if (result.canceled) return 0
    const count = await importContentFiles(instanceId, kind, result.filePaths)
    return count
  })
  handle('content:backup-world', (instanceId: string, folderName: string) => backupWorld(instanceId, folderName))
  handle('content:delete-world', (instanceId: string, folderName: string) => deleteWorld(instanceId, folderName))

  // -------------------------------------------------------------------------
  // modrinth
  // -------------------------------------------------------------------------
  handle('modrinth:search', (params: Parameters<typeof searchModrinth>[0]) => searchModrinth(params))
  handle('modrinth:project', (id: string) => getProject(id))
  handle('modrinth:versions', (projectId: string, mcVersion?: string, loader?: string) => getVersions(projectId, mcVersion, loader))
  handle('modrinth:translations', (ids: string[]) => getModrinthTranslations(ids))
  handle('modrinth:install', (opts: Parameters<typeof installProject>[0]) => installProject(opts))
  handle('modrinth:install-folder', (opts: Parameters<typeof installProjectToFolder>[0]) => installProjectToFolder(opts))
  handle('modrinth:install-pack', async (projectId: string, instanceName?: string, versionId?: string) => {
    const before = snapshotVersionFolders()
    const result = await installModpack(projectId, versionId, instanceName)
    const inst = readInstance(result.instanceId)
    if (inst) await cleanupIntermediates(before, inst.versionId)
    return result
  })
  handle('modrinth:featured', (type?: 'mod' | 'resourcepack' | 'shader' | 'datapack' | 'modpack') => featured(type ?? 'mod'))

  // -------------------------------------------------------------------------
  // curseforge
  // -------------------------------------------------------------------------
  handle('curseforge:search', (params: Parameters<typeof searchCurseForge>[0]) => searchCurseForge(params))
  handle('curseforge:categories', (type: Parameters<typeof getCurseForgeCategories>[0]) => getCurseForgeCategories(type))
  handle('curseforge:project', (id: number) => getCurseForgeMod(id))
  handle('curseforge:files', (id: number, mcVersion?: string, loader?: string) => getCurseForgeFiles(id, mcVersion, loader))
  handle('curseforge:translations', (ids: number[]) => getCurseForgeTranslations(ids))
  handle('curseforge:install', (opts: Parameters<typeof installCurseForgeFile>[0]) => installCurseForgeFile(opts))
  handle('curseforge:install-folder', (opts: Parameters<typeof installCurseForgeFileToFolder>[0]) => installCurseForgeFileToFolder(opts))
  handle('curseforge:install-pack', (opts: Parameters<typeof installCurseForgePack>[0]) => installCurseForgePack(opts))

  // -------------------------------------------------------------------------
  // java
  // -------------------------------------------------------------------------
  handle('java:list', () => listJava(false))
  handle('java:scan', () => scanJava(true))
  handle('java:download', async (major: number) => {
    const group = new TaskGroupHandle(`下载 Java ${major}`)
    try {
      const info = await downloadJava(major, group)
      group.finish('done')
      return info
    } catch (e) {
      group.fail(e)
      throw e
    }
  })
  handle('java:delete', (javaPath: string) => deleteJava(javaPath))

  // -------------------------------------------------------------------------
  // accounts
  // -------------------------------------------------------------------------
  handle('accounts:list', () => {
    const list = listAccounts()
    // Avatar migration may fetch remote textures; never block the first paint on it.
    void refreshStoredAvatars()
      .then((changed) => {
        if (changed) emit('accounts:changed', listAccounts())
      })
      .catch((e) => consoleLog('auth', 'warn', `头像迁移失败: ${e instanceof Error ? e.message : String(e)}`))
    return list
  })
  handle('accounts:active', () => activeAccount())
  handle('accounts:set-active', (id: string) => setActiveAccount(id))
  handle('accounts:add-offline', (username: string) => addOfflineAccount(username))
  handle('accounts:start-microsoft', () => startMicrosoftLogin())
  handle('accounts:poll-microsoft', (flowId: string) => pollMicrosoftLogin(flowId))
  handle('accounts:cancel-microsoft', (flowId: string) => cancelMicrosoftLogin(flowId))
  handle('accounts:add-authlib', (server: string, username: string, password: string) => addAuthlibAccount(server, username, password))
  handle('accounts:remove', (id: string) => removeAccount(id))
  handle('accounts:refresh', (id: string) => refreshAccount(id))

  // -------------------------------------------------------------------------
  // servers
  // -------------------------------------------------------------------------
  handle('servers:list', (instanceId: string) => listServers(instanceId))
  handle('servers:add', (instanceId: string, entry: { name: string; ip: string; icon?: string }) => addServer(instanceId, entry))
  handle('servers:remove', (instanceId: string, ip: string) => removeServer(instanceId, ip))
  handle('servers:ping', (ip: string) => pingServer(ip))

  // -------------------------------------------------------------------------
  // logs
  // -------------------------------------------------------------------------
  handle('logs:files', (instanceId: string) => listLogFiles(instanceId))
  handle('logs:read', (p: string, maxBytes?: number) => readLogFile(p, maxBytes))
  handle('logs:latest', () => latestConsole())
  handle('logs:analyze', (instanceId: string) => analyzeInstanceCrash(instanceId))
  handle('logs:clear-console', () => clearConsoleBuffer())

  // -------------------------------------------------------------------------
  // news
  // -------------------------------------------------------------------------
  handle('news:list', (force?: boolean) => getNews(!!force))
}

export { isRunning }
