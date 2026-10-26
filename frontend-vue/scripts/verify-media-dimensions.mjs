import { chromium } from "playwright"
import { mkdirSync, writeFileSync } from "node:fs"
import { join } from "node:path"

/**
 * M4 第三片（人物 / 片商 / 系列 / 口味 / 播放列表 / 字幕）的真机验收。
 *
 * 用法：
 *   BASE=http://127.0.0.1:5011 ADMIN_PASS=e2e12345 node scripts/verify-media-dimensions.mjs
 *
 * 前置：隔离后端（临时库 + DistDir=$PWD/dist）。装置由脚本自己生成到
 * `.e2e/dim-fixture`（每个条目一个目录：占位 .mp4 + Kodi NFO）。
 * 为什么用 NFO 而不是 TMDB：人物/片商/系列三个维度只有刮削后才有数据，
 * 离线装置走 local NFO 才能稳定复现（在线刮削依赖外网，且结果会变）。
 *
 * 覆盖：首页 facets 筛选 → 人物列表/详情 → 片商/系列 → 口味画像 →
 * 播放列表（建/加/重排/移除）→ 字幕对话框 → 零控制台错误。
 */

const BASE = process.env.BASE || "http://127.0.0.1:5011"
const ADMIN_PASS = process.env.ADMIN_PASS || "e2e12345"
const FIXTURE_ROOT = process.env.FIXTURE_ROOT || join(process.cwd(), "..", ".e2e", "dim-fixture")
const LIB_NAME = "E2E Dim Lib"
const PLAYLIST_NAME = "E2E Queue"

const log = (...args) => console.log("[media-dim]", ...args)
const results = []
function check(name, ok, extra = "") {
  results.push({ name, ok, extra })
  log(ok ? "PASS" : "FAIL", name, extra)
}

// ---------- 0. 装置 ----------
// 三个条目刻意构成两张维度网：
//   系列 Blade Runner ×2、片商 Warner ×2、导演 Denis Villeneuve ×2（跨片商）、
//   Harrison Ford 同时出现在两部 Blade Runner 里（演员聚合）。
const ITEMS = [
  {
    dir: "Blade Runner (1982)",
    file: "Blade Runner (1982).mp4",
    nfo: `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<movie>
  <title>Blade Runner</title>
  <originaltitle>Blade Runner</originaltitle>
  <year>1982</year>
  <rating>8.1</rating>
  <plot>Rick Deckard hunts replicants in a rainy Los Angeles.</plot>
  <genre>Science Fiction</genre>
  <genre>Drama</genre>
  <studio>Warner Bros.</studio>
  <series>Blade Runner</series>
  <director>Ridley Scott</director>
  <actor><name>Harrison Ford</name><role>Rick Deckard</role><order>0</order></actor>
</movie>`,
  },
  {
    dir: "Blade Runner 2049 (2017)",
    file: "Blade Runner 2049 (2017).mp4",
    nfo: `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<movie>
  <title>Blade Runner 2049</title>
  <originaltitle>Blade Runner 2049</originaltitle>
  <year>2017</year>
  <rating>8.0</rating>
  <plot>K uncovers a secret that could tip what is left of society into chaos.</plot>
  <genre>Science Fiction</genre>
  <genre>Drama</genre>
  <studio>Warner Bros.</studio>
  <series>Blade Runner</series>
  <director>Denis Villeneuve</director>
  <actor><name>Ryan Gosling</name><role>K</role><order>0</order></actor>
  <actor><name>Harrison Ford</name><role>Rick Deckard</role><order>1</order></actor>
</movie>`,
  },
  {
    dir: "Arrival (2016)",
    file: "Arrival (2016).mp4",
    nfo: `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<movie>
  <title>Arrival</title>
  <originaltitle>Arrival</originaltitle>
  <year>2016</year>
  <rating>7.9</rating>
  <plot>A linguist is recruited to communicate with alien visitors.</plot>
  <genre>Science Fiction</genre>
  <genre>Drama</genre>
  <studio>Paramount</studio>
  <director>Denis Villeneuve</director>
  <actor><name>Amy Adams</name><role>Louise Banks</role><order>0</order></actor>
</movie>`,
  },
]

for (const item of ITEMS) {
  const dir = join(FIXTURE_ROOT, item.dir)
  mkdirSync(dir, { recursive: true })
  // 扫描只按扩展名发现视频，内容无所谓；留一点字节避免 0 大小的奇怪路径
  writeFileSync(join(dir, item.file), "0".repeat(1024))
  writeFileSync(join(dir, `${item.dir}.nfo`), item.nfo)
}
log("fixture:", FIXTURE_ROOT)

// ---------- 1. 浏览器与登录 ----------
const browser = await chromium.launch({ channel: "msedge", headless: true })
const page = await browser.newPage({ viewport: { width: 1440, height: 900 } })
const errors = []
page.on("console", (msg) => {
  if (msg.type() === "error") errors.push(msg.text())
})
page.on("pageerror", (e) => errors.push(String(e)))

const token = await page.request
  .post(`${BASE}/api/auth/login`, { data: { username: "admin", password: ADMIN_PASS } })
  .then((r) => r.json())
  .then((body) => body.data.token)
page.addInitScript((value) => localStorage.setItem("token", value), token)

const api = async (method, path, data) =>
  page.request[method](`${BASE}/api${path}`, { data, headers: { Authorization: token } }).then((r) => r.json())

// 挂载 /e2e（addition 是**字符串化的 JSON**，传对象会 400 空响应体）
const mount = await api("post", "/admin/storage/create", {
  mount_path: "/e2e",
  driver: "Local",
  addition: JSON.stringify({ root_folder_path: FIXTURE_ROOT }),
})
log("mount:", mount.code, mount.code === 200 ? "" : mount.message)

// 清残留库（按名字）
for (const lib of (await api("get", "/media/libraries")).data ?? []) {
  if (lib.name === LIB_NAME) {
    await api("post", `/media/libraries/${lib.id}/delete`, {})
    log("cleaned leftover library", lib.id)
  }
}
// 清残留播放列表（/playlists 只返回当前用户的，逐个删）
for (const playlist of (await api("get", "/media/playlists")).data ?? []) {
  if (playlist.name === PLAYLIST_NAME) {
    await api("post", `/media/playlists/${playlist.id}/delete`, {})
    log("cleaned leftover playlist", playlist.id)
  }
}

// ---------- 2. 建库 + 扫描 ----------
const lib = await api("post", "/media/libraries", {
  name: LIB_NAME,
  path: "/e2e",
  type: 2,
  auto_scan: false,
})
check("建库", lib.code === 200 && lib.data?.id > 0, JSON.stringify(lib.message ?? ""))
const libId = lib.data?.id

await api("post", `/media/libraries/${libId}/scan`, {})
let scanState = ""
for (let i = 0; i < 80; i++) {
  const tasks = await api("get", "/media/tasks")
  const task = (tasks.data ?? []).find((t) => t.library_id === libId)
  scanState = task?.state ?? ""
  if (scanState === "completed" || scanState === "failed") break
  await page.waitForTimeout(250)
}
check("扫描完成", scanState === "completed", scanState)

const items = await api("get", "/media/items?per_page=50")
const itemList = items.data?.content ?? []
check("按目录聚合出 3 个条目", itemList.length === 3, `got ${itemList.length}`)

// ---------- 3. 后端契约（先验数据面，UI 验表现面） ----------
const facets = await api("get", `/media/facets?library_id=${libId}`)
const sciFi = (facets.data?.genres ?? []).find((g) => g.value === "Science Fiction")
check("facets：题材聚合 Science Fiction ×3", sciFi?.count === 3, JSON.stringify(sciFi))

const persons = await api("get", "/media/persons?per_page=50")
const personRows = persons.data?.content ?? []
const villeneuve = personRows.find((p) => p.name === "Denis Villeneuve")
check("persons：5 位创作者", personRows.length === 5, `got ${personRows.length}`)
check("persons：Denis Villeneuve 关联 2 部", villeneuve?.item_count === 2, JSON.stringify(villeneuve))

const studios = await api("get", "/media/studios?per_page=50")
const warner = (studios.data?.content ?? []).find((s) => s.name === "Warner Bros.")
check("studios：Warner Bros. 关联 2 部", warner?.item_count === 2, JSON.stringify(warner))

const series = await api("get", "/media/series?per_page=50")
const bladeRunner = (series.data?.content ?? []).find((s) => s.name === "Blade Runner")
check("series：Blade Runner 关联 2 部", bladeRunner?.item_count === 2, JSON.stringify(bladeRunner))

const stats = await api("get", `/media/stats?library_id=${libId}`)
check("stats：条目数 3", stats.data?.item_count === 3, JSON.stringify(stats.data?.item_count))
check(
  "stats：题材分布含 Science Fiction",
  (stats.data?.by_genre ?? []).some((g) => g.value === "Science Fiction"),
)

// ---------- 4. 媒体首页：facets 筛选 + 维度入口 ----------
await page.goto(`${BASE}/#/media`, { waitUntil: "networkidle" })
await page.waitForSelector(".ol-media-facets", { timeout: 10000 })

const facetChips = await page.locator(".ol-media-facets .ol-media-chip-tag").count()
check("首页渲染 facets 筛选条", facetChips > 3, `${facetChips} chips`)

const navCount = await page.locator(".ol-media-dim-nav .el-button").count()
check("首页渲染 5 个维度入口", navCount === 5, `${navCount} buttons`)

const cardsBefore = await page.locator(".ol-media-grid .ol-media-card").count()
await page.locator(".ol-media-facets .ol-media-chip-tag", { hasText: "Blade Runner" }).count()
// 先按「年份 2016」筛：只剩 Arrival
const yearChip = page.locator(".ol-media-facets .ol-media-chip-tag", { hasText: "2016" }).first()
await yearChip.click()
await page.waitForTimeout(600)
const cardsYear = await page.locator(".ol-media-grid .ol-media-card").count()
check("按年份 2016 筛选后只剩 1 条", cardsYear === 1, `${cardsYear} (before ${cardsBefore})`)
await yearChip.click()
await page.waitForTimeout(500)

// ---------- 5. 人物列表 / 详情 ----------
await page.locator(".ol-media-dim-nav .el-button", { hasText: "People" }).click()
await page.waitForSelector(".ol-media-person-card", { timeout: 10000 })
const personCards = await page.locator(".ol-media-person-card").count()
check("人物列表 5 张卡片", personCards === 5, `${personCards}`)

await page.locator(".ol-media-person-card", { hasText: "Denis Villeneuve" }).first().click()
await page.waitForSelector(".ol-media-person-head", { timeout: 10000 })
// 头部容器先渲染、聚合数据后到（两个请求并行），必须等标题真的变成人名再断言
await page
  .waitForFunction(
    () => (document.querySelector(".ol-media-detail-title-plain")?.textContent ?? "").includes("Denis"),
    null,
    { timeout: 10000 },
  )
  .catch(() => {})
const headName = await page.locator(".ol-media-detail-title-plain").textContent()
check("人物详情标题", headName?.includes("Denis Villeneuve"), headName ?? "")
const personItems = await page.locator(".ol-media-grid .ol-media-card").count()
check("人物详情 2 部作品", personItems === 2, `${personItems}`)
const roleTags = await page.locator(".ol-media-person-head .el-tag").allInnerTexts()
check("人物详情含 Director 角色标签", roleTags.some((tag) => tag.includes("Director")), roleTags.join("|"))

// ---------- 6. 片商 / 系列 ----------
await page.goto(`${BASE}/#/media`, { waitUntil: "networkidle" })
await page.locator(".ol-media-dim-nav .el-button", { hasText: "Studios" }).click()
await page.waitForSelector(".ol-media-taxonomy-chip", { timeout: 10000 })
const studioChips = await page.locator(".ol-media-taxonomy-chip").allInnerTexts()
check(
  "片商页含 Warner Bros. 与 Paramount",
  studioChips.some((t) => t.includes("Warner Bros.")) && studioChips.some((t) => t.includes("Paramount")),
  studioChips.join("|"),
)
await page.locator(".ol-media-taxonomy-chip", { hasText: "Warner Bros." }).first().click()
await page.waitForSelector(".ol-media-grid .ol-media-card", { timeout: 10000 })
const studioItems = await page.locator(".ol-media-grid .ol-media-card").count()
check("片商详情显示 Warner 名下 2 部", studioItems === 2, `${studioItems}`)

await page.goto(`${BASE}/#/media/series`, { waitUntil: "networkidle" })
await page.waitForSelector(".ol-media-taxonomy-chip", { timeout: 10000 })
const seriesChips = await page.locator(".ol-media-taxonomy-chip").allInnerTexts()
check("系列页含 Blade Runner", seriesChips.some((t) => t.includes("Blade Runner")), seriesChips.join("|"))

// ---------- 7. 口味画像 ----------
await page.goto(`${BASE}/#/media/taste`, { waitUntil: "networkidle" })
await page.waitForSelector(".ol-media-stat-card", { timeout: 10000 })
const statValues = await page.locator(".ol-media-stat-value").allInnerTexts()
check("口味页统计卡有 6 张", statValues.length === 6, `${statValues.length}`)
check("口味页条目数 = 3", statValues[0]?.trim() === "3", statValues.join("|"))
const barRows = await page.locator(".ol-media-bar-row").count()
check("口味页条形图有数据", barRows >= 3, `${barRows} rows`)

// ---------- 8. 播放列表：建 → 加 → 重排 → 移除 ----------
await page.goto(`${BASE}/#/media/playlists`, { waitUntil: "networkidle" })
await page.locator(".el-button", { hasText: "New playlist" }).first().click()
await page.waitForSelector(".el-dialog", { timeout: 5000 })
await page.locator(".el-dialog input").first().fill(PLAYLIST_NAME)
await page.locator(".el-dialog .el-button--primary", { hasText: "Create" }).click()
await page.waitForTimeout(800)
const playlistCards = await page.locator(".ol-media-playlist-card").count()
check("播放列表创建后出现 1 张卡片", playlistCards === 1, `${playlistCards}`)

// 从条目详情加入：第一部（Blade Runner 1982）
await page.goto(`${BASE}/#/media`, { waitUntil: "networkidle" })
await page.locator(".ol-media-grid .ol-media-card", { hasText: "Blade Runner" }).first().click()
await page.waitForSelector(".ol-media-detail-actions", { timeout: 10000 })
check(
  "详情页出现「加入播放列表」按钮",
  (await page.locator(".ol-media-detail-actions .el-button", { hasText: "Add to playlist" }).count()) === 1,
)
await page.locator(".ol-media-detail-actions .el-button", { hasText: "Add to playlist" }).click()
await page.waitForSelector(".el-dialog .el-select", { timeout: 5000 })
// 选中已有列表（select 选项带条目数后缀）
await page.locator(".el-dialog .el-select").click()
await page.locator(".el-select-dropdown__item", { hasText: PLAYLIST_NAME }).first().click()
await page.locator(".el-dialog__footer .el-button--primary").click()
await page.waitForTimeout(800)

// 第二部（Blade Runner 2049）
await page.goto(`${BASE}/#/media`, { waitUntil: "networkidle" })
await page.locator(".ol-media-grid .ol-media-card", { hasText: "Blade Runner 2049" }).first().click()
await page.waitForSelector(".ol-media-detail-actions", { timeout: 10000 })
await page.locator(".ol-media-detail-actions .el-button", { hasText: "Add to playlist" }).click()
await page.waitForSelector(".el-dialog .el-select", { timeout: 5000 })
await page.locator(".el-dialog .el-select").click()
await page.locator(".el-select-dropdown__item", { hasText: PLAYLIST_NAME }).first().click()
await page.locator(".el-dialog__footer .el-button--primary").click()
await page.waitForTimeout(800)

const playlists = await api("get", "/media/playlists")
const queue = (playlists.data ?? []).find((p) => p.name === PLAYLIST_NAME)
check("播放列表落库 2 个条目", queue?.item_count === 2, JSON.stringify(queue?.item_count))
check(
  "播放列表摘要带 preview_items（含 poster_path 字段）",
  Array.isArray(queue?.preview_items) &&
    queue.preview_items.length === 2 &&
    queue.preview_items.every((entry) => "poster_path" in entry),
  JSON.stringify(queue?.preview_items),
)

await page.goto(`${BASE}/#/media/playlists`, { waitUntil: "networkidle" })
await page.locator(".ol-media-playlist-card", { hasText: PLAYLIST_NAME }).locator(".el-button", { hasText: "Open" }).click()
await page.waitForSelector(".ol-media-queue-row", { timeout: 10000 })
const queueRows = await page.locator(".ol-media-queue-row").count()
check("播放列表详情 2 行", queueRows === 2, `${queueRows}`)
const firstTitle = await page.locator(".ol-media-queue-title").first().innerText()
const secondTitle = await page.locator(".ol-media-queue-title").nth(1).innerText()
check("队列顺序 = 入库顺序", firstTitle.includes("Blade Runner") && secondTitle.includes("2049"), `${firstTitle} / ${secondTitle}`)

// 上移第二行（等价于把第一行下移，但点第二行更稳）
await page.locator(".ol-media-queue-row").nth(1).locator("button").first().click()
await page.waitForTimeout(900)
const reordered = await page.locator(".ol-media-queue-title").first().innerText()
check("重排后第一行为 Blade Runner 2049", reordered.includes("2049"), reordered)

// 移除一行
await page.locator(".ol-media-queue-row").first().locator("button").last().click()
await page.waitForSelector(".el-message-box", { timeout: 5000 })
await page.locator(".el-message-box__btns .el-button--primary").click()
await page.waitForTimeout(900)
const rowsAfterRemove = await page.locator(".ol-media-queue-row").count()
check("移除后剩 1 行", rowsAfterRemove === 1, `${rowsAfterRemove}`)

// ---------- 9. 字幕对话框 ----------
await page.goto(`${BASE}/#/media`, { waitUntil: "networkidle" })
await page.locator(".ol-media-grid .ol-media-card", { hasText: "Arrival" }).first().click()
await page.waitForSelector(".ol-media-detail-actions", { timeout: 10000 })
await page.locator(".ol-media-detail-actions .el-button", { hasText: "Subtitles" }).click()
await page.waitForSelector(".el-dialog", { timeout: 5000 })
const dialogTitle = await page.locator(".el-dialog__title").last().innerText()
check("字幕对话框打开", dialogTitle.toLowerCase().includes("subtitle"), dialogTitle)
// 未配置 provider 时后端返回非 200，前端如实提示（不断言具体文案，只确认弹层没崩）
await page.waitForTimeout(1200)
const subtitleBody = await page.locator(".el-dialog__body").last().innerText()
check("字幕对话框有内容且未崩", subtitleBody.length > 0, subtitleBody.slice(0, 60))

// ---------- 10. 收尾 ----------
check("零控制台错误", errors.length === 0, errors.slice(0, 3).join(" | "))

await browser.close()

const failed = results.filter((r) => !r.ok)
log(`\n${results.length - failed.length}/${results.length} passed`)
if (failed.length > 0) {
  for (const item of failed) log("FAILED:", item.name, item.extra)
  process.exit(1)
}
