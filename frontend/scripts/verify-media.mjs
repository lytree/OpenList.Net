import { chromium } from "playwright"

/**
 * 媒体域第一片（浏览 / 详情 / 播放 / 进度）的真机验收。
 *
 * 用法：`BASE=http://127.0.0.1:5001 node scripts/verify-media.mjs`
 * 前置：隔离后端（临时库 + AdminPassword=e2e12345 + DistDir=dist），
 *       挂载 /e2e → `G:\Share\ol-v2-media`，且已建库（E2E Movies）并完成一次扫描：
 *       Budapest/（NFO + poster.jpg + fanart.jpg + video.mp4）、NoSidecar/（只有 mp4）。
 * 装置由生成脚本创建，见 `docs/unified-media-product-plan.md` §27。
 */

const BASE = process.env.BASE || "http://127.0.0.1:5001"
const SHOT = process.env.SHOT_DIR || "F:/Code/Dotnet/OpenList.Net/.e2e-media"

const log = (...args) => console.log("[media]", ...args)
const results = []
function check(name, ok, extra = "") {
  results.push({ name, ok, extra })
  log(ok ? "PASS" : "FAIL", name, extra)
}

const browser = await chromium.launch({ channel: "msedge", headless: true })
const page = await browser.newPage({ viewport: { width: 1440, height: 900 }, locale: "en-US" })
const errors = []
page.on("console", (msg) => {
  if (msg.type() === "error") errors.push(msg.text())
})
page.on("pageerror", (e) => errors.push(String(e)))

const token = await page
  .request.post(`${BASE}/api/auth/login`, {
    data: { username: process.env.ADMIN_USER || "admin", password: process.env.ADMIN_PASS || "e2e12345" },
  })
  .then((r) => r.json())
  .then((body) => body.data.token)
page.addInitScript((value) => localStorage.setItem("token", value), token)

const apiGet = async (path) =>
  page.request
    .get(`${BASE}/api${path}`, { headers: { Authorization: token } })
    .then((r) => r.json())

// ---------- 1. 媒体库浏览页 ----------
await page.goto(`${BASE}/#/media/library`, { waitUntil: "networkidle" })
await page.waitForTimeout(1800)

const libTabs = await page.locator(".ol-media-libs .el-button").allTextContents()
check("库页签渲染（全部 + E2E Movies）", libTabs.length === 2 && libTabs.includes("E2E Movies"), JSON.stringify(libTabs))

const cards = await page.locator(".ol-media-grid .ol-media-card").count()
check("网格 2 个条目", cards === 2, `cards=${cards}`)

// 海报 <img> 真实加载（走 /api/media/items/{id}/image + ?access_token=）
const posterLoaded = await page
  .locator(".ol-media-grid .ol-media-poster img")
  .first()
  .evaluate((img) => img.complete && img.naturalWidth > 0)
  .catch(() => false)
check("海报经图片端点真实渲染", posterLoaded === true, "")

const pendingTag = await page.locator(".ol-media-grid .ol-media-card", { hasText: "NoSidecar" }).locator(".el-tag").textContent().catch(() => "")
check("未刮削条目显示 Pending 标签", /pending/i.test(pendingTag || ""), pendingTag || "(无)")

// ---------- 2. 关键词过滤 ----------
await page.locator(".ol-media-tools input").first().fill("Budapest")
await page.waitForTimeout(1200)
const filtered = await page.locator(".ol-media-grid .ol-media-card").count()
check("关键词过滤命中 1 条", filtered === 1, `cards=${filtered}`)
await page.locator(".ol-media-tools input").first().fill("")
await page.waitForTimeout(1200)

// ---------- 3. 收藏（网格星标，乐观更新） ----------
const budapestCard = page.locator(".ol-media-grid .ol-media-card", { hasText: "The Grand Budapest Hotel" }).first()
await budapestCard.hover()
await budapestCard.locator(".ol-media-fav").click()
await page.waitForTimeout(700)
const stateAfterFav = await apiGet("/media/items/1/state")
check("点星后服务端收藏态落库", stateAfterFav.data?.favorite === true, JSON.stringify(stateAfterFav.data ?? {}))

// ---------- 4. 详情页 ----------
await budapestCard.click()
await page.waitForTimeout(1500)
check("详情路由", /media\/item\/1/.test(page.url()), page.url())

const detailTitle = (await page.locator(".ol-media-detail-title").textContent().catch(() => "")) || ""
check("NFO 标题与年份", detailTitle.includes("The Grand Budapest Hotel") && detailTitle.includes("2014"), detailTitle)

const detailTags = (await page.locator(".ol-media-detail-tags").innerText().catch(() => "")) || ""
check(
  "评分 / 原名 / 题材标签",
  detailTags.includes("8.1") && detailTags.includes("Grand Budapest Hotel") && detailTags.includes("Comedy"),
  detailTags.replace(/\s+/g, " ").slice(0, 80),
)

const overview = (await page.locator(".ol-media-detail-overview").textContent().catch(() => "")) || ""
check("NFO 简介渲染", /legendary concierge/.test(overview), overview.slice(0, 60))

const backdropLoaded = await page
  .locator(".ol-media-backdrop")
  .evaluate((img) => img.complete && img.naturalWidth > 0)
  .catch(() => false)
check("背景图（fanart）加载", backdropLoaded === true, "")

const posterReq = await page.request.get(`${BASE}/api/media/items/1/image?kind=poster`, { headers: { Authorization: token } })
check("图片端点 Content-Type", posterReq.headers()["content-type"] === "image/jpeg", posterReq.headers()["content-type"])
const missing = await page.request.get(`${BASE}/api/media/items/2/image`, { headers: { Authorization: token } })
check("无海报条目 404", missing.status() === 404, `status=${missing.status()}`)
const anon = await page.request.get(`${BASE}/api/media/items/1/image`)
check("匿名访问图片 401", anon.status() === 401, `status=${anon.status()}`)

// ---------- 5. 播放 + 进度上报 ----------
await page.locator(".ol-media-file button", { hasText: "Play" }).first().click()
// 等元素出现而不是固定 sleep 2s：A–H 连跑时机器有负载，路由切换 + 建 video 元素
// 可能超过 2s，`count()` 会当场返回 0 造成假红（断言内容没变，只是不再赌时序）。
const video = page.locator(".ol-mediaplayer-video")
const playerMounted = await video
  .first()
  .waitFor({ state: "visible", timeout: 15000 })
  .then(() => true)
  .catch(() => false)
check("内嵌播放器挂载", playerMounted, "")

// 真实 seek 到 15s：timeupdate 触发节流保存（10 秒粒度 + 首次立即保存）
await video.evaluate((el) => {
  el.currentTime = 15
})
await page.waitForTimeout(1500)
const stateAfterPlay = await apiGet("/media/items/1/state")
const progress = stateAfterPlay.data?.progress_seconds ?? 0
check("进度上报落库（约 15s）", progress >= 10 && progress <= 20, `progress=${progress}`)

const progressLine = (await page.locator(".ol-media-detail-progress").textContent().catch(() => "")) || ""
check("详情页显示续播位置", /0:15|0:1[4-9]/.test(progressLine), progressLine || "(无)")

await page.screenshot({ path: `${SHOT}/media-playing.png` })

// ---------- 6. 媒体首页 vs 媒体库：两个界面 ----------
// 媒体库页只负责「找」：不该再混入首页聚合（继续观看横滑条）
await page.goto(`${BASE}/#/media/library`, { waitUntil: "networkidle" })
await page.waitForTimeout(800)
const stripOnLibrary = await page.locator(".ol-media-strip").count()
check("媒体库页不含首页聚合条", stripOnLibrary === 0, `strip=${stripOnLibrary}`)

// 媒体首页（#/media）才是聚合推荐：继续观看 / 最近入库
await page.goto(`${BASE}/#/media`, { waitUntil: "networkidle" })
await page.waitForTimeout(1500)
const navLabels = (await page.locator(".ol-media-nav .el-button").allTextContents()).map((s) => s.trim())
check("媒体域导航含首页与媒体库入口", navLabels.includes("Media home") && navLabels.includes("Libraries"), JSON.stringify(navLabels))

const sectionTitles = (await page.locator(".ol-media-section-title").allTextContents()).map((s) => s.trim())
check(
  "媒体首页渲染继续观看 + 最近入库",
  sectionTitles.includes("Continue watching") && sectionTitles.includes("Recently added"),
  JSON.stringify(sectionTitles),
)

const stripCard = await page.locator(".ol-media-strip .ol-media-card", { hasText: "The Grand Budapest Hotel" }).count()
check("继续观看条出现该条目", stripCard === 1, `strip=${stripCard}`)

// ---------- 7. 标记看完 ----------
await page.locator(".ol-media-strip .ol-media-card", { hasText: "The Grand Budapest Hotel" }).first().click()
await page.waitForTimeout(1200)
await page.locator("button", { hasText: "Mark watched" }).first().click()
await page.waitForTimeout(800)
const stateAfterWatched = await apiGet("/media/items/1/state")
check("看过标记落库且进度清零", stateAfterWatched.data?.watched === true && (stateAfterWatched.data?.progress_seconds ?? 0) === 0,
  JSON.stringify({ watched: stateAfterWatched.data?.watched, p: stateAfterWatched.data?.progress_seconds }))
await page.screenshot({ path: `${SHOT}/media-detail.png` })

// ---------- 汇总 ----------
check("全程无控制台错误", errors.length === 0, errors.slice(0, 3).join(" | "))

const passed = results.filter((r) => r.ok).length
log(`SUMMARY ${passed}/${results.length}`)
await browser.close()
process.exit(passed === results.length ? 0 : 1)
