/**
 * 手机端文件列表（卡片行）验收。
 *
 * 守的是「列表模式在 <= 767px 换成卡片行」这条结构切换，以及它与表格一致的交互语义。
 * 纯 CSS 撑不开四列表格，所以这一步只能靠真实视口验证：元素宽度、横向溢出、
 * 断点两侧的组件互斥，jsdom 里量不出来。
 *
 * 前置：
 *   1. 一个**已构建**的前端（`DistDir` 指向 `frontend/dist`，脚本会直接打开它）；
 *   2. 该后端已有一个可读挂载（`MOUNT` 指向挂载路径，默认 `/e2e-phone`），
 *      不存在时脚本会用 `MOUNT_ROOT` 自己建一个（同 `e2e-upload.mjs` 的约定）。
 *
 * 用法：
 *   BASE=http://127.0.0.1:5177 ADMIN_PASSWORD=admin123 node scripts/verify-phone-list.mjs
 *
 * 浏览器优先用本机 Edge（`channel: "msedge"`），没有 Playwright 自带 Chromium 也能跑。
 */
import { mkdirSync } from "node:fs"
import { chromium } from "playwright"

const BASE = process.env.BASE ?? "http://127.0.0.1:5177"
const ADMIN = process.env.ADMIN ?? "admin"
const PASSWORD = process.env.ADMIN_PASSWORD ?? "admin123"
const MOUNT = process.env.MOUNT ?? "/e2e-phone"
const MOUNT_ROOT = process.env.MOUNT_ROOT ?? "F:/Code/github/OpenList.Net/.e2e/phone/storage"
const SHOT_DIR = process.env.SHOT_DIR ?? "F:/Code/github/OpenList.Net/.e2e/phone/shots"

/** 手机断点（与 src/composables/useIsPhone.ts / styles/responsive.css 一致）。 */
const PHONE = { width: 390, height: 844 }
const DESKTOP = { width: 1024, height: 800 }

const results = []
function check(name, ok, detail = "") {
  results.push({ name, ok, detail })
  console.log(`${ok ? "  ok  " : "  FAIL"} ${name}${detail ? ` — ${detail}` : ""}`)
}

async function api(path, { method = "GET", body, token } = {}) {
  const res = await fetch(`${BASE}${path}`, {
    method,
    headers: {
      "Content-Type": "application/json;charset=utf-8",
      ...(token ? { Authorization: token } : {}),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  })
  return res.json()
}

async function ensureMount(token) {
  const list = await api("/api/admin/storage/list", { token })
  // 分页信封：{ content, total }
  const rows = Array.isArray(list?.data) ? list.data : (list?.data?.content ?? [])
  if (rows.some((s) => s.mount_path === MOUNT)) return
  const created = await api("/api/admin/storage/create", {
    method: "POST",
    token,
    body: {
      id: 0,
      mount_path: MOUNT,
      driver: "Local",
      addition: JSON.stringify({ root_folder_path: MOUNT_ROOT }),
      order: 0,
      disabled: false,
      remark: "e2e phone list",
    },
  })
  if (created?.code !== 200) throw new Error(`创建挂载失败: ${JSON.stringify(created)}`)
}

/** 页面里量一次横向溢出：文档宽度不该超过视口（表格内部滚动不算）。 */
async function overflow(page) {
  return page.evaluate(() => {
    const el = document.documentElement
    return { scrollWidth: el.scrollWidth, clientWidth: el.clientWidth }
  })
}

/** 把 "rgb(r, g, b)" 折算成 0–255 的感知亮度，用来比「深色主题下文字是否翻成浅色」。 */
function luminance(color) {
  const nums = color.match(/[\d.]+/g)?.slice(0, 3).map(Number) ?? [0, 0, 0]
  return 0.299 * nums[0] + 0.587 * nums[1] + 0.114 * nums[2]
}

const login = await api("/api/auth/login", { method: "POST", body: { username: ADMIN, password: PASSWORD } })
if (login?.code !== 200) throw new Error(`登录失败: ${JSON.stringify(login)}`)
// 登录信封的 data 是 { token }，而请求头直接放裸 token（无 "Bearer " 前缀）
const token = typeof login.data === "string" ? login.data : login.data?.token
if (!token) throw new Error(`登录返回里没有 token: ${JSON.stringify(login.data)}`)
await ensureMount(token)

mkdirSync(SHOT_DIR, { recursive: true })

const browser = await chromium.launch({ channel: process.env.BROWSER_CHANNEL ?? "msedge" })
try {
  const context = await browser.newContext({
    viewport: PHONE,
    locale: "en-US",
    deviceScaleFactor: 2,
    isMobile: true,
    hasTouch: true,
  })
  const page = await context.newPage()
  const consoleErrors = []
  page.on("console", (msg) => {
    if (msg.type() === "error") consoleErrors.push(msg.text())
  })

  await page.addInitScript(
    ({ token }) => {
      localStorage.setItem("token", token)
      // 视图模式是字符串默认值 → vueuse 用 string 序列化，写原样值（**不要** JSON.stringify，
      // 否则读回来是带引号的 `"list"`，只会被判成「非 list」而落到网格）
      localStorage.setItem("ol-view-mode", "list")
      // 主题不在这里写：initScript 每次导航都跑，会把下面「切深色」的改动覆盖回浅色。
    },
    { token },
  )

  // 文件浏览是独立门面 /files，当前目录用 ?path= 指定（深链）
  await page.goto(`${BASE}/#/files?path=${encodeURIComponent(MOUNT)}`, { waitUntil: "networkidle" })
  // 固定为浅色再量（主题只在初始化时读一次，改完要 reload 才生效）
  await page.evaluate(() => localStorage.setItem("theme-mode", "light"))
  await page.reload({ waitUntil: "networkidle" })
  await page.waitForSelector(".ol-row", { timeout: 15000 })

  // ---- 结构：手机断点内走卡片行，表格必须完全不在 DOM 里 ----
  const rowCount = await page.locator(".ol-row").count()
  check("手机端列表渲染为卡片行", rowCount >= 3, `${rowCount} 行`)
  check("手机端不渲染表格", (await page.locator(".vxe-table").count()) === 0)

  // ---- 布局：四列压成两行后不该再横向溢出 ----
  const phoneOverflow = await overflow(page)
  check(
    "390px 无横向溢出",
    phoneOverflow.scrollWidth <= phoneOverflow.clientWidth + 1,
    `scrollWidth=${phoneOverflow.scrollWidth} clientWidth=${phoneOverflow.clientWidth}`,
  )

  // ---- 副信息：目录/文件都在第二行给出「大小/类型 · 时间」 ----
  const firstMeta = (await page.locator(".ol-row-meta").first().innerText()).trim()
  check("副信息含分隔符与时间", firstMeta.includes("·") && /\d{4}-\d{2}-\d{2}/.test(firstMeta), firstMeta)

  // ---- 交互：勾选框只选中、不打开 ----
  // 点外层：Element Plus 的 `.el-checkbox__original` 是隐藏的原生 input，
  // Playwright 会判定为不可见（真实用户点的也是这层视觉勾选框）。
  await page.locator(".ol-row-check").first().click()
  await page.waitForTimeout(300)
  const stillOnList = await page.locator(".ol-row").count()
  check("勾选不触发打开", stillOnList === rowCount, `行数仍为 ${stillOnList}`)
  check("勾选后行进入选中态", (await page.locator(".ol-row.is-selected").count()) === 1)

  // ---- 交互：「更多」菜单与桌面同一份动作（文件可写时 7 项，目录 5 项） ----
  // 每行的 dropdown 都会预渲染在 popper 里，只数「可见」的那一份。
  await page.locator('.ol-row:has-text("readme.txt") .ol-row-more').click()
  await page.waitForTimeout(400)
  const menuItems = await page.locator(".el-dropdown-menu__item:visible").count()
  check("文件行更多菜单 7 项", menuItems === 7, `${menuItems} 项`)
  const hasDanger = await page.locator(".el-dropdown-menu__item.is-danger:visible").count()
  check("删除项标红", hasDanger === 1)
  await page.keyboard.press("Escape")
  await page.waitForTimeout(200)

  await page.screenshot({ path: `${SHOT_DIR}/phone-list-light.png`, fullPage: false })

  // ---- 深色主题下可读性 ----
  // 行本身不铺底色（它坐在 Card 上），所以要看「文字颜色有没有跟着主题翻过来」，
  // 而不是看行的 background。
  const readColor = () =>
    page.evaluate(() => getComputedStyle(document.querySelector(".ol-row-name")).color)
  const lightColor = await readColor()
  await page.evaluate(() => localStorage.setItem("theme-mode", "dark"))
  await page.reload({ waitUntil: "networkidle" })
  await page.waitForSelector(".ol-row", { timeout: 15000 })
  const darkColor = await readColor()
  const isDarkClass = await page.evaluate(() => document.documentElement.classList.contains("dark"))
  check(
    "深色主题下名称文字转为浅色",
    isDarkClass && darkColor !== lightColor && luminance(darkColor) > luminance(lightColor),
    `light=${lightColor} dark=${darkColor}`,
  )
  await page.screenshot({ path: `${SHOT_DIR}/phone-list-dark.png`, fullPage: false })

  // ---- 断点另一侧：>= 768px 必须回到表格，卡片行消失 ----
  await page.setViewportSize(DESKTOP)
  await page.waitForSelector(".vxe-table", { timeout: 15000 })
  const desktopRows = await page.locator(".ol-row").count()
  const desktopOverflow = await overflow(page)
  check("1024px 回到表格", (await page.locator(".vxe-table").count()) === 1 && desktopRows === 0)
  check(
    "1024px 无横向溢出",
    desktopOverflow.scrollWidth <= desktopOverflow.clientWidth + 1,
    `scrollWidth=${desktopOverflow.scrollWidth} clientWidth=${desktopOverflow.clientWidth}`,
  )
  await page.screenshot({ path: `${SHOT_DIR}/desktop-list.png`, fullPage: false })

  // ---- 缩回手机尺寸：卡片行要回来（监听器没被销毁） ----
  await page.setViewportSize(PHONE)
  await page.waitForSelector(".ol-row", { timeout: 15000 })
  check("缩回 390px 卡片行恢复", (await page.locator(".ol-row").count()) >= 3)

  // ---- 控制台无报错 ----
  check("无控制台错误", consoleErrors.length === 0, consoleErrors.join(" | ").slice(0, 200))
} finally {
  await browser.close()
}

const failed = results.filter((r) => !r.ok)
console.log(`\n${results.length - failed.length}/${results.length} 项通过`)
if (failed.length > 0) process.exitCode = 1
