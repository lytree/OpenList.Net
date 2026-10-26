import { chromium } from "playwright"
import { execFileSync } from "node:child_process"
import { mkdirSync, writeFileSync } from "node:fs"
import { join } from "node:path"

/**
 * M3 收尾两个预览器（video360 / flash）的真机验收。
 *
 * 用法：
 *   BASE=http://127.0.0.1:5012 ADMIN_PASS=e2e12345 node scripts/verify-preview-extra.mjs
 *
 * 前置：隔离后端（临时库 + AdminPassword + DistDir=frontend/dist）。
 * 装置：脚本自建 `.e2e/preview-extra`：
 * - `pano_360.mp4`：ffmpeg 生成的 8 秒 2:1 视频（等距柱状投影的典型宽高比；
 *   内容是不是真全景不重要，球面贴图只关心「有没有画面」）；
 * - `legacy.swf`：手工拼的**最小合法 SWF**（FWS 头 + RECT + 12fps + 单帧 ShowFrame），
 *   作用是把 Ruffle 的 wasm 真正跑起来 —— 无效字节会让 Ruffle 直接报解析错误，
 *   那就只验证了「错误 UI 能显示」，验证不到播放链路。
 *
 * 无头浏览器的 WebGL 需要显式打开 SwiftShader，否则 three 建不出上下文，
 * 页面会走降级分支 —— 那样测到的是降级而不是全景渲染。
 */

const BASE = process.env.BASE || "http://127.0.0.1:5012"
const ADMIN_PASS = process.env.ADMIN_PASS || "e2e12345"
const FIXTURE_ROOT = process.env.FIXTURE_ROOT || join(process.cwd(), "..", ".e2e", "preview-extra")
const TARGET = "/e2e"

const log = (...args) => console.log("[preview-extra]", ...args)
const results = []
function check(name, ok, extra = "") {
  results.push({ name, ok, extra })
  log(ok ? "PASS" : "FAIL", name, extra)
}

// ---------- 0. 装置 ----------
mkdirSync(FIXTURE_ROOT, { recursive: true })

const pano = join(FIXTURE_ROOT, "pano_360.mp4")
try {
  execFileSync(
    "ffmpeg",
    [
      "-y",
      "-f", "lavfi",
      "-i", "testsrc=size=1280x640:rate=24:duration=8",
      "-pix_fmt", "yuv420p",
      pano,
    ],
    { stdio: "ignore" },
  )
  log("ffmpeg: pano_360.mp4 ok")
} catch (e) {
  log("ffmpeg 失败，video360 部分会跳过：", String(e.message ?? e))
}

/**
 * 最小合法 SWF（未压缩 FWS）。
 * 结构：header(8) + RECT + frameRate(2) + frameCount(2) + SetBackgroundColor + ShowFrame + End
 */
function buildMinimalSwf() {
  const bits = []
  const push = (value, count) => {
    for (let i = count - 1; i >= 0; i--) bits.push((value >> i) & 1)
  }
  // RECT：nbits=15，四个 15 位 twips (0,0)-(11000,8000) 即 550×400 px
  push(15, 5)
  push(0, 15)
  push(11000, 15)
  push(0, 15)
  push(8000, 15)
  while (bits.length % 8) bits.push(0)
  const rect = []
  for (let i = 0; i < bits.length; i += 8) {
    rect.push(parseInt(bits.slice(i, i + 8).join(""), 2))
  }

  const body = Buffer.concat([
    Buffer.from(rect),
    // frameRate 12fps 存成 8.8 定点：低字节 0x00 高字节 0x0C
    Buffer.from([0x00, 0x0c]),
    // frameCount = 1
    Buffer.from([0x01, 0x00]),
    // SetBackgroundColor (code 9, len 3) → (9<<6)|3 = 0x0243，小端 43 02 + RGB
    Buffer.from([0x43, 0x02, 0xff, 0xff, 0xff]),
    // ShowFrame (code 1, len 0) → (1<<6)|0 = 0x40
    Buffer.from([0x40, 0x00]),
    // End (code 0, len 0)
    Buffer.from([0x00, 0x00]),
  ])

  const header = Buffer.alloc(8)
  header.write("FWS", 0, "ascii")
  header.writeUInt8(9, 3) // version 9（Ruffle 支持到 32，9 足够）
  header.writeUInt32LE(8 + body.length, 4)
  return Buffer.concat([header, body])
}

const swfPath = join(FIXTURE_ROOT, "legacy.swf")
writeFileSync(swfPath, buildMinimalSwf())
log("swf:", swfPath)

// ---------- 1. 浏览器与登录 ----------
const browser = await chromium.launch({
  channel: "msedge",
  headless: true,
  // 无头 Chromium 默认没有 GPU，必须显式启用 SwiftShader 才能拿到 WebGL 上下文
  args: ["--use-gl=angle", "--use-angle=swiftshader", "--enable-unsafe-swiftshader"],
})
const page = await browser.newPage({ viewport: { width: 1440, height: 900 } })
const errors = []
page.on("console", (msg) => {
  // WebGL/SwiftShader 的驱动级警告不算页面错误，只收 error 级别
  if (msg.type() === "error") errors.push(msg.text())
})
page.on("pageerror", (e) => errors.push(String(e)))

const token = await page.request
  .post(`${BASE}/api/auth/login`, { data: { username: "admin", password: ADMIN_PASS } })
  .then((r) => r.json())
  .then((b) => b.data.token)
page.addInitScript((value) => localStorage.setItem("token", value), token)

const api = async (method, path, data) =>
  page.request[method](`${BASE}/api${path}`, { data, headers: { Authorization: token } }).then((r) => r.json())

// 挂载 /e2e（幂等；addition 是字符串化 JSON）
const mount = await api("post", "/admin/storage/create", {
  mount_path: TARGET,
  driver: "Local",
  addition: JSON.stringify({ root_folder_path: FIXTURE_ROOT }),
})
log("mount:", mount.code, mount.code === 200 ? "" : mount.message)

const openFile = async (name, wait = 3000) => {
  await page.goto(`${BASE}/#/?path=${encodeURIComponent(`${TARGET}/${name}`)}`, {
    waitUntil: "networkidle",
  })
  await page.waitForTimeout(wait)
}

/** 当前预览器显示名（下拉只在有多个候选时出现）。 */
async function selectedPreview() {
  const select = page.locator(".ol-preview-actions .el-select").first()
  if ((await select.count()) === 0) return ""
  return (await select.textContent())?.trim() ?? ""
}

/** 从预览器下拉里切换到指定显示名。 */
async function switchPreview(label) {
  await page.locator(".ol-preview-actions .el-select").first().click()
  await page.locator(".el-select-dropdown__item", { hasText: label }).first().click()
  await page.waitForTimeout(3500)
}

// ---------- 2. video360 ----------
if (process.env.SKIP_360 !== "1") {
  await openFile("pano_360.mp4")
  check("视频默认选中普通播放器", /Video Player/i.test(await selectedPreview()), await selectedPreview())

  // 全景标记命中后，下拉里应多出 Video360
  await page.locator(".ol-preview-actions .el-select").first().click()
  const options = await page.locator(".el-select-dropdown__item").allInnerTexts()
  check("下拉里出现 Video360 Player", options.some((o) => /Video360/i.test(o)), options.join("|"))
  await page.locator(".el-select-dropdown__item", { hasText: "Video360" }).first().click()
  await page.waitForTimeout(4000)

  const canvas = page.locator(".ol-video360 canvas").first()
  const hasCanvas = (await canvas.count()) > 0
  check("360 视图渲染出 canvas", hasCanvas)
  if (hasCanvas) {
    const box = await canvas.boundingBox()
    check("canvas 有实际尺寸", (box?.width ?? 0) > 400 && (box?.height ?? 0) > 200, JSON.stringify(box))
    // 转视角：拖动后画面应仍在（three 每帧渲染，这里只验证不崩）
    await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2)
    await page.mouse.down()
    await page.mouse.move(box.x + box.width / 2 + 120, box.y + box.height / 2 + 40, { steps: 8 })
    await page.mouse.up()
    await page.waitForTimeout(600)
    check("拖动转视角后 canvas 仍在", (await page.locator(".ol-video360 canvas").count()) > 0)
  }

  const fallback = await page.locator(".ol-video360-fallback").count()
  check("未触发 WebGL 降级", fallback === 0, `fallback=${fallback}`)
  const hint = await page.locator(".ol-preview-hint", { hasText: "Drag to look around" }).count()
  check("显示拖动提示", hint > 0)

  /*
   * 「canvas 有尺寸但全黑」是这个预览器最典型的翻车方式（球体侧别写错就会这样，
   * WebGL 又读不回像素），所以直接查视频源状态：有尺寸 + 时间在走，
   * 说明纹理那侧确实拿到了帧。
   */
  const playback = await page.evaluate(() => {
    const v = document.querySelector(".ol-video360-video")
    if (!v) return null
    return { time: v.currentTime, width: v.videoWidth, height: v.videoHeight, paused: v.paused }
  })
  check(
    "视频源在解码且时间前进",
    Boolean(playback && playback.width > 0 && playback.time > 0),
    JSON.stringify(playback),
  )

  // 切回普通播放器：three 的渲染循环与 canvas 必须被清理
  await switchPreview("Video Player")
  const leftover = await page.locator(".ol-video360 canvas").count()
  check("切走后 canvas 已销毁", leftover === 0, `leftover=${leftover}`)
}

// ---------- 3. flash ----------
await openFile("legacy.swf", 4000)
check("swf 自动选中 Flash Player", /Flash Player/i.test(await selectedPreview()), await selectedPreview())

// Ruffle 的 DOM 是**两层 shadow root**：<ruffle-player> → shadow → <ruffle-stage> → shadow → <canvas>，
// 所以必须递归穿透 shadow root 找 canvas（querySelector 穿不过去）。
const playerInfo = await page.evaluate(() => {
  const host = document.querySelector(".ol-flash-host")
  const child = host?.firstElementChild
  if (!child) return { tag: "", canvas: false, w: 0, h: 0, path: "", text: "" }

  const path = []
  let canvas = null
  let text = ""

  const walk = (node, depth) => {
    if (!node || depth > 6 || canvas) return
    const tag = node.tagName ? node.tagName.toLowerCase() : ""
    if (tag) path.push(tag)
    if (tag === "canvas") {
      canvas = node
      return
    }
    if (node.shadowRoot) {
      for (const inner of node.shadowRoot.children) {
        walk(inner, depth + 1)
        if (canvas) return
      }
      text += node.shadowRoot.textContent ?? ""
    }
    if (node.children) {
      for (const inner of node.children) {
        walk(inner, depth + 1)
        if (canvas) return
      }
    }
  }
  walk(child, 0)

  return {
    tag: child.tagName.toLowerCase(),
    canvas: Boolean(canvas),
    w: canvas ? canvas.width : 0,
    h: canvas ? canvas.height : 0,
    path: path.join(">"),
    text: text.slice(0, 120),
  }
})
check("Ruffle 播放器元素已挂载", /ruffle/.test(playerInfo.tag), playerInfo.tag)
check(
  "Ruffle 渲染出 canvas（wasm 已启动）",
  playerInfo.canvas && playerInfo.w > 0,
  `${playerInfo.path} ${playerInfo.w}x${playerInfo.h}`,
)

const flashError = await page.locator(".ol-flash-preview .el-alert").count()
check("未出现 Ruffle 加载失败提示", flashError === 0, `alerts=${flashError}`)

// 区块内没有可见错误文案即可（Ruffle 的内容在 shadow root 里，innerText 取不到）
const flashText = await page.locator(".ol-flash-preview").innerText()
check(
  "Flash 区块未显示失败文案",
  !/Cannot start the Flash player|Failed to load/i.test(flashText),
  flashText.slice(0, 60),
)

// ---------- 4. 收尾 ----------
check("零控制台错误", errors.length === 0, errors.slice(0, 4).join(" | "))

await browser.close()

const failed = results.filter((r) => !r.ok)
log(`\n${results.length - failed.length}/${results.length} passed`)
if (failed.length > 0) {
  for (const item of failed) log("FAILED:", item.name, item.extra)
  process.exit(1)
}
