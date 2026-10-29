import { chromium } from "playwright"
import { execFileSync } from "node:child_process"

/**
 * 批量重命名（正则 / 序号 / 查找替换）真机验收。
 *
 * 用法：`BASE=http://127.0.0.1:5001 node scripts/verify-rename.mjs`
 * 前置：隔离后端（临时库 + `AdminPassword=e2e12345` + `/e2e` 挂载）。
 * 脚本自己重建装置，幂等；跑完不清理，便于排查。
 */

const BASE = process.env.BASE || "http://127.0.0.1:5001"
const ROOT = process.env.E2E_ROOT || "G:\\Share\\ol-v2-rename"
const USER = process.env.ADMIN_USER || "admin"
const PASS = process.env.ADMIN_PASS || "e2e12345"

const log = (...args) => console.log("[rename]", ...args)
const results = []
function check(name, ok, extra = "") {
  results.push({ name, ok, extra })
  log(ok ? "PASS" : "FAIL", name, extra)
}

// ---- 装置：6 个 IMG_00x.txt + 一个目录，重命名后顺序好断言 ----
execFileSync("python", [
  "-c",
  `
import os, shutil
root = r"${ROOT}"
if os.path.exists(root):
    shutil.rmtree(root)
os.makedirs(root)
for i in range(1, 7):
    open(os.path.join(root, "IMG_%03d.txt" % i), "w", encoding="utf8").write("x")
os.makedirs(os.path.join(root, "keep-dir"))
print("[rename] fixture:", sorted(os.listdir(root)))
`,
])

const browser = await chromium.launch({ channel: "msedge", headless: true })
const page = await browser.newPage({ viewport: { width: 1440, height: 900 }, locale: "en-US" })
const errors = []
page.on("console", (msg) => {
  if (msg.type() === "error") errors.push(msg.text())
})
page.on("pageerror", (e) => errors.push(String(e)))

// 登录：批量写操作需要权限（游客只读）
const token = await page
  .request.post(`${BASE}/api/auth/login`, { data: { username: USER, password: PASS } })
  .then((r) => r.json())
  .then((body) => body.data.token)
await page.addInitScript((value) => localStorage.setItem("token", value), token)

await page.goto(`${BASE}/#/?path=/e2e`, { waitUntil: "networkidle" })
await page.waitForTimeout(1500)

const listNames = async () =>
  page.locator(".el-table__body .ol-file-name").allTextContents()

/** 全选当前页并打开批量重命名对话框。 */
async function selectAllAndOpen() {
  await page.locator(".el-table__header .el-checkbox").first().click()
  await page.waitForTimeout(300)
  await page.locator(".ol-batch-bar button", { hasText: "重命名" }).first().click()
  await page.waitForTimeout(500)
  return page.locator(".el-dialog:visible").filter({ hasText: "批量重命名" }).first()
}

const visibleDialog = () =>
  page.locator(".el-dialog:visible").filter({ hasText: "批量重命名" }).first()

// ---------- 1. 正则替换 ----------
let dialog = await selectAllAndOpen()
check("批量重命名对话框打开", (await dialog.count()) > 0)

const inputs = dialog.locator("input.el-input__inner")
await inputs.nth(0).fill("^IMG_(\\d+)")
await inputs.nth(1).fill("photo-$1")
await page.waitForTimeout(400)

const previewNames = await dialog.locator(".ol-rename-row").allTextContents()
check(
  "预览实时算出新名",
  previewNames.some((t) => t.includes("photo-001.txt")),
  previewNames.slice(0, 2).join(" | "),
)
const summary = await dialog.locator(".ol-rename-summary").innerText()
check("汇总显示将重命名 7 项（6 文件 + 目录未命中）", /将重命名 6 项/.test(summary), summary.replace(/\s+/g, " "))

await dialog.locator("button", { hasText: "应用" }).first().click()
await page.waitForTimeout(1500)
const afterRegex = await listNames()
check(
  "服务端已改名",
  afterRegex.some((t) => t.includes("photo-006.txt")) && !afterRegex.some((t) => t.includes("IMG_001.txt")),
  afterRegex.join(","),
)

// ---------- 2. 冲突检测：全部改成同一个名字 ----------
dialog = await selectAllAndOpen()
const inputs2 = dialog.locator("input.el-input__inner")
await inputs2.nth(0).fill("^.*$")
await inputs2.nth(1).fill("same.txt")
await page.waitForTimeout(400)
const conflictAlert = await dialog.locator(".el-alert").first().innerText().catch(() => "")
check("撞名被拦下并说明原因", /重复/.test(conflictAlert), conflictAlert.replace(/\s+/g, " ").slice(0, 60))
check("有问题时「应用」按钮禁用", await dialog.locator("button", { hasText: "应用" }).first().isDisabled())

// ---------- 3. 非法字符 ----------
await inputs2.nth(1).fill("bad/name.txt")
await page.waitForTimeout(400)
const invalidAlert = await dialog.locator(".el-alert").first().innerText().catch(() => "")
check("非法字符被拦下", /不合法/.test(invalidAlert), invalidAlert.replace(/\s+/g, " ").slice(0, 60))

// ---------- 4. 序号命名 ----------
await dialog.locator(".el-radio", { hasText: "序号命名" }).first().click()
await page.waitForTimeout(300)
await dialog.locator("input.el-input__inner").first().fill("ep{number}")
const numberInputs = dialog.locator(".el-input-number input")
await numberInputs.nth(1).fill("2")
await numberInputs.nth(1).blur()
await page.waitForTimeout(400)
const seqPreview = await dialog.locator(".ol-rename-row").allTextContents()
// 目录也占一个序号（keep-dir → ep01，无扩展名），文件从 ep02 起
check(
  "序号模式补零到两位（目录先占号）",
  seqPreview.some((t) => t.includes("ep01")) && seqPreview.some((t) => t.includes("ep02.txt")),
  seqPreview.slice(0, 2).join(" | "),
)
await dialog.locator("button", { hasText: "应用" }).first().click()
await page.waitForTimeout(1500)
const afterSeq = await listNames()
check(
  "序号重命名生效",
  afterSeq.some((t) => t.includes("ep01")) && afterSeq.some((t) => t.includes("ep07.txt")),
  afterSeq.join(","),
)

// ---------- 5. 查找替换 ----------
dialog = await selectAllAndOpen()
await dialog.locator(".el-radio", { hasText: "查找替换" }).first().click()
await page.waitForTimeout(300)
const inputs3 = dialog.locator("input.el-input__inner")
await inputs3.nth(0).fill("ep")
await inputs3.nth(1).fill("clip-")
await page.waitForTimeout(400)
const replacePreview = await dialog.locator(".ol-rename-row").allTextContents()
check(
  "查找替换预览",
  replacePreview.some((t) => t.includes("clip-02.txt")),
  replacePreview.slice(0, 2).join(" | "),
)
await dialog.locator("button", { hasText: "应用" }).first().click()
await page.waitForTimeout(1500)
const afterReplace = await listNames()
check("查找替换生效", afterReplace.some((t) => t.includes("clip-07.txt")), afterReplace.join(","))

check("全程无控制台错误", errors.length === 0, errors.slice(0, 3).join(" | "))

const passed = results.filter((r) => r.ok).length
log(`SUMMARY ${passed}/${results.length}`)
await browser.close()
process.exit(passed === results.length ? 0 : 1)
