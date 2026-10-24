// Generates a compact MC百科 name index from PCL-CE's cached mcmod database.
import fs from 'node:fs'
import zlib from 'node:zlib'
import path from 'node:path'

const source = path.resolve('../PCL-CE-dev/Plain Craft Launcher 2/Resources/mcmod.buf')
if (!fs.existsSync(source)) {
  console.log('mcmod.buf not found, keep existing index')
  process.exit(0)
}
const buf = zlib.gunzipSync(fs.readFileSync(source))

function varint(data, offset) {
  let value = 0
  let shift = 0
  while (offset < data.length) {
    const byte = data[offset++]
    value |= (byte & 0x7f) << shift
    if (!(byte & 0x80)) return [value >>> 0, offset]
    shift += 7
  }
  throw new Error('unexpected end of varint')
}

function fields(data) {
  let offset = 0
  const out = []
  while (offset < data.length) {
    let key
    ;[key, offset] = varint(data, offset)
    const field = key >>> 3
    const wire = key & 7
    if (wire === 0) {
      let value
      ;[value, offset] = varint(data, offset)
      out.push([field, wire, value])
    } else if (wire === 2) {
      let length
      ;[length, offset] = varint(data, offset)
      out.push([field, wire, data.subarray(offset, offset + length)])
      offset += length
    } else if (wire === 1) {
      out.push([field, wire, data.subarray(offset, offset + 8)])
      offset += 8
    } else if (wire === 5) {
      out.push([field, wire, data.subarray(offset, offset + 4)])
      offset += 4
    } else {
      throw new Error(`unsupported wire type ${wire}`)
    }
  }
  return out
}

const byCf = {}
const byMr = {}
const byCn = {}
let total = 0
for (const [, wire, entry] of fields(buf)) {
  if (wire !== 2) continue
  const row = {}
  for (const [field, , value] of fields(entry)) {
    if (field === 1) row.wikiId = value
    if (field === 2) row.chineseName = Buffer.from(value).toString('utf8')
    if (field === 3) row.cfSlug = Buffer.from(value).toString('utf8')
    if (field === 4) row.mrSlug = Buffer.from(value).toString('utf8')
  }
  const raw = (row.chineseName || '').trim()
  const match = raw.match(/^(.*?)\s*[（(]([^）)]+)[）)]\s*$/)
  const chinese = (match ? match[1] : raw).replace(/\s+/g, ' ').trim()
  const english = match ? match[2].replace(/\s+/g, ' ').trim() : ''
  if (!chinese || !/[\u3400-\u9fff]/.test(chinese)) continue
  if (row.cfSlug && !byCf[row.cfSlug]) byCf[row.cfSlug] = chinese
  if (row.mrSlug && !byMr[row.mrSlug]) byMr[row.mrSlug] = chinese
  if (chinese && english && !byCn[chinese]) byCn[chinese] = english
  total++
}

const out = path.resolve('src/main/core/mcmod-names.json')
fs.writeFileSync(out, JSON.stringify({ cf: byCf, mr: byMr, cn: byCn }))
console.log(`mcmod names: ${total} entries, cf=${Object.keys(byCf).length}, mr=${Object.keys(byMr).length}, cn=${Object.keys(byCn).length} -> ${out}`)
