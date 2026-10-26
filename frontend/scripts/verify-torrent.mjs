import { readFileSync } from "node:fs"
import { chromium } from "playwright"

/**
 * 种子预览 + 拖入 .torrent 的真机验收。
 *
 * 用法：`BASE=http://127.0.0.1:5001 TARGET=/e2e EXPECT=.e2e-torrent/expected.json node scripts/verify-torrent.mjs`
 * 前提：后端托管 `dist`；TARGET 挂载到装置目录，里面有
 *      `bundle.torrent`（多文件）/ `single.torrent`（单文件）/ `notes.txt`（非种子）。
 * 期望值（info_hash、文件数、行数）由装置的生成脚本写出，这里只做比对 ——
 * 哈希在测试侧另算一遍，避免「前端自己算、自己验」的自证循环。
 */

const BASE = process.env.BASE || "http://127.0.0.1:5001"
const TARGET = process.env.TARGET || "/e2e"
const EXPECT_FILE = process.env.EXPECT || "F:/Code/Dotnet/OpenList.Net/.e2e-torrent/expected.json"
const SHOT = process.env.SHOT_DIR || "F:/Code/Dotnet/OpenList.Net/.e2e-torrent"
const FIXTURE_DIR = process.env.FIXTURE || "G:/Share/ol-v2-torrent"

const expected = JSON.parse(readFileSync(EXPECT_FILE, "utf8"))

const log = (...args) => console.log("[torrent]", ...args)
const results = []
function check(name, ok, extra = "") {
  results.push({ name, ok, extra })
  log(ok ? "PASS" : "FAIL", name, extra)
}

const browser = await chromium.launch({ channel: "msedge", headless: true })
const page = await browser.newPage({ viewport: { width: 1440, height: 900 } })
const errors = []
page.on("console", (msg) => {
  if (msg.type() === "error") errors.push(msg.text())
})
page.on("pageerror", (e) => errors.push(String(e)))

// 登录：离线下载按钮按 `offline_download` 权限显隐，游客看不到
const token = await page
  .request.post(`${BASE}/api/auth/login`, {
    data: { username: process.env.ADMIN_USER || "admin", password: process.env.ADMIN_PASS || "e2e12345" },
  })
  .then((r) => r.json())
  .then((body) => body.data.token)
await page.addInitScript((value) => localStorage.setItem("token", value), token)

async function openFile(name, wait = 2500) {
  await page.goto(`${BASE}/#/?path=${encodeURIComponent(`${TARGET}/${name}`)}`, {
    waitUntil: "networkidle",
  })
  await page.waitForTimeout(wait)
}

async function selectedPreview() {
  const select = page.locator(".ol-preview-actions .el-select").first()
  if ((await select.count()) === 0) return ""
  return (await select.textContent())?.trim() ?? ""
}

/** 当前打开的对话框（EP 会把关掉的对话框留在 DOM 里，必须限定 :visible）。 */
const dialog = () => page.locator(".el-dialog:visible").first()

async function closeDialog() {
  if ((await page.locator(".el-dialog:visible").count()) === 0) return
  await dialog().locator("button:has-text('取消')").first().click().catch(() => {})
  await page.waitForTimeout(400)
}

/** 在列表区域模拟「拖入一个本地文件」。 */
async function dropFile(name) {
  const base64 = readFileSync(`${FIXTURE_DIR}/${name}`).toString("base64")
  await page.evaluate(
    ({ fileName, b64 }) => {
      const binary = atob(b64)
      const bytes = new Uint8Array(binary.length)
      for (let i = 0; i < binary.length; i += 1) bytes[i] = binary.charCodeAt(i)
      const file = new File([bytes], fileName, { type: "application/x-bittorrent" })
      const dt = new DataTransfer()
      dt.items.add(file)
      const target = document.querySelector(".ol-files")
      const opts = { dataTransfer: dt, bubbles: true, cancelable: true }
      target.dispatchEvent(new DragEvent("dragover", opts))
      target.dispatchEvent(new DragEvent("drop", opts))
    },
    { fileName: name, b64: base64 },
  )
  await page.waitForTimeout(900)
}

// ---------- 1. 多文件种子：元信息 ----------
await openFile("bundle.torrent", 3000)
check("种子走 Torrent 预览器", /Torrent/i.test(await selectedPreview()), await selectedPreview())

const title = (await page.locator(".ol-torrent-title").textContent().catch(() => "")) || ""
check("种子名称", title === expected.multi.name, `title="${title}"`)

const tags = await page.locator(".ol-torrent-tags .el-tag").allTextContents()
// 体积按 UI 的 humanSize 口径算：<1 KB 时单位是 B 且不留小数。
// （原先这里写死 `${total/1024} KB`，装置一小就永远对不上 —— 80 B 会渲染成 "80 B"。）
const KB = 1024
const sizeTag =
  expected.multi.total < KB ? `${expected.multi.total} B` : `${(expected.multi.total / KB).toFixed(2)} KB`
check(
  "标签含体积 / 文件数 / 私有",
  tags.some((tag) => tag.replace(/\s+/g, " ").trim() === sizeTag) &&
    tags.includes("3 files") &&
    tags.includes("Private"),
  `${JSON.stringify(tags)} (want ${sizeTag})`,
)

const hash = (await page.locator(".ol-torrent-hash").textContent().catch(() => "")) || ""
check(
  "info_hash 与装置一致（独立算的 SHA-1）",
  hash.trim() === expected.multi.hash,
  `got=${hash.trim()} want=${expected.multi.hash}`,
)

const metaText = (await page.locator(".ol-torrent-meta").innerText().catch(() => "")) || ""
check(
  "分片数 / tracker / 创建者 / 注释都渲染",
  /5 × 16/.test(metaText) &&
    /Trackers[\s\S]*2/.test(metaText) &&
    metaText.includes("ol-v2-fixture") &&
    metaText.includes("ol 种子预览装置"),
  metaText.replace(/\s+/g, " ").slice(0, 90),
)

// ---------- 2. 文件树 ----------
let rows = await page.locator(".ol-torrent-row").count()
check(
  "根层 3 项 + 两个一级目录默认展开 = 5 行",
  rows === expected.multi.rows,
  `rows=${rows} want=${expected.multi.rows}`,
)

const names = await page.locator(".ol-torrent-name").allTextContents()
check(
  "目录在前且含子文件",
  names[0] === "pics" && names.includes("cover.png") && names.includes("b.txt"),
  JSON.stringify(names),
)

// 折叠 pics：行数应减 1
await page.locator(".ol-torrent-row", { hasText: "pics" }).first().click()
await page.waitForTimeout(300)
const afterCollapse = await page.locator(".ol-torrent-row").count()
check("点击目录可折叠", afterCollapse === expected.multi.rows - 1, `rows=${afterCollapse}`)
await page.locator(".ol-torrent-row", { hasText: "pics" }).first().click()
await page.waitForTimeout(300)

await page.screenshot({ path: `${SHOT}/torrent-multi.png` })

// ---------- 3. 预览器里的「离线下载」→ 对话框预填磁力 ----------
await page.locator(".ol-torrent-actions button", { hasText: "Offline download" }).first().click()
await page.waitForTimeout(800)
const magic = await dialog().locator("textarea").first().inputValue().catch(() => "")
check(
  "点离线下载后磁力链接已预填",
  magic.startsWith(`magnet:?xt=urn:btih:${expected.multi.hash}`) &&
    magic.includes(`dn=${encodeURIComponent(expected.multi.name)}`) &&
    magic.includes("tr=udp"),
  magic.slice(0, 90),
)
await page.screenshot({ path: `${SHOT}/torrent-magnet.png` })
await closeDialog()

// ---------- 4. 单文件种子 ----------
await openFile("single.torrent", 2500)
const singleTitle = (await page.locator(".ol-torrent-title").textContent().catch(() => "")) || ""
const singleRows = await page.locator(".ol-torrent-row").count()
const singleHash = (await page.locator(".ol-torrent-hash").textContent().catch(() => "")) || ""
check(
  "单文件种子：名称 / 1 行 / 无目录",
  singleTitle === expected.single.name &&
    singleRows === expected.single.rows &&
    (await page.locator(".ol-torrent-name").first().textContent()) === expected.single.name,
  `title=${singleTitle} rows=${singleRows}`,
)
check("单文件 info_hash 正确", singleHash.trim() === expected.single.hash, singleHash.trim())

// ---------- 5. 拖入 .torrent ----------
await openFile("notes.txt", 2000)
await dropFile("bundle.torrent")
const dropMagnet = await dialog().locator("textarea").first().inputValue().catch(() => "")
check(
  "拖入 .torrent 直接打开离线下载并预填磁力",
  dropMagnet.startsWith(`magnet:?xt=urn:btih:${expected.multi.hash}`),
  dropMagnet.slice(0, 80),
)
await closeDialog()

// ---------- 6. 拖入非种子文件被拒 ----------
await dropFile("notes.txt")
const warned = await page
  .locator(".el-message--warning")
  .first()
  .textContent()
  .catch(() => "")
check("拖入非种子文件给出提示而不是静默吞掉", /\.torrent/i.test(warned || ""), warned || "(无提示)")
check("非种子拖入不打开对话框", (await page.locator(".el-dialog:visible").count()) === 0, "")

// ---------- 汇总 ----------
check("全程无控制台错误", errors.length === 0, errors.slice(0, 3).join(" | "))

const passed = results.filter((r) => r.ok).length
log(`SUMMARY ${passed}/${results.length}`)
await browser.close()
process.exit(passed === results.length ? 0 : 1)
