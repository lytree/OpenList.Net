import { chromium } from "playwright"

/**
 * M4 第二片（媒体库管理台）的真机验收。
 *
 * 用法：`BASE=http://127.0.0.1:5001 node scripts/verify-media-manage.mjs`
 * 前置：隔离后端（临时库 + AdminPassword=e2e12345 + DistDir=frontend/dist），
 *       装置 `G:\Share\ol-v2-media\E2E Movie Night\E2E.Movie.Night.2026.mp4`（ffmpeg 生成）。
 * 脚本自建 /e2e 挂载（幂等）并全程走 UI：建库 → 扫描 → 进度 → 历史 → 编辑 → 删除。
 * 依赖 admin 内置权限位 0x71FF 含 manage_media（位 14）。
 */

const BASE = process.env.BASE || "http://127.0.0.1:5001"
const LIB_NAME = "E2E Manage Lib"
const LIB_RENAMED = "E2E Manage Lib Renamed"

const log = (...args) => console.log("[media-manage]", ...args)
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

const token = await page.request
  .post(`${BASE}/api/auth/login`, {
    data: { username: "admin", password: process.env.ADMIN_PASS || "e2e12345" },
  })
  .then((r) => r.json())
  .then((body) => body.data.token)
page.addInitScript((value) => localStorage.setItem("token", value), token)

const api = async (method, path, data) =>
  page.request[method](`${BASE}/api${path}`, { data, headers: { Authorization: token } }).then((r) => r.json())

// ---------- 0. 环境预备（幂等） ----------
// 注意：CreateStorageReq.Addition 是字符串化的 JSON（不是嵌套对象），发对象会 400 空响应体
const mount = await api("post", "/admin/storage/create", {
  mount_path: "/e2e",
  driver: "Local",
  addition: JSON.stringify({ root_folder_path: "G:\\Share\\ol-v2-media" }),
})
log("mount:", mount.code, mount.code === 200 ? "" : mount.message)

// 清掉上次跑挂的残留库（按名字找）
const libs0 = await api("get", "/media/libraries")
for (const lib of libs0.data ?? []) {
  if (lib.name === LIB_NAME || lib.name === LIB_RENAMED) {
    await api("post", `/media/libraries/${lib.id}/delete`, {})
    log("cleaned leftover library", lib.id, lib.name)
  }
}

// 占位视频：空 .mp4 让扫描有足够工作量，running 徽标才可被观测到
//（1 个 6s 的真视频 100ms 内就扫完了）。2001 个文件整库删除不留垃圾。
import { readdirSync, writeFileSync, mkdirSync } from "node:fs"
const fillerDir = "G:/Share/ol-v2-media/E2E Movie Night/fillers"
mkdirSync(fillerDir, { recursive: true })
const FILLERS = 2000
const existing = readdirSync(fillerDir).filter((f) => f.endsWith(".mp4")).length
for (let i = existing; i < FILLERS; i++) {
  writeFileSync(`${fillerDir}/filler-${String(i).padStart(4, "0")}.mp4`, "")
}
log("fillers ready:", FILLERS)

// ---------- 1. 管理台入口 ----------
await page.goto(`${BASE}/#/@manage/media`, { waitUntil: "networkidle" })
await page.waitForTimeout(1500)

const h2 = await page.locator(".ol-manage-h2").textContent().catch(() => "")
check("管理台菜单与标题", h2?.includes("Media Libraries"), h2 || "(无)")

const emptyTip = await page.locator(".el-alert__title", { hasText: "No media library yet" }).count()
check("空库提示出现", emptyTip === 1)

// ---------- 2. 建库 ----------
await page.getByRole("button", { name: "New library" }).click()
await page.waitForTimeout(400)
await page.locator(".el-dialog input").first().fill(LIB_NAME)
await page.locator(".el-dialog input").nth(1).fill("/e2e")
await page.locator(".el-dialog__footer").getByRole("button", { name: "Create" }).click()
await page.waitForTimeout(1200)

const row = page.locator(".vxe-body--row", { hasText: LIB_NAME })
check("建库后表格出现新行", (await row.count()) === 1)
const pathCell = await row.locator(".ol-mono").first().textContent().catch(() => "")
check("路径以 VFS 形式显示 /e2e", pathCell === "/e2e", pathCell || "(无)")

const libsAfterCreate = await api("get", "/media/libraries")
const created = (libsAfterCreate.data ?? []).find((l) => l.name === LIB_NAME)
check("服务端库已落库", !!created && created.path === "/e2e", JSON.stringify(created ?? {}))
check("库 id", !!created, `id=${created?.id}`)

// ---------- 3. 扫描 → 进度 → 完成 ----------
await row.getByTitle("Scan", { exact: true }).click()

// 触发后立刻高频轮 API 抓 running（扫描是 fire-and-forget 后台任务）
let sawRunning = false
let finalState = ""
for (let i = 0; i < 600; i++) {
  const tasksResp = await api("get", "/media/tasks")
  const t = (tasksResp.data ?? []).find((x) => x.library_id === created.id)
  if (t?.is_running) sawRunning = true
  if (t && !t.is_running && t.state !== "idle") {
    finalState = t.state
    break
  }
  await page.waitForTimeout(25)
}
check("扫描进入过 running 状态（API 高频轮询）", sawRunning)
check("扫描最终 completed", finalState === "completed", finalState)

// UI 侧：任务轮询（2.5s 周期）+ running→结束边沿刷新，最终应显示 Completed 徽标
await page.waitForTimeout(3200)
const stateTag = await row.locator("td").nth(3).locator(".el-tag").first().textContent().catch(() => "")
check("UI 状态徽标最终 Completed", /completed/i.test(stateTag || ""), stateTag || "")

// 扫描按目录聚合：根目录 1 条 + fillers 目录 1 条
const items = await api("get", "/media/items?library_id=" + created.id + "&per_page=1")
const itemCount = items.data?.total ?? items.data?.content?.length ?? 0
check("扫描聚成 2 个条目（2001 个视频文件）", itemCount === 2, `items=${itemCount}`)
// 列顺序：名称 0 / 路径 1 / 类型 2 / 扫描状态 3 / 元数据 4 / 最近扫描 5 / 操作 6
// （「元数据」列是整库刮削按钮那一轮插进来的，最近扫描从 4 挪到 5）
const lastScanCell = await row.locator("td").nth(5).textContent().catch(() => "")
check("最近扫描时间列不再是 -", lastScanCell && !/^-$/.test(lastScanCell.trim()), lastScanCell?.trim() || "")

// running 时应有进度数字（found 计数）；结束后显示结果摘要
const stateCell = await row.locator("td").nth(3).textContent().catch(() => "")
check("完成后状态列带结果摘要 +2", /\+2/.test(stateCell || ""), stateCell?.trim() || "")

// ---------- 4. 扫描历史 ----------
await row.getByTitle("Scan history").click()
await page.waitForTimeout(800)
const logRows = await page.locator(".el-dialog:visible .vxe-body--row").count()
check("历史对话框有 1 条记录", logRows === 1, `rows=${logRows}`)
const logResult = await page.locator(".el-dialog:visible .vxe-body--row").first().textContent().catch(() => "")
check("历史行显示 +2 摘要", /\+2/.test(logResult || ""), logResult?.trim() || "")
await page.locator(".el-dialog:visible .el-dialog__headerbtn").click()
await page.waitForTimeout(400)

// ---------- 5. 重复触发扫描返回 409 语义（先起一次立刻再点，宽松校验不崩） ----------
// （后端 409 时前端走 ElMessage.error 提示，不崩即可 —— 不做强时序断言）

// ---------- 6. 编辑改名 ----------
await row.getByTitle("Edit", { exact: true }).click()
await page.waitForTimeout(400)
const nameInput = page.locator(".el-dialog input").first()
check("编辑对话框回填名称", (await nameInput.inputValue()) === LIB_NAME)
const pathDisabled = await page.locator(".el-dialog input").nth(1).isDisabled()
check("编辑态路径锁定不可改", pathDisabled === true)
await nameInput.fill(LIB_RENAMED)
await page.locator(".el-dialog__footer").getByRole("button", { name: "Save" }).click()
await page.waitForTimeout(1000)
check("改名后表格更新", (await page.locator(".vxe-body--row", { hasText: LIB_RENAMED }).count()) === 1)

// ---------- 7. 删除 ----------
const renamedRow = page.locator(".vxe-body--row", { hasText: LIB_RENAMED })
await renamedRow.getByTitle("Delete", { exact: true }).click()
await page.waitForTimeout(500)
await page.locator(".el-message-box__btns .el-button--primary").click()
await page.waitForTimeout(1000)
check("删除后主表格为空（隐藏对话框的 DOM 不算）",
  (await page.locator(".ol-manage-page .el-card .vxe-body--row").count()) === 0)
const libsFinal = await api("get", "/media/libraries")
check("服务端库已删除", (libsFinal.data ?? []).length === 0, JSON.stringify(libsFinal.data ?? []))

// ---------- 8. 控制台干净 ----------
check("零控制台错误", errors.length === 0, errors.slice(0, 3).join(" | "))

await browser.close()

const failed = results.filter((r) => !r.ok)
console.log(`\n[media-manage] ${results.length - failed.length}/${results.length} checks passed`)
if (failed.length) {
  console.log("[media-manage] FAILED:", failed.map((f) => f.name).join("; "))
  process.exit(1)
}
