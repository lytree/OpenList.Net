import { chromium } from "playwright"
import { mkdirSync, writeFileSync } from "node:fs"
import { join } from "node:path"
import { tmpdir } from "node:os"

/**
 * 播放列表「整单连播」的真机验收（docs §39）。
 *
 * 用法（前置：隔离后端 + DistDir 指向 $PWD/dist + /ping 可用）：
 *   BASE=http://127.0.0.1:5011 ADMIN_PASS=e2e12345 node scripts/verify-playlist-queue.mjs
 *
 * 装置自带（临时目录里两个目录各一个 mp4 + NFO），不依赖 gen_fixtures.py ——
 * 这条链路只关心「队列推进」，不需要 TMDB 或复杂的维度数据。
 *
 * 覆盖：播放全部 → 队列位置/高亮 → 下一集/上一集 → ended 自动连播 → 播完收尾 →
 * 中文界面下的同一套控件（顺带验 zh 字典真的被加载、Element Plus 也跟着换语言）。
 *
 * 为什么要手动 dispatch `ended`：装置里的 mp4 是占位文件，浏览器永远播不到结尾，
 * 而「自动连播」是本功能的核心分支，只能靠事件驱动来验证。
 */

const BASE = process.env.BASE || "http://127.0.0.1:5011"
const ADMIN_PASS = process.env.ADMIN_PASS || "e2e12345"
const ROOT = process.env.FIXTURE_ROOT || join(tmpdir(), `ol-queue-${Date.now()}`)
const LIB_NAME = "E2E Queue Lib"
const PL_NAME = "E2E Queue Round"

const log = (...args) => console.log("[queue]", ...args)
const results = []
function check(name, ok, extra = "") {
  results.push({ name, ok, extra })
  log(ok ? "PASS" : "FAIL", name, extra)
}

// ---------- 0. 浏览器与装置 ----------
const browser = await chromium.launch({ channel: "msedge", headless: true })

/**
 * 装置里的「视频」必须是**真能解码**的文件。
 *
 * 第一版用了 2 KB 的占位 `.mp4`：`MediaPlayer` 的 `onError` 会把 `<video>` 换成错误占位，
 * 于是页面上根本没有 video 元素 —— 「自动连播」那条断言连事件都发不出去（表现为超时，
 * 而不是断言失败，很容易被误读成功能坏了）。这里用 canvas + MediaRecorder 现录一段
 * 1.2 秒的 webm，扫出来的条目就有可播放的源。
 */
async function recordTinyWebm() {
  const rec = await browser.newPage({ locale: "en-US" })
  const b64 = await rec.evaluate(async () => {
    const canvas = document.createElement("canvas")
    canvas.width = 160
    canvas.height = 120
    const ctx = canvas.getContext("2d")
    const chunks = []
    const recorder = new MediaRecorder(canvas.captureStream(15), { mimeType: "video/webm;codecs=vp8" })
    recorder.ondataavailable = (event) => chunks.push(event.data)
    const stopped = new Promise((resolve) => (recorder.onstop = resolve))
    recorder.start()
    let tick = 0
    const timer = setInterval(() => {
      ctx.fillStyle = tick++ % 2 ? "#c0392b" : "#0a84ff"
      ctx.fillRect(0, 0, canvas.width, canvas.height)
    }, 60)
    await new Promise((resolve) => setTimeout(resolve, 1200))
    clearInterval(timer)
    recorder.stop()
    await stopped
    const bytes = new Uint8Array(await new Blob(chunks, { type: "video/webm" }).arrayBuffer())
    let binary = ""
    for (const byte of bytes) binary += String.fromCharCode(byte)
    return btoa(binary)
  })
  await rec.close()
  return Buffer.from(b64, "base64")
}

// 文件名与 NFO 标题刻意不同：断言看的是 NFO 里的 title，能顺带证明「入库标题取自元数据」。
const ITEMS = [
  { dir: "First (2020)", file: "First (2020).webm", title: "Queue Alpha" },
  { dir: "Second (2021)", file: "Second (2021).webm", title: "Queue Bravo" },
]
const video = await recordTinyWebm()
log("recorded fixture video:", video.length, "bytes")
for (const item of ITEMS) {
  const dir = join(ROOT, item.dir)
  mkdirSync(dir, { recursive: true })
  writeFileSync(join(dir, item.file), video)
  writeFileSync(
    join(dir, item.file.replace(/\.webm$/, ".nfo")),
    `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<movie>
  <title>${item.title}</title>
  <year>2020</year>
  <plot>Fixture for the playlist queue round.</plot>
</movie>`,
  )
}
log("fixture:", ROOT)

// ---------- 1. 登录 ----------
const page = await browser.newPage({ viewport: { width: 1440, height: 900 }, locale: "en-US" })
const errors = []
page.on("console", (msg) => {
  if (msg.type() !== "error") return
  errors.push(`${msg.text()} @ ${msg.location()?.url ?? ""}`)
})
page.on("pageerror", (e) => errors.push(String(e)))

const token = await page.request
  .post(`${BASE}/api/auth/login`, { data: { username: "admin", password: ADMIN_PASS } })
  .then((r) => r.json())
  .then((body) => body.data.token)
check("管理员登录", Boolean(token))
page.addInitScript((value) => localStorage.setItem("token", value), token)
// 断言文案写死英文，所以首屏把语言钉在 en —— 否则中文 Windows 上 `navigator.language`
// 会让 detectLocale() 直接选中文，一上来所有英文选择器全部落空（第 6 节才切 zh）。
// 只在没设置过时兜底：addInitScript 每次导航都会跑，写成无条件覆盖会把第 6 节的 zh 冲掉。
page.addInitScript(() => {
  if (!localStorage.getItem("lang")) localStorage.setItem("lang", "en")
})

const api = async (method, path, data) =>
  page.request[method](`${BASE}/api${path}`, { data, headers: { Authorization: token } }).then((r) => r.json())

// 清残留：/e2e 挂载、同名媒体库、同名播放列表（脚本要能反复跑）
// `storage/list` 是分页信封（{content,total}），不是裸数组
for (const storage of (await api("get", "/admin/storage/list")).data?.content ?? []) {
  if (storage.mount_path === "/e2e") await api("post", `/admin/storage/delete?id=${storage.id}`, {})
}
for (const lib of (await api("get", "/media/libraries")).data ?? []) {
  if (lib.name === LIB_NAME) await api("post", `/media/libraries/${lib.id}/delete`, {})
}
for (const playlist of (await api("get", "/media/playlists")).data ?? []) {
  if (playlist.name === PL_NAME) await api("post", `/media/playlists/${playlist.id}/delete`, {})
}

const mount = await api("post", "/admin/storage/create", {
  mount_path: "/e2e",
  driver: "Local",
  // addition 是**字符串化的 JSON**，传对象后端会 400 且响应体为空
  addition: JSON.stringify({ root_folder_path: ROOT }),
})
check("挂载 /e2e", mount.code === 200, JSON.stringify(mount.message ?? ""))

// ---------- 2. 建库 + 扫描 + 建播放列表 ----------
const lib = await api("post", "/media/libraries", { name: LIB_NAME, path: "/e2e", type: 2, auto_scan: false })
const libId = lib.data?.id
check("建库", lib.code === 200 && libId > 0, JSON.stringify(lib.message ?? ""))

await api("post", `/media/libraries/${libId}/scan`, {})
let scanState = ""
for (let i = 0; i < 80; i++) {
  const task = ((await api("get", "/media/tasks")).data ?? []).find((t) => t.library_id === libId)
  scanState = task?.state ?? ""
  if (scanState === "completed" || scanState === "failed") break
  await page.waitForTimeout(250)
}
check("扫描完成", scanState === "completed", scanState)

const itemList = (await api("get", "/media/items?per_page=50")).data?.content ?? []
check("扫描出 2 个条目", itemList.length === 2, `got ${itemList.length}`)
const byTitle = (title) => itemList.find((it) => it.title === title)
const alpha = byTitle("Queue Alpha")
const bravo = byTitle("Queue Bravo")
check("条目标题取自 NFO", Boolean(alpha && bravo), itemList.map((it) => it.title).join(" | "))

const created = await api("post", "/media/playlists", { name: PL_NAME })
const playlistId = created.data?.id
check("建播放列表", created.code === 200 && playlistId > 0, JSON.stringify(created.message ?? ""))
await api("post", `/media/playlists/${playlistId}/items`, { item_id: alpha.id })
await api("post", `/media/playlists/${playlistId}/items`, { item_id: bravo.id })
const detail = await api("get", `/media/playlists/${playlistId}`)
check(
  "播放列表顺序 = 加入顺序",
  JSON.stringify((detail.data?.items ?? []).map((it) => it.title)) === JSON.stringify(["Queue Alpha", "Queue Bravo"]),
  JSON.stringify((detail.data?.items ?? []).map((it) => it.title)),
)

// ---------- 3. UI：播放全部 → 队列位置 ----------
await page.goto(`${BASE}/#/media/playlists/${playlistId}`, { waitUntil: "networkidle" })
await page.waitForSelector(".ol-media-queue-row", { timeout: 10000 })

const playAll = page.locator(".ol-media-title-row .el-button", { hasText: "Play all" })
check("工具栏有「播放全部」", (await playAll.count()) === 1)
// 播之前一条都没看过：角标是"真状态"，不是无条件渲染的装饰
check("播前没有「已看」角标", (await page.locator(".ol-media-queue-badge").count()) === 0)
await playAll.click()
await page.waitForSelector(".ol-mediaplayer", { timeout: 10000 })
check("点击后播放器出现", (await page.locator(".ol-mediaplayer").count()) === 1)
check("播放器里有 video 元素（装置可解码）", (await page.locator(".ol-mediaplayer video").count()) === 1)

const posText = () => page.locator(".ol-media-queue-pos").innerText()
check("队列位置 1 / 2", (await posText()) === "1 / 2", await posText())
const playingRows = page.locator(".ol-media-queue-row.is-playing")
check("高亮恰好 1 行", (await playingRows.count()) === 1)
check(
  "高亮的是第一条",
  (await playingRows.first().locator(".ol-media-queue-title").innerText()) === "Queue Alpha",
  await playingRows.first().locator(".ol-media-queue-title").innerText(),
)

// ---------- 4. 下一集 / 上一集 ----------
await page.locator(".ol-media-queue-nav span[title='Next'] button").click()
await page.waitForFunction(
  () => document.querySelector(".ol-media-queue-pos")?.textContent?.trim() === "2 / 2",
  null,
  { timeout: 10000 },
)
check("下一集 → 2 / 2", (await posText()) === "2 / 2", await posText())
check(
  "高亮换成第二条",
  (await playingRows.first().locator(".ol-media-queue-title").innerText()) === "Queue Bravo",
  await playingRows.first().locator(".ol-media-queue-title").innerText(),
)
check(
  "末条时「下一集」禁用",
  await page.locator(".ol-media-queue-nav span[title='Next'] button").isDisabled(),
)

await page.locator(".ol-media-queue-nav span[title='Previous'] button").click()
await page.waitForFunction(
  () => document.querySelector(".ol-media-queue-pos")?.textContent?.trim() === "1 / 2",
  null,
  { timeout: 10000 },
)
check("上一集 → 1 / 2", (await posText()) === "1 / 2", await posText())
check(
  "首条时「上一集」禁用",
  await page.locator(".ol-media-queue-nav span[title='Previous'] button").isDisabled(),
)

// ---------- 5. ended 自动连播 + 播完收尾 ----------
// 装置是真能解码的 webm，但**不靠自然播完**：1.2 秒的片子要 play() + 等 ended，
// 在 headless 里对自动播放策略和计时都敏感。这里直接派发 ended（它就是这个分支的触发源），
// 断言只关心「队列有没有往前推」。
const fireEnded = () =>
  page.evaluate(() => {
    const element = document.querySelector(".ol-mediaplayer video")
    if (!element) throw new Error("no video element")
    element.dispatchEvent(new Event("ended"))
  })
await fireEnded()
await page.waitForFunction(
  () => document.querySelector(".ol-media-queue-pos")?.textContent?.trim() === "2 / 2",
  null,
  { timeout: 10000 },
)
check("播完自动进下一条", (await posText()) === "2 / 2", await posText())

await fireEnded()
await page.waitForSelector(".ol-mediaplayer", { state: "detached", timeout: 10000 })
check("最后一条播完关闭播放器", (await page.locator(".ol-mediaplayer").count()) === 0)
const finishMsg = await page.locator(".el-message").last().innerText().catch(() => "")
check("给出「播完了」提示", finishMsg.includes("Reached the end of the playlist"), finishMsg)
check("播完后没有残留高亮", (await page.locator(".ol-media-queue-row.is-playing").count()) === 0)

// 两条都播完了：角标要当场出现（不等刷新页面）—— 状态是 onEnded 里回写的
await page
  .waitForFunction(() => document.querySelectorAll(".ol-media-queue-badge").length === 2, null, {
    timeout: 10000,
  })
  .catch(() => {})
const queueBadges = (await page.locator(".ol-media-queue-badge").allInnerTexts()).map((text) => text.trim())
check(
  "两条播完后都带上「已看」角标",
  queueBadges.length === 2 && queueBadges.every((text) => text === "Watched"),
  queueBadges.join("|"),
)

// ---------- 5.5 观看会话落库（§43） ----------
// 会话是"看了多久"的事实来源：起播发一次、结束（换文件/播完/关播放器）收一次尾。
// 这里只验链路打通：两条会话都写进去了、都能标出"看完"。
const history = await api("get", "/media/watch/history?days=30&limit=12")
const historyRows = history.data ?? []
check("观看历史 2 条", historyRows.length === 2, `got ${historyRows.length}`)
check(
  "两条都标记为看完",
  historyRows.length === 2 && historyRows.every((row) => row.completed === true),
  historyRows.map((row) => `${row.title}:${row.completed}`).join(" | "),
)

const watchStats = await api("get", "/media/watch/stats?days=7")
check("逐日统计回显 window_days", watchStats.data?.window_days === 7, `${watchStats.data?.window_days}`)
check("逐日统计 7 个点", (watchStats.data?.days ?? []).length === 7, `${(watchStats.data?.days ?? []).length}`)

// 口味页的「最近观看」面板吃的是同一份数据：走一遍 UI，确认不是只有接口通
await page.goto(`${BASE}/#/media/taste`, { waitUntil: "networkidle" })
await page.waitForSelector(".ol-media-stat-card", { timeout: 10000 })
const watchRows = await page.locator(".ol-media-watch-row").count()
check("口味页「最近观看」列出 2 条", watchRows === 2, `${watchRows} rows`)
await page.goto(`${BASE}/#/media/playlists/${playlistId}`, { waitUntil: "networkidle" })
await page.waitForSelector(".ol-media-queue-row", { timeout: 10000 })

// ---------- 6. 中文界面下的同一套控件 ----------
// 直接写 localStorage 再刷新：语言切换器本身在 BasicLayout 里，属于 M0 的既有能力，
// 这里要验的是「zh 字典被加载 + 连播控件有中文」。
await page.evaluate(() => localStorage.setItem("lang", "zh"))
await page.reload({ waitUntil: "networkidle" })
await page.waitForSelector(".ol-media-queue-row", { timeout: 10000 })
check("html lang = zh", (await page.evaluate(() => document.documentElement.lang)) === "zh")
check(
  "按钮变成「播放全部」",
  (await page.locator(".ol-media-title-row .el-button", { hasText: "播放全部" }).count()) === 1,
)
check("没有裸 key 上屏", !(await page.locator(".ol-media-title-row").innerText()).includes("media."))
await page.locator(".ol-media-title-row .el-button", { hasText: "播放全部" }).click()
await page.waitForSelector(".ol-mediaplayer", { timeout: 10000 })
check("中文态也能连播", (await posText()) === "1 / 2", await posText())
const stopText = await page.locator(".ol-mediaplayer-bar .el-button").last().innerText()
check("播放器按钮已中文化", stopText.includes("关闭播放器"), stopText)

// ---------- 7. 收尾 ----------
// 语言写回 en，免得后续脚本（默认英文断言的 verify-*）被这条脚本带跑偏
await page.evaluate(() => localStorage.setItem("lang", "en"))
check("零控制台错误", errors.length === 0, errors.slice(0, 3).join(" | "))

await browser.close()

const failed = results.filter((r) => !r.ok)
log(`\n${results.length - failed.length}/${results.length} passed`)
if (failed.length > 0) {
  for (const item of failed) log("FAILED:", item.name, item.extra)
  process.exit(1)
}
