import type { CSSProperties } from 'react'
import type { AuroraApi } from '@shared/api'

export const api: AuroraApi = window.aurora

export function fileUrl(p?: string | null): string | undefined {
  if (!p) return undefined
  const normalized = p.replace(/\\/g, '/')
  return `aurora-file:///${normalized
    .split('/')
    .map((part) => encodeURIComponent(part))
    .join('/')}`
}

export function formatBytes(bytes: number): string {
  if (!bytes || bytes <= 0) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let i = 0
  let v = bytes
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024
    i++
  }
  return `${v.toFixed(v >= 100 || i === 0 ? 0 : 1)} ${units[i]}`
}

export function formatDuration(ms: number): string {
  if (!ms || ms <= 0) return '0 分钟'
  const minutes = Math.floor(ms / 60000)
  if (minutes < 60) return `${Math.max(1, minutes)} 分钟`
  const hours = Math.floor(minutes / 60)
  const rest = minutes % 60
  if (hours < 24) return rest ? `${hours} 小时 ${rest} 分钟` : `${hours} 小时`
  const days = Math.floor(hours / 24)
  return `${days} 天 ${hours % 24} 小时`
}

export function timeAgo(ts?: number): string {
  if (!ts) return '从未'
  const diff = Date.now() - ts
  if (diff < 60_000) return '刚刚'
  if (diff < 3_600_000) return `${Math.floor(diff / 60_000)} 分钟前`
  if (diff < 86_400_000) return `${Math.floor(diff / 3_600_000)} 小时前`
  if (diff < 7 * 86_400_000) return `${Math.floor(diff / 86_400_000)} 天前`
  const d = new Date(ts)
  return `${d.getFullYear()}/${d.getMonth() + 1}/${d.getDate()}`
}

export function formatDate(value?: string | number): string {
  if (!value) return '-'
  const d = typeof value === 'number' ? new Date(value) : new Date(value)
  if (Number.isNaN(d.getTime())) return String(value)
  return `${d.getFullYear()}/${String(d.getMonth() + 1).padStart(2, '0')}/${String(d.getDate()).padStart(2, '0')}`
}

export function formatNumber(n: number | null | undefined): string {
  const value = Number(n)
  if (!Number.isFinite(value)) return '0'
  if (value >= 100_000_000) return `${(value / 100_000_000).toFixed(1)} 亿`
  if (value >= 10_000) return `${(value / 10_000).toFixed(1)} 万`
  return value.toLocaleString()
}

export function cn(...values: (string | false | null | undefined)[]): string {
  return values.filter(Boolean).join(' ')
}

export function initials(name: string): string {
  if (!name) return '?'
  const trimmed = name.trim()
  if (/[\u4e00-\u9fa5]/.test(trimmed)) return trimmed.slice(0, 1)
  return trimmed.slice(0, 2).toUpperCase()
}

export const GRADIENTS: Record<string, [string, string, string]> = {
  aurora: ['#5eead4', '#818cf8', '#f472b6'],
  sunset: ['#fdba74', '#fb7185', '#c084fc'],
  ocean: ['#67e8f9', '#60a5fa', '#a78bfa'],
  forest: ['#86efac', '#34d399', '#22d3ee'],
  candy: ['#f9a8d4', '#c084fc', '#f472b6'],
  ember: ['#fbbf24', '#fb923c', '#f43f5e'],
  violet: ['#c4b5fd', '#818cf8', '#38bdf8'],
  mint: ['#a7f3d0', '#5eead4', '#60a5fa']
}

export function gradientOf(key?: string): [string, string, string] {
  return GRADIENTS[key ?? 'aurora'] ?? GRADIENTS.aurora
}

export const LOADER_LABEL: Record<string, string> = {
  vanilla: '原版',
  fabric: 'Fabric',
  quilt: 'Quilt',
  forge: 'Forge',
  neoforge: 'NeoForge',
  optifine: 'OptiFine'
}

export const LOADER_ICON: Record<string, string> = {
  vanilla: '🧊',
  fabric: '🧵',
  quilt: '🧶',
  forge: '🔨',
  neoforge: '⚙️',
  optifine: '🎨'
}

export function instanceGradientVars(color?: string): CSSProperties {
  const [a, b, c] = gradientOf(color)
  return {
    ['--c1' as never]: a,
    ['--c2' as never]: b,
    ['--c3' as never]: c
  } as CSSProperties
}

export function heroGradientVars(color?: string): CSSProperties {
  const [a, b, c] = gradientOf(color)
  return {
    ['--grad-1' as never]: a,
    ['--grad-2' as never]: b,
    ['--grad-3' as never]: c
  } as CSSProperties
}

export function errorMessage(e: unknown): string {
  const raw = e instanceof Error ? e.message : String(e)
  return raw.replace(/^Error invoking remote method '[^']+': (Error: )?/, '')
}

export async function withToast<T>(fn: () => Promise<T>, onError?: (msg: string) => void): Promise<T | undefined> {
  try {
    return await fn()
  } catch (e) {
    const msg = errorMessage(e)
    if (onError) onError(msg)
    else window.dispatchEvent(new CustomEvent('aurora-error', { detail: msg }))
    return undefined
  }
}
