import index from './mcmod-names.json'

interface NameEntry {
  name?: string
  at: number
}

const cache = new Map<string, NameEntry>()
let queue: Promise<unknown> = Promise.resolve()

function serialized<T>(task: () => Promise<T>): Promise<T> {
  const run = queue.then(task, task)
  queue = run.catch(() => undefined)
  return run
}
const TTL = 7 * 24 * 60 * 60 * 1000
const FAIL_TTL = 60 * 60 * 1000

function decode(text: string): string {
  return text
    .replace(/&amp;/g, '&')
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&quot;/g, '"')
    .replace(/&#39;/g, "'")
    .replace(/&nbsp;/g, ' ')
}

/** Look up a mod's Chinese name on MC百科 (search.mcmod.cn). */
async function lookup(name: string): Promise<string | undefined> {
  const key = name.trim()
  if (!key) return undefined
  const cached = cache.get(key)
  const now = Date.now()
  if (cached && now - cached.at < (cached.name ? TTL : FAIL_TTL)) return cached.name
  try {
    const res = await fetch(`https://search.mcmod.cn/s?key=${encodeURIComponent(key)}`, {
      headers: { 'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36' },
      signal: AbortSignal.timeout(7_000)
    })
    if (!res.ok) throw new Error(`HTTP ${res.status}`)
    const html = await res.text()
    const links = [...html.matchAll(/<a[^>]+href="(https:\/\/www\.mcmod\.cn\/class\/\d+\.html)"[^>]*>([\s\S]*?)<\/a>/g)]
    const wanted = key.toLowerCase()
    for (const match of links) {
      const raw = decode(match[2].replace(/<em>/gi, '').replace(/<\/em>/gi, '').replace(/<[^>]+>/g, ' '))
        .replace(/\s+/g, ' ')
        .trim()
      const paren = raw.match(/^(?:\[[^\]]*\]\s*)?(.+?)\s*[（(]\s*([^）)]+?)\s*[）)]/)
      if (!paren) continue
      const chinese = paren[1].replace(/^\[[^\]]*\]\s*/, '').trim()
      const english = paren[2].trim()
      if (!chinese || !/[\u3400-\u9fff]/.test(chinese)) continue
      const norm = (value: string) => value.toLowerCase().replace(/[^a-z0-9]/g, '')
      if (norm(english) === norm(wanted) || norm(raw).includes(norm(wanted))) {
        cache.set(key, { name: chinese, at: now })
        return chinese
      }
    }
    cache.set(key, { name: undefined, at: now })
    return undefined
  } catch {
    cache.set(key, { name: undefined, at: now })
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

const localIndex = index as { cf: Record<string, string>; mr: Record<string, string>; cn: Record<string, string> }

export function chineseNameFor(source: 'modrinth' | 'curseforge', slug?: string): string | undefined {
  const key = slug?.trim().toLowerCase()
  if (!key) return undefined
  return source === 'curseforge' ? localIndex.cf[key] : localIndex.mr[key]
}

export interface ModNameQuery { name: string; slug?: string; source: 'modrinth' | 'curseforge' }

export async function translateModNames(queries: ModNameQuery[]): Promise<Record<string, string>> {
  const result: Record<string, string> = {}
  const missing: string[] = []
  for (const query of queries) {
    const name = query.name?.trim()
    if (!name) continue
    const slug = query.slug?.trim().toLowerCase()
    const chinese = slug ? chineseNameFor(query.source, slug) : undefined
    if (chinese) result[name] = chinese
    else if (!missing.includes(name)) missing.push(name)
  }
  return result
}

/** Chinese mod name -> English name via MC百科 search results. */
export async function lookupEnglishName(chineseName: string): Promise<string | undefined> {
  const key = chineseName.trim()
  if (!key || !/[\u3400-\u9fff]/.test(key)) return undefined
  try {
    const res = await fetch(`https://search.mcmod.cn/s?key=${encodeURIComponent(key)}`, {
      headers: { 'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36' },
      signal: AbortSignal.timeout(7_000)
    })
    if (!res.ok) return undefined
    const html = await res.text()
    const links = [...html.matchAll(/<a[^>]+href="(https:\/\/www\.mcmod\.cn\/class\/\d+\.html)"[^>]*>([\s\S]*?)<\/a>/g)]
    const wanted = key.replace(/\s+/g, '').toLowerCase()
    for (const match of links) {
      const raw = decode(match[2].replace(/<[^>]+>/g, ' ')).replace(/\s+/g, ' ').trim()
      const paren = raw.match(/^(?:\[[^\]]*\]\s*)?(.+?)\s*[（(]\s*([^）)]+?)\s*[）)]/)
      if (!paren) continue
      const chinese = paren[1].replace(/^\[[^\]]*\]\s*/, '').trim()
      const english = paren[2].trim()
      if (chinese.replace(/\s+/g, '').toLowerCase() === wanted && /[a-z]/i.test(english)) return english
    }
  } catch {}
  return undefined
}
