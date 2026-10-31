import { execFileSync } from "node:child_process"
import fs from "node:fs"
import path from "node:path"
import { fileURLToPath } from "node:url"
import { chromium } from "playwright"

/**
 * I 轮：缩略图服务（`/p?type=thumb`）验收 —— 对应 docs/thumbnail-service-plan.md §12 的 9 条。
 *
 * 前置：隔离后端在 `BASE` 跑，已有一个指向 `E2E_ROOT` 的 `/e2e` 挂载（由 run-regression 的
 *      `round_thumb` 建好），且**本机有可用的 ffmpeg**（后端生成缩略图全靠它）。
 *
 * fixture 全部现造：图片/音频/视频用 ffmpeg（PATH → `OPENLIST_FFMPEG` → 仓库 `.e2e/bin`），
 * PDF 用脚本内拼的最小合法文件（xref 偏移现算）。没有 ffmpeg 时直接判一条 FAIL 并退出 ——
 * 那不是「功能坏了」，而是这一轮的前置没满足，不能让它伪装成「9 条全过」。
 *
 * 用法：`BASE=http://127.0.0.1:5055 ADMIN_PASS=e2e12345 E2E_ROOT=... node scripts/verify-thumb.mjs`
 */

const BASE = process.env.BASE || "http://127.0.0.1:5055"
const USER = process.env.ADMIN_USER || "admin"
const PASS = process.env.ADMIN_PASS || process.env.ADMIN_PASSWORD || "e2e12345"
const ROOT = process.env.E2E_ROOT || "G:\\Share\\ol-thumb"
/** 设置项 → 快照是 20s 轮询（ThumbnailOptionsProvider），开关切换要等一次刷新。 */
const OPTIONS_REFRESH_MS = 22000

const log = (...a) => console.log("[thumb]", ...a)
const results = []
function check(name, ok, extra = "") {
  results.push({ name, ok, extra })
  log(ok ? "PASS" : "FAIL", name, extra)
}

// ---------------------------------------------------------------- ffmpeg & fixture

/** 与后端 `FfmpegTool` 同一套查找顺序，保证「脚本能造 fixture」和「服务能生成」用的是同一个。 */
function resolveFfmpeg() {
  const fromEnv = process.env.OPENLIST_FFMPEG
  if (fromEnv && fs.existsSync(fromEnv)) return fromEnv

  const exe = process.platform === "win32" ? "ffmpeg.exe" : "ffmpeg"
  for (const dir of (process.env.PATH || "").split(path.delimiter)) {
    if (!dir) continue
    const candidate = path.join(dir, exe)
    if (fs.existsSync(candidate)) return candidate
  }

  const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..")
  const bundled = path.join(repo, ".e2e", "bin", exe)
  return fs.existsSync(bundled) ? bundled : null
}

/** 手拼一个最小但**合法**的单页 PDF（偏移现算，PDFium/其它解析器都能打开）。 */
function buildPdf(label = "OpenList") {
  const parts = []
  const offsets = []
  let size = 0
  const push = (text) => {
    offsets.push(size)
    parts.push(text)
    size += Buffer.byteLength(text, "latin1")
  }

  parts.push("%PDF-1.4\n")
  size += Buffer.byteLength("%PDF-1.4\n", "latin1")

  push("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n")
  push("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n")
  push(
    "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] " +
      "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>\nendobj\n",
  )
  const stream = `BT /F1 20 Tf 30 100 Td (${label}) Tj ET`
  push(`4 0 obj\n<< /Length ${stream.length} >>\nstream\n${stream}\nendstream\nendobj\n`)
  push("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n")

  const xref = size
  let tail = `xref\n0 6\n0000000000 65535 f \n`
  for (const offset of offsets) tail += `${String(offset).padStart(10, "0")} 00000 n \n`
  tail += `trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`
  parts.push(tail)
  return Buffer.from(parts.join(""), "latin1")
}

const ffmpeg = resolveFfmpeg()
if (!ffmpeg) {
  check("前置：找到 ffmpeg（否则缩略图服务无法生成任何图片）", false, "PATH / OPENLIST_FFMPEG / .e2e/bin 都没有")
  log(`SUMMARY ${results.filter((r) => r.ok).length}/${results.length}`)
  process.exit(1)
}
log("ffmpeg:", ffmpeg)

if (fs.existsSync(ROOT)) fs.rmSync(ROOT, { recursive: true, force: true })
fs.mkdirSync(ROOT, { recursive: true })

const run = (args) => execFileSync(ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", ...args], { stdio: "inherit" })

/** 造一张 JPEG。`size` 不同即内容与字节数都不同，用于「改了文件 → 缓存失效」那条。 */
function makeJpeg(name, size) {
  run([
    "-f", "lavfi", "-i", `testsrc=size=${size}:rate=1:duration=1`,
    "-frames:v", "1", "-pix_fmt", "yuvj420p", path.join(ROOT, name),
  ])
}

makeJpeg("photo.jpg", "320x200")
run(["-f", "lavfi", "-i", "color=c=orange:size=200x200:duration=1", "-frames:v", "1", path.join(ROOT, "cover.png")])
// 纯音频 mp3（无封面）→ 期望 404 no-cover
run(["-f", "lavfi", "-i", "sine=frequency=440:duration=2", "-c:a", "libmp3lame", "-q:a", "6", path.join(ROOT, "silent.mp3")])
// 带内嵌封面的 mp3 → 期望 200
run([
  "-i", path.join(ROOT, "cover.png"),
  "-i", path.join(ROOT, "silent.mp3"),
  "-map", "0:v", "-map", "1:a",
  "-c:v", "mjpeg", "-c:a", "copy",
  "-id3v2_version", "3", "-disposition:v", "attached_pic",
  path.join(ROOT, "song.mp3"),
])
// 短视频：列表**不该**下发它的 thumb（防一屏 20 次抽帧），详情请求才给
run(["-f", "lavfi", "-i", "testsrc=size=320x200:rate=10:duration=2", "-pix_fmt", "yuv420p", path.join(ROOT, "clip.mp4")])

fs.writeFileSync(path.join(ROOT, "notes.txt"), "plain text, no thumbnail\n")
fs.writeFileSync(path.join(ROOT, "doc.pdf"), buildPdf())
log("fixture:", fs.readdirSync(ROOT).sort().join(", "))

// ---------------------------------------------------------------- API helpers

const token = await fetch(`${BASE}/api/auth/login`, {
  method: "POST",
  headers: { "Content-Type": "application/json" },
  body: JSON.stringify({ username: USER, password: PASS }),
})
  .then((r) => r.json())
  .then((b) => b?.data?.token)

if (!token) {
  check("登录拿到 token", false, "登录失败，后续断言无法进行")
  log(`SUMMARY ${results.filter((r) => r.ok).length}/${results.length}`)
  process.exit(1)
}

const auth = { Authorization: token }

async function api(pathname, init = {}) {
  const res = await fetch(`${BASE}${pathname}`, {
    ...init,
    headers: { "Content-Type": "application/json", ...auth, ...(init.headers || {}) },
  })
  const text = await res.text()
  try {
    return { res, body: JSON.parse(text) }
  } catch {
    return { res, body: text }
  }
}

async function listDir(dir = "/e2e") {
  const { body } = await api("/api/fs/list", {
    method: "POST",
    body: JSON.stringify({ path: dir, page: 1, per_page: 0, refresh: true }),
  })
  return body?.data?.content ?? []
}

const byName = (objs, name) => objs.find((o) => o.name === name)

// ---------------------------------------------------------------- 1. 默认关闭

const off = await listDir()
check(
  "默认关闭：fs/list 不下发 thumb",
  (byName(off, "photo.jpg")?.thumb ?? "") === "",
  `thumb=${byName(off, "photo.jpg")?.thumb}`,
)

// 后端只认「设置项 → OPENLIST_FFMPEG → PATH」，不扫仓库里的 .e2e/bin：
// 把本次解析到的绝对路径写进 thumb_ffmpeg_path，保证「脚本造的 fixture」与
// 「服务端生成」用的是同一个可执行文件（否则会出现「fixture 有了、生成却 500」）。
const saved = await api("/api/admin/setting/save", {
  method: "POST",
  body: JSON.stringify([
    { key: "thumb_enable", value: "true", type: "bool", flag: 0, group: 14 },
    { key: "thumb_ffmpeg_path", value: ffmpeg, type: "string", flag: 1, group: 14 },
  ]),
})
check("开启 thumb_enable 并指定 ffmpeg", saved.res.status === 200, `status=${saved.res.status}`)
log(`等待设置快照刷新（${OPTIONS_REFRESH_MS / 1000}s）…`)
await new Promise((r) => setTimeout(r, OPTIONS_REFRESH_MS))

// ---------------------------------------------------------------- 2. 列表下发与生成

const on = await listDir()
const photo = byName(on, "photo.jpg")
check("开启后图片条目带 thumb", Boolean(photo?.thumb), photo?.thumb ?? "null")
check(
  "视频条目不下发 thumb（列表防抽帧）",
  (byName(on, "clip.mp4")?.thumb ?? "") === "",
  `thumb=${byName(on, "clip.mp4")?.thumb}`,
)
check(
  "PDF 条目不下发 thumb（PDF 未实现，见 §6.4）",
  (byName(on, "doc.pdf")?.thumb ?? "") === "",
  `thumb=${byName(on, "doc.pdf")?.thumb}`,
)

const thumbUrl = photo?.thumb ?? ""
const first = await fetch(thumbUrl)
const firstBytes = Buffer.from(await first.arrayBuffer())
check("首次 GET thumb → 200", first.status === 200, `status=${first.status}`)
check(
  "Content-Type 是图片",
  (first.headers.get("content-type") || "").startsWith("image/"),
  first.headers.get("content-type") || "",
)
check("X-Thumb-Cache: generated", first.headers.get("x-thumb-cache") === "generated", first.headers.get("x-thumb-cache") || "")
check("返回的是真图片字节（不是 0 字节/HTML）", firstBytes.length > 200, `bytes=${firstBytes.length}`)

const second = await fetch(thumbUrl)
await second.arrayBuffer()
check("二次 GET 命中缓存", second.headers.get("x-thumb-cache") === "hit", second.headers.get("x-thumb-cache") || "")
check(
  "命中与生成返回同一 ETag",
  first.headers.get("etag") === second.headers.get("etag"),
  `${first.headers.get("etag")} vs ${second.headers.get("etag")}`,
)

// ---------------------------------------------------------------- 3. 源文件变更 → 缓存失效

makeJpeg("photo.jpg", "400x260")
const third = await fetch(thumbUrl)
const thirdBytes = Buffer.from(await third.arrayBuffer())
check("改文件后同 URL 重新生成", third.headers.get("x-thumb-cache") === "generated", third.headers.get("x-thumb-cache") || "")
check("改文件后图片内容变化", !firstBytes.equals(thirdBytes), `${firstBytes.length} vs ${thirdBytes.length} bytes`)

// ---------------------------------------------------------------- 4. 不支持的类型

const txt = await fetch(`${BASE}/p/e2e/notes.txt?type=thumb`)
check("txt → 415", txt.status === 415, `status=${txt.status}`)
check("txt 带 X-Thumb-Reason: unsupported", txt.headers.get("x-thumb-reason") === "unsupported", txt.headers.get("x-thumb-reason") || "")

// ---------------------------------------------------------------- 5. 尺寸夹取

const huge = await fetch(`${BASE}/p/e2e/photo.jpg?type=thumb&w=9999&h=9999&fmt=png`)
const hugeBytes = Buffer.from(await huge.arrayBuffer())
let edge = 0
if (hugeBytes.length > 24 && huge.headers.get("content-type") === "image/png") {
  // PNG IHDR：宽高在 16..24（大端）
  edge = Math.max(hugeBytes.readUInt32BE(16), hugeBytes.readUInt32BE(20))
}
check("w=9999 仍返回 200", huge.status === 200, `status=${huge.status}`)
check("边长被夹到 ≤1024", edge > 0 && edge <= 1024, `edge=${edge} bytes=${hugeBytes.length}`)

// ---------------------------------------------------------------- 6. 音频封面

const withCover = await fetch(`${BASE}/p/e2e/song.mp3?type=thumb`)
check("带封面的 mp3 → 200", withCover.status === 200, `status=${withCover.status}`)
await withCover.arrayBuffer()

const noCover = await fetch(`${BASE}/p/e2e/silent.mp3?type=thumb`)
check("无封面的 mp3 → 404", noCover.status === 404, `status=${noCover.status}`)
check("无封面 reason=no-cover", noCover.headers.get("x-thumb-reason") === "no-cover", noCover.headers.get("x-thumb-reason") || "")
await noCover.arrayBuffer()

// ---------------------------------------------------------------- 7. PDF（当前明确未实现）

const pdf = await fetch(`${BASE}/p/e2e/doc.pdf?type=thumb`)
check(
  "PDF → 415（未实现；实现后此条改为 200）",
  pdf.status === 415 && pdf.headers.get("x-thumb-reason") === "pdf-unsupported",
  `status=${pdf.status} reason=${pdf.headers.get("x-thumb-reason")}`,
)
await pdf.arrayBuffer()

// ---------------------------------------------------------------- 8. 不传 type 时行为不变（最重要）

const original = fs.statSync(path.join(ROOT, "photo.jpg")).size
const plain = await fetch(`${BASE}/p/e2e/photo.jpg`)
const plainBytes = Buffer.from(await plain.arrayBuffer())
check("不带 type 仍是原文件", plain.status === 200 && plainBytes.length === original, `${plainBytes.length} vs ${original}`)

// ---------------------------------------------------------------- 9. 前端：列表槽位 / 网格 src

const browser = await chromium.launch({ channel: "msedge", headless: true })
const errors = []
const page = await browser.newPage({ viewport: { width: 1440, height: 900 }, locale: "en-US" })
page.on("console", (m) => {
  if (m.type() === "error") errors.push(m.text())
})
page.on("pageerror", (e) => errors.push(String(e)))
await page.addInitScript((t) => {
  localStorage.setItem("token", t)
}, token)

async function openFiles(mode, viewport) {
  if (viewport) await page.setViewportSize(viewport)
  await page.goto(`${BASE}/#/files?path=%2Fe2e`, { waitUntil: "networkidle" })
  await page.evaluate((m) => localStorage.setItem("ol-view-mode", m), mode)
  await page.reload({ waitUntil: "networkidle" })
  await page.waitForTimeout(1200)
}

await openFiles("list")
const listThumbs = await page.locator(".ol-file-name .ol-thumb img").count()
check("桌面表格名称列渲染缩略图槽位", listThumbs > 0, `img=${listThumbs}`)
const listSrc = await page.locator(".ol-file-name .ol-thumb img").first().getAttribute("src").catch(() => "")
check("列表槽位 src 含 type=thumb", (listSrc || "").includes("type=thumb"), (listSrc || "").slice(0, 80))
// 无封面音频（silent.mp3）的 thumb 必然 404，槽位要退回图标而不是留空/裂图
const iconSlots = await page.locator(".ol-file-name .ol-thumb.is-icon").count()
check("无缩略图的条目退回图标槽位", iconSlots > 0, `icon=${iconSlots}`)

await openFiles("grid")
const gridSrc = await page.locator(".ol-grid-card .ol-grid-thumb img").first().getAttribute("src").catch(() => "")
check("网格卡片 src 含 type=thumb", (gridSrc || "").includes("type=thumb"), (gridSrc || "").slice(0, 80))

await openFiles("list", { width: 390, height: 844 })
const phoneThumbs = await page.locator(".ol-row .ol-thumb").count()
check("手机卡片行渲染缩略图槽位", phoneThumbs > 0, `slot=${phoneThumbs}`)

// 资源 404 与脚本错误分开算：列表对「无封面音频」「视频」本来就会发出取不到图的请求
// （后端无法预知 mp3 有没有内嵌封面），前端按约定降级为图标。把这类 404 当成失败，
// 会让这一轮永远红 —— 真正要守的是「没有脚本级异常」。
const resourceErrors = errors.filter((e) => /Failed to load resource/i.test(e))
const scriptErrors = errors.filter((e) => !/Failed to load resource/i.test(e))
check("无脚本级控制台错误", scriptErrors.length === 0, scriptErrors.slice(0, 3).join(" | "))
log(`资源 404 ${resourceErrors.length} 条（无封面音频/视频的已知降级路径，不计失败）`)
await browser.close()

const passed = results.filter((r) => r.ok).length
log(`SUMMARY ${passed}/${results.length}`)
process.exit(passed === results.length ? 0 : 1)
