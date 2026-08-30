// Generates public/icons/icon-192.png and icon-512.png without any image library:
// a dark rounded square with a 3×2 grid of lighter "keys" (the deck look). Run: node scripts/gen-icons.mjs
import { deflateSync } from "node:zlib";
import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const outDir = join(here, "..", "public", "icons");
mkdirSync(outDir, { recursive: true });

const crcTable = new Uint32Array(256).map((_, n) => {
  let c = n;
  for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
  return c >>> 0;
});
const crc32 = (buf) => {
  let c = 0xffffffff;
  for (const b of buf) c = crcTable[(c ^ b) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
};
const chunk = (type, data) => {
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length);
  const body = Buffer.concat([Buffer.from(type, "ascii"), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(body));
  return Buffer.concat([len, body, crc]);
};

function png(size, pixel) {
  const raw = Buffer.alloc((size * 4 + 1) * size);
  for (let y = 0; y < size; y++) {
    raw[y * (size * 4 + 1)] = 0; // filter: none
    for (let x = 0; x < size; x++) {
      const [r, g, b, a] = pixel(x, y);
      raw.set([r, g, b, a], y * (size * 4 + 1) + 1 + x * 4);
    }
  }
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(size, 0);
  ihdr.writeUInt32BE(size, 4);
  ihdr[8] = 8; // bit depth
  ihdr[9] = 6; // RGBA
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk("IHDR", ihdr),
    chunk("IDAT", deflateSync(raw)),
    chunk("IEND", Buffer.alloc(0)),
  ]);
}

const inRoundedRect = (x, y, x0, y0, w, h, r) => {
  const cx = Math.max(x0 + r, Math.min(x, x0 + w - r));
  const cy = Math.max(y0 + r, Math.min(y, y0 + h - r));
  return (x - cx) ** 2 + (y - cy) ** 2 <= r * r;
};

function icon(size) {
  const pad = size * 0.06; // maskable safe zone friendly (content well inside 80%)
  const keys = [
    [0, 0, [0x2f, 0x6f, 0xdb]],
    [1, 0, [0xc2, 0x40, 0x8f]],
    [2, 0, [0x3c, 0x8d, 0x5a]],
    [0, 1, [0xd0, 0x8a, 0x2a]],
    [1, 1, [0x8a, 0x2f, 0x2f]],
    [2, 1, [0x4a, 0x4a, 0x4a]],
  ];
  const cols = 3;
  const rows = 2;
  const gap = size * 0.05;
  const gridW = size - pad * 2 - gap * 2;
  const key = (gridW - gap * (cols - 1)) / cols;
  const gridH = key * rows + gap * (rows - 1);
  const top = (size - gridH) / 2;
  const left = pad + gap;
  return png(size, (x, y) => {
    if (!inRoundedRect(x + 0.5, y + 0.5, 0, 0, size, size, size * 0.2)) return [0, 0, 0, 0];
    for (const [c, r, rgb] of keys) {
      const kx = left + c * (key + gap);
      const ky = top + r * (key + gap);
      if (inRoundedRect(x + 0.5, y + 0.5, kx, ky, key, key, key * 0.22)) return [...rgb, 255];
    }
    return [0x12, 0x12, 0x12, 255];
  });
}

for (const size of [192, 512]) {
  writeFileSync(join(outDir, `icon-${size}.png`), icon(size));
  console.log(`wrote icon-${size}.png`);
}
