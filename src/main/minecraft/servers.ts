import fs from 'node:fs'
import path from 'node:path'
import net from 'node:net'
import { gameDirOf } from './content'
import { parseNbtFile, writeNbtFile, nbt, TAG_COMPOUND, TAG_BYTE, TAG_STRING, type NbtTag } from '../core/nbt'
import type { ServerEntry, ServerStatus } from '../../shared/types'

function serversFile(instanceId: string): string {
  return path.join(gameDirOf(instanceId), 'servers.dat')
}

function toPlainServer(tag: NbtTag): ServerEntry | null {
  if (tag.type !== 'compound') return null
  const name = tag.value.name
  const ip = tag.value.ip
  if (!ip || ip.type !== 'string') return null
  const icon = tag.value.icon
  const hidden = tag.value.hidden
  return {
    name: name && name.type === 'string' ? name.value : ip.value,
    ip: ip.value,
    icon: icon && icon.type === 'string' ? `data:image/png;base64,${icon.value}` : undefined,
    hidden: hidden && hidden.type === 'byte' ? hidden.value === 1 : false
  }
}

export async function listServers(instanceId: string): Promise<ServerEntry[]> {
  const file = serversFile(instanceId)
  if (!fs.existsSync(file)) return []
  const tag = parseNbtFile(file)
  if (!tag || tag.type !== 'compound') return []
  const servers = tag.value.servers
  if (!servers || servers.type !== 'list') return []
  return servers.value.map(toPlainServer).filter((s): s is ServerEntry => !!s)
}

function writeServers(instanceId: string, servers: ServerEntry[]): void {
  const list: NbtTag = {
    type: 'list',
    elementType: TAG_COMPOUND,
    value: servers.map((s) => {
      const fields: Record<string, NbtTag> = {
        name: nbt.string(s.name),
        ip: nbt.string(s.ip)
      }
      if (s.icon) {
        const b64 = s.icon.replace(/^data:image\/png;base64,/, '')
        fields.icon = nbt.string(b64)
      }
      if (s.hidden) fields.hidden = nbt.byte(1)
      return nbt.compound(fields)
    })
  }
  const root = nbt.compound({ servers: list })
  writeNbtFile(serversFile(instanceId), root, true)
}

export async function addServer(instanceId: string, entry: ServerEntry): Promise<ServerEntry[]> {
  const list = await listServers(instanceId)
  const ip = entry.ip.trim()
  if (!ip) throw new Error('服务器地址不能为空')
  const existing = list.findIndex((s) => s.ip === ip)
  const next = { name: entry.name?.trim() || ip, ip, icon: entry.icon }
  if (existing >= 0) list[existing] = next
  else list.push(next)
  writeServers(instanceId, list)
  return list
}

export async function removeServer(instanceId: string, ip: string): Promise<ServerEntry[]> {
  const list = (await listServers(instanceId)).filter((s) => s.ip !== ip)
  writeServers(instanceId, list)
  return list
}

// ---------------------------------------------------------------------------
// Minecraft server ping (status protocol)
// ---------------------------------------------------------------------------

function varInt(value: number): Buffer {
  const bytes: number[] = []
  let v = value
  for (;;) {
    if ((v & ~0x7f) === 0) {
      bytes.push(v)
      break
    }
    bytes.push((v & 0x7f) | 0x80)
    v >>>= 7
  }
  return Buffer.from(bytes)
}

function readVarInt(buf: Buffer, offset: number): { value: number; size: number } | null {
  let value = 0
  let size = 0
  let current = 0
  for (;;) {
    if (offset + size >= buf.length) return null
    const byte = buf[offset + size]
    value |= (byte & 0x7f) << (7 * current)
    size++
    current++
    if ((byte & 0x80) === 0) break
    if (size > 5) return null
  }
  return { value, size }
}

function mcString(value: string): Buffer {
  const str = Buffer.from(value, 'utf8')
  return Buffer.concat([varInt(str.length), str])
}

function packet(id: number, body: Buffer): Buffer {
  const payload = Buffer.concat([varInt(id), body])
  return Buffer.concat([varInt(payload.length), payload])
}

function parseMotd(desc: unknown): { text: string; html?: string } {
  if (!desc) return { text: '' }
  if (typeof desc === 'string') return { text: desc }
  const obj = desc as Record<string, unknown>
  let text = ''
  let html = ''
  const walk = (node: Record<string, unknown>): void => {
    const t = typeof node.text === 'string' ? node.text : ''
    text += t
    const color = typeof node.color === 'string' ? node.color : undefined
    const safe = t.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
    html += color ? `<span style="color:${cssColor(color)}">${safe}</span>` : safe
    if (Array.isArray(node.extra)) for (const child of node.extra) if (child && typeof child === 'object') walk(child as Record<string, unknown>)
  }
  walk(obj)
  return { text, html }
}

const COLOR_MAP: Record<string, string> = {
  black: '#000000',
  dark_blue: '#0000AA',
  dark_green: '#00AA00',
  dark_aqua: '#00AAAA',
  dark_red: '#AA0000',
  dark_purple: '#AA00AA',
  gold: '#FFAA00',
  gray: '#AAAAAA',
  dark_gray: '#555555',
  blue: '#5555FF',
  green: '#55FF55',
  aqua: '#55FFFF',
  red: '#FF5555',
  light_purple: '#FF55FF',
  yellow: '#FFFF55',
  white: '#FFFFFF'
}

function cssColor(color: string): string {
  if (color.startsWith('#')) return color
  return COLOR_MAP[color] ?? '#FFFFFF'
}

export function pingServer(address: string, timeout = 5000): Promise<ServerStatus> {
  return new Promise((resolve) => {
    const cleaned = address.trim().replace(/^minecraft:\/\//i, '')
    const [host, portRaw] = splitAddress(cleaned)
    const port = portRaw ?? 25565
    const started = Date.now()
    const socket = new net.Socket()
    let buffer = Buffer.alloc(0)
    let settled = false
    const finish = (status: ServerStatus): void => {
      if (settled) return
      settled = true
      try {
        socket.destroy()
      } catch {}
      resolve(status)
    }
    const timer = setTimeout(() => finish({ online: false, error: '连接超时' }), timeout)
    socket.on('error', (err) => {
      clearTimeout(timer)
      finish({ online: false, error: err.message })
    })
    socket.on('close', () => {
      clearTimeout(timer)
      if (!settled) finish({ online: false, error: '连接已关闭' })
    })
    socket.connect(port, host, () => {
      const handshakeBody = Buffer.concat([varInt(767), mcString(host), Buffer.from([(port >> 8) & 0xff, port & 0xff]), varInt(1)])
      socket.write(Buffer.concat([packet(0x00, handshakeBody), packet(0x00, Buffer.alloc(0))]))
    })
    socket.on('data', (chunk) => {
      buffer = Buffer.concat([buffer, chunk])
      const length = readVarInt(buffer, 0)
      if (!length) return
      if (buffer.length < length.size + length.value) return
      const body = buffer.subarray(length.size, length.size + length.value)
      let offset = 0
      const packetId = readVarInt(body, offset)
      if (!packetId) return
      offset += packetId.size
      if (packetId.value !== 0x00) return
      const strLen = readVarInt(body, offset)
      if (!strLen) return
      offset += strLen.size
      const jsonText = body.subarray(offset, offset + strLen.value).toString('utf8')
      clearTimeout(timer)
      try {
        const json = JSON.parse(jsonText) as {
          version?: { name?: string; protocol?: number }
          players?: { online?: number; max?: number; sample?: { name: string; id: string }[] }
          description?: unknown
          favicon?: string
        }
        const motd = parseMotd(json.description)
        finish({
          online: true,
          version: json.version ? { name: json.version.name ?? '', protocol: json.version.protocol ?? 0 } : undefined,
          players: json.players
            ? { online: json.players.online ?? 0, max: json.players.max ?? 0, sample: json.players.sample }
            : undefined,
          motd,
          ping: Date.now() - started,
          favicon: json.favicon
        })
      } catch (e) {
        finish({ online: false, error: `响应解析失败: ${String(e)}` })
      }
    })
  })
}

function splitAddress(address: string): [string, number?] {
  // IPv6 in brackets
  if (address.startsWith('[')) {
    const close = address.indexOf(']')
    if (close > 0) {
      const host = address.slice(1, close)
      const rest = address.slice(close + 1)
      const port = rest.startsWith(':') ? parseInt(rest.slice(1), 10) : undefined
      return [host, Number.isFinite(port) ? port : undefined]
    }
  }
  const idx = address.lastIndexOf(':')
  if (idx > 0 && address.indexOf(':') === idx) {
    const port = parseInt(address.slice(idx + 1), 10)
    if (Number.isFinite(port)) return [address.slice(0, idx), port]
  }
  return [address, undefined]
}
