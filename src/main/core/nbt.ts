import fs from 'node:fs'
import zlib from 'node:zlib'

// Minimal Named Binary Tag implementation sufficient for level.dat / servers.dat.

export type NbtTag =
  | { type: 'byte'; value: number }
  | { type: 'short'; value: number }
  | { type: 'int'; value: number }
  | { type: 'long'; value: number }
  | { type: 'float'; value: number }
  | { type: 'double'; value: number }
  | { type: 'byteArray'; value: Buffer }
  | { type: 'string'; value: string }
  | { type: 'list'; elementType: number; value: NbtTag[] }
  | { type: 'compound'; value: Record<string, NbtTag> }
  | { type: 'intArray'; value: number[] }
  | { type: 'longArray'; value: number[] }

const TAG_END = 0
const TAG_BYTE = 1
const TAG_SHORT = 2
const TAG_INT = 3
const TAG_LONG = 4
const TAG_FLOAT = 5
const TAG_DOUBLE = 6
const TAG_BYTE_ARRAY = 7
const TAG_STRING = 8
const TAG_LIST = 9
const TAG_COMPOUND = 10
const TAG_INT_ARRAY = 11
const TAG_LONG_ARRAY = 12

class Reader {
  offset = 0
  constructor(private buf: Buffer) {}

  get remaining(): number {
    return this.buf.length - this.offset
  }

  u8(): number {
    return this.buf.readUInt8(this.offset++)
  }
  i8(): number {
    return this.buf.readInt8(this.offset++)
  }
  i16(): number {
    const v = this.buf.readInt16BE(this.offset)
    this.offset += 2
    return v
  }
  u16(): number {
    const v = this.buf.readUInt16BE(this.offset)
    this.offset += 2
    return v
  }
  i32(): number {
    const v = this.buf.readInt32BE(this.offset)
    this.offset += 4
    return v
  }
  i64(): number {
    const v = this.buf.readBigInt64BE(this.offset)
    this.offset += 8
    return Number(v)
  }
  f32(): number {
    const v = this.buf.readFloatBE(this.offset)
    this.offset += 4
    return v
  }
  f64(): number {
    const v = this.buf.readDoubleBE(this.offset)
    this.offset += 8
    return v
  }
  bytes(n: number): Buffer {
    const v = this.buf.subarray(this.offset, this.offset + n)
    this.offset += n
    return v
  }
  string(): string {
    const len = this.u16()
    return this.bytes(len).toString('utf8')
  }
}

function readPayload(r: Reader, type: number): NbtTag {
  switch (type) {
    case TAG_BYTE:
      return { type: 'byte', value: r.i8() }
    case TAG_SHORT:
      return { type: 'short', value: r.i16() }
    case TAG_INT:
      return { type: 'int', value: r.i32() }
    case TAG_LONG:
      return { type: 'long', value: r.i64() }
    case TAG_FLOAT:
      return { type: 'float', value: r.f32() }
    case TAG_DOUBLE:
      return { type: 'double', value: r.f64() }
    case TAG_BYTE_ARRAY: {
      const len = r.i32()
      return { type: 'byteArray', value: Buffer.from(r.bytes(len)) }
    }
    case TAG_STRING:
      return { type: 'string', value: r.string() }
    case TAG_LIST: {
      const elementType = r.u8()
      const len = r.i32()
      const value: NbtTag[] = []
      for (let i = 0; i < len; i++) value.push(readPayload(r, elementType))
      return { type: 'list', elementType, value }
    }
    case TAG_COMPOUND: {
      const value: Record<string, NbtTag> = {}
      for (;;) {
        const t = r.u8()
        if (t === TAG_END) break
        const name = r.string()
        value[name] = readPayload(r, t)
      }
      return { type: 'compound', value }
    }
    case TAG_INT_ARRAY: {
      const len = r.i32()
      const value: number[] = []
      for (let i = 0; i < len; i++) value.push(r.i32())
      return { type: 'intArray', value }
    }
    case TAG_LONG_ARRAY: {
      const len = r.i32()
      const value: number[] = []
      for (let i = 0; i < len; i++) value.push(r.i64())
      return { type: 'longArray', value }
    }
    default:
      throw new Error(`未知的 NBT 类型: ${type}`)
  }
}

export function parseNbt(buffer: Buffer): NbtTag {
  let data = buffer
  if (data.length > 2 && data[0] === 0x1f && data[1] === 0x8b) {
    data = zlib.gunzipSync(data)
  }
  const r = new Reader(data)
  const type = r.u8()
  const name = r.string()
  const tag = readPayload(r, type)
  if (tag.type === 'compound') {
    // expose root name on the object for debugging
    Object.defineProperty(tag.value, '$name', { value: name, enumerable: false })
  }
  return tag
}

export function parseNbtFile(file: string): NbtTag | null {
  try {
    return parseNbt(fs.readFileSync(file))
  } catch {
    return null
  }
}

export function plain(tag: NbtTag | undefined): unknown {
  if (!tag) return undefined
  switch (tag.type) {
    case 'compound': {
      const out: Record<string, unknown> = {}
      for (const [k, v] of Object.entries(tag.value)) out[k] = plain(v)
      return out
    }
    case 'list':
      return tag.value.map(plain)
    case 'byteArray':
    case 'intArray':
    case 'longArray':
      return tag.value
    default:
      return tag.value
  }
}

export function tagNumber(tag: NbtTag | undefined, fallback = 0): number {
  if (!tag) return fallback
  if (tag.type === 'byte' || tag.type === 'short' || tag.type === 'int' || tag.type === 'long' || tag.type === 'float' || tag.type === 'double') {
    return tag.value
  }
  return fallback
}

export function tagString(tag: NbtTag | undefined, fallback = ''): string {
  return tag && tag.type === 'string' ? tag.value : fallback
}

function tagTypeId(tag: NbtTag): number {
  switch (tag.type) {
    case 'byte':
      return TAG_BYTE
    case 'short':
      return TAG_SHORT
    case 'int':
      return TAG_INT
    case 'long':
      return TAG_LONG
    case 'float':
      return TAG_FLOAT
    case 'double':
      return TAG_DOUBLE
    case 'byteArray':
      return TAG_BYTE_ARRAY
    case 'string':
      return TAG_STRING
    case 'list':
      return TAG_LIST
    case 'compound':
      return TAG_COMPOUND
    case 'intArray':
      return TAG_INT_ARRAY
    case 'longArray':
      return TAG_LONG_ARRAY
  }
}

function writeString(out: number[], value: string): void {
  const buf = Buffer.from(value, 'utf8')
  out.push((buf.length >> 8) & 0xff, buf.length & 0xff)
  for (const b of buf) out.push(b)
}

function writePayload(out: number[], tag: NbtTag): void {
  switch (tag.type) {
    case 'byte':
      out.push(tag.value & 0xff)
      break
    case 'short':
      out.push((tag.value >> 8) & 0xff, tag.value & 0xff)
      break
    case 'int':
      out.push((tag.value >>> 24) & 0xff, (tag.value >>> 16) & 0xff, (tag.value >>> 8) & 0xff, tag.value & 0xff)
      break
    case 'long': {
      const buf = Buffer.alloc(8)
      buf.writeBigInt64BE(BigInt(Math.trunc(tag.value)))
      out.push(...buf)
      break
    }
    case 'float': {
      const buf = Buffer.alloc(4)
      buf.writeFloatBE(tag.value)
      out.push(...buf)
      break
    }
    case 'double': {
      const buf = Buffer.alloc(8)
      buf.writeDoubleBE(tag.value)
      out.push(...buf)
      break
    }
    case 'byteArray': {
      const len = tag.value.length
      out.push((len >>> 24) & 0xff, (len >>> 16) & 0xff, (len >>> 8) & 0xff, len & 0xff)
      out.push(...tag.value)
      break
    }
    case 'string':
      writeString(out, tag.value)
      break
    case 'list': {
      out.push(tag.elementType)
      const len = tag.value.length
      out.push((len >>> 24) & 0xff, (len >>> 16) & 0xff, (len >>> 8) & 0xff, len & 0xff)
      for (const item of tag.value) writePayload(out, item)
      break
    }
    case 'compound': {
      for (const [name, child] of Object.entries(tag.value)) {
        out.push(tagTypeId(child))
        writeString(out, name)
        writePayload(out, child)
      }
      out.push(TAG_END)
      break
    }
    case 'intArray': {
      const len = tag.value.length
      out.push((len >>> 24) & 0xff, (len >>> 16) & 0xff, (len >>> 8) & 0xff, len & 0xff)
      for (const v of tag.value) out.push((v >>> 24) & 0xff, (v >>> 16) & 0xff, (v >>> 8) & 0xff, v & 0xff)
      break
    }
    case 'longArray': {
      const len = tag.value.length
      out.push((len >>> 24) & 0xff, (len >>> 16) & 0xff, (len >>> 8) & 0xff, len & 0xff)
      for (const v of tag.value) {
        const buf = Buffer.alloc(8)
        buf.writeBigInt64BE(BigInt(Math.trunc(v)))
        out.push(...buf)
      }
      break
    }
  }
}

export function serializeNbt(root: NbtTag, rootName = ''): Buffer {
  const out: number[] = []
  out.push(tagTypeId(root))
  writeString(out, rootName)
  writePayload(out, root)
  return Buffer.from(out)
}

export function writeNbtFile(file: string, root: NbtTag, gzip = true, rootName = ''): void {
  const raw = serializeNbt(root, rootName)
  fs.writeFileSync(file, gzip ? zlib.gzipSync(raw) : raw)
}

export const nbt = {
  byte: (value: number): NbtTag => ({ type: 'byte', value }),
  int: (value: number): NbtTag => ({ type: 'int', value }),
  long: (value: number): NbtTag => ({ type: 'long', value }),
  string: (value: string): NbtTag => ({ type: 'string', value }),
  compound: (value: Record<string, NbtTag>): NbtTag => ({ type: 'compound', value }),
  list: (elementType: number, value: NbtTag[]): NbtTag => ({ type: 'list', elementType, value })
}

export { TAG_COMPOUND, TAG_STRING, TAG_BYTE }
