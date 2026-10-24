import fs from 'node:fs'
import path from 'node:path'
import type { CrashAnalysis } from '../../shared/types'

interface Rule {
  test: RegExp
  cause: string
  summary: string
  suggestions: string[]
  confidence: CrashAnalysis['confidence']
}

const RULES: Rule[] = [
  {
    test: /java\.lang\.OutOfMemoryError|There is insufficient memory for the Java Runtime Environment/i,
    cause: '内存不足',
    summary: '游戏因内存不足而崩溃。分配的内存过小，或整合包/光影对内存的需求过高。',
    suggestions: ['在实例设置中提高最大内存（建议 4GB 以上）', '关闭其他占用内存的程序', '减少同时加载的模组或降低光影质量'],
    confidence: 'high'
  },
  {
    test: /Could not create the Java Virtual Machine|A fatal exception has occurred\. Program will exit/i,
    cause: 'Java 无法启动',
    summary: 'Java 虚拟机没有成功启动，通常是 Java 版本与游戏不匹配、Java 安装损坏，或 JVM 参数/内存设置无效。',
    suggestions: [
      '下载正确版本的 Java：设置 → Java → 下载 Java，选择与游戏版本匹配的版本',
      '在实例设置中选择正确版本的 Java',
      '检查 JVM 参数与最大内存是否有效'
    ],
    confidence: 'high'
  },
  {
    test: /UnsupportedClassVersionError|class file version (\d+)(?:\.\d+)? is not supported/i,
    cause: 'Java 版本不匹配',
    summary: '当前 Java 版本过低或过高，无法运行该游戏版本。',
    suggestions: ['在实例设置中选择匹配的 Java 版本', '使用启动器的“下载 Java”功能安装正确版本'],
    confidence: 'high'
  },
  {
    test: /DuplicateModsFoundException|Duplicate mods found|Found duplicate mods/i,
    cause: '模组重复',
    summary: 'mods 文件夹中存在重复的模组文件，导致加载失败。',
    suggestions: ['打开模组目录，删除重复的模组', '检查是否有相同模组的不同版本'],
    confidence: 'high'
  },
  {
    test: /Missing or unsupported mandatory dependencies|Mod \S+ requires .* which is missing|Missing dependency/i,
    cause: '缺少前置模组',
    summary: '某个模组缺少必需的前置依赖。',
    suggestions: ['根据日志提示安装缺少的前置模组', '使用下载中心的依赖自动安装功能'],
    confidence: 'high'
  },
  {
    test: /Mixin apply failed|MixinTransformerError|mixin\.injection\.throwables/i,
    cause: '模组冲突（Mixin）',
    summary: '一个或多个模组在注入游戏代码时发生冲突。',
    suggestions: ['逐个禁用最近添加的模组以定位冲突', '确认所有模组与当前游戏版本兼容', '更新模组加载器到最新版本'],
    confidence: 'medium'
  },
  {
    test: /NoSuchMethodError|NoClassDefFoundError|ClassNotFoundException/i,
    cause: '模组版本不兼容',
    summary: '模组调用了不存在的类或方法，通常由模组与游戏版本、加载器版本不匹配引起。',
    suggestions: ['检查崩溃日志中提到的模组，更新或移除不兼容的模组', '确认模组支持当前 Minecraft 版本'],
    confidence: 'medium'
  },
  {
    test: /GLFW error|Pixel format not accelerated|Failed to create (?:the )?(?:window|display)|OpenGL|WGL/i,
    cause: '图形驱动异常',
    summary: '游戏无法创建图形上下文，通常与显卡驱动或远程桌面环境有关。',
    suggestions: ['更新显卡驱动', '在远程桌面/虚拟机上尝试关闭光影', '检查是否强制使用集成显卡'],
    confidence: 'medium'
  },
  {
    test: /java\.lang\.Error: Invalid or corrupt jarfile|zip END header not found|invalid LOC header/i,
    cause: '游戏文件损坏',
    summary: '客户端或依赖库文件损坏，无法读取。',
    suggestions: ['在版本管理中重新安装该版本', '删除损坏的库文件后重新启动以自动修复'],
    confidence: 'high'
  },
  {
    test: /The game crashed whilst|Description: .*Exception|Exception in thread "main"/i,
    cause: '游戏崩溃',
    summary: '游戏主线程抛出异常并退出。',
    suggestions: ['查看下方日志中的 Caused by 部分定位原因', '尝试移除最近安装的模组或资源包'],
    confidence: 'low'
  },
  {
    test: /Address already in use|Failed to bind to port|Perhaps a server is already running/i,
    cause: '端口被占用',
    summary: '游戏需要绑定的端口已被其他程序占用。',
    suggestions: ['关闭可能占用端口的程序', '重启电脑后重试'],
    confidence: 'medium'
  }
]

function flattenDescription(desc: unknown): string {
  if (!desc) return ''
  if (typeof desc === 'string') return desc
  if (typeof desc === 'object') {
    const obj = desc as Record<string, unknown>
    if (typeof obj.text === 'string') {
      const extra = Array.isArray(obj.extra) ? obj.extra.map(flattenDescription).join('') : ''
      return obj.text + extra
    }
    return JSON.stringify(desc)
  }
  return String(desc)
}

function findNewest(dir: string, pattern: RegExp): string | null {
  if (!fs.existsSync(dir)) return null
  let newest: { file: string; mtime: number } | null = null
  for (const name of fs.readdirSync(dir)) {
    if (!pattern.test(name)) continue
    const file = path.join(dir, name)
    try {
      const st = fs.statSync(file)
      if (!st.isFile()) continue
      if (!newest || st.mtimeMs > newest.mtime) newest = { file, mtime: st.mtimeMs }
    } catch {}
  }
  return newest?.file ?? null
}

export function analyzeCrash(gameDir: string): CrashAnalysis | null {
  const crashFile = findNewest(path.join(gameDir, 'crash-reports'), /\.txt$/i)
  const logFile = path.join(gameDir, 'logs', 'latest.log')
  let text = ''
  let source = ''
  if (crashFile) {
    try {
      text = fs.readFileSync(crashFile, 'utf8').slice(0, 400_000)
      source = crashFile
    } catch {}
  }
  if (fs.existsSync(logFile)) {
    try {
      const raw = fs.readFileSync(logFile, 'utf8')
      text = raw.slice(-300_000)
      source = logFile
    } catch {}
  }
  // A JVM that refuses to start may exit before Minecraft creates latest.log.
  // The launcher captures that stderr in logs/aurora-*.log, so include its tail.
  const launchLog = findNewest(path.join(gameDir, 'logs'), /^aurora-.*\.log$/i)
  if (launchLog) {
    try {
      const raw = fs.readFileSync(launchLog, 'utf8')
      const tail = raw.slice(-120_000)
      text = text ? `${text}\n${tail}` : tail
      if (!source) source = launchLog
    } catch {}
  }
  if (!text) return null

  let best: { rule: Rule; match: RegExpMatchArray } | null = null
  for (const rule of RULES) {
    const match = text.match(rule.test)
    if (match) {
      best = { rule, match }
      break
    }
  }
  if (!best) {
    return {
      cause: '未知原因',
      confidence: 'low',
      summary: '未能自动识别崩溃原因，请查看完整日志。',
      suggestions: ['查看实例的日志与崩溃报告', '尝试禁用最近安装的模组'],
      evidence: source ? [path.basename(source)] : [],
      relatedMods: []
    }
  }
  const evidence: string[] = []
  const lines = text.split(/\r?\n/)
  for (const line of lines) {
    if (best.rule.test.test(line) && evidence.length < 5) {
      evidence.push(line.trim().slice(0, 240))
      continue
    }
    if (/Caused by:|Exception in thread|ERROR|FATAL/.test(line) && evidence.length < 8) {
      evidence.push(line.trim().slice(0, 240))
    }
  }
  const relatedMods = new Set<string>()
  const modPatterns = [/modid[=: ]+([a-z0-9_-]{2,40})/gi, /mod (?:id )?['"]?([a-z0-9_-]{2,40})['"]?/gi]
  for (const pattern of modPatterns) {
    for (const m of text.matchAll(pattern)) {
      if (m[1] && !['minecraft', 'forge', 'fabric', 'neoforge', 'quilt', 'java'].includes(m[1].toLowerCase())) {
        relatedMods.add(m[1])
      }
      if (relatedMods.size > 8) break
    }
  }
  return {
    cause: best.rule.cause,
    confidence: best.rule.confidence,
    summary: best.rule.summary,
    suggestions: best.rule.suggestions,
    evidence,
    relatedMods: [...relatedMods]
  }
}

export function latestCrashReport(gameDir: string): { file: string; text: string } | null {
  const crashFile = findNewest(path.join(gameDir, 'crash-reports'), /\.txt$/i)
  if (!crashFile) return null
  try {
    return { file: crashFile, text: fs.readFileSync(crashFile, 'utf8').slice(0, 200_000) }
  } catch {
    return null
  }
}
