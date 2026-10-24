import fs from 'node:fs'
import fsp from 'node:fs/promises'
import path from 'node:path'
import crypto from 'node:crypto'

export async function ensureDir(dir: string): Promise<void> {
  await fsp.mkdir(dir, { recursive: true })
}

export function ensureDirSync(dir: string): void {
  fs.mkdirSync(dir, { recursive: true })
}

export async function readJson<T>(file: string, fallback: T): Promise<T> {
  try {
    const raw = await fsp.readFile(file, 'utf8')
    return JSON.parse(stripBom(raw)) as T
  } catch {
    return fallback
  }
}

export function readJsonSync<T>(file: string, fallback: T): T {
  try {
    return JSON.parse(stripBom(fs.readFileSync(file, 'utf8'))) as T
  } catch {
    return fallback
  }
}

export async function writeJson(file: string, data: unknown): Promise<void> {
  await ensureDir(path.dirname(file))
  const tmp = `${file}.${process.pid}.tmp`
  await fsp.writeFile(tmp, JSON.stringify(data, null, 2), 'utf8')
  await fsp.rename(tmp, file)
}

export function writeJsonSync(file: string, data: unknown): void {
  ensureDirSync(path.dirname(file))
  const tmp = `${file}.${process.pid}.tmp`
  fs.writeFileSync(tmp, JSON.stringify(data, null, 2), 'utf8')
  fs.renameSync(tmp, file)
}

export function stripBom(s: string): string {
  return s.charCodeAt(0) === 0xfeff ? s.slice(1) : s
}

export async function exists(file: string): Promise<boolean> {
  try {
    await fsp.access(file)
    return true
  } catch {
    return false
  }
}

export function existsSync(file: string): boolean {
  return fs.existsSync(file)
}

export async function fileSize(file: string): Promise<number> {
  try {
    return (await fsp.stat(file)).size
  } catch {
    return 0
  }
}

export function sha1FileSync(file: string): string {
  const hash = crypto.createHash('sha1')
  const fd = fs.openSync(file, 'r')
  const buf = Buffer.alloc(1024 * 512)
  try {
    let n: number
    while ((n = fs.readSync(fd, buf, 0, buf.length, null)) > 0) hash.update(buf.subarray(0, n))
  } finally {
    fs.closeSync(fd)
  }
  return hash.digest('hex')
}

export async function sha1File(file: string): Promise<string> {
  return new Promise((resolve, reject) => {
    const hash = crypto.createHash('sha1')
    const stream = fs.createReadStream(file)
    stream.on('data', (d) => hash.update(d))
    stream.on('end', () => resolve(hash.digest('hex')))
    stream.on('error', reject)
  })
}

export function sha1Text(text: string): string {
  return crypto.createHash('sha1').update(text).digest('hex')
}

export async function removeDir(dir: string): Promise<void> {
  await fsp.rm(dir, { recursive: true, force: true })
}

export async function copyDir(src: string, dest: string): Promise<void> {
  await fsp.cp(src, dest, { recursive: true, force: true })
}

export async function dirSize(dir: string): Promise<{ size: number; files: number }> {
  let size = 0
  let files = 0
  async function walk(d: string): Promise<void> {
    let entries: fs.Dirent[]
    try {
      entries = await fsp.readdir(d, { withFileTypes: true })
    } catch {
      return
    }
    for (const e of entries) {
      const p = path.join(d, e.name)
      if (e.isDirectory()) await walk(p)
      else if (e.isFile()) {
        files++
        try {
          size += (await fsp.stat(p)).size
        } catch {}
      }
    }
  }
  await walk(dir)
  return { size, files }
}

export async function listFiles(dir: string): Promise<fs.Dirent[]> {
  try {
    return await fsp.readdir(dir, { withFileTypes: true })
  } catch {
    return []
  }
}

export function timestampName(prefix: string, ext = ''): string {
  const d = new Date()
  const p = (n: number) => String(n).padStart(2, '0')
  return `${prefix}-${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}-${p(d.getHours())}${p(d.getMinutes())}${p(d.getSeconds())}${ext}`
}

export function safeFileName(name: string, fallback = 'file'): string {
  const cleaned = name
    .replace(/[\\/:*?"<>|\r\n\t]/g, '_')
    .replace(/\s+/g, ' ')
    .trim()
    .slice(0, 120)
  return cleaned || fallback
}

export function uid(prefix = ''): string {
  return prefix + crypto.randomBytes(6).toString('hex')
}
