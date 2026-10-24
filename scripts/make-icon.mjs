// Generates a 256x256 PNG launcher icon without external image dependencies.
import zlib from 'node:zlib'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const SIZE = 256

const pixels = Buffer.alloc(SIZE * SIZE * 4)

function lerp(a, b, t) {
  return a + (b - a) * t
}

for (let y = 0; y < SIZE; y++) {
  for (let x = 0; x < SIZE; x++) {
    const i = (y * SIZE + x) * 4
    const u = x / SIZE
    const v = y / SIZE
    // aurora background gradient
    const t = Math.min(1, Math.max(0, (u * 0.6 + v * 0.5)))
    let r = lerp(18, 32, t)
    let g = lerp(24, 96, t)
    let b = lerp(48, 128, t)
    // diagonal aurora bands
    const band1 = Math.exp(-Math.pow((v - 0.38 - Math.sin(u * 3.1) * 0.14) * 9, 2))
    const band2 = Math.exp(-Math.pow((v - 0.62 - Math.cos(u * 2.4) * 0.12) * 11, 2))
    r += band1 * 40 + band2 * 20
    g += band1 * 170 + band2 * 80
    b += band1 * 140 + band2 * 170
    // dark vignette
    const cx = u - 0.5
    const cy = v - 0.5
    const vignette = 1 - Math.min(1, Math.sqrt(cx * cx + cy * cy) * 1.15)
    r *= 0.55 + vignette * 0.55
    g *= 0.55 + vignette * 0.55
    b *= 0.55 + vignette * 0.55
    // rounded square mask
    const margin = 10
    const radius = 52
    const qx = Math.abs(x - SIZE / 2) - (SIZE / 2 - margin - radius)
    const qy = Math.abs(y - SIZE / 2) - (SIZE / 2 - margin - radius)
    const outside = Math.hypot(Math.max(qx, 0), Math.max(qy, 0)) - radius
    if (outside > 0) {
      const alpha = Math.max(0, 1 - outside / 2.5)
      pixels[i] = Math.round(r)
      pixels[i + 1] = Math.round(g)
      pixels[i + 2] = Math.round(b)
      pixels[i + 3] = Math.round(alpha * 255)
      continue
    }
    // play triangle
    const px = x - SIZE * 0.41
    const py = y - SIZE * 0.3
    const inTri = px >= 0 && px <= SIZE * 0.28 && py >= px * 0.55 && py <= SIZE * 0.7 - px * 0.55
    if (inTri) {
      r = 245
      g = 250
      b = 255
    }
    pixels[i] = Math.round(Math.min(255, r))
    pixels[i + 1] = Math.round(Math.min(255, g))
    pixels[i + 2] = Math.round(Math.min(255, b))
    pixels[i + 3] = 255
  }
}

// build PNG scanlines with filter byte 0
const raw = Buffer.alloc((SIZE * 4 + 1) * SIZE)
for (let y = 0; y < SIZE; y++) {
  raw[y * (SIZE * 4 + 1)] = 0
  pixels.copy(raw, y * (SIZE * 4 + 1) + 1, y * SIZE * 4, (y + 1) * SIZE * 4)
}
const compressed = zlib.deflateSync(raw, { level: 9 })

function crc32(buf) {
  let c = ~0
  for (let i = 0; i < buf.length; i++) {
    c ^= buf[i]
    for (let k = 0; k < 8; k++) c = (c >>> 1) ^ (0xedb88320 & -(c & 1))
  }
  return ~c >>> 0
}

function chunk(type, data) {
  const len = Buffer.alloc(4)
  len.writeUInt32BE(data.length)
  const typeBuf = Buffer.from(type, 'ascii')
  const crc = Buffer.alloc(4)
  crc.writeUInt32BE(crc32(Buffer.concat([typeBuf, data])))
  return Buffer.concat([len, typeBuf, data, crc])
}

const ihdr = Buffer.alloc(13)
ihdr.writeUInt32BE(SIZE, 0)
ihdr.writeUInt32BE(SIZE, 4)
ihdr[8] = 8
ihdr[9] = 6
ihdr[10] = 0
ihdr[11] = 0
ihdr[12] = 0

const png = Buffer.concat([
  Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
  chunk('IHDR', ihdr),
  chunk('IDAT', compressed),
  chunk('IEND', Buffer.alloc(0))
])

const outDir = path.join(root, 'resources')
fs.mkdirSync(outDir, { recursive: true })
fs.writeFileSync(path.join(outDir, 'icon.png'), png)
console.log(`icon written: ${png.length} bytes`)
