import { chromium } from "playwright"

/**
 * 管理台仪表盘（§35）的真机验收。
 *
 * 用法：`BASE=http://127.0.0.1:5055 ADMIN_PASS=e2e12345 FIXTURE=G:/Share/ol-v2-dash \
 *        node scripts/verify-dashboard.mjs`
 * 前置：隔离后端（临时库 + AdminPassword + DistDir=frontend/dist）+ `gen_fixtures.py media`
 *       生成的装置（`Budapest/` 带 Kodi NFO、`NoSidecar/` 只有 mp4）。
 *
 * 断言的是**真实数字**而不是「有没有渲染」：仪表盘的价值全在数字对不对，
 * 只验「卡片存在」等于没验。所以脚本先把装置跑成一个确定的库状态
 * （2 个条目 / 1 已匹配 / 1 待刮削 / 匹配率 50% / 题材 Comedy+Drama），
 * 再逐张卡片、逐个单元格去对。
 *
 * **为什么建第 2 个用户和一个分享**：隔离库里只有内置 admin，用户卡永远是 1，
 * 「计数逻辑是否正确」根本区分不出来。多一条数据才能证明卡片读的是真数据。
 */

const BASE = process.env.BASE || "http://127.0.0.1:5055"
const PASS = process.env.ADMIN_PASS || "e2e12345"
const ROOT = process.env.FIXTURE || "G:/Share/ol-v2-dash"
const LIB = "E2E Dash Lib"
const GUEST = "dash_guest"
const GUEST_PASS = "dash12345"

const log = (...args) => console.log("[dashboard]", ...args)
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

const token = await page.request
  .post(`${BASE}/api/auth/login`, { data: { username: "admin", password: PASS } })
  .then((r) => r.json())
  .then((body) => body.data?.token)
if (!token) throw new Error("admin login failed")
await page.addInitScript((value) => localStorage.setItem("token", value), token)

const api = async (method, path, data) =>
  page.request[method](`${BASE}/api${path}`, { data, headers: { Authorization: token } }).then((r) =>
    r.json(),
  )

// ---------- 1. 装置：挂载 + 库 + 扫描（幂等） ----------
const storages = await api("get", "/admin/storage/list")
const mounted = (storages.data?.content ?? []).some((s) => s.mount_path === "/e2e")
if (!mounted) {
  // Addition 是字符串化 JSON（发对象会 400 且响应体为空）
  const created = await api("post", "/admin/storage/create", {
    mount_path: "/e2e",
    driver: "Local",
    addition: JSON.stringify({ root_folder_path: ROOT }),
  })
  check("建挂载 /e2e", created.code === 200, created.message)
} else {
  log("mount /e2e 已存在，复用")
}

const libs = await api("get", "/media/libraries")
for (const lib of libs.data ?? []) {
  if (lib.name === LIB) {
    await api("post", `/media/libraries/${lib.id}/delete`, {})
    log("清理残留库", lib.id)
  }
}
const createdLib = await api("post", "/media/libraries", {
  name: LIB,
  path: "/e2e",
  type: 0,
  auto_scan: false,
})
check("建媒体库", createdLib.code === 200, createdLib.message)
const libraryId = createdLib.data?.id

await api("post", `/media/libraries/${libraryId}/scan`, {})
let scanState = ""
for (let i = 0; i < 600; i++) {
  const tasks = await api("get", "/media/tasks")
  const task = (tasks.data ?? []).find((t) => t.library_id === libraryId)
  if (task && !task.is_running && task.state !== "idle") {
    scanState = task.state
    break
  }
  await page.waitForTimeout(25)
}
check("扫描完成", scanState === "completed", scanState)

// ---------- 2. 补齐「非默认」数据：第二个用户 + 一条分享 ----------
let usersResp = await api("get", "/admin/user/list")
if (!(usersResp.data?.content ?? []).some((u) => u.username === GUEST)) {
  const u = await api("post", "/admin/user/create", {
    username: GUEST,
    password: GUEST_PASS,
    role: 0,
    permission: 0,
  })
  check("建普通用户", u.code === 200, u.message)
  usersResp = await api("get", "/admin/user/list")
}
// 期望值不写死：初始化时后端会建 `guest` 内置账号（默认停用），绝对值随时可能变，
// 断言「UI 数字 == 接口算出来的数字」才是这张卡真正要证的事。
const allUsers = usersResp.data?.content ?? []
const expectUsers = String(allUsers.length)
const expectAdmins = String(allUsers.filter((u) => u.role === 2).length)
const expectDisabled = String(allUsers.filter((u) => u.disabled).length)
check("用户数已非默认（≥3：guest + admin + 脚本新建）", allUsers.length >= 3, `${allUsers.length}`)

const sharesResp0 = await api("get", "/share/list")
if ((sharesResp0.data?.content ?? []).length === 0) {
  const s = await api("post", "/share/create", { files: ["/e2e"], pwd: "", max_accessed: 0 })
  check("建分享", s.code === 200, s.message)
}

// ---------- 3. 接口侧的真实数字（UI 断言的期望值来源） ----------
const statsResp = await api("get", "/media/stats")
const stats = statsResp.data ?? {}
check("接口 item_count = 2", stats.item_count === 2, `item_count=${stats.item_count}`)
check("接口 library_count = 1", stats.library_count === 1, `library_count=${stats.library_count}`)
check("接口 by_genre 含 Comedy/Drama", JSON.stringify(stats.by_genre ?? []).includes("Comedy") &&
  JSON.stringify(stats.by_genre ?? []).includes("Drama"), JSON.stringify(stats.by_genre ?? []))

const scrapeResp = await api("get", "/media/scrape/tasks")
const libStatus = (scrapeResp.data ?? []).find((s) => s.library_id === libraryId)
check("库已匹配 1 条", libStatus?.matched === 1, `matched=${libStatus?.matched}`)
check("库待刮削 1 条", libStatus?.pending === 1, `pending=${libStatus?.pending}`)

// ---------- 4. 页面 ----------
await page.goto(`${BASE}/#/@manage/dashboard`, { waitUntil: "networkidle" })
await page.waitForTimeout(1000)

const heading = (await page.locator(".ol-manage-toolbar .ol-manage-h2").innerText().catch(() => "")).trim()
check("页面标题", heading === "Dashboard", heading)

// 卡片：6 张，顺序即 用户 / 挂载 / 分享 / 媒体库 / 容量 / 索引对象
const cards = page.locator(".ol-dash-card")
const cardCount = await cards.count()
check("6 张指标卡", cardCount === 6, `count=${cardCount}`)
const cardValues = []
for (let i = 0; i < cardCount; i++) {
  cardValues.push((await cards.nth(i).locator(".ol-dash-card-value").innerText()).trim())
}
const cardLabels = []
for (let i = 0; i < cardCount; i++) {
  cardLabels.push((await cards.nth(i).locator(".ol-dash-card-label").innerText()).trim())
}
log("cards:", cardLabels.map((l, i) => `${l}=${cardValues[i]}`).join(" | "))
check("用户卡 = 接口用户数", cardValues[0] === expectUsers, `${cardValues[0]} vs ${expectUsers}`)
check("挂载卡 = 1", cardValues[1] === "1", cardValues[1])
check("分享卡 = 1", cardValues[2] === "1", cardValues[2])
check("媒体库卡 = 1", cardValues[3] === "1", cardValues[3])
check("容量卡是体积（非占位符）", /\d/.test(cardValues[4]) && cardValues[4] !== "-", cardValues[4])

const userSub = (await cards.nth(0).locator(".ol-dash-card-sub").innerText()).trim()
check(
  `用户卡副标题 = ${expectAdmins} 管理员 / ${expectDisabled} 停用`,
  userSub.includes(expectAdmins) && userSub.includes(expectDisabled),
  userSub,
)
const libSub = (await cards.nth(3).locator(".ol-dash-card-sub").innerText()).trim()
check("媒体库卡副标题：2 个条目", libSub.includes("2"), libSub)

// 元数据健康表（第一块面板）
const healthPanel = page.locator(".ol-dash-panel").first()
const rows = healthPanel.locator(".el-table__body tr")
check("健康表 1 行", (await rows.count()) === 1, `rows=${await rows.count()}`)
const cells = []
const cellCount = await rows.first().locator("td").count()
for (let i = 0; i < cellCount; i++) {
  cells.push((await rows.first().locator("td").nth(i).innerText()).trim())
}
log("row:", cells.join(" | "))
check("库名", cells[0] === LIB, cells[0])
check("匹配率 50%", cells[1].includes("50%"), cells[1])
check("已匹配 = 1", cells[2] === "1", cells[2])
check("待刮削 = 1", cells[3] === "1", cells[3])
check("未匹配 = 0", cells[4] === "0", cells[4])
check("状态 = Pending", cells[5].includes("Pending"), cells[5])
check("健康面板汇总含 1 of 2", (await healthPanel.innerText()).includes("1 of 2"), "")

// 驱动分布 / 题材分布
const driverPanel = page.locator(".ol-dash-panel").filter({ hasText: "Mounts by driver" })
const driverLabels = await driverPanel.locator(".ol-dash-bar-label").allInnerTexts()
check("驱动分布含 Local", driverLabels.map((s) => s.trim()).includes("Local"), driverLabels.join(","))

const genrePanel = page.locator(".ol-dash-panel").filter({ hasText: "Genres" })
const genreLabels = (await genrePanel.locator(".ol-dash-bar-label").allInnerTexts()).map((s) => s.trim())
check("题材分布含 Comedy", genreLabels.includes("Comedy"), genreLabels.join(","))
check("题材分布含 Drama", genreLabels.includes("Drama"), genreLabels.join(","))

// 系统状态
const sysLabels = (await page.locator(".ol-dash-sys-label").allInnerTexts()).map((s) => s.trim())
check("系统面板 3 行", sysLabels.length === 3, sysLabels.join(","))
check(
  "系统面板覆盖索引/扫描/刮削",
  sysLabels.join(",") === "Search index,File scan,Metadata scrape",
  sysLabels.join(","),
)

// 刷新按钮会重新取数（不靠时间戳：格式化到分钟，同分钟内看不出变化）
const refreshReq = page.waitForRequest((r) => r.url().includes("/api/media/stats"), { timeout: 15000 })
await page.locator(".ol-manage-toolbar button").last().click()
await refreshReq
check("刷新重新拉取", true, "")

// ---------- 5. 导航：dashboard 已放开，plugins 已移除 ----------
const navTexts = (await page.locator(".ol-manage-menu .el-menu-item").allInnerTexts()).map((s) =>
  s.replace(/\s+/g, " ").trim(),
)
check("导航 12 项", navTexts.length === 12, navTexts.join(","))
check("导航无 Plugins", !navTexts.some((t) => /Plugins/i.test(t)), navTexts.join(","))
check("导航首项为 Dashboard", navTexts[0] === "Dashboard", navTexts[0])
check("Dashboard 未标 (soon)", !navTexts[0].includes("soon"), navTexts[0])
const disabledCount = await page.locator(".ol-manage-menu .el-menu-item.is-disabled").count()
// backup-restore 已在 §37 放开，现在只剩 About 一个 (soon)
check("仅 About 仍未就绪", disabledCount === 1, `disabled=${disabledCount}`)

check("零控制台错误", errors.length === 0, errors.slice(0, 3).join(" | "))

// ---------- 汇总 ----------
const failed = results.filter((r) => !r.ok)
log(`\n${results.length - failed.length}/${results.length} passed`)
if (failed.length) {
  log("FAILED:", failed.map((r) => `${r.name} (${r.extra})`).join("; "))
}
await browser.close()
process.exit(failed.length ? 1 : 0)
