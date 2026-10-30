import { chromium } from "playwright"
import { mkdirSync, writeFileSync } from "node:fs"

/**
 * M4 管理台「整库刮削」按钮的真机验收。
 *
 * 用法：`BASE=http://127.0.0.1:5013 ADMIN_PASS=e2e12345 node scripts/verify-scrape.mjs`
 * 前置：隔离后端（临时库 + AdminPassword + DistDir=frontend/dist）。
 *
 * 装置由脚本自建到 `G:/Share/ol-v2-scrape`：
 *   - `Movie One (2001)/movie.mp4` 等 3 个目录（**不带 NFO** → 扫描后条目是 Pending）
 *   - `Empty Lib/`（没有任何视频 → 该库 pending 0，用来验证按钮置灰）
 *
 * **为什么不断言真实的刮削结果**：TMDB provider 的 API 基址是硬编码的
 * `https://api.themoviedb.org/3`，离线装置既拿不到 key 也没网。于是本脚本覆盖
 * 「状态契约 + 按钮三态 + 未配置 provider 的报错透传 + 零控制台错误」；
 * 至于并发守卫（409）、进度换算、连续失败熔断，由 `MediaScrapeCoordinatorTests` 单测覆盖
 * —— 那三件事本来也不该靠网络才能验证。
 */

const BASE = process.env.BASE || "http://127.0.0.1:5013"
const PASS = process.env.ADMIN_PASS || "e2e12345"
const ROOT = process.env.SCRAPE_ROOT || "G:/Share/ol-v2-scrape"
const LIB_FULL = "E2E Scrape Lib"
const LIB_EMPTY = "E2E Scrape Empty"

const log = (...args) => console.log("[scrape]", ...args)
const results = []
function check(name, ok, extra = "") {
  results.push({ name, ok, extra })
  log(ok ? "PASS" : "FAIL", name, extra)
}

// ---------- 0. 装置（幂等） ----------
for (const name of ["Movie One (2001)", "Movie Two (2002)", "Movie Three (2003)", "Empty Lib"]) {
  mkdirSync(`${ROOT}/${name}`, { recursive: true })
}
for (const dir of ["Movie One (2001)", "Movie Two (2002)", "Movie Three (2003)"]) {
  // 空文件就够：扫描器按扩展名判定，不校验大小（verify-media-manage 的 2000 个 filler 同理）
  writeFileSync(`${ROOT}/${dir}/movie.mp4`, "")
}
log("fixture ready:", ROOT, "-> 3 videos / 1 empty dir")

const browser = await chromium.launch({ channel: "msedge", headless: true })
const page = await browser.newPage({ viewport: { width: 1440, height: 900 }, locale: "en-US" })

const errors = []
page.on("console", (msg) => {
  if (msg.type() !== "error") return
  const url = msg.location()?.url ?? ""
  // 「点了刮削、后端答 400 provider 未配置」是预期内的业务拒绝；
  // 浏览器会为 4xx 资源单独记一条 console error，不算页面缺陷。
  if (url.includes("/media/libraries/") && url.endsWith("/scrape")) return
  errors.push(`${msg.text()} @ ${url}`)
})
page.on("pageerror", (e) => errors.push(String(e)))

const token = await page.request
  .post(`${BASE}/api/auth/login`, { data: { username: "admin", password: PASS } })
  .then((r) => r.json())
  .then((body) => body.data.token)
page.addInitScript((value) => localStorage.setItem("token", value), token)

const api = async (method, path, data) =>
  page.request[method](`${BASE}/api${path}`, { data, headers: { Authorization: token } }).then((r) => r.json())

// ---------- 1. 挂载 + 清残留库 ----------
// CreateStorageReq.Addition 是字符串化 JSON（不是嵌套对象），发对象会 400 空响应体
const mount = await api("post", "/admin/storage/create", {
  mount_path: "/e2e",
  driver: "Local",
  addition: JSON.stringify({ root_folder_path: ROOT }),
})
log("mount:", mount.code, mount.code === 200 ? "" : mount.message)

const libs0 = await api("get", "/media/libraries")
for (const lib of libs0.data ?? []) {
  if (lib.name === LIB_FULL || lib.name === LIB_EMPTY) {
    await api("post", `/media/libraries/${lib.id}/delete`, {})
    log("cleaned leftover library", lib.id, lib.name)
  }
}

const mkLib = async (name, path, type) => {
  const resp = await api("post", "/media/libraries", { name, path, type, auto_scan: false })
  if (resp.code !== 200) throw new Error(`create library ${name}: ${resp.message}`)
  return resp.data.id
}
const fullId = await mkLib(LIB_FULL, "/e2e", 0)
const emptyId = await mkLib(LIB_EMPTY, "/e2e/Empty Lib", 0)
log("libraries:", fullId, emptyId)

// ---------- 2. 扫描 A 库（条目落成 Pending） ----------
const scan = await api("post", `/media/libraries/${fullId}/scan`, {})
check("触发扫描", scan.code === 200, scan.message)

let finalState = ""
for (let i = 0; i < 600; i++) {
  const tasksResp = await api("get", "/media/tasks")
  const t = (tasksResp.data ?? []).find((x) => x.library_id === fullId)
  if (t && !t.is_running && t.state !== "idle") {
    finalState = t.state
    break
  }
  await page.waitForTimeout(25)
}
check("扫描完成", finalState === "completed", finalState)

// ---------- 3. 刮削状态契约（API） ----------
const statusResp = await api("get", "/media/scrape/tasks")
check("状态端点 200", statusResp.code === 200, statusResp.message)
const full = (statusResp.data ?? []).find((s) => s.library_id === fullId)
const empty = (statusResp.data ?? []).find((s) => s.library_id === emptyId)
check("状态含 A 库", !!full, JSON.stringify(full ?? {}))
check("状态含 B 库", !!empty, JSON.stringify(empty ?? {}))

check("A 库 pending = 3", full?.pending === 3, `pending=${full?.pending}`)
check("A 库 unmatched = 0", full?.unmatched === 0, `unmatched=${full?.unmatched}`)
check("A 库 matched = 0（无 NFO 即无元数据）", full?.matched === 0, `matched=${full?.matched}`)
check("A 库未在运行", full?.running === false, `running=${full?.running}`)
// 未运行时批次数必须是 0：否则界面会拿上一轮的残留显示一个假的百分比
check("未运行时 batch_size = 0", full?.batch_size === 0, `batch_size=${full?.batch_size}`)
check("未运行时 batch_done = 0", full?.batch_done === 0, `batch_done=${full?.batch_done}`)
check("B 库 pending = 0", empty?.pending === 0, `pending=${empty?.pending}`)

// ---------- 4. 未配置 provider 时的报错透传 ----------
const scrapeResp = await api("post", `/media/libraries/${fullId}/scrape`, {})
check("未配置 TMDB 时返回 400", scrapeResp.code === 400, `code=${scrapeResp.code}`)
// 文案要点出该配哪个设置项，否则用户只知道「失败」
check(
  "错误信息指出要配 media_tmdb_api_key",
  /media_tmdb_api_key/.test(scrapeResp.message ?? ""),
  scrapeResp.message ?? "",
)

const afterFail = await api("get", "/media/scrape/tasks")
const fullAfter = (afterFail.data ?? []).find((s) => s.library_id === fullId)
// 启动失败不能把守卫留在里面：否则这一库会永远显示「正在刮削」且再也点不动
check("启动失败没留下运行态", fullAfter?.running === false, `running=${fullAfter?.running}`)
check("pending 保持不变", fullAfter?.pending === 3, `pending=${fullAfter?.pending}`)

// ---------- 5. UI：列与按钮三态 ----------
await page.goto(`${BASE}/#/@manage/media`, { waitUntil: "networkidle" })
await page.waitForTimeout(1500)

const rowFull = page.locator(".vxe-body--row", { hasText: LIB_FULL })
const rowEmpty = page.locator(".vxe-body--row", { hasText: LIB_EMPTY })
check("管理台出现两个库", (await rowFull.count()) === 1 && (await rowEmpty.count()) === 1)

// 列顺序：名称 0 / 路径 1 / 类型 2 / 扫描状态 3 / 元数据 4 / 最近扫描 5 / 操作 6
const metaCell = await rowFull.locator("td").nth(4).textContent().catch(() => "")
check("元数据列显示 3 pending", /3 pending/.test(metaCell || ""), metaCell?.trim() || "")

const scrapeBtn = rowFull.locator(".ol-manage-scrape")
check("A 库刮削按钮存在", (await scrapeBtn.count()) === 1)
check("有待刮削条目时按钮可用", (await scrapeBtn.isDisabled()) === false)

const emptyBtn = rowEmpty.locator(".ol-manage-scrape")
check("B 库刮削按钮存在", (await emptyBtn.count()) === 1)
check("无待刮削条目时按钮置灰", (await emptyBtn.isDisabled()) === true)
// title 挂在外层 span 上（与「扫描/历史/编辑/删除」等图标按钮的写法一致），
// 不是按钮本身的原生属性。
const titleOf = (btn) => btn.evaluate((el) => el.closest("span")?.getAttribute("title") ?? "")
const emptyTitle = await titleOf(emptyBtn)
check(
  "置灰原因写在 title 上",
  /Nothing to scrape/i.test(emptyTitle || ""),
  emptyTitle || "(无)",
)
const fullTitle = await titleOf(scrapeBtn)
check(
  "可用态的 title 说明待刮削条数",
  /3 pending/.test(fullTitle || ""),
  fullTitle || "(无)",
)

// ---------- 6. UI：确认框 → 报错提示 ----------
await scrapeBtn.click()
await page.waitForTimeout(600)
const boxTitle = await page.locator(".el-message-box__title").textContent().catch(() => "")
check("确认框标题是 Scrape metadata", /Scrape metadata/i.test(boxTitle || ""), boxTitle || "")
const boxBody = await page.locator(".el-message-box__message").textContent().catch(() => "")
check("确认框带上库名", boxBody?.includes(LIB_FULL) === true, boxBody?.slice(0, 60) || "")
check("确认框带上待刮削条数", /3 item/.test(boxBody || ""), boxBody?.slice(0, 130) || "")

await page.locator(".el-message-box__btns .el-button--primary").click()
// 等 toast（未配置 provider 的报错）
await page.waitForSelector(".el-message--error", { timeout: 8000 }).catch(() => {})
const toast = await page.locator(".el-message--error").first().textContent().catch(() => "")
check("报错 toast 透出后端原文", /media_tmdb_api_key/.test(toast || ""), toast?.trim() || "")

// 失败之后界面不该出现幽灵进度
await page.waitForTimeout(2800)
const metaAfter = await rowFull.locator("td").nth(4).textContent().catch(() => "")
check("失败后仍显示 3 pending", /3 pending/.test(metaAfter || ""), metaAfter?.trim() || "")
const progressAfter = await rowFull.locator(".el-progress").count()
check("失败后没有残留进度条", progressAfter === 0, `progress=${progressAfter}`)

// ---------- 7. 运行态（拦截状态响应构造，不依赖真实 provider） ----------
// 真实刮削需要 TMDB key + 公网，离线装置造不出来。这里只验证**界面分支**：
// 拦截状态端点回一份 running 的载荷，看进度条/剩余条数/按钮态是否按契约渲染。
// 后端那半边（409 守卫、进度换算、熔断）由 MediaScrapeCoordinatorTests 覆盖。
const runningPayload = {
  code: 200,
  message: "success",
  data: [
    {
      library_id: fullId,
      library_name: LIB_FULL,
      pending: 2,
      unmatched: 0,
      matched: 1,
      running: true,
      batch_size: 3,
      batch_done: 1,
      failed: 0,
      error: null,
    },
    {
      library_id: emptyId,
      library_name: LIB_EMPTY,
      pending: 0,
      unmatched: 0,
      matched: 0,
      running: false,
      batch_size: 0,
      batch_done: 0,
      failed: 0,
      error: null,
    },
  ],
}
await page.route("**/api/media/scrape/tasks", (route) =>
  route.fulfill({
    status: 200,
    contentType: "application/json",
    body: JSON.stringify(runningPayload),
  }),
)
// 管理台 2.5s 轮询一次，等它把拦到的载荷渲染出来
await page.waitForTimeout(3200)

const bar = rowFull.locator(".el-progress")
check("运行中出现进度条", (await bar.count()) === 1)
// 进度条自带文字被关掉（列宽只有 150px），百分比与剩余条数由下方一行小字承载
const metaRunning = await rowFull.locator("td").nth(4).textContent().catch(() => "")
// batch_done 1 / batch_size 3 → 33%
check("进度百分比按 batch_done/batch_size 算", /33%/.test(metaRunning || ""), metaRunning?.trim() || "")
check("显示剩余条数 2 left", /2 left/.test(metaRunning || ""), metaRunning?.trim() || "")
// 运行态不该同时堆标签：一个格子里三种数字一起跳，看不出是在推进还是卡住
// 只看元数据格：整行里「类型」列也渲染了一个 tag，按行统计会永远 > 0
check(
  "运行态不显示计数标签",
  (await rowFull.locator("td").nth(4).locator(".el-tag").count()) === 0,
)
check("运行中刮削按钮置灰", (await rowFull.locator(".ol-manage-scrape").isDisabled()) === true)
check(
  "运行中按钮带 loading",
  (await rowFull.locator(".ol-manage-scrape.is-loading").count()) === 1,
)

await page.unroute("**/api/media/scrape/tasks")
// 回到真实状态：进度条消失、按钮恢复可用
await page.waitForTimeout(3200)
check("解开拦截后退回真实状态", (await rowFull.locator(".el-progress").count()) === 0)
check("解开拦截后按钮恢复可用", (await rowFull.locator(".ol-manage-scrape").isDisabled()) === false)

// ---------- 7. 控制台干净 ----------
check(
  "零控制台错误（已排除预期的刮削 400）",
  errors.length === 0,
  errors.slice(0, 3).join(" | "),
)

await browser.close()

const failed = results.filter((r) => !r.ok)
console.log(`\n[scrape] SUMMARY ${results.length - failed.length}/${results.length} passed`)
if (failed.length) {
  for (const f of failed) console.log("  FAIL:", f.name, f.extra)
  process.exit(1)
}
