import { chromium } from "playwright"
import { execFileSync } from "node:child_process"
import { readFileSync } from "node:fs"

/**
 * 文本编辑器（CodeMirror）真机验收。
 *
 * 用法：`BASE=http://127.0.0.1:5001 node scripts/verify-editor.mjs`
 * 前置：隔离后端（临时库 + `AdminPassword=e2e12345` + `/e2e` 挂载）。
 * 覆盖：打开编辑器、改动标记、Ctrl+S 落盘、刷新后仍在、非 UTF-8 降级只读。
 */

const BASE = process.env.BASE || "http://127.0.0.1:5001"
const ROOT = process.env.E2E_ROOT || "G:\\Share\\ol-v2-edit"
const USER = process.env.ADMIN_USER || "admin"
const PASS = process.env.ADMIN_PASS || "e2e12345"

const log = (...args) => console.log("[editor]", ...args)
const results = []
function check(name, ok, extra = "") {
  results.push({ name, ok, extra })
  log(ok ? "PASS" : "FAIL", name, extra)
}

// ---- 装置：一个 UTF-8 文件 + 一个 GBK 文件 ----
execFileSync("python", [
  "-c",
  `
import os, shutil
root = r"${ROOT}"
if os.path.exists(root):
    shutil.rmtree(root)
os.makedirs(root)
open(os.path.join(root, "note.txt"), "w", encoding="utf8", newline="\\n").write("hello editor\\n")
# GBK：用非 ASCII 才能验证「非 UTF-8 降级只读」
open(os.path.join(root, "legacy.txt"), "wb").write("中文内容，GBK 编码\\n".encode("gbk"))
print("[editor] fixture:", sorted(os.listdir(root)))
`,
])

const browser = await chromium.launch({ channel: "msedge", headless: true })
const page = await browser.newPage({ viewport: { width: 1440, height: 900 }, locale: "en-US" })
const errors = []
page.on("console", (msg) => {
  if (msg.type() === "error") errors.push(msg.text())
})
page.on("pageerror", (e) => errors.push(String(e)))

const token = await page
  .request.post(`${BASE}/api/auth/login`, { data: { username: USER, password: PASS } })
  .then((r) => r.json())
  .then((body) => body.data.token)
await page.addInitScript((value) => localStorage.setItem("token", value), token)

/** 打开文件并切到「Text Editor」预览器。 */
async function openInEditor(name) {
  await page.goto(`${BASE}/#/?path=${encodeURIComponent(`/e2e/${name}`)}`, { waitUntil: "networkidle" })
  await page.waitForTimeout(1500)
  const select = page.locator(".ol-preview-actions .el-select").first()
  await select.click()
  await page.waitForTimeout(300)
  await page.locator(".el-select-dropdown__item", { hasText: "Text Editor" }).first().click()
  await page.waitForTimeout(2000)
}

// ---------- 1. 打开与编辑 ----------
await openInEditor("note.txt")
const editorVisible = (await page.locator(".ol-editor-host .cm-content").count()) > 0
check("编辑器挂载（CodeMirror DOM）", editorVisible)

const initial = await page.locator(".ol-editor-host .cm-content").innerText()
check("载入文件内容", /hello editor/.test(initial), JSON.stringify(initial.slice(0, 40)))

await page.locator(".ol-editor-host .cm-content").click()
await page.keyboard.press("Control+End")
await page.keyboard.type("\nsecond line")
await page.waitForTimeout(500)

const dirtyTag = await page.locator(".ol-preview-toolbar .el-tag", { hasText: "Unsaved" }).count()
check("改动后出现「未保存」标记", dirtyTag > 0, `tags=${dirtyTag}`)

const saveButton = page.locator(".ol-preview-toolbar button", { hasText: "Save" }).first()
check("有改动时保存按钮可用", await saveButton.isEnabled())

// ---------- 2. Ctrl+S 落盘 ----------
await page.keyboard.press("Control+s")
await page.waitForTimeout(1800)

const onDisk = readFileSync(`${ROOT}\\note.txt`, "utf8")
check("文件已写回磁盘", /second line/.test(onDisk), JSON.stringify(onDisk))

const dirtyAfter = await page.locator(".ol-preview-toolbar .el-tag", { hasText: "Unsaved" }).count()
check("保存后「未保存」标记消失", dirtyAfter === 0)

// ---------- 3. 撤销回原样也不再算脏 ----------
await page.locator(".ol-editor-host .cm-content").click()
await page.keyboard.press("Control+z")
await page.waitForTimeout(600)
check("撤销后未保存标记重新出现", (await page.locator(".ol-preview-toolbar .el-tag", { hasText: "Unsaved" }).count()) > 0)

// ---------- 4. 非 UTF-8 降级只读 ----------
await openInEditor("legacy.txt")
const warning = await page.locator(".ol-office-body .el-alert").first().innerText().catch(() => "")
check("非 UTF-8 给出明确提示", /not valid UTF-8|UTF-8/.test(warning), warning.replace(/\s+/g, " ").slice(0, 60))
check(
  "非 UTF-8 时保存按钮禁用",
  await page.locator(".ol-preview-toolbar button", { hasText: "Save" }).first().isDisabled(),
)
check("非 UTF-8 仍可查看内容", (await page.locator(".ol-editor-host .cm-content").innerText()).length > 0)

check("全程无控制台错误", errors.length === 0, errors.slice(0, 3).join(" | "))

const passed = results.filter((r) => r.ok).length
log(`SUMMARY ${passed}/${results.length}`)
await browser.close()
process.exit(passed === results.length ? 0 : 1)
