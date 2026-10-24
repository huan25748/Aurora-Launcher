import { fetchJson } from './http'
import { lookupEnglishName } from './modnames'

interface CacheEntry {
  text?: string
  at: number
}

const cache = new Map<string, CacheEntry>()
const TTL = 12 * 60 * 60 * 1000
const FAIL_TTL = 10 * 60 * 1000

async function readTranslation(url: string): Promise<string | undefined> {
  const cached = cache.get(url)
  const now = Date.now()
  if (cached && now - cached.at < (cached.text ? TTL : FAIL_TTL)) return cached.text
  try {
    const raw = await fetchJson<{ translated?: string }>(url, {
      timeout: 8_000,
      retries: 0,
      headers: { 'User-Agent': 'AuroraLauncher/1.0.0 (Electron)' }
    })
    const text = typeof raw.translated === 'string' ? raw.translated.trim() : ''
    cache.set(url, { text: text || undefined, at: now })
    return text || undefined
  } catch {
    cache.set(url, { text: undefined, at: now })
    return undefined
  }
}

async function runLimited<T>(items: T[], limit: number, worker: (item: T) => Promise<void>): Promise<void> {
  let next = 0
  const runners = Array.from({ length: Math.min(limit, items.length) }, async () => {
    while (next < items.length) {
      const index = next++
      await worker(items[index])
    }
  })
  await Promise.all(runners)
}

export async function translateModrinthMany(ids: string[]): Promise<Map<string, string>> {
  const map = new Map<string, string>()
  await runLimited([...new Set(ids)], 6, async (id) => {
    const text = await readTranslation(`https://mod.mcimirror.top/translate/modrinth/${encodeURIComponent(id)}`)
    if (text) map.set(id, text)
  })
  return map
}

export async function translateCurseForgeMany(ids: number[]): Promise<Map<number, string>> {
  const map = new Map<number, string>()
  await runLimited([...new Set(ids)], 6, async (id) => {
    const text = await readTranslation(`https://mod.mcimirror.top/translate/curseforge/${id}`)
    if (text) map.set(id, text)
  })
  return map
}

export async function translateModrinthOne(id: string): Promise<string | undefined> {
  return readTranslation(`https://mod.mcimirror.top/translate/modrinth/${encodeURIComponent(id)}`)
}

export async function translateCurseForgeOne(id: number): Promise<string | undefined> {
  return readTranslation(`https://mod.mcimirror.top/translate/curseforge/${id}`)
}

const queryCache = new Map<string, string>()

/** Chinese query -> English for Modrinth/CurseForge full-text search. */
export async function translateQueryToEnglish(query?: string): Promise<string | undefined> {
  const text = query?.trim()
  if (!text || !/[\u3400-\u9fff]/.test(text)) return text || undefined
  const direct = await lookupEnglishName(text)
  if (direct) {
    queryCache.set(text, direct)
    return direct
  }
  const cached = queryCache.get(text)
  if (cached) return cached
  try {
    const url = `https://api.mymemory.translated.net/get?q=${encodeURIComponent(text)}&langpair=zh-CN|en`
    const raw = await fetchJson<{ responseData?: { translatedText?: string } }>(url, {
      timeout: 6_000,
      retries: 0,
      headers: { 'User-Agent': 'AuroraLauncher/1.0.0 (Electron)' }
    })
    const translated = raw.responseData?.translatedText?.trim()
    if (translated && !/MYMEMORY WARNING/i.test(translated)) {
      queryCache.set(text, translated)
      return translated
    }
  } catch {}
  return text
}
