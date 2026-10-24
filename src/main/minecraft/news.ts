import { fetchJson } from '../core/http'
import { readJsonSync, writeJsonSync } from '../core/fsx'
import { dirs } from '../core/paths'
import path from 'node:path'
import type { NewsItem } from '../../shared/types'

interface MojangNewsEntry {
  title: string
  tag?: string
  category?: string
  date?: string
  text?: string
  id?: string
  image?: { url?: string; title?: string }
  readMoreLink?: string
  newsPage?: string
  type?: string
}

interface MojangNews {
  version: number
  entries: MojangNewsEntry[]
}

const CACHE = () => path.join(dirs().cache, 'news.json')
const TTL = 30 * 60 * 1000

const STATIC_NEWS: NewsItem[] = [
  {
    id: 'welcome',
    title: '欢迎使用 Aurora Launcher',
    summary: '全新的界面与更快的下载体验。前往「下载」页面安装你的第一个游戏版本，或在「实例」中管理已有的整合包。',
    tag: '公告',
    pinned: true
  },
  {
    id: 'fabric-tip',
    title: '小提示：一键安装模组加载器',
    summary: '在版本页面选择 Fabric、Quilt、Forge 或 NeoForge，即可在安装游戏时自动配置加载器，无需手动操作。',
    tag: '技巧'
  },
  {
    id: 'modrinth',
    title: '内置 Modrinth 资源中心',
    summary: '在启动器内直接搜索并安装模组、资源包、光影与整合包，支持自动补全前置依赖。',
    tag: '功能'
  }
]

export async function getNews(force = false): Promise<NewsItem[]> {
  const cacheFile = CACHE()
  if (!force) {
    const cached = readJsonSync<{ at: number; items: NewsItem[] } | null>(cacheFile, null)
    if (cached && Date.now() - cached.at < TTL && cached.items?.length) return cached.items
  }
  try {
    const data = await fetchJson<MojangNews>('https://launchercontent.mojang.com/v2/news.json', { timeout: 15_000 })
    const items: NewsItem[] = (data.entries ?? []).slice(0, 12).map((entry, index) => {
      const image = entry.image?.url
        ? entry.image.url.startsWith('http')
          ? entry.image.url
          : `https://launchercontent.mojang.com${entry.image.url}`
        : undefined
      return {
        id: entry.id ?? `mojang-${index}`,
        title: entry.title,
        summary: entry.text ?? entry.category ?? '',
        url: entry.readMoreLink ?? entry.newsPage,
        date: entry.date,
        image,
        tag: entry.tag ?? entry.category,
        pinned: index === 0
      }
    })
    const merged = [...STATIC_NEWS.slice(0, 2), ...items]
    writeJsonSync(cacheFile, { at: Date.now(), items: merged })
    return merged
  } catch {
    return STATIC_NEWS
  }
}
