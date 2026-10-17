// Dependency-free, deterministic PNG generation. P glyph stays inside maskable safe circle.
import { mkdir, writeFile } from 'node:fs/promises'
import { deflateSync } from 'node:zlib'
const crc32 = (bytes) => {
  let crc = 0xffffffff
  for (const byte of bytes) {
    crc ^= byte
    for (let bit = 0; bit < 8; bit++) crc = (crc >>> 1) ^ ((crc & 1) ? 0xedb88320 : 0)
  }
  return (crc ^ 0xffffffff) >>> 0
}
function chunk(type, data) {
  const label = Buffer.from(type)
  const length = Buffer.alloc(4); length.writeUInt32BE(data.length)
  const crc = Buffer.alloc(4); crc.writeUInt32BE(crc32(Buffer.concat([label, data])))
  return Buffer.concat([length, label, data, crc])
}
await mkdir(new URL('../public/icons/', import.meta.url), { recursive: true })
for (const size of [192, 512]) {
  const pixels = Buffer.alloc((size * 4 + 1) * size)
  for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) {
    const u = x / size; const v = y / size
    const stem = u > .34 && u < .43 && v > .27 && v < .73
    const bowl = u > .42 && u < .67 && v > .27 && v < .54 && !(u < .57 && v > .36 && v < .45)
    const color = stem || bowl ? [246, 243, 237, 255] : [35, 56, 45, 255]
    pixels.set(color, y * (size * 4 + 1) + 1 + x * 4)
  }
  const header = Buffer.alloc(13); header.writeUInt32BE(size, 0); header.writeUInt32BE(size, 4); header[8] = 8; header[9] = 6
  const png = Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk('IHDR', header), chunk('IDAT', deflateSync(pixels)), chunk('IEND', Buffer.alloc(0))])
  for (const name of ['icon', 'maskable']) await writeFile(new URL(`../public/icons/${name}-${size}.png`, import.meta.url), png)
}
console.log('Generated 192/512 PNG icons (any + maskable).')
