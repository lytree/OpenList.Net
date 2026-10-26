import { chromium } from "playwright"

/**
 * A / E / G 的自动化回归：认证与守卫、管理台模块、通用外观。
 *
 * 这三块此前没有脚本 —— B/C/D/F 都有专门的真机验收，而「登录会不会放错人进去」
 * 「管理台 8 个页签是不是都能打开」只能靠人手点，M5 的 A–H 就只能算口头承诺。
 *
 * 用法：`BASE=http://127.0.0.1:5055 ADMIN_PASS=e2e12345 node scripts/verify-common.mjs`
 * 前置：隔离后端托管 dist（临时库），无需任何挂载/装置。
 */

const BASE = process.env.BASE || "http://127.0.0.1:5055"
const PASS = process.env.ADMIN_PASS || "e2e12345"
const SHOT = process.env.SHOT_DIR || "F:/Code/Dotnet/OpenList.Net/.e2e/common"

const log = (...a) => console.log("[common]", ...a)
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

const goto = async (hash) => {
  await page.goto(`${BASE}/${hash}`, { waitUntil: "networkidle" })
  await page.waitForTimeout(800)
}

// ---------- A. 守卫：游客可浏览，但进不了管理台 ----------
// 注意：首页**没有** requiresAuth —— 允许游客浏览是本产品的既定语义（与 Go 版一致），
// 所以这里不假设「未登录会被踢到登录页」，只验证「游客拿不到管理台」。
await goto("#/@manage/storages")
check("游客访问管理台被弹回首页", !/#\/@manage/.test(page.url()), page.url())
check(
  "游客身份下不显示用户名",
  (await page.locator(".ol-user").count()) === 0,
  `ol-user=${await page.locator(".ol-user").count()}`,
)

// ---------- A. 错误口令不放行 ----------
await goto("#/@login")
check("直接访问登录页可渲染", (await page.locator(".ol-login-page").count()) === 1, page.url())
await page.locator(".ol-login-page input").first().fill("admin")
await page.locator(".ol-login-page input").nth(1).fill("wrong-password")
await page.locator(".ol-login-card button").first().click()
await page.waitForTimeout(1500)
const stillLogin = await page.locator(".ol-login-page").count()
const badToken = await page.evaluate(() => localStorage.getItem("token"))
check("错误口令不放行", stillLogin === 1 && !badToken, `login=${stillLogin} token=${badToken}`)
const errMsg = (await page.locator(".el-alert, .el-message").first().innerText().catch(() => "")) || ""
check("错误口令有可见提示", errMsg.trim().length > 0, errMsg.replace(/\s+/g, " ").slice(0, 60))

// 正确口令
await page.locator(".ol-login-page input").nth(1).fill(PASS)
await page.locator(".ol-login-card button").first().click()
await page.waitForTimeout(2500)
const token = await page.evaluate(() => localStorage.getItem("token"))
check("登录成功并落 token", Boolean(token) && !/#\/@login/.test(page.url()), page.url())

// 刷新后仍登录（token 恢复 + 守卫放行）
await page.reload({ waitUntil: "networkidle" })
await page.waitForTimeout(1200)
check("刷新后保持登录", (await page.locator(".ol-login-page").count()) === 0, page.url())

// ---------- E. 管理台 10 个已就绪模块都能打开 ----------
// （backup-restore / about 仍是 (soon) 禁用态，不在断言范围内；
//   plugins 已于 §35 从导航移除 —— .NET 侧没有 .so 插件宿主，留着只会误导）
const ADMIN_PAGES = [
  ["#/@manage/dashboard", "Dashboard"],
  ["#/@manage/storages", "Storages"],
  ["#/@manage/users", "Users"],
  ["#/@manage/metas", "Metas"],
  ["#/@manage/shares", "Shares"],
  ["#/@manage/indexes", "Indexes"],
  ["#/@manage/tasks", "Tasks"],
  ["#/@manage/media", "Media"],
  ["#/@manage/messenger", "Messages"],
  ["#/@manage/settings", "Settings"],
]
for (const [hash, label] of ADMIN_PAGES) {
  const before = errors.length
  await goto(hash)
  const bodyText = (await page.locator("main, .ol-manage-page").first().innerText().catch(() => "")) || ""
  const hasSurface =
    (await page.locator(".el-table, .el-form, .el-card, .el-tabs").count()) > 0 || bodyText.trim().length > 0
  check(
    `管理台 /${label} 渲染`,
    hasSurface && errors.length === before,
    `surface=${hasSurface} newErrors=${errors.length - before}`,
  )
}

// ---------- G. 外观：暗色主题 + 语言控件 ----------
await goto("#/")
const themeBtn = page.locator(".el-header button.el-button.is-circle").last()
const darkBefore = await page.evaluate(() => document.documentElement.classList.contains("dark"))
await themeBtn.click()
await page.waitForTimeout(600)
const darkAfter = await page.evaluate(() => document.documentElement.classList.contains("dark"))
check("主题按钮切换 html.dark", darkBefore !== darkAfter, `${darkBefore} -> ${darkAfter}`)

const persisted = await page.evaluate(() => localStorage.getItem("theme-mode"))
check("主题选择持久化", Boolean(persisted), `theme-mode=${persisted}`)

// 刷新后仍是暗色（说明是「记住偏好」而不是只改了当前 DOM）
await page.reload({ waitUntil: "networkidle" })
await page.waitForTimeout(1200)
const darkKept = await page.evaluate(() => document.documentElement.classList.contains("dark"))
check("刷新后主题保持", darkKept === darkAfter, `dark=${darkKept}`)

// 切回浅色，避免影响后续截图/轮次
await page.locator(".el-header button.el-button.is-circle").last().click()
await page.waitForTimeout(400)

const langTrigger = page.locator(".el-header .el-dropdown").first()
check("语言控件存在", (await langTrigger.count()) > 0)
// 字典目前只有 en（见 i18n/index.ts 的 availableLanguages），因此只验证控件可用，
// 不假装验证「多语言切换」—— 那需要先有第二份字典。
const langText = (await langTrigger.innerText().catch(() => "")) || ""
check("语言控件显示当前语言", langText.trim().length > 0, langText.replace(/\s+/g, " "))

// ---------- A. 退出登录 ----------
const userMenu = page.locator(".ol-user").first()
if ((await userMenu.count()) > 0) {
  await userMenu.click()
  await page.waitForTimeout(500)
  const logout = page.locator(".el-dropdown-menu__item", { hasText: /Logout|Sign out/i }).first()
  if ((await logout.count()) > 0) {
    await logout.click()
    await page.waitForTimeout(1500)
  }
}
const afterLogout = await page.evaluate(() => localStorage.getItem("token"))
// 退出后停在首页而不是登录页 —— 首页对游客开放（见上），所以「回到游客态」是正确行为；
// 真正要断言的是凭据被清掉、且 UI 不再呈现已登录身份。
check(
  "退出登录后清 token 并回到游客态",
  !afterLogout && (await page.locator(".ol-user").count()) === 0,
  `${page.url()} token=${afterLogout} user=${await page.locator(".ol-user").count()}`,
)

check("全程无控制台错误", errors.length === 0, errors.slice(0, 3).join(" | "))

await page.screenshot({ path: `${SHOT}.png` }).catch(() => {})
const passed = results.filter((r) => r.ok).length
log(`SUMMARY ${passed}/${results.length}`)
await browser.close()
process.exit(passed === results.length ? 0 : 1)
