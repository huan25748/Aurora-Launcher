import fs from 'node:fs'
import path from 'node:path'
import { consoleBuffer, clearConsole } from '../core/bus'
import { dirs } from '../core/paths'
import { listFiles } from '../core/fsx'
import { gameDirOf } from './content'
import { analyzeCrash, latestCrashReport } from './crash'
import type { CrashAnalysis, LogFileInfo, LogLine } from '../../shared/types'

export async function listLogFiles(instanceId: string): Promise<LogFileInfo[]> {
  const base = gameDirOf(instanceId)
  const out: LogFileInfo[] = []
  const addDir = async (dir: string, kind: LogFileInfo['kind'], pattern: RegExp): Promise<void> => {
    for (const entry of await listFiles(dir)) {
      if (!entry.isFile() || !pattern.test(entry.name)) continue
      const full = path.join(dir, entry.name)
      try {
        const st = fs.statSync(full)
        out.push({
          name: entry.name,
          path: full,
          size: st.size,
          mtime: st.mtimeMs,
          kind: entry.name === 'latest.log' ? 'latest' : kind
        })
      } catch {}
    }
  }
  await addDir(path.join(base, 'logs'), 'launcher', /\.log$/i)
  await addDir(path.join(base, 'crash-reports'), 'crash', /\.txt$/i)
  return out.sort((a, b) => b.mtime - a.mtime)
}

function assertReadable(target: string): void {
  const root = path.resolve(dirs().root)
  const file = path.resolve(target)
  const rel = path.relative(root, file)
  if (rel.startsWith('..') || path.isAbsolute(rel)) throw new Error('非法路径')
}

export async function readLogFile(target: string, maxBytes = 400_000): Promise<string> {
  assertReadable(target)
  const st = fs.statSync(target)
  if (!st.isFile()) throw new Error('不是文件')
  const start = Math.max(0, st.size - maxBytes)
  const fd = fs.openSync(target, 'r')
  try {
    const size = st.size - start
    const buf = Buffer.alloc(size)
    fs.readSync(fd, buf, 0, size, start)
    return buf.toString('utf8')
  } finally {
    fs.closeSync(fd)
  }
}

export function latestConsole(): LogLine[] {
  return consoleBuffer()
}

export function clearConsoleBuffer(): void {
  clearConsole()
}

export function analyzeInstanceCrash(instanceId: string): CrashAnalysis | null {
  return analyzeCrash(gameDirOf(instanceId))
}

export function crashReport(instanceId: string): { file: string; text: string } | null {
  return latestCrashReport(gameDirOf(instanceId))
}
