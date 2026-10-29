/**
 * 右键菜单 / 快捷键 / 下载直链 UI 冒烟 e2e。
 *
 * 单脚本完成：API 造数据（隔离目录 + 1 个文件）→ 启动后端（5244）+
 * Vite dev（5173）→ Playwright 无头 Edge：行右键菜单、空白右键菜单、
 * Ctrl+A / Delete / Ctrl+C / Escape 快捷键、文件页下载直链、favicon。
 *
 * 用法：node scripts/e2e-ctx-menu.mjs
 */
import { spawn } from "node:child_process"
import path from "node:path"
import { chromium } from "playwright"

const BASE = "http://127.0.0.1:5244"
const DEV = "http://localhost:5173"
const SERVER_DIR = "F:/Code/Dotnet/OpenList.Net/src/OpenList.Server"
const FRONTEND_DIR = "F:/Code/Dotnet/OpenList.Net/frontend"
const TEST_DIR = "/Shared/ol-ctx-test"
const TEST_FILE = "ctx-probe.txt"
const FILE_CONTENT = "hello context menu e2e\n"

let failures = 0
const ok = (cond, label) => {
  console.log(`${cond ? "PASS" : "FAIL"}  ${label}`)
  if (!cond) failures++
}

const waitHttp = async (url, proc, timeoutMs = 60000) => {
  for (let i = 0; i < timeoutMs / 500; i++) {
    try {
      const r = await fetch(url)
      if (r.ok) return
    } catch { /* not yet */ }
    if (proc.exitCode !== null) throw new Error(`process exited early: ${url}`)
    await new Promise((r) => setTimeout(r, 500))
  }
  throw new Error(`timeout waiting for ${url}`)
}

async function main() {
  // ---- 启动服务（造数据要等后端就绪后再做） ----
  const viteBin = path.join(FRONTEND_DIR, "node_modules", "vite", "bin", "vite.js")
  const backend = spawn("dotnet", ["bin/Debug/net10.0/OpenList.Server.dll"], {
    cwd: SERVER_DIR,
    env: { ...process.env, ASPNETCORE_ENVIRONMENT: "Development", ASPNETCORE_URLS: "http://127.0.0.1:5244" },
    stdio: ["ignore", "ignore", "inherit"],
  })
  const vite = spawn(process.execPath, [viteBin, "--port", "5173", "--strictPort"], {
    cwd: FRONTEND_DIR,
    stdio: ["ignore", "ignore", "inherit"],
  })
  try {
    await waitHttp(`${BASE}/api/public/settings`, backend)
    await waitHttp(`${DEV}`, vite)

    // ---- 造数据：登录 + 建隔离目录 + 传一个探测文件 ----
  const login = await fetch(`${BASE}/api/auth/login`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ username: "admin", password: "admin" }),
  }).then((r) => r.json())
  ok(login.code === 200, "api login")
  const token = login.data.token

  // 后端可能已在跑（重复执行）；先清残留
  await fetch(`${BASE}/api/fs/remove`, {
    method: "POST",
    headers: { "Content-Type": "application/json", Authorization: token },
    body: JSON.stringify({ dir: "/Shared", names: ["ol-ctx-test"] }),
  })
  const mkdir = await fetch(`${BASE}/api/fs/mkdir`, {
    method: "POST",
    headers: { "Content-Type": "application/json", Authorization: token },
    body: JSON.stringify({ path: TEST_DIR }),
  }).then((r) => r.json())
  ok(mkdir.code === 200, `mkdir ${TEST_DIR}`)

  const put = await fetch(`${BASE}/api/fs/put`, {
    method: "PUT",
    headers: {
      Authorization: token,
      "File-Path": encodeURIComponent(`${TEST_DIR}/${TEST_FILE}`),
      "Content-Type": "text/plain",
    },
    body: FILE_CONTENT,
  }).then((r) => r.json())
  ok(put.code === 200, "seed file via /fs/put")

  // ---- 无头 Edge ----
  const browser = await chromium.launch({ channel: "msedge", headless: true })
    const page = await browser.newPage({ locale: "en-US" })
    const consoleErrors = []
    page.on("console", (m) => {
      if (m.type() === "error") consoleErrors.push({ text: m.text(), url: m.location()?.url ?? "" })
    })
    page.on("pageerror", (e) => consoleErrors.push(String(e)))

    // 登录
    await page.goto(`${DEV}/#/@login`, { waitUntil: "networkidle" })
    await page.locator('input[placeholder]:not([readonly])').first().fill("admin")
    await page.locator("input[type=password]").fill("admin")
    await page.getByRole("button").last().click()
    await page.waitForURL((u) => !u.href.includes("@login"), { timeout: 15000 })

    // 进入隔离目录
    await page.goto(`${DEV}/#/?path=${encodeURIComponent(TEST_DIR)}`, { waitUntil: "networkidle" })
    await page.waitForTimeout(1200)
    ok(await page.locator(".ol-file-name", { hasText: TEST_FILE }).first().isVisible(), "seed file listed")

    // ---- 行右键菜单 ----
    await page.locator(".ol-file-name", { hasText: TEST_FILE }).first().click({ button: "right" })
    const ctx = page.locator(".ol-ctx-menu")
    await ctx.waitFor({ state: "visible", timeout: 5000 })
    ok(await ctx.getByText("Open").isVisible(), "row ctx: open")
    ok(await ctx.getByText("Download").isVisible(), "row ctx: download")
    ok(await ctx.getByText("Copy link").isVisible(), "row ctx: copy link")
    ok(await ctx.getByText("Rename").isVisible(), "row ctx: rename")
    ok(await ctx.getByText("Delete").isVisible(), "row ctx: delete")
    await page.keyboard.press("Escape")
    await ctx.waitFor({ state: "hidden", timeout: 3000 })
    ok(true, "Escape closes row ctx menu")

    // ---- 空白右键菜单（点工具栏卡片右下角空白；卡片 padding=0 时卡片底=表格行） ----
    const barBox = await page.locator(".ol-files .el-card").first().boundingBox()
    await page.mouse.click(barBox.x + barBox.width - 30, barBox.y + barBox.height - 8, { button: "right" })
    await ctx.waitFor({ state: "visible", timeout: 5000 })
    ok(await ctx.getByText("New folder").isVisible(), "blank ctx: new folder")
    ok(await ctx.getByText("Choose Files").isVisible(), "blank ctx: upload")
    ok(await ctx.getByText("Select All").isVisible(), "blank ctx: select all")
    await page.keyboard.press("Escape")
    await ctx.waitFor({ state: "hidden", timeout: 3000 })

    // ---- 快捷键：Ctrl+A / Delete / Ctrl+C / Escape ----
    await page.keyboard.press("Control+a")
    await page.waitForTimeout(300)
    ok(await page.locator(".ol-batch-bar").isVisible(), "Ctrl+A shows batch bar")

    await page.keyboard.press("Delete")
    const confirmBox = page.locator(".el-overlay-message-box")
    await confirmBox.waitFor({ state: "visible", timeout: 5000 })
    ok(await confirmBox.getByText("Delete").first().isVisible(), "Delete opens confirm box")
    await confirmBox.getByRole("button", { name: /cancel/i }).click()
    await confirmBox.waitFor({ state: "hidden", timeout: 5000 })
    ok(true, "Delete confirm cancelled")

    await page.keyboard.press("Control+c")
    await page.waitForTimeout(300)
    ok(await page.locator(".ol-files-bar .el-tag", { hasText: "⧉" }).first().isVisible(), "Ctrl+C shows clipboard tag")

    await page.keyboard.press("Escape")
    await page.waitForTimeout(300)
    ok(!(await page.locator(".ol-batch-bar").isVisible().catch(() => false)), "Escape clears selection")

    // Ctrl+V 粘贴到同目录（copy 自身 → 后端应报错或成功，这里只验证流程触发不崩）
    await page.keyboard.press("Control+a")
    await page.keyboard.press("Control+c")
    await page.keyboard.press("Control+v")
    await page.waitForTimeout(1500)
    ok(true, "paste flow triggered without crash")

    // ---- 下载直链（行菜单里 fetch /d 验证内容一致） ----
    const dl = await page.evaluate(async ({ dir, name }) => {
      const resp = await fetch(`/d${dir}/${name}`)
      return { status: resp.status, text: await resp.text() }
    }, { dir: TEST_DIR, name: TEST_FILE })
    ok(dl.status === 200 && dl.text === FILE_CONTENT, `/d download returns original content (status ${dl.status})`)
    if (dl.text !== FILE_CONTENT) console.log("  got:", dl.text.slice(0, 120))

    // ---- 文件页：下载按钮 + raw_url ----
    await page.locator(".ol-file-name", { hasText: TEST_FILE }).first().click()
    await page.waitForTimeout(1200)
    ok(await page.getByRole("button", { name: "Download" }).isVisible(), "file page shows download button")
    ok(await page.locator(".ol-mono").first().isVisible(), "file page shows raw_url")

    // ---- favicon ----
    const fav = await page.evaluate(async () => (await fetch("/favicon.svg")).status)
    ok(fav === 200, `favicon.svg served (${fav})`)

    const realErrors = consoleErrors.filter(
      (e) => !/favicon|ResizeObserver/i.test(e.text) && !/favicon/i.test(e.url),
    )
    ok(realErrors.length === 0, `no console errors (${realErrors.length})`)
    if (realErrors.length) console.log(realErrors.slice(0, 5))

    await browser.close()

    // ---- 清理 ----
    const rm = await fetch(`${BASE}/api/fs/remove`, {
      method: "POST",
      headers: { "Content-Type": "application/json", Authorization: token },
      body: JSON.stringify({ dir: "/Shared", names: ["ol-ctx-test"] }),
    }).then((r) => r.json())
    ok(rm.code === 200, "cleanup test dir")
  } finally {
    backend.kill()
    vite.kill()
  }

  console.log(failures === 0 ? "\nALL PASS" : `\n${failures} FAILURES`)
  process.exit(failures === 0 ? 0 : 1)
}

main().catch((e) => {
  console.error(e)
  process.exit(1)
})
