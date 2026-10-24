import AdmZip from 'adm-zip'
import fs from 'node:fs'
import path from 'node:path'

export interface ZipEntryInfo {
  name: string
  size: number
  isDirectory: boolean
}

export function listZip(file: string): ZipEntryInfo[] {
  const zip = new AdmZip(file)
  return zip.getEntries().map((e) => ({ name: e.entryName, size: e.header.size, isDirectory: e.isDirectory }))
}

export function readEntryBuffer(file: string, names: string[]): Buffer | null {
  try {
    const zip = new AdmZip(file)
    for (const name of names) {
      const entry = zip.getEntry(name)
      if (entry && !entry.isDirectory) return entry.getData()
    }
    // fall back to case-insensitive match
    const lower = names.map((n) => n.toLowerCase())
    for (const entry of zip.getEntries()) {
      if (!entry.isDirectory && lower.includes(entry.entryName.toLowerCase())) return entry.getData()
    }
    return null
  } catch {
    return null
  }
}

export function readEntryText(file: string, names: string[], maxBytes = 2 * 1024 * 1024): string | null {
  const buf = readEntryBuffer(file, names)
  if (!buf) return null
  return buf.subarray(0, maxBytes).toString('utf8')
}

export function readJsonEntry<T>(file: string, names: string[]): T | null {
  const text = readEntryText(file, names)
  if (!text) return null
  try {
    return JSON.parse(text) as T
  } catch {
    return null
  }
}

/** Extract native libraries, skipping META-INF and excluded prefixes. */
export function extractNatives(jar: string, destDir: string, excludes: string[] = ['META-INF/']): number {
  let count = 0
  const zip = new AdmZip(jar)
  for (const entry of zip.getEntries()) {
    if (entry.isDirectory) continue
    const name = entry.entryName.replace(/\\/g, '/')
    if (excludes.some((ex) => name.startsWith(ex))) continue
    if (name.toLowerCase().endsWith('.class')) continue
    const target = path.join(destDir, name)
    if (!isInside(destDir, target)) continue
    fs.mkdirSync(path.dirname(target), { recursive: true })
    fs.writeFileSync(target, entry.getData())
    count++
  }
  return count
}

export function extractZipSafe(file: string, destDir: string): number {
  let count = 0
  const zip = new AdmZip(file)
  for (const entry of zip.getEntries()) {
    const name = entry.entryName.replace(/\\/g, '/')
    const target = path.join(destDir, name)
    if (!isInside(destDir, target)) continue
    if (entry.isDirectory) {
      fs.mkdirSync(target, { recursive: true })
      continue
    }
    fs.mkdirSync(path.dirname(target), { recursive: true })
    fs.writeFileSync(target, entry.getData())
    count++
  }
  return count
}

export function zipDirectory(srcDir: string, zipFile: string): void {
  const zip = new AdmZip()
  zip.addLocalFolder(srcDir)
  fs.mkdirSync(path.dirname(zipFile), { recursive: true })
  zip.writeZip(zipFile)
}

export function zipFileToBuffer(zipFile: string): Buffer {
  return fs.readFileSync(zipFile)
}

function isInside(parent: string, child: string): boolean {
  const rel = path.relative(path.resolve(parent), path.resolve(child))
  return !!rel && !rel.startsWith('..') && !path.isAbsolute(rel)
}

export function isJar(file: string): boolean {
  return /\.jar$/i.test(file) || /\.jar\.disabled$/i.test(file)
}

export function isJarEnabled(file: string): boolean {
  return /\.jar$/i.test(file) && !/\.disabled$/i.test(file)
}
