import { app } from 'electron'
import { fetchJson } from './http'
import type { UpdateInfo } from '../../shared/types'

const REPO = 'huan25748/Aurora-Launcher'
const RELEASES_URL = `https://github.com/${REPO}/releases`
const API_URL = `https://api.github.com/repos/${REPO}/releases/latest`

interface GitHubRelease {
  tag_name?: string
  name?: string
  body?: string
  html_url?: string
  published_at?: string
  prerelease?: boolean
  draft?: boolean
}

let cached: { at: number; info: UpdateInfo } | null = null

function normalizeVersion(value: string): string {
  return value.trim().replace(/^v/i, '')
}

function versionParts(value: string): number[] {
  return normalizeVersion(value)
    .split('-')[0]
    .split('.')
    .map((part) => Number.parseInt(part.replace(/[^\d]/g, ''), 10) || 0)
}

function isNewer(latest: string, current: string): boolean {
  const a = versionParts(latest)
  const b = versionParts(current)
  const length = Math.max(a.length, b.length)
  for (let i = 0; i < length; i++) {
    const left = a[i] ?? 0
    const right = b[i] ?? 0
    if (left !== right) return left > right
  }
  return false
}

export async function checkForUpdates(force = false): Promise<UpdateInfo> {
  if (!force && cached && Date.now() - cached.at < 10 * 60_000) return cached.info
  const currentVersion = app.getVersion()
  const release = await fetchJson<GitHubRelease>(API_URL, {
    timeout: 15_000,
    headers: { Accept: 'application/vnd.github+json', 'User-Agent': 'AuroraLauncher-Updater' }
  })
  const latestVersion = normalizeVersion(release.tag_name || release.name || currentVersion)
  const info: UpdateInfo = {
    currentVersion,
    latestVersion,
    hasUpdate: isNewer(latestVersion, currentVersion),
    releaseUrl: release.html_url || RELEASES_URL,
    title: release.name || `Aurora Launcher ${latestVersion}`,
    notes: (release.body || '').trim(),
    publishedAt: release.published_at
  }
  cached = { at: Date.now(), info }
  return info
}
