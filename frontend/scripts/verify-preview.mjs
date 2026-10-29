import { chromium } from "playwright"

/**
 * 预览器（archive / url / docx / xlsx / asciinema）的真机验收。
 *
 * 用法：`BASE=http://127.0.0.1:5001 TARGET=/e2e node scripts/verify-preview.mjs`
 * 前提：后端托管 `dist`，TARGET 目录是 `verify-preview.mjs` 配套装置
 *      （bundle.zip / book.xlsx / notes.docx / demo.cast / shortcut.url）。
 * 装置由 `.e2e-prev` 的构建脚本生成，见 `docs/unified-media-product-plan.md` §23。
 */

const BASE = process.env.BASE || "http://127.0.0.1:5001"
const TARGET = process.env.TARGET || "/e2e"
const SHOT = process.env.SHOT_DIR || "F:/Code/Dotnet/OpenList.Net/.e2e-prev"

const log = (...args) => console.log("[preview]", ...args)
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

/** 打开某个文件的预览页并等它稳定下来（异步分包 + 取数都要时间）。 */
async function openFile(name, wait = 2500) {
  await page.goto(`${BASE}/#/?path=${encodeURIComponent(`${TARGET}/${name}`)}`, {
    waitUntil: "networkidle",
  })
  await page.waitForTimeout(wait)
}

/** 当前选中的预览器显示名（下拉框只在有多个候选时出现）。 */
async function selectedPreview() {
  const select = page.locator(".ol-preview-actions .el-select").first()
  if ((await select.count()) === 0) return ""
  return (await select.textContent())?.trim() ?? ""
}

// ---------- 1. 归档预览：建树 / 导航 / 内联预览 ----------
await openFile("bundle.zip", 3000)
check("归档预览器被自动选中", /Archive/i.test(await selectedPreview()), await selectedPreview())

const rootRows = await page.locator(".ol-archive-row").count()
check("归档根层 4 个条目（3 目录 + hello.txt）", rootRows === 4, `rows=${rootRows}`)

const firstRow = await page.locator(".ol-archive-row .ol-archive-name").first().textContent()
check("目录排在文件之前", firstRow === "deep", `first=${firstRow}`)

const deepRow = page.locator(".ol-archive-row", { hasText: "deep" }).first()
await deepRow.click()
await page.waitForTimeout(400)
const deepRows = await page.locator(".ol-archive-row .ol-archive-name").allTextContents()
// deep/a/b/c.txt 里只有 a 是显式补出来的隐式目录
check("隐式目录逐层展开（deep → a）", deepRows.join(",") === "a", JSON.stringify(deepRows))

await page.locator(".ol-archive-crumb", { hasText: "." }).first().click()
await page.waitForTimeout(400)
await page.locator(".ol-archive-row", { hasText: "pics" }).first().click()
await page.waitForTimeout(400)
await page.locator(".ol-archive-row", { hasText: "cover.png" }).first().click()
await page.waitForTimeout(2000)

const innerImg = page.locator(".ol-archive-inner img").first()
const imgOk = await innerImg
  .evaluate((el) => el.complete && el.naturalWidth > 0)
  .catch(() => false)
check("归档内图片通过 /ae?inner 渲染成功", imgOk, `cards=${await page.locator(".ol-archive-inner").count()}`)

const innerSrc = await innerImg.getAttribute("src").catch(() => "")
check("内联预览走 /ae 且带 inner 参数", /\/ae\/.+inner=/.test(decodeURIComponent(innerSrc || "")), innerSrc || "")

await page.locator(".ol-archive-crumb", { hasText: "." }).first().click()
await page.waitForTimeout(400)
check("面包屑回到归档根", (await page.locator(".ol-archive-row").count()) === 4)

await page.screenshot({ path: `${SHOT}/preview-archive.png` })

// ---------- 2. .url 快捷方式 ----------
await openFile("shortcut.url", 1800)
check(".url 走 url_open 预览器", /URL/i.test(await selectedPreview()), await selectedPreview())
const urlText = await page.locator(".ol-url-target").first().textContent().catch(() => "")
check("解析出目标地址", urlText === "https://example.com/landing?x=1", `url=${urlText}`)
await page.screenshot({ path: `${SHOT}/preview-url.png` })

// ---------- 3. xlsx ----------
await openFile("book.xlsx", 3000)
check("xlsx 走 XLS 预览器", /XLS/i.test(await selectedPreview()), await selectedPreview())
const cellText = (await page.locator(".ol-sheet-table").innerText().catch(() => "")) || ""
check("表格渲染出内容", /apple/.test(cellText) && /42/.test(cellText), `cells=${cellText.replace(/\s+/g, " ").slice(0, 60)}`)
const sheetSelect = page.locator(".ol-preview-toolbar .el-select").first()
check("多工作表出现切换器", (await sheetSelect.count()) > 0)
if ((await sheetSelect.count()) > 0) {
  await sheetSelect.click()
  await page.waitForTimeout(300)
  await page.locator(".el-select-dropdown__item", { hasText: "Extra" }).first().click()
  await page.waitForTimeout(500)
  const extra = await page.locator(".ol-sheet-table").innerText().catch(() => "")
  check("切到 Extra 表", /second sheet/.test(extra), `cells=${extra.replace(/\s+/g, " ").slice(0, 40)}`)
}
await page.screenshot({ path: `${SHOT}/preview-xlsx.png` })

// ---------- 4. docx ----------
await openFile("notes.docx", 3500)
check("docx 走 DOC 预览器", /DOC/i.test(await selectedPreview()), await selectedPreview())
const docText = (await page.locator(".ol-office-doc").innerText().catch(() => "")) || ""
check("docx 渲染出正文", /docx preview smoke test/.test(docText), `text=${docText.replace(/\s+/g, " ").slice(0, 60)}`)
await page.screenshot({ path: `${SHOT}/preview-docx.png` })

// ---------- 5. asciinema ----------
await openFile("demo.cast", 3000)
check("cast 走 Asciinema 预览器", /Asciinema/i.test(await selectedPreview()), await selectedPreview())
const playerKids = await page.locator(".ol-asciinema-host > *").count()
check("播放器挂载出 DOM", playerKids > 0, `children=${playerKids}`)
await page.screenshot({ path: `${SHOT}/preview-cast.png` })

// ---------- 汇总 ----------
check("全程无控制台错误", errors.length === 0, errors.slice(0, 3).join(" | "))

const passed = results.filter((r) => r.ok).length
log(`SUMMARY ${passed}/${results.length}`)
await browser.close()
process.exit(passed === results.length ? 0 : 1)
