import fs from 'node:fs'
import path from 'node:path'
import crypto from 'node:crypto'
import { settingsDir, dirs, authlibJarPath } from '../core/paths'
import { fetchJson, fetchText, downloadFile } from '../core/http'
import { readJsonSync, writeJsonSync, ensureDir, existsSync } from '../core/fsx'
import { consoleLog, emit } from '../core/bus'
import { getSettings } from '../core/settings'
import { uid } from '../core/fsx'
import type { Account, DeviceCodeInfo, MsPollResult } from '../../shared/types'

const ACCOUNTS_FILE = () => path.join(settingsDir(), 'accounts.json')

interface AccountStore {
  activeId: string
  accounts: Account[]
}

let store: AccountStore | null = null

function readStore(): AccountStore {
  if (store) return store
  store = readJsonSync<AccountStore>(ACCOUNTS_FILE(), { activeId: '', accounts: [] })
  if (!Array.isArray(store.accounts)) store.accounts = []
  return store
}

function saveStore(): void {
  if (!store) return
  ensureDir(settingsDir())
  writeJsonSync(ACCOUNTS_FILE(), store)
  emit('accounts:changed', store.accounts as unknown as Record<string, unknown>[])
}

export function listAccounts(): Account[] {
  return readStore().accounts
}

export function activeAccount(): Account | null {
  const s = readStore()
  return s.accounts.find((a) => a.id === s.activeId) ?? s.accounts[0] ?? null
}

export function setActiveAccount(id: string): Account | null {
  const s = readStore()
  const acc = s.accounts.find((a) => a.id === id) ?? null
  if (!acc) return null
  s.activeId = id
  acc.lastUsedAt = Date.now()
  saveStore()
  return acc
}

export function removeAccount(id: string): void {
  const s = readStore()
  s.accounts = s.accounts.filter((a) => a.id !== id)
  if (s.activeId === id) s.activeId = s.accounts[0]?.id ?? ''
  saveStore()
}

export function accountUuid(account: Account): string {
  return account.uuid.replace(/-/g, '')
}

export function offlineUuid(name: string): string {
  const hash = crypto.createHash('md5').update(`OfflinePlayer:${name}`, 'utf8').digest('hex')
  const chars = hash.split('')
  chars[12] = '3'
  chars[16] = ((parseInt(chars[16], 16) & 0x3) | 0x8).toString(16)
  return chars.join('')
}

export async function addOfflineAccount(username: string): Promise<Account> {
  const name = username.trim()
  if (!name) throw new Error('用户名不能为空')
  if (!/^[A-Za-z0-9_]{1,16}$/.test(name)) throw new Error('用户名只能包含字母、数字与下划线，且不超过 16 个字符')
  const s = readStore()
  const existing = s.accounts.find((a) => a.type === 'offline' && a.username.toLowerCase() === name.toLowerCase())
  if (existing) {
    s.activeId = existing.id
    saveStore()
    return existing
  }
  const account: Account = {
    id: uid('acc-'),
    type: 'offline',
    username: name,
    uuid: offlineUuid(name),
    accessToken: '0',
    createdAt: Date.now(),
    lastUsedAt: Date.now()
  }
  s.accounts.push(account)
  s.activeId = account.id
  saveStore()
  return account
}

// ---------------------------------------------------------------------------
// Microsoft OAuth (device code flow)
// ---------------------------------------------------------------------------

interface Flow {
  id: string
  deviceCode: string
  interval: number
  expiresAt: number
  clientId: string
}

const flows = new Map<string, Flow>()

function clientId(): string {
  const id = getSettings().msClientId?.trim()
  return id || 'c36a9fb6-4f2a-41ff-90bd-ae7cc92031eb'
}

async function postForm<T>(url: string, form: Record<string, string>): Promise<T> {
  const body = new URLSearchParams(form).toString()
  const res = await fetch(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded', Accept: 'application/json' },
    body
  })
  const text = await res.text()
  let json: unknown = null
  try {
    json = JSON.parse(text)
  } catch {
    json = { error: 'invalid_response', error_description: text }
  }
  if (!res.ok) {
    const err = json as { error?: string; error_description?: string }
    const e = new Error(err.error_description || err.error || `HTTP ${res.status}`) as Error & {
      code?: string
      details?: unknown
    }
    e.code = err.error
    e.details = json
    throw e
  }
  return json as T
}

export async function startMicrosoftLogin(): Promise<DeviceCodeInfo> {
  const id = clientId()
  const data = await postForm<{
    device_code: string
    user_code: string
    verification_uri: string
    expires_in: number
    interval: number
    message?: string
  }>('https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode', {
    client_id: id,
    scope: 'XboxLive.signin offline_access'
  })
  const flow: Flow = {
    id: uid('ms-'),
    deviceCode: data.device_code,
    interval: Math.max(2, data.interval || 5),
    expiresAt: Date.now() + (data.expires_in ?? 900) * 1000,
    clientId: id
  }
  flows.set(flow.id, flow)
  return {
    flowId: flow.id,
    userCode: data.user_code,
    verificationUri: data.verification_uri,
    expiresIn: data.expires_in ?? 900,
    interval: flow.interval,
    message: data.message
  }
}

interface MsTokenResponse {
  access_token: string
  refresh_token: string
  expires_in: number
  token_type: string
}

interface XboxResponse {
  Token: string
  DisplayClaims: { xui: { uhs: string }[] }
}

async function xboxLive(msAccessToken: string): Promise<{ token: string; uhs: string }> {
  const xbl = await postJson<XboxResponse>('https://user.auth.xboxlive.com/user/authenticate', {
    Properties: {
      AuthMethod: 'RPS',
      SiteName: 'user.auth.xboxlive.com',
      RpsTicket: `d=${msAccessToken}`
    },
    RelyingParty: 'http://auth.xboxlive.com',
    TokenType: 'JWT'
  })
  let xsts: XboxResponse
  try {
    xsts = await postJson<XboxResponse>('https://xsts.auth.xboxlive.com/xsts/authorize', {
      Properties: { SandboxId: 'RETAIL', UserTokens: [xbl.Token] },
      RelyingParty: 'rp://api.minecraftservices.com/',
      TokenType: 'JWT'
    })
  } catch (e) {
    const err = e as Error & { xerr?: number }
    const map: Record<number, string> = {
      2148916233: '该微软账号尚未创建 Xbox 档案，请先在 Xbox 官网创建',
      2148916235: 'Xbox Live 在你所在的国家/地区不可用',
      2148916236: '该账号需要进行成人验证',
      2148916237: '该账号需要进行成人验证',
      2148916238: '该账号是未成年人账号，需要加入家庭组',
      2148916227: '该账号已被 Xbox Live 封禁'
    }
    const hint = err.xerr ? map[err.xerr] : undefined
    throw new Error(hint ?? `Xbox Live 验证失败: ${err.message}`)
  }
  return { token: xsts.Token, uhs: xsts.DisplayClaims.xui[0].uhs }
}

async function postJson<T>(url: string, body: unknown, headers: Record<string, string> = {}): Promise<T> {
  const res = await fetch(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Accept: 'application/json', ...headers },
    body: JSON.stringify(body)
  })
  const text = await res.text()
  if (!res.ok) {
    let detail = text
    try {
      const j = JSON.parse(text) as { XErr?: number; Message?: string }
      if (j.XErr) {
        const e = new Error(j.Message || 'Xbox 验证失败') as Error & { xerr?: number }
        e.xerr = j.XErr
        throw e
      }
      if (j.Message) detail = j.Message
    } catch (e) {
      if (e instanceof Error && (e as Error & { xerr?: number }).xerr) throw e
    }
    throw new Error(`HTTP ${res.status}: ${detail.slice(0, 300)}`)
  }
  return JSON.parse(text) as T
}

interface McProfile {
  id: string
  name: string
  skins?: { id: string; state: string; url: string; variant?: string }[]
  capes?: { id: string; state: string; url: string }[]
}

async function loginMinecraft(msAccessToken: string): Promise<{ token: string; profile: McProfile; xuid?: string }> {
  const { token, uhs } = await xboxLive(msAccessToken)
  const mc = await postJson<{ access_token: string; expires_in: number }>(
    'https://api.minecraftservices.com/authentication/login_with_xbox',
    { identityToken: `XBL3.0 x=${uhs};${token}` }
  )
  const profile = await fetchJson<McProfile>('https://api.minecraftservices.com/minecraft/profile', {
    headers: { Authorization: `Bearer ${mc.access_token}` },
    timeout: 20_000
  })
  return { token: mc.access_token, profile, xuid: uhs }
}

async function fetchAvatarData(url: string, timeout = 5_000): Promise<{ data: string; mime: string } | undefined> {
  try {
    const res = await fetch(url, { signal: AbortSignal.timeout(timeout), headers: { 'User-Agent': 'AuroraLauncher/1.0.0' } })
    if (!res.ok) return undefined
    const mime = (res.headers.get('content-type') ?? 'image/png').split(';')[0].trim()
    if (!mime.startsWith('image/')) return undefined
    const buffer = Buffer.from(await res.arrayBuffer())
    if (buffer.length < 200) return undefined
    return { data: buffer.toString('base64'), mime }
  } catch {
    return undefined
  }
}

async function fetchHeadAvatar(uuid: string): Promise<{ url?: string; mode?: 'head3d' | 'face'; source?: string }> {
  if (!uuid) return {}
  const encoded = encodeURIComponent(uuid)
  const sources3d: [string, string][] = [
    ['mineatar', `https://api.mineatar.io/head/${encoded}?scale=8`],
    ['visage', `https://visage.surgeplay.com/head/128/${encoded}`],
    ['crafatar', `https://crafatar.com/renders/head/${encoded}?size=128&overlay`],
    ['mineskin', `https://mineskin.eu/head/${encoded}/128`],
    ['mcheads', `https://mc-heads.net/head/${encoded}/128`]
  ]
  for (const [source, url] of sources3d) {
    const avatar = await fetchAvatarData(url, source === 'mcheads' ? 3_000 : 5_000)
    if (avatar) return { url: `data:${avatar.mime};base64,${avatar.data}`, mode: 'head3d', source }
  }
  const sources2d: [string, string][] = [
    ['visage-face', `https://visage.surgeplay.com/face/128/${encoded}`],
    ['mineskin-face', `https://mineskin.eu/avatar/${encoded}/128`],
    ['crafatar-face', `https://crafatar.com/avatars/${encoded}?size=128&overlay`]
  ]
  for (const [source, url] of sources2d) {
    const avatar = await fetchAvatarData(url, 4_000)
    if (avatar) return { url: `data:${avatar.mime};base64,${avatar.data}`, mode: 'face', source }
  }
  return {}
}

async function toAccount(ms: MsTokenResponse, mc: { token: string; profile: McProfile; xuid?: string }): Promise<Account> {
  const skin = mc.profile.skins?.find((s) => s.state === 'ACTIVE') ?? mc.profile.skins?.[0]
  // Mojang still returns http:// for some textures; the renderer CSP allows https only.
  const skinUrl = skin?.url?.replace(/^http:\/\//i, 'https://')
  const avatar = await fetchHeadAvatar(mc.profile.id)
  return {
    id: uid('acc-'),
    type: 'microsoft',
    username: mc.profile.name,
    uuid: mc.profile.id,
    accessToken: mc.token,
    refreshToken: ms.refresh_token,
    expiresAt: Date.now() + (ms.expires_in ?? 86400) * 1000,
    skinUrl,
    avatarUrl: avatar.url,
    avatarMode: avatar.mode,
    avatarSource: avatar.source,
    avatarVersion: 2,
    xuid: mc.xuid,
    createdAt: Date.now(),
    lastUsedAt: Date.now()
  }
}

export async function refreshStoredAvatars(): Promise<number> {
  const s = readStore()
  let changed = 0
  const DAY = 24 * 60 * 60 * 1000
  const now = Date.now()
  for (const account of s.accounts) {
    if (account.type !== 'microsoft') continue
    const tried = account.avatar3dTriedAt ?? 0
    if (account.avatarVersion === 2 && account.avatarUrl?.startsWith('data:')) continue
    if (!account.avatarUrl && now - tried < DAY) continue
    if (account.avatarMode === 'face' && now - tried < DAY) {
      account.avatarUrl = undefined
      account.avatarMode = undefined
      changed++
      continue
    }
    if (!account.uuid) continue
    account.avatar3dTriedAt = now
    const avatar = await fetchHeadAvatar(account.uuid)
    if (avatar.url) {
      const same = account.avatarUrl === avatar.url && account.avatarMode === avatar.mode
      account.avatarUrl = avatar.url
      account.avatarMode = avatar.mode
      account.avatarSource = avatar.source
      account.avatarVersion = 2
      if (!same) changed++
    } else if (account.avatarUrl || account.avatarMode === 'face') {
      account.avatarUrl = undefined
      account.avatarMode = undefined
      changed++
    }
  }
  if (changed) saveStore()
  return changed
}

export async function pollMicrosoftLogin(flowId: string): Promise<MsPollResult> {
  const flow = flows.get(flowId)
  if (!flow) return { status: 'error', message: '登录会话已失效，请重新开始' }
  if (Date.now() > flow.expiresAt) {
    flows.delete(flowId)
    return { status: 'expired', message: '设备代码已过期，请重新开始登录' }
  }
  let token: MsTokenResponse
  try {
    token = await postForm<MsTokenResponse>('https://login.microsoftonline.com/consumers/oauth2/v2.0/token', {
      grant_type: 'urn:ietf:params:oauth:grant-type:device_code',
      client_id: flow.clientId,
      device_code: flow.deviceCode
    })
  } catch (e) {
    const err = e as Error & { code?: string }
    if (err.code === 'authorization_pending') return { status: 'pending' }
    if (err.code === 'slow_down') {
      flow.interval = Math.min(30, flow.interval + 2)
      return { status: 'pending' }
    }
    if (err.code === 'expired_token') {
      flows.delete(flowId)
      return { status: 'expired', message: '设备代码已过期' }
    }
    if (err.code === 'authorization_declined') {
      flows.delete(flowId)
      return { status: 'error', message: '登录已被取消' }
    }
    return { status: 'error', message: err.message }
  }
  flows.delete(flowId)
  try {
    const mc = await loginMinecraft(token.access_token)
    const account = await toAccount(token, mc)
    const s = readStore()
    s.accounts = s.accounts.filter((a) => !(a.type === 'microsoft' && a.uuid === account.uuid))
    s.accounts.push(account)
    s.activeId = account.id
    saveStore()
    consoleLog('auth', 'info', `微软登录成功: ${account.username}`)
    return { status: 'success', account }
  } catch (e) {
    return { status: 'error', message: e instanceof Error ? e.message : String(e) }
  }
}

export function cancelMicrosoftLogin(flowId: string): void {
  flows.delete(flowId)
}

async function refreshMicrosoftToken(refreshToken: string): Promise<MsTokenResponse> {
  return postForm<MsTokenResponse>('https://login.microsoftonline.com/consumers/oauth2/v2.0/token', {
    grant_type: 'refresh_token',
    client_id: clientId(),
    refresh_token: refreshToken
  })
}

export async function refreshAccount(id: string): Promise<Account | null> {
  const s = readStore()
  const account = s.accounts.find((a) => a.id === id)
  if (!account) return null
  if (account.type !== 'microsoft' || !account.refreshToken) return account
  try {
    const token = await refreshMicrosoftToken(account.refreshToken)
    const mc = await loginMinecraft(token.access_token)
    const skin = mc.profile.skins?.find((x) => x.state === 'ACTIVE') ?? mc.profile.skins?.[0]
    const skinUrl = skin?.url?.replace(/^http:\/\//i, 'https://') ?? account.skinUrl
    const avatar = await fetchHeadAvatar(mc.profile.id)
    const updated: Account = {
      ...account,
      accessToken: mc.token,
      refreshToken: token.refresh_token ?? account.refreshToken,
      expiresAt: Date.now() + (token.expires_in ?? 86400) * 1000,
      username: mc.profile.name,
      uuid: mc.profile.id,
      skinUrl,
      avatarUrl: avatar.url,
      avatarMode: avatar.mode,
      avatarSource: avatar.source,
      avatarVersion: 2,
      xuid: mc.xuid ?? account.xuid,
      lastUsedAt: Date.now()
    }
    s.accounts = s.accounts.map((a) => (a.id === id ? updated : a))
    saveStore()
    return updated
  } catch (e) {
    consoleLog('auth', 'warn', `刷新微软令牌失败: ${e instanceof Error ? e.message : String(e)}`)
    return account
  }
}

export async function ensureFreshAccount(id?: string): Promise<Account | null> {
  const s = readStore()
  const account = id ? s.accounts.find((a) => a.id === id) ?? activeAccount() : activeAccount()
  if (!account) return null
  if (account.type === 'microsoft' && account.refreshToken) {
    const expires = account.expiresAt ?? 0
    if (expires && expires - Date.now() < 120_000) {
      return (await refreshAccount(account.id)) ?? account
    }
  }
  if (s.activeId !== account.id) setActiveAccount(account.id)
  return account
}

// ---------------------------------------------------------------------------
// authlib-injector / third party Yggdrasil
// ---------------------------------------------------------------------------

function normalizeServer(server: string): string {
  let s = server.trim().replace(/\/+$/, '')
  if (!/^https?:\/\//i.test(s)) s = `https://${s}`
  return s
}

interface YggdrasilAuthResponse {
  accessToken: string
  clientToken?: string
  selectedProfile?: { id: string; name: string }
  availableProfiles?: { id: string; name: string }[]
}

interface YggdrasilMeta {
  meta?: { serverName?: string; implementationName?: string; implementationVersion?: string }
  skinDomains?: string[]
  signaturePublickey?: string
}

export async function addAuthlibAccount(serverRaw: string, username: string, password: string): Promise<Account> {
  const server = normalizeServer(serverRaw)
  if (!username || !password) throw new Error('请输入用户名与密码')
  const clientToken = crypto.randomBytes(16).toString('hex')
  const res = await fetch(`${server}/authserver/authenticate`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
    body: JSON.stringify({ username, password, clientToken, agent: { name: 'Minecraft', version: 1 }, requestUser: false })
  })
  const text = await res.text()
  if (!res.ok) throw new Error(`验证失败 (HTTP ${res.status}): ${text.slice(0, 200)}`)
  const data = JSON.parse(text) as YggdrasilAuthResponse
  const profile = data.selectedProfile ?? data.availableProfiles?.[0]
  if (!profile) throw new Error('该账户没有可用的角色')
  let meta: YggdrasilMeta = {}
  try {
    meta = await fetchJson<YggdrasilMeta>(`${server}/api/yggdrasil`, { timeout: 10_000 })
  } catch {}
  const account: Account = {
    id: uid('acc-'),
    type: 'authlib',
    username: profile.name,
    uuid: profile.id.replace(/-/g, ''),
    accessToken: data.accessToken,
    authServer: server,
    authServerName: meta.meta?.serverName || new URL(server).hostname,
    createdAt: Date.now(),
    lastUsedAt: Date.now()
  }
  const s = readStore()
  s.accounts.push(account)
  s.activeId = account.id
  saveStore()
  return account
}

let authlibPathCache: string | null = null

export async function authlibJar(): Promise<string> {
  if (authlibPathCache && existsSync(authlibPathCache)) return authlibPathCache
  const meta = await fetchJson<{ version: string; download_url: string; checksums?: { sha256?: string } }>(
    'https://authlib-injector.yushi.moe/artifact/latest.json',
    { timeout: 20_000 }
  )
  const dest = authlibJarPath(meta.version)
  if (!existsSync(dest)) {
    await downloadFile({ url: meta.download_url, dest, signal: undefined, retries: 2 })
  }
  authlibPathCache = dest
  return dest
}

export function accountDisplayName(account: Account): string {
  return account.username
}
