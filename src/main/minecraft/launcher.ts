import fs from 'node:fs'
import fsp from 'node:fs/promises'
import path from 'node:path'
import os from 'node:os'
import { spawn, type ChildProcess } from 'node:child_process'
import { dirs, versionJarPath, assetsIndexPath } from '../core/paths'
import { ensureDir, existsSync } from '../core/fsx'
import { consoleLog, emit } from '../core/bus'
import { getSettings, suggestedMemory } from '../core/settings'
import { TaskGroupHandle } from '../core/tasks'
import { extractNatives } from '../core/zip'
import { readInstance, updateInstance, addPlayTime, ensureInstanceFolders, touchLaunch } from './instances'
import { loadVersionChain, resolveLibraries, classpathLibraries, readLocalVersionJson, logConfigName, type ArgumentEntry, type Rule, type VersionChain, type AssetIndex } from './version'
import { ensureInstalled, materializeLegacyAssets } from './installer'
import { pickJava } from './java'
import { ensureFreshAccount, authlibJar, accountUuid } from './auth'
import { analyzeCrash } from './crash'
import { emitGameState } from './gameState'
import type { Account, GameState, Instance, JavaInfo, LaunchOptions, LaunchResult } from '../../shared/types'

const LAUNCHER_NAME = 'AuroraLauncher'
const LAUNCHER_VERSION = '1.0.0'

export interface LaunchPlan {
  java: JavaInfo
  args: string[]
  cwd: string
  nativesDir: string
  classpath: string[]
  logFile: string
}

interface Features {
  [key: string]: boolean
}

export function osName(): string {
  return process.platform === 'win32' ? 'windows' : process.platform === 'darwin' ? 'osx' : 'linux'
}

function evalArguments(entries: ArgumentEntry[] | undefined, features: Features, replace: (s: string) => string): string[] {
  const out: string[] = []
  if (!entries) return out
  for (const entry of entries) {
    if (typeof entry === 'string') {
      out.push(replace(entry))
      continue
    }
    if (!entry.value) continue
    if (!rulesAllow(entry.rules, features)) continue
    if (Array.isArray(entry.value)) out.push(...entry.value.map((v) => replace(v)))
    else out.push(replace(entry.value))
  }
  return out
}

function systemVersionString(): string {
  const raw = process.getSystemVersion?.()
  if (raw) return raw
  try {
    return os.release()
  } catch {
    return ''
  }
}

function compareDotted(a: string, b: string): number {
  const pa = a.split(/[._+-]/).map((x) => parseInt(x, 10)).filter((x) => !Number.isNaN(x))
  const pb = b.split(/[._+-]/).map((x) => parseInt(x, 10)).filter((x) => !Number.isNaN(x))
  const len = Math.max(pa.length, pb.length)
  for (let i = 0; i < len; i++) {
    const x = pa[i] ?? 0
    const y = pb[i] ?? 0
    if (x !== y) return x - y
  }
  return 0
}

function ruleMatches(rule: Rule, features: Features): boolean {
  if (rule.os) {
    if (rule.os.name && rule.os.name !== osName()) return false
    if (rule.os.arch) {
      const arch = rule.os.arch
      const mine = process.arch === 'x64' ? 'x86_64' : process.arch
      if (arch !== mine && !(arch === 'x86' && process.arch === 'ia32')) return false
    }
    if (rule.os.versionRange) {
      const systemVersion = systemVersionString()
      const { min, max } = rule.os.versionRange
      if (min && compareDotted(systemVersion, min) < 0) return false
      if (max && compareDotted(systemVersion, max) > 0) return false
    }
  }
  if (rule.features) {
    for (const [k, v] of Object.entries(rule.features)) {
      if ((features[k] ?? false) !== v) return false
    }
  }
  return true
}

function rulesAllow(rules: Rule[] | undefined, features: Features): boolean {
  if (!rules || !rules.length) return true
  let allowed = false
  for (const rule of rules) {
    if (ruleMatches(rule, features)) allowed = rule.action === 'allow'
  }
  return allowed
}

function splitLegacyArgs(raw: string): string[] {
  // legacy minecraftArguments never contains spaces inside a single value
  return raw.split(/\s+/).filter(Boolean)
}

export function buildClasspath(chain: VersionChain, features: Features): string[] {
  const resolved = resolveLibraries(chain.libraries, features)
  const libs = classpathLibraries(resolved)
  const paths: string[] = []
  for (const lib of libs) paths.push(path.join(dirs().libraries, lib.path))
  // Modern Forge/NeoForge load the patched client as the `minecraft` module. Adding the
  // inherited vanilla jar as well makes it an automatic module (_1._20._1) that exports
  // the same packages, causing java.lang.module.ResolutionException on startup.
  const usesPatchedModule = /cpw\.mods\.bootstraplauncher/i.test(chain.mainClass)
  // Only omit the inherited vanilla jar. A self-contained Forge profile (imported PCL
  // instance) has its own patched jar and must keep it.
  const isInheritedVanilla = chain.inheritedJarId !== chain.id
  if (!usesPatchedModule || !isInheritedVanilla) {
    const jar = versionJarPath(chain.inheritedJarId)
    if (existsSync(jar)) paths.push(jar)
  }
  return paths
}

export function extractNativesFor(chain: VersionChain, features: Features, nativesDir: string): string[] {
  fs.rmSync(nativesDir, { recursive: true, force: true })
  fs.mkdirSync(nativesDir, { recursive: true })
  const resolved = resolveLibraries(chain.libraries, features)
  const jars: string[] = []
  for (const entry of resolved) {
    if (!entry.extract) continue
    if (!entry.artifact?.path) continue
    const jar = path.join(dirs().libraries, entry.artifact.path)
    if (!existsSync(jar)) continue
    const excludes = entry.lib.extract?.exclude ?? ['META-INF/']
    try {
      extractNatives(jar, nativesDir, excludes)
      jars.push(jar)
    } catch (e) {
      consoleLog('launcher', 'warn', `解压本地库失败 ${path.basename(jar)}: ${String(e)}`)
    }
  }
  return jars
}

function defaultJvmArgs(): string[] {
  return [
    '-Djava.library.path=${natives_directory}',
    '-Dminecraft.launcher.brand=${launcher_name}',
    '-Dminecraft.launcher.version=${launcher_version}',
    '-cp',
    '${classpath}'
  ]
}

function isOldVersion(chain: VersionChain): boolean {
  return !chain.jvmArgs?.length && !!chain.minecraftArguments
}

function quickPlaySupported(chain: VersionChain): boolean {
  const t = chain.releaseTime ?? ''
  return t >= '2023-04-04'
}

export interface BuiltLaunch {
  args: string[]
  cwd: string
  nativesDir: string
  classpath: string[]
}

export async function buildLaunchArgs(
  instance: Instance,
  chain: VersionChain,
  account: Account,
  java: JavaInfo,
  options: LaunchOptions,
  workingDir: string,
  nativesDir: string
): Promise<BuiltLaunch> {
  const settings = getSettings()
  const width = options.width ?? instance.width ?? 854
  const height = options.height ?? instance.height ?? 480
  const fullscreen = options.fullscreen ?? instance.fullscreen ?? false
  const server = options.server ?? instance.serverToJoin ?? ''
  const world = options.world ?? ''

  const features: Features = {
    has_custom_resolution: true,
    is_demo_user: false,
    has_quick_plays_support: quickPlaySupported(chain),
    is_quick_play_singleplayer: !!world,
    is_quick_play_multiplayer: !!server && !world,
    is_quick_play_realms: false
  }

  const configuredMemory = instance.memory || settings.defaultMemory || 0
  const memoryMb = Math.max(512, configuredMemory || suggestedMemory())
  const classpath = buildClasspath(chain, features)
  const classpathString = classpath.join(path.delimiter)
  const uuid = accountUuid(account)
  const accessToken = account.accessToken || '0'
  const userType = account.type === 'microsoft' ? 'msa' : account.type === 'authlib' ? 'mojang' : 'legacy'

  const replacements: Record<string, string> = {
    '${auth_player_name}': account.username,
    '${version_name}': instance.versionId,
    '${game_directory}': workingDir,
    '${assets_root}': dirs().assets,
    '${assets_index_name}': chain.assetIndexId,
    '${auth_uuid}': uuid,
    '${auth_access_token}': accessToken,
    '${auth_session}': `token:${accessToken}:${uuid}`,
    '${clientid}': '',
    '${auth_xuid}': account.xuid ?? '',
    '${user_type}': userType,
    '${version_type}': instance.loader === 'vanilla' ? 'release' : instance.loader,
    '${natives_directory}': nativesDir,
    '${launcher_name}': LAUNCHER_NAME,
    '${launcher_version}': LAUNCHER_VERSION,
    '${classpath}': classpathString,
    '${classpath_separator}': path.delimiter,
    '${library_directory}': dirs().libraries,
    '${user_properties}': '{}',
    '${resolution_width}': String(width),
    '${resolution_height}': String(height),
    '${quickPlayPath}': path.join(dirs().launcher, 'quickplay.log'),
    '${quickPlaySingleplayer}': world,
    '${quickPlayMultiplayer}': server,
    '${quickPlayRealms}': ''
  }
  const replace = (s: string): string => {
    let out = s
    for (const [k, v] of Object.entries(replacements)) out = out.split(k).join(v)
    return out
  }

  // --- JVM arguments ---
  let jvm: string[]
  if (chain.jvmArgs?.length) {
    jvm = evalArguments(chain.jvmArgs, features, replace)
  } else {
    jvm = defaultJvmArgs().map(replace)
    if (chain.logging?.client) {
      const logFile = path.join(dirs().assets, 'log_configs', logConfigName(chain.logging.client.file))
      jvm.unshift(replace(chain.logging.client.argument.replaceAll('${path}', logFile)))
    }
  }
  if (chain.logging?.client && !jvm.some((a) => a.includes('log4j.configurationFile'))) {
    const logFile = path.join(dirs().assets, 'log_configs', logConfigName(chain.logging.client.file))
    jvm.unshift(replace(chain.logging.client.argument.replaceAll('${path}', logFile)))
  }

  const memoryArgs = [`-Xmx${memoryMb}M`, `-Xms${Math.max(512, Math.floor(memoryMb / 2))}M`]
  const gcArgs = [
    '-XX:+UseG1GC',
    '-XX:+ParallelRefProcEnabled',
    '-XX:MaxGCPauseMillis=200',
    '-XX:+UnlockExperimentalVMOptions',
    '-XX:+DisableExplicitGC',
    '-XX:G1NewSizePercent=20',
    '-XX:G1MaxNewSizePercent=40',
    '-XX:G1HeapRegionSize=16M',
    '-XX:G1ReservePercent=15',
    '-XX:G1HeapWastePercent=5',
    '-XX:G1MixedGCCountTarget=4',
    '-XX:InitiatingHeapOccupancyPercent=20',
    '-XX:G1MixedGCLiveThresholdPercent=90',
    '-XX:G1RSetUpdatingPauseTimePercent=5',
    '-XX:SurvivorRatio=32',
    '-XX:MaxTenuringThreshold=1',
    '-XX:+PerfDisableSharedMem'
  ]
  // Modern versions (1.21.2+/26.x) ship launcher-provided JVM defaults such as
  // ZGC and compact object headers. Apply them, but let our memory settings and
  // explicit user GC flags win to avoid "multiple garbage collectors" failures.
  const defaultUserJvm = evalArguments(chain.defaultUserJvm, features, replace).filter((arg) => !/^-Xm[sx]/.test(arg))
  const extraJvm = (instance.jvmArgs ?? settings.jvmArgs ?? '').trim()
  const userJvm = extraJvm ? extraJvm.split(/\s+/).filter(Boolean) : []
  const hasUserGc = userJvm.some((a) => /^-XX:\+Use/.test(a))
  const hasMemoryArg = jvm.some((a) => a.startsWith('-Xmx'))
  const hasUserXmx = userJvm.some((a) => a.startsWith('-Xmx'))
  const hasDefaultGc = defaultUserJvm.some((a) => /^-XX:\+Use/.test(a))
  const hasGc = jvm.some((a) => a.startsWith('-XX:+Use')) || hasDefaultGc
  const filteredDefaultJvm = hasUserGc
    ? defaultUserJvm.filter((a) => !/^-XX:.*(?:GC|G1|ZGC|Shenandoah|Parallel)/.test(a))
    : defaultUserJvm

  const prefix: string[] = []
  if (!hasMemoryArg && !hasUserXmx) prefix.push(...memoryArgs)
  // Only use our tuned G1 defaults when neither the version nor the user picked a collector.
  if (!hasGc && !hasUserGc && filteredDefaultJvm.length === 0) prefix.push(...gcArgs)
  if (java.major >= 9 && !jvm.some((a) => a.includes('-Dfile.encoding'))) prefix.push('-Dfile.encoding=UTF-8')
  if (java.major > 8) {
    if (!jvm.some((a) => a.includes('-Dstdout.encoding'))) prefix.push('-Dstdout.encoding=UTF-8')
    if (!jvm.some((a) => a.includes('-Dstderr.encoding'))) prefix.push('-Dstderr.encoding=UTF-8')
  }
  prefix.push('-Dlog4j2.formatMsgNoLookups=true')
  if (settings.proxyEnabled) {
    prefix.push(
      `-Dhttp.proxyHost=${settings.proxyHost}`,
      `-Dhttp.proxyPort=${settings.proxyPort}`,
      `-Dhttps.proxyHost=${settings.proxyHost}`,
      `-Dhttps.proxyPort=${settings.proxyPort}`
    )
  }
  prefix.push(...userJvm)

  // authlib-injector for third party authentication
  if (account.type === 'authlib' && account.authServer) {
    const jar = await authlibJar()
    jvm.push(`-javaagent:${jar}=${account.authServer}`)
    consoleLog('auth', 'info', `使用第三方验证: ${account.authServer}`)
  }

  // --- Game arguments ---
  let game: string[]
  if (chain.gameArgs?.length) {
    game = evalArguments(chain.gameArgs, features, replace)
  } else if (chain.minecraftArguments) {
    game = splitLegacyArgs(chain.minecraftArguments).map(replace)
  } else {
    game = []
  }
  const hasWidth = game.includes('--width')
  if (!hasWidth) game.push('--width', String(width), '--height', String(height))
  if (fullscreen) game.push('--fullscreen')

  const hasServerArg = game.some((a) => a === '--server' || a === '--quickPlayMultiplayer')
  if (server && !hasServerArg) {
    if (quickPlaySupported(chain)) {
      game.push('--quickPlayMultiplayer', server)
    } else {
      const [host, port] = server.split(':')
      game.push('--server', host, '--port', port || '25565')
    }
  }
  if (world && quickPlaySupported(chain) && !game.includes('--quickPlaySingleplayer')) {
    game.push('--quickPlaySingleplayer', world)
  }
  const extraGame = (instance.gameArgs ?? settings.gameArgs ?? '').trim()
  if (extraGame) game.push(...extraGame.split(/\s+/).filter(Boolean))

  const args = [...prefix, ...filteredDefaultJvm, ...jvm, chain.mainClass, ...game]
  return { args, cwd: workingDir, nativesDir, classpath }
}

// ---------------------------------------------------------------------------
// Launch orchestration
// ---------------------------------------------------------------------------

let currentProcess: ChildProcess | null = null
let currentInstanceId: string | null = null
let currentStartedAt = 0
let currentLogFile: string | null = null
let logStream: fs.WriteStream | null = null

export function isRunning(): boolean {
  return !!currentProcess && currentProcess.exitCode === null && !currentProcess.killed
}

export function runningInstanceId(): string | null {
  return isRunning() ? currentInstanceId : null
}

export async function stopGame(): Promise<void> {
  if (!currentProcess) return
  consoleLog('launcher', 'warn', '正在停止游戏…')
  try {
    currentProcess.kill()
  } catch {}
  const proc = currentProcess
  await new Promise<void>((resolve) => {
    const timer = setTimeout(() => {
      try {
        proc.kill('SIGKILL')
      } catch {}
      resolve()
    }, 4000)
    proc.once('exit', () => {
      clearTimeout(timer)
      resolve()
    })
  })
}

function applyFirstLaunchLanguage(instance: Instance): void {
  const file = path.join(instance.gameDir, 'options.txt')
  let lines: string[] = []
  if (fs.existsSync(file)) {
    try {
      lines = fs.readFileSync(file, 'utf8').split('\n').map((line) => line.trimEnd())
    } catch {}
  }
  const withoutLang = lines.filter((line) => !line.trim().startsWith('lang:'))
  withoutLang.push('lang:zh_cn')
  fs.mkdirSync(instance.gameDir, { recursive: true })
  fs.writeFileSync(file, withoutLang.join('\n') + '\n', 'utf8')
}

export async function launchGame(instanceId: string, options: LaunchOptions, group: TaskGroupHandle): Promise<LaunchResult> {
  if (isRunning()) return { ok: false, error: '已有游戏正在运行' }
  const instance = readInstance(instanceId)
  if (!instance) return { ok: false, error: '实例不存在' }
  if (!instance.versionId) return { ok: false, error: '该实例尚未关联游戏版本，请先安装版本' }
  ensureInstanceFolders(instanceId)
  if (getSettings().firstLaunchChinese && instance.playCount === 0 && !instance.languageInitialized) {
    try {
      applyFirstLaunchLanguage(instance)
      await updateInstance(instance.id, { languageInitialized: true })
      consoleLog('launcher', 'info', `首次启动：已将 ${instance.name} 的游戏语言设为简体中文`)
    } catch (e) {
      consoleLog('launcher', 'warn', `首次启动写入中文语言失败: ${e instanceof Error ? e.message : String(e)}`)
    }
  }

  const prep = group.step('准备启动信息', 1)
  prep.start(1, instance.name)
  const account = await ensureFreshAccount(options.accountId)
  if (!account) {
    prep.fail()
    throw new Error('没有可用的账户，请先添加账户')
  }
  const chain = await loadVersionChain(instance.versionId)
  const java = await pickJava(chain.javaMajor ?? 8, instance.javaPath)
  if (!java) {
    prep.fail()
    throw new Error('未找到可用的 Java 运行时，请在设置中下载 Java')
  }
  prep.done(`Java ${java.version || java.major}`)

  const ensureStep = group.step('检查游戏文件', 1)
  ensureStep.start(1)
  await ensureInstalled(instance.versionId, group)
  ensureStep.done()

  // legacy assets
  if (chain.assetIndexId) {
    const indexFile = assetsIndexPath(chain.assetIndexId)
    if (existsSync(indexFile)) {
      try {
        const index = JSON.parse(fs.readFileSync(indexFile, 'utf8')) as AssetIndex
        if (index.virtual || index.map_to_resources) {
          const target = index.map_to_resources
            ? path.join(dirs().root, 'resources')
            : path.join(dirs().assets, 'virtual', chain.assetIndexId)
          if (!existsSync(target)) await materializeLegacyAssets(chain, index, group)
        }
      } catch {}
    }
  }

  const nativesDir = path.join(dirs().natives, instance.id)
  const nativesStep = group.step('解压本地库', 1)
  nativesStep.start(1)
  const features: Features = {
    has_custom_resolution: true,
    has_quick_plays_support: quickPlaySupported(chain),
    is_quick_play_singleplayer: !!(options.world ?? ''),
    is_quick_play_multiplayer: !!(options.server ?? instance.serverToJoin ?? '') && !(options.world ?? '')
  }
  const nativeJars = extractNativesFor(chain, features, nativesDir)
  nativesStep.done(`${nativeJars.length} 个本地库`)

  const argsStep = group.step('构建启动参数', 1)
  argsStep.start(1)
  const built = await buildLaunchArgs(instance, chain, account, java, options, instance.gameDir, nativesDir)
  argsStep.done()

  const logDir = path.join(instance.gameDir, 'logs')
  await ensureDir(logDir)
  const stamp = new Date()
  const pad = (n: number): string => String(n).padStart(2, '0')
  const logFile = path.join(
    logDir,
    `aurora-${stamp.getFullYear()}${pad(stamp.getMonth() + 1)}${pad(stamp.getDate())}-${pad(stamp.getHours())}${pad(stamp.getMinutes())}${pad(stamp.getSeconds())}.log`
  )
  logStream = fs.createWriteStream(logFile, { flags: 'a' })
  currentLogFile = logFile

  const launchStep = group.step('启动游戏进程', 1)
  launchStep.start(1, path.basename(java.path))
  const env: NodeJS.ProcessEnv = {
    ...process.env,
    APPDATA: process.env.APPDATA,
    // Encourage Java to use the GPU when available
    __GL_SHADER_DISK_CACHE: '1'
  }
  if (java.source === 'managed') {
    const javaRoot = path.dirname(path.dirname(java.path))
    env.PATH = `${path.dirname(java.path)}${path.delimiter}${env.PATH ?? ''}`
    env.JAVA_HOME = javaRoot
  }
  const child = spawn(java.path, built.args, {
    cwd: instance.gameDir,
    env,
    windowsHide: false,
    detached: false,
    stdio: ['ignore', 'pipe', 'pipe']
  })
  currentProcess = child
  currentInstanceId = instance.id
  currentStartedAt = Date.now()
  await touchLaunch(instance.id)

  consoleLog('launcher', 'info', `启动 ${instance.name} (${instance.versionId}) · Java ${java.version || java.major}`)
  consoleLog('launcher', 'debug', `${java.path} ${built.args.map((a) => (a.includes(' ') ? `"${a}"` : a)).join(' ')}`)

  const pushLine = (source: 'game' | 'launcher', text: string): void => {
    for (const line of text.split(/\r?\n/)) {
      if (!line) continue
      consoleLog('game', /error|exception|fatal/i.test(line) ? 'error' : 'info', line)
      try {
        logStream?.write(`${line}\n`)
      } catch {}
    }
  }
  const header = `--- Aurora Launcher launch ${new Date().toISOString()} ---\ninstance: ${instance.name}\nversion: ${instance.versionId}\njava: ${java.path} (${java.version || java.major})\ncommand: ${java.path} ${built.args.join(' ')}\n\n`
  logStream.write(header)

  child.stdout?.on('data', (chunk: Buffer) => pushLine('game', chunk.toString('utf8')))
  child.stderr?.on('data', (chunk: Buffer) => pushLine('game', chunk.toString('utf8')))
  let settled = false
  child.on('error', (err) => {
    if (settled) return
    settled = true
    consoleLog('launcher', 'error', `进程启动失败: ${err.message}`)
    try {
      logStream?.write(`[launcher] failed to start: ${err.message}\n`)
      logStream?.end()
    } catch {}
    logStream = null
    currentProcess = null
    currentInstanceId = null
    emitGameState({
      instanceId: instance.id,
      instanceName: instance.name,
      state: 'crashed',
      startedAt: currentStartedAt,
      crashReport: `无法启动 Java 进程：${err.message}`
    })
  })
  child.on('exit', (code, signal) => {
    if (settled) return
    settled = true
    const running = Date.now() - currentStartedAt
    void addPlayTime(instance.id, running)
    logStream?.end()
    logStream = null
    currentProcess = null
    const state: GameState = {
      instanceId: instance.id,
      instanceName: instance.name,
      state: code === 0 ? 'exited' : 'crashed',
      exitCode: code ?? undefined,
      startedAt: currentStartedAt
    }
    if (code !== 0 && code !== null) {
      try {
        const crash = analyzeCrash(instance.gameDir)
        if (crash) state.crashReport = crash.summary
      } catch {}
      consoleLog('launcher', 'error', `游戏异常退出 (code ${code}${signal ? `, signal ${signal}` : ''})，运行 ${Math.round(running / 1000)} 秒`)
    } else {
      consoleLog('launcher', 'info', `游戏已退出，运行 ${Math.round(running / 1000)} 秒`)
    }
    currentInstanceId = null
    emitGameState(state)
  })

  launchStep.done()
  emitGameState({
    instanceId: instance.id,
    instanceName: instance.name,
    state: 'running',
    pid: child.pid,
    startedAt: currentStartedAt
  })
  if (getSettings().minimizeOnLaunch) {
    // window handling is done in the ipc layer via emitted event
    emit('launch:minimize', true)
  }
  return { ok: true, pid: child.pid }
}

export function currentLogPath(): string | null {
  return currentLogFile
}
