// Generates OpenList.Desktop app icon (blue rounded square + white ring "O")
// Output: app-icon.png (256x256 RGBA) and app-icon.ico (PNG-embedded).
const fs = require("fs");
const path = require("path");
const zlib = require("zlib");

const S = 256;
const px = Buffer.alloc(S * S * 4);

function smooth(edge0, edge1, x) {
  const t = Math.min(1, Math.max(0, (x - edge0) / (edge1 - edge0)));
  return t * t * (3 - 2 * t);
}

function roundedRectDist(x, y, cx, cy, hw, hh, r) {
  const qx = Math.abs(x - cx) - (hw - r);
  const qy = Math.abs(y - cy) - (hh - r);
  const ax = Math.max(qx, 0), ay = Math.max(qy, 0);
  return Math.sqrt(ax * ax + ay * ay) + Math.min(Math.max(qx, qy), 0) - r;
}

for (let y = 0; y < S; y++) {
  for (let x = 0; x < S; x++) {
    const i = (y * S + x) * 4;
    // sample at pixel center
    const sx = x + 0.5, sy = y + 0.5;

    // background: rounded square with vertical gradient
    const d = roundedRectDist(sx, sy, S / 2, S / 2, S / 2 - 1, S / 2 - 1, 56);
    const bgA = 1 - smooth(-0.5, 0.5, d);
    const t = sy / S;
    const br = 37 + (30 - 37) * t, bg = 99 + (64 - 99) * t, bb = 235 + (175 - 235) * t;

    // white ring (outer 76, inner 46) with AA
    const dist = Math.sqrt((sx - S / 2) ** 2 + (sy - S / 2) ** 2);
    const ringA = smooth(76 + 0.5, 75.5 - 0.5, dist) * (1 - smooth(45.5 - 0.5, 46 + 0.5, dist));

    const r = bg * (1 - ringA) + 255 * ringA;
    const g = bg2 = bg * (1 - ringA) + 255 * ringA;
    const b = bb * (1 - ringA) + 255 * ringA;

    px[i] = Math.round(r);
    px[i + 1] = Math.round(g);
    px[i + 2] = Math.round(b);
    px[i + 3] = Math.round(255 * bgA);
  }
}

// --- PNG encode ---
function crc32(buf) {
  let table = crc32.table;
  if (!table) {
    table = crc32.table = new Int32Array(256);
    for (let n = 0; n < 256; n++) {
      let c = n;
      for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
      table[n] = c;
    }
  }
  let c = 0xffffffff;
  for (let i = 0; i < buf.length; i++) c = table[(c ^ buf[i]) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

function chunk(type, data) {
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length);
  const body = Buffer.concat([Buffer.from(type, "ascii"), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(body));
  return Buffer.concat([len, body, crc]);
}

const ihdr = Buffer.alloc(13);
ihdr.writeUInt32BE(S, 0);
ihdr.writeUInt32BE(S, 4);
ihdr[8] = 8;  // bit depth
ihdr[9] = 6;  // RGBA
// scanlines with filter byte 0
const raw = Buffer.alloc(S * (S * 4 + 1));
for (let y = 0; y < S; y++) {
  raw[y * (S * 4 + 1)] = 0;
  px.copy(raw, y * (S * 4 + 1) + 1, y * S * 4, (y + 1) * S * 4);
}
const png = Buffer.concat([
  Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
  chunk("IHDR", ihdr),
  chunk("IDAT", zlib.deflateSync(raw, { level: 9 })),
  chunk("IEND", Buffer.alloc(0)),
]);

const outDir = path.join(__dirname, "..", "src", "OpenList.Desktop", "Assets");
fs.mkdirSync(outDir, { recursive: true });
fs.writeFileSync(path.join(outDir, "app-icon.png"), png);

// --- ICO (single 256x256 PNG entry) ---
const entry = Buffer.alloc(16);
entry[0] = 0; // 256
entry[1] = 0;
entry[2] = 0; // colors
entry[3] = 0;
entry.writeUInt16LE(1, 4);  // planes
entry.writeUInt16LE(32, 6); // bpp
entry.writeUInt32LE(png.length, 8);
entry.writeUInt32LE(6 + 16, 12); // offset
const header = Buffer.alloc(6);
header.writeUInt16LE(0, 0);
header.writeUInt16LE(1, 2);
header.writeUInt16LE(1, 4);
fs.writeFileSync(path.join(outDir, "app-icon.ico"), Buffer.concat([header, entry, png]));

console.log("icon written:", fs.readdirSync(outDir).join(", "));
