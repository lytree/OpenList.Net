import { chromium } from "playwright"

/**
 * A / E / F / G 的自动化回归：认证与守卫、管理台模块、关于页、通用外观。
 *
 * 这几块此前没有脚本 —— 其余轮次各有专门的真机验收，而「登录会不会放错人进去」
 * 「管理台每个页签是不是都能打开」「关于页报的版本与接口是否一致」只能靠人手点，
 * M5 的 A–H 就只能算口头承诺。
 *
 * 关于页没有单独一轮：它只有 10 来个字段、没有写操作，为一个纯读页面单独起一遍
 * 后端 + 数据库不划算，所以挂在「管理台页签全开」这一轮里（见 F 段）。
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
const page = await browser.newPage({ viewport: { width: 1440, height: 900 }, locale: "en-US" })
const errors = []
page.on("console", (msg) => {
  if (msg.type() === "error") errors.push(msg.text())
})
page.on("pageerror", (e) => errors.push(String(e)))

const goto = async (hash) => {
  await page.goto(`${BASE}/${hash}`, { waitUntil: "networkidle" })
  await page.waitForTimeout(800)
}

// ---------- A. 守卫：游客可浏览文件，但进不了媒体库 / 管理台 ----------
// 允许游客浏览文件是本产品的既定语义（与 Go 版一致），所以不假设「未登录一律踢去登录页」。
// 但媒体域（/api/media/**）全部需要登录，`/` 对游客必须落到文件浏览 —— 否则页面挂载时
// 连发 401、被全局 401 处理弹回登录页，而登录页的「以访客身份浏览」又回到 `/`，来回死循环。
await goto("#/")
check(
  "游客打开首页落在文件浏览",
  /#\/files/.test(page.url()) && (await page.locator(".ol-auth").count()) === 0,
  page.url(),
)

await goto("#/media")
check("游客进媒体库被要求登录", /#\/@login/.test(page.url()), page.url())

await goto("#/@manage/storages")
check("游客访问管理台被弹回首页", !/#\/@manage/.test(page.url()), page.url())
check(
  "游客身份下不显示用户名",
  (await page.locator(".ol-user").count()) === 0,
  `ol-user=${await page.locator(".ol-user").count()}`,
)

// ---------- A. 错误口令不放行 ----------
await goto("#/@login")
check("直接访问登录页可渲染", (await page.locator(".ol-auth").count()) === 1, page.url())
await page.locator(".ol-auth input").first().fill("admin")
await page.locator(".ol-auth input").nth(1).fill("wrong-password")
await page.locator(".ol-auth-card button.el-button--primary").first().click()
await page.waitForTimeout(1500)
const stillLogin = await page.locator(".ol-auth").count()
const badToken = await page.evaluate(() => localStorage.getItem("token"))
check("错误口令不放行", stillLogin === 1 && !badToken, `login=${stillLogin} token=${badToken}`)
const errMsg = (await page.locator(".el-alert, .el-message").first().innerText().catch(() => "")) || ""
check("错误口令有可见提示", errMsg.trim().length > 0, errMsg.replace(/\s+/g, " ").slice(0, 60))

// 正确口令
await page.locator(".ol-auth input").nth(1).fill(PASS)
await page.locator(".ol-auth-card button.el-button--primary").first().click()
await page.waitForTimeout(2500)
const token = await page.evaluate(() => localStorage.getItem("token"))
check("登录成功并落 token", Boolean(token) && !/#\/@login/.test(page.url()), page.url())

// 刷新后仍登录（token 恢复 + 守卫放行）
await page.reload({ waitUntil: "networkidle" })
await page.waitForTimeout(1200)
check("刷新后保持登录", (await page.locator(".ol-auth").count()) === 0, page.url())

// ---------- E. 管理台 12 个模块都能打开 ----------
// （12 个全部就绪：about 是最后一个，见 §38；plugins 已于 §35 从导航移除 ——
//   .NET 侧没有 .so 插件宿主，留着只会误导）
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
  ["#/@manage/backup-restore", "Backup"],
  ["#/@manage/settings", "Settings"],
  ["#/@manage/about", "About"],
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

// ---------- F. 关于页：每个数字都要和接口对得上 ----------
// 这一页不放任何写死的期望值（版本号随构建变、路径随部署变，写死必然假失败），
// 断言的是「界面 == GET /api/admin/system/info」。
await goto("#/@manage/about")
await page.waitForSelector(".ol-about-hero", { timeout: 15000 })
const info = await page.request
  .get(`${BASE}/api/admin/system/info`, { headers: { Authorization: token } })
  .then((r) => r.json())
  .then((b) => b.data)
check("system/info 返回版本", Boolean(info?.version) && Boolean(info?.tag), JSON.stringify(info?.tag))

const heroTag = (await page.locator(".ol-about-tag").innerText()).trim()
check("横幅版本号 == 接口 tag", heroTag === info.tag, `${heroTag} vs ${info.tag}`)
const heroCommit = ((await page.locator(".ol-about-commit").innerText()) || "").trim()
check(
  "横幅提交号 == 接口 commit 前 7 位",
  heroCommit === `#${(info.commit || "").slice(0, 7)}`,
  `${heroCommit} vs #${(info.commit || "").slice(0, 7)}`,
)

// 键值行摊平成字典：键是文案、值是展示值，逐行比对（顺序变也不影响）
const rowMap = await page.evaluate(() => {
  const out = {}
  for (const el of document.querySelectorAll(".ol-about-row")) {
    const k = el.querySelector(".ol-about-key")?.textContent?.trim() ?? ""
    const v = el.querySelector(".ol-about-val")?.textContent?.trim() ?? ""
    if (k) out[k] = v
  }
  return out
})
const rows = Object.keys(rowMap).length
check("至少 14 行键值（运行环境 + 数据库 + 路径 + 服务）", rows >= 14, `rows=${rows}`)
check("Framework == 接口 runtime", rowMap["Framework"] === info.runtime, `${rowMap["Framework"]}`)
check("Operating system == 接口 os", rowMap["Operating system"] === info.os, `${rowMap["Operating system"]}`)
check(
  "Architecture 以接口的架构开头",
  (rowMap["Architecture"] || "").startsWith(info.architecture),
  rowMap["Architecture"],
)
check("Database == 接口 type", rowMap["Database"] === info.database.type, rowMap["Database"])
check(
  "Data directory == 接口 data_dir",
  rowMap["Data directory"] === info.paths.data_dir,
  `${rowMap["Data directory"]} vs ${info.paths.data_dir}`,
)
check(
  "Frontend assets == 接口 dist_dir",
  rowMap["Frontend assets"] === info.paths.dist_dir,
  `${rowMap["Frontend assets"]} vs ${info.paths.dist_dir}`,
)
check(
  "Log file == 接口 log_file（本实例启用了文件日志）",
  info.paths.log_enabled ? rowMap["Log file"] === info.paths.log_file : true,
  `${rowMap["Log file"]}`,
)

// 运行时长与时钟偏差是现算的：只验格式，不验数值（数值每刷新一次都在变）
check("Uptime 是格式化后的时长（不是 -1s 那种原始值）", /^\d+[dhms]/.test(rowMap["Uptime"] || ""), rowMap["Uptime"])
check("Clock skew 带符号秒", /^[+-]?\d+s$/.test(rowMap["Clock skew"] || ""), rowMap["Clock skew"])

// 内置服务：5 个标签（关闭的也要列出 —— 「什么没开」同样是信息）
const capTexts = await page.locator(".ol-about-tags .el-tag").allInnerTexts()
check("内置服务 5 个标签", capTexts.length === 5, capTexts.join("|"))
check(
  "S3 标签状态 == 接口 features.s3",
  (capTexts[0] || "").includes(info.features.s3 ? "on" : "off"),
  capTexts[0],
)
check(
  "Max connections 为 0 时显示 ∞",
  info.features.max_connections === 0 ? rowMap["Max connections"] === "∞" : true,
  rowMap["Max connections"],
)

// 链接：4 条外链，必须带 target=_blank + rel=noopener（后台页面别把站内导航带走）
const links = await page.locator(".ol-about-links a").evaluateAll((els) =>
  els.map((a) => ({ href: a.getAttribute("href"), target: a.getAttribute("target"), rel: a.getAttribute("rel") })),
)
check("4 条外链", links.length === 4, links.map((l) => l.href).join(" "))
check(
  "外链都指向 GitHub 且新窗口打开",
  links.every((l) => l.href?.startsWith("https://github.com/") && l.target === "_blank" && (l.rel || "").includes("noopener")),
  JSON.stringify(links[0]),
)

// 导航：12 项、全部可点（about 就绪后不该再有 (soon)）
const navTexts = await page.locator(".ol-manage-menu .el-menu-item").allInnerTexts()
const navDisabled = await page.locator(".ol-manage-menu .el-menu-item.is-disabled").count()
check("导航 12 项", navTexts.length === 12, navTexts.join("|"))
check("没有 (soon) 条目", !navTexts.some((t) => t.includes("(soon)")), navTexts.filter((t) => t.includes("soon")).join("|"))
check("没有禁用项", navDisabled === 0, `disabled=${navDisabled}`)

// 刷新会重新打接口（不是只改了本地状态）
let infoCalls = 0
page.on("request", (r) => {
  if (r.url().includes("/api/admin/system/info")) infoCalls += 1
})
await page.locator(".ol-manage-toolbar button.el-button").last().click()
await page.waitForTimeout(1200)
check("点刷新重新请求 system/info", infoCalls >= 1, `calls=${infoCalls}`)

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
