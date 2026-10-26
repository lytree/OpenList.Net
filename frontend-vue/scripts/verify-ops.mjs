import { chromium } from "playwright"
import { execFileSync } from "node:child_process"

/**
 * M1 操作集（第二批 7 个操作）真机验收。
 *
 * 前置：隔离后端在 `BASE` 跑（临时库 + `AdminPassword=e2e12345` + 挂载 `/e2e`），
 *      脚本会自己重建测试目录（幂等，跑完不清理，便于排查）。
 *
 * 关键技巧：用 `addInitScript` 删掉 `showSaveFilePicker`，强制走 Blob 回退路径 ——
 * headless 下系统级"另存为"对话框无法交互，删掉它才能验证 zip 真的产出了文件。
 */

const BASE = process.env.BASE || "http://127.0.0.1:5001"
const USER = process.env.ADMIN_USER || "admin"
const PASS = process.env.ADMIN_PASS || "e2e12345"
const SHOT = process.env.SHOT_DIR || "F:/Code/Dotnet/OpenList.Net/.e2e"
const ROOT = process.env.E2E_ROOT || "G:\\Share\\ol-v2-ops"

// ---- 重建测试数据（40 txt + bundle.zip + 子目录A + 空目录B + README.md）----
execFileSync(
  "python",
  [
    "-c",
    `
import os, shutil, zipfile
root = r"${ROOT}"
if os.path.exists(root):
    shutil.rmtree(root)
os.makedirs(root)
for i in range(1, 41):
    open(os.path.join(root, "file-%02d.txt" % i), "w", encoding="utf8").write("demo %d\\n" % i)
os.makedirs(os.path.join(root, "子目录A"))
open(os.path.join(root, "子目录A", "inner.txt"), "w", encoding="utf8").write("inner\\n")
os.makedirs(os.path.join(root, "空目录B"))
with zipfile.ZipFile(os.path.join(root, "bundle.zip"), "w") as z:
    z.writestr("hello.txt", "hello from zip\\n")
    z.writestr("sub/second.txt", "nested\\n")
open(os.path.join(root, "README.md"), "w", encoding="utf8").write("# ol-v2-ops\\n\\n操作集 e2e 的临时目录。\\n")
print("[ops] fixture entries:", len(os.listdir(root)))
`,
  ],
  { stdio: "inherit" },
)

const log = (...a) => console.log("[ops]", ...a)
const results = []
function check(name, ok, extra = "") {
  results.push({ name, ok, extra })
  log(ok ? "PASS" : "FAIL", name, extra)
}

const browser = await chromium.launch({ channel: "msedge", headless: true })
const page = await browser.newPage({ viewport: { width: 1440, height: 900 } })
const errors = []
page.on("console", (m) => {
  if (m.type() === "error") errors.push(m.text())
})
page.on("pageerror", (e) => errors.push(String(e)))

await page.addInitScript(() => {
  try {
    delete window.showSaveFilePicker
  } catch {
    window.showSaveFilePicker = undefined
  }
})

const search = page.locator("input[placeholder='在当前目录筛选']")

async function waitList() {
  await page.waitForTimeout(900)
}

/** 上一步若残留对话框（例如业务返回非 200），先关掉，避免遮罩挡住后续点击。 */
async function ensureNoDialog() {
  if ((await page.locator(".el-dialog:visible").count()) === 0) return
  const cancel = page.locator(".el-dialog:visible button:has-text('取消')").first()
  if ((await cancel.count()) > 0) await cancel.click().catch(() => {})
  else {
    const close = page.locator(".el-dialog:visible button:has-text('关闭')").first()
    if ((await close.count()) > 0) await close.click().catch(() => {})
  }
  await page.waitForTimeout(400)
}

/** 用过滤框把目标行挤到第一页，再右键点菜单项。 */
async function rowMenu(name, menuText) {
  await ensureNoDialog()
  await search.fill(name)
  await page.waitForTimeout(400)
  const row = page.locator(".el-table__body tr").filter({ hasText: name }).first()
  await row.locator(".ol-file-name").first().click({ button: "right" })
  await page.waitForTimeout(250)
  const item = page.locator(".ol-ctx-item").filter({ hasText: menuText }).first()
  const exists = (await item.count()) > 0
  if (exists) await item.click()
  await page.waitForTimeout(350)
  await search.fill("")
  await page.waitForTimeout(400)
  return exists
}

async function moreMenu(item) {
  await ensureNoDialog()
  await page.locator("button:has-text('更多')").first().click()
  await page.waitForTimeout(250)
  await page.locator(".el-dropdown-menu__item").filter({ hasText: item }).first().click()
  await page.waitForTimeout(350)
}

async function closeDialog() {
  await page.locator(".el-dialog:visible button:has-text('取消')").first().click()
  await page.waitForTimeout(350)
}

// ---------- 登录 ----------
await page.goto(`${BASE}/#/@login`, { waitUntil: "networkidle" })
await page.locator("input").first().fill(USER)
await page.locator("input[type=password]").first().fill(PASS)
await page.locator("button:has-text('Login')").first().click()
await page.waitForTimeout(1500)
check("登录成功", (await page.locator(".ol-user-name").count()) > 0)

// ---------- 进入挂载点 ----------
await page.goto(`${BASE}/#/?path=/e2e`, { waitUntil: "networkidle" })
await waitList()
const totalTag = await page.locator(".ol-files-bar .el-tag").last().textContent().catch(() => "")
check("挂载点列出 44 项", /共 44 项/.test(totalTag || ""), `tag="${totalTag}"`)
check("可写（工具栏出现「更多」）", (await page.locator("button:has-text('更多')").count()) > 0)

// ---------- 1. 新建文件 ----------
await moreMenu("新建文件")
check("新建文件对话框打开", (await page.locator(".el-dialog:visible .el-dialog__title:has-text('新建文件')").count()) > 0)
await page.locator(".el-dialog:visible input").first().fill("e2e-new.txt")
await page.locator(".el-dialog:visible button:has-text('创建')").first().click()
await waitList()
check("新建文件后列表出现该项", (await page.locator(".el-table__body tr").filter({ hasText: "e2e-new.txt" }).count()) > 0)

// ---------- 2. 删除空目录 ----------
await moreMenu("删除空目录")
check("删除空目录对话框打开", (await page.locator(".el-dialog:visible .el-dialog__title:has-text('删除空目录')").count()) > 0)
await page.locator(".el-dialog:visible button:has-text('开始清理')").first().click()
await waitList()
check("空目录 B 已被递归清理", (await page.locator(".el-table__body tr").filter({ hasText: "空目录B" }).count()) === 0)
check("非空目录 A 保留", (await page.locator(".el-table__body tr").filter({ hasText: "子目录A" }).count()) > 0)

// ---------- 3. 解压 ----------
const hasDecompress = await rowMenu("bundle.zip", "解压")
check("归档文件右键有「解压」", hasDecompress)
check("解压对话框打开", (await page.locator(".el-dialog:visible .el-dialog__title:has-text('解压')").count()) > 0)
await page.locator(".el-dialog:visible button:has-text('开始解压')").first().click()
await waitList()
const extracted = await page.request.post(`${BASE}/api/fs/list`, {
  data: { path: "/e2e/bundle", password: "", page: 1, per_page: 0 },
})
const extractedBody = await extracted.json()
check(
  "解压产出可读目录（含 hello.txt）",
  extractedBody.code === 200 &&
    (extractedBody.data?.content ?? []).some((o) => o.name === "hello.txt"),
  `code=${extractedBody.code} items=${(extractedBody.data?.content ?? []).length}`,
)

// ---------- 4. 分享 ----------
await rowMenu("子目录A", "分享")
check("分享对话框打开", (await page.locator(".el-dialog:visible .el-dialog__title:has-text('创建分享')").count()) > 0)
check("分享对话框列出待分享路径", /子目录A/.test(await page.locator(".el-dialog:visible .el-dialog__body").innerText()))
await page.locator(".el-dialog:visible button:has-text('创建')").first().click()
await page.waitForTimeout(1200)
const shareLink = await page.locator(".el-dialog:visible input").first().inputValue().catch(() => "")
check("分享创建成功并给出 #/s/ 链接", /#\/s\/[\w-]+/.test(shareLink), shareLink)
await page.locator(".el-dialog:visible button:has-text('关闭')").first().click()
await page.waitForTimeout(300)

const code = (shareLink.match(/#\/s\/([\w-]+)/) || [])[1]
if (code) {
  const resp = await page.request.post(`${BASE}/api/fs/list`, {
    data: { path: "/", password: "" },
    headers: { Authorization: code },
  })
  const body = await resp.json()
  // 分享的根就是「子目录A」本身，所以列 "/" 看到的是它内部的内容。
  check(
    "分享码可匿名列目录",
    body.code === 200 && (body.data?.content ?? []).some((o) => o.name === "inner.txt"),
    `code=${body.code} items=${(body.data?.content ?? []).map((o) => o.name).join(",")}`,
  )
} else {
  check("分享码可匿名列目录", false, "no code")
}

// ---------- 5. 打包下载（Blob 回退）----------
await rowMenu("子目录A", "打包下载")
check("打包下载对话框打开", (await page.locator(".el-dialog:visible .el-dialog__title:has-text('打包下载')").count()) > 0)
const downloadPromise = page.waitForEvent("download", { timeout: 30000 }).catch(() => null)
await page.locator(".el-dialog:visible button:has-text('开始打包')").first().click()
const download = await downloadPromise
check("触发 zip 下载", Boolean(download), download ? `name=${download.suggestedFilename()}` : "no download event")
if (download) {
  const filePath = await download.path()
  const { readFileSync, statSync } = await import("node:fs")
  const { unzipSync } = await import("fflate")
  const size = filePath ? statSync(filePath).size : 0
  let names = []
  try {
    names = filePath ? Object.keys(unzipSync(readFileSync(filePath))) : []
  } catch (err) {
    names = [`unzip failed: ${err.message}`]
  }
  check(
    "zip 可被标准实现解开且含 inner.txt",
    names.some((n) => n.endsWith("inner.txt")),
    `${size} bytes, entries=${names.join(",")}`,
  )
}
await page.locator(".el-dialog:visible button:has-text('关闭')").first().click()
await page.waitForTimeout(300)

// ---------- 6. 递归移动（渲染 + 必填校验）----------
await rowMenu("子目录A", "递归移动")
check("递归移动对话框打开", (await page.locator(".el-dialog:visible .el-dialog__title:has-text('递归移动')").count()) > 0)
await page.locator(".el-dialog:visible button:has-text('开始移动')").first().click()
await page.waitForTimeout(600)
check("未选目标目录时给出提示且不提交", (await page.locator(".el-message--warning").count()) > 0)
await closeDialog()

// ---------- 6b. 递归移动真实执行（整树搬移 + 删源）----------
const token = await page.evaluate(() => localStorage.getItem("token"))
const apiPost = (apiPath, body) =>
  page.request.post(`${BASE}/api${apiPath}`, {
    data: body,
    headers: { Authorization: token ?? "" },
  })
await apiPost("/fs/mkdir", { path: "/e2e/moved-into" })
await apiPost("/fs/mkdir", { path: "/e2e/子目录C" })
await page.goto(`${BASE}/#/?path=/e2e`, { waitUntil: "networkidle" })
// 目录是走 API 建的，SPA 自己的列表还停在旧快照；整页重载拿到最新结果，
// 同时绕开后端目录缓存（fs/list 默认带缓存）。
await page.reload({ waitUntil: "networkidle" })
await waitList()

await rowMenu("子目录C", "递归移动")
const moveDlg = page.locator(".el-dialog:visible").filter({ hasText: "递归移动" }).first()
await moveDlg.locator("button:has-text('选择')").first().click()
await page.waitForTimeout(800)
const picker = page.locator(".el-dialog:visible").filter({ hasText: "选择目标目录" }).first()
await picker.locator(".ol-picker-item").filter({ hasText: "moved-into" }).first().click()
await page.waitForTimeout(300)
await picker.locator("button").filter({ hasText: "Choose here" }).first().click()
await page.waitForTimeout(400)
await moveDlg.locator("button:has-text('开始移动')").first().click()
await page.waitForTimeout(1500)

const srcGone = await apiPost("/fs/get", { path: "/e2e/子目录C", password: "" })
const dstExists = await apiPost("/fs/get", { path: "/e2e/moved-into/子目录C", password: "" })
const [srcBody, dstBody] = [await srcGone.json(), await dstExists.json()]
check(
  "递归移动：源消失、目标出现",
  srcBody.code !== 200 && dstBody.code === 200 && dstBody.data?.is_dir === true,
  `src=${srcBody.code} dst=${dstBody.code}`,
)

// ---------- 7. 离线下载（未配置工具 → 禁用）----------
await moreMenu("离线下载")
check("离线下载对话框打开", (await page.locator(".el-dialog:visible .el-dialog__title:has-text('离线下载')").count()) > 0)
const offlineAlert = await page.locator(".el-dialog:visible .el-alert").innerText().catch(() => "")
const submitDisabled = await page
  .locator(".el-dialog:visible button:has-text('提交任务')")
  .first()
  .isDisabled()
  .catch(() => false)
check(
  "未配置下载工具时明确禁用",
  /没有启用任何离线下载工具/.test(offlineAlert) && submitDisabled,
  `disabled=${submitDisabled}`,
)
await closeDialog()

await page.screenshot({ path: `${SHOT}/ops-final.png` })
check("控制台无错误", errors.length === 0, errors.slice(0, 3).join(" | "))

await browser.close()

const failed = results.filter((r) => !r.ok)
console.log("\n==== SUMMARY ====")
for (const r of results) console.log(`${r.ok ? "PASS" : "FAIL"} ${r.name} ${r.extra || ""}`)
console.log(`\n${results.length - failed.length}/${results.length} passed`)
process.exit(failed.length ? 1 : 0)
