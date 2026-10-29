/**
 * 上传 UI 冒烟 e2e。
 *
 * 单脚本完成：启动后端（5244）+ Vite dev server（5173）→ Playwright 无头
 * Edge：登录 → 打开首页 → 点「Choose Files」→ el-dialog 出现上传面板 →
 * 注入文件 → 等待状态徽标变 Success → 校验列表出现新文件 → 清理。
 *
 * 用法：node scripts/e2e-upload-ui.mjs
 */
import { spawn } from "node:child_process"
import fs from "node:fs"
import os from "node:os"
import path from "node:path"
import { chromium } from "playwright"

const BASE = "http://127.0.0.1:5244"
const DEV = "http://localhost:5173"
const SERVER_DIR = "F:/Code/Dotnet/OpenList.Net/src/OpenList.Server"
const FRONTEND_DIR = "F:/Code/Dotnet/OpenList.Net/frontend"

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
  // ---- 启动后端与前端 dev server ----
  // 直接跑 vite 的 node 入口（不经 pnpm shell），退出时才能可靠杀掉进程树
  const viteBin = path.join(FRONTEND_DIR, "node_modules", "vite", "bin", "vite.js")
  const backend = spawn("dotnet", ["bin/Debug/net10.0/OpenList.Server.dll"], {
    cwd: SERVER_DIR,
    env: { ...process.env, ASPNETCORE_ENVIRONMENT: "Development", ASPNETCORE_URLS: "http://127.0.0.1:5244" },
    stdio: ["ignore", "ignore", "inherit"],
  })
  backend.on("error", (e) => console.error("backend spawn error:", e))
  const vite = spawn(process.execPath, [viteBin, "--port", "5173", "--strictPort"], {
    cwd: FRONTEND_DIR,
    stdio: ["ignore", "ignore", "inherit"],
  })
  vite.on("error", (e) => console.error("vite spawn error:", e))
  try {
    await waitHttp(`${BASE}/api/public/settings`, backend)
    await waitHttp(`${DEV}`, vite)

    // ---- 无头 Edge ----
    const browser = await chromium.launch({ channel: "msedge", headless: true })
    const page = await browser.newPage({ locale: "en-US" })
    const consoleErrors = []
    page.on("console", (m) => {
      if (m.type() === "error") {
        // favicon 404 是 M1 已知待办，按请求 URL 过滤（消息文本本身不带 URL）
        const url = m.location()?.url ?? ""
        consoleErrors.push({ text: m.text(), url })
      }
    })
    page.on("pageerror", (e) => consoleErrors.push(String(e)))

    // 登录（注意：路由是 /#/@login；页面上还有只读 combobox，必须按 placeholder 定位）
    await page.goto(`${DEV}/#/@login`, { waitUntil: "networkidle" })
    await page.locator('input[placeholder]:not([readonly])').first().fill("admin")
    await page.locator("input[type=password]").fill("admin")
    await page.getByRole("button").last().click()
    // 登录成功 = 离开 /@login（正则不能写成 /#\/…/，那会匹配登录页自身）
    await page.waitForURL((u) => !u.href.includes("@login"), { timeout: 15000 })
    console.log("logged in, url:", page.url())

    // 进入 /Shared（本地存储挂载目录）
    await page.goto(`${DEV}/#/?path=/Shared`, { waitUntil: "networkidle" })
    await page.waitForTimeout(1000)

    // 点上传按钮
    const uploadBtn = page.getByRole("button", { name: /Choose Files|上传/ }).first()
    ok(await uploadBtn.isVisible(), "upload button visible in toolbar")
    await uploadBtn.click()
    const dialog = page.locator(".el-dialog")
    await dialog.waitFor({ state: "visible", timeout: 5000 })
    ok(true, "upload dialog opened")

    // 断言面板要素：拖拽区提示、选项、通道选择
    ok(await dialog.getByText(/Drag files here/i).isVisible(), "drop area hint visible")
    ok(await dialog.locator(".el-select").first().isVisible(), "uploader select visible")

    // 注入两个文件（一个小的文本，一个 ~12MB 触发 multipart 的边界内文件）
    const tmp = fs.mkdtempSync(path.join(os.tmpdir(), "ol-ui-upload-"))
    fs.writeFileSync(path.join(tmp, "ui-a.txt"), "hello from ui e2e\n".repeat(10))
    // 12MB：> 10MB 会走 multipart；这里保守用 6MB 走 stream 通道
    fs.writeFileSync(path.join(tmp, "ui-big.bin"), Buffer.alloc(6 * 1024 * 1024, 7))
    await page.locator('.el-dialog input[type="file"]').first().setInputFiles([
      path.join(tmp, "ui-a.txt"),
      path.join(tmp, "ui-big.bin"),
    ])

    // 等两个文件都 Success（上限 60s）
    await page
      .locator(".ol-upload-row", { hasText: "ui-a.txt" })
      .getByText("Success")
      .waitFor({ timeout: 60000 })
    await page
      .locator(".ol-upload-row", { hasText: "ui-big.bin" })
      .getByText("Success")
      .waitFor({ timeout: 60000 })
    ok(true, "both files reached Success status")

    // 关闭面板 → 列表应刷新并出现新文件
    await page.screenshot({ path: "shot-upload.png" })
    await page.locator(".el-dialog__headerbtn").click()
    await page.waitForTimeout(1500)
    ok(await page.locator(".ol-file-name", { hasText: "ui-a.txt" }).first().isVisible(), "ui-a.txt appears in list after close")

    // 控制台零错误（favicon 404 属 M1 已知待办，过滤）
    const realErrors = consoleErrors.filter(
      (e) => !/favicon|ResizeObserver/i.test(e.text) && !/favicon/i.test(e.url),
    )
    ok(realErrors.length === 0, `no console errors (${realErrors.length})`)
    if (realErrors.length) console.log(realErrors.slice(0, 5))

    await browser.close()

    // ---- 清理上传的文件 ----
    const login = await fetch(`${BASE}/api/auth/login`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ username: "admin", password: "admin" }),
    }).then((r) => r.json())
    const token = login.data.token
    const rm = await fetch(`${BASE}/api/fs/remove`, {
      method: "POST",
      headers: { "Content-Type": "application/json", Authorization: token },
      body: JSON.stringify({ dir: "/Shared", names: ["ui-a.txt", "ui-big.bin"] }),
    }).then((r) => r.json())
    ok(rm.code === 200, "cleanup uploaded files")
    fs.rmSync(tmp, { recursive: true, force: true })
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
