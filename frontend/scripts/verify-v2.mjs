import { chromium } from "playwright"

/**
 * M1 浏览体验（目录树 / 视图切换 / 分页 / 过滤 / README）的真机验收。
 *
 * 用法：`BASE=http://127.0.0.1:5001 TARGET=/e2e node scripts/verify-v2.mjs`
 * 前提：后端托管 `dist`，TARGET 目录是 `verify-ops.mjs` 造的那份固定装置
 *      （40 txt + 子目录A + 空目录B + bundle.zip + README.md = 44 项，无图片）。
 *      **先跑本脚本、再跑 verify-ops**：后者会重建装置，顺序反了断言会漂。
 */

const BASE = process.env.BASE || "http://127.0.0.1:5000"
const SHOT = process.env.SHOT_DIR || "F:/Code/Dotnet/OpenList.Net/.tmp"
const TARGET = process.env.TARGET || "/e2e"
/** 目标目录的条目总数（用于分页断言）；默认 44 对应 `verify-ops.mjs` 造的固定装置。 */
const TOTAL = Number(process.env.EXPECT_TOTAL || 44)
const PAGE_SIZE = 30

const log = (...a) => console.log("[verify]", ...a)
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

const modeBtn = (label) => page.locator(`.el-button-group .el-button:has-text('${label}')`).first()

await page.goto(`${BASE}/#/?path=${encodeURIComponent(TARGET)}`, { waitUntil: "networkidle" })
await page.waitForTimeout(1500)

// --- 目录树 ---
const treeNodes = await page.locator(".ol-tree .el-tree-node").count()
check("目录树渲染", treeNodes > 0, `nodes=${treeNodes}`)

// --- 列表 + 分页 ---
const rows = await page.locator(".el-table__body tr").count()
check(`列表按每页 ${PAGE_SIZE} 条分页`, rows === PAGE_SIZE, `rows=${rows}`)

const totalTag = await page.locator(".ol-files-bar .el-tag").last().textContent().catch(() => "")
check("总数标签", new RegExp(`共 ${TOTAL} 项`).test(totalTag || ""), `tag="${totalTag}"`)

const pagerCount = await page.locator(".ol-pager").count()
const pagerTotal = await page.locator(".ol-pager-total").first().textContent().catch(() => "")
check("分页条出现且总数正确", pagerCount > 0 && pagerTotal.includes(String(TOTAL)), `total="${pagerTotal}"`)

if (pagerCount > 0) {
  await page.locator(".ol-pager-actions .el-button", { hasText: "2" }).first().click()
  await page.waitForTimeout(500)
  const rowsP2 = await page.locator(".el-table__body tr").count()
  check(`第 2 页只剩 ${TOTAL - PAGE_SIZE} 条`, rowsP2 === TOTAL - PAGE_SIZE, `rows=${rowsP2}`)
  await page.locator(".ol-pager-actions .el-button", { hasText: "1" }).first().click()
  await page.waitForTimeout(400)
}

// --- README ---
const readmeTitle = await page.locator(".ol-readme-title").textContent().catch(() => "")
const readmeH1 = await page.locator(".ol-readme-body h1").textContent().catch(() => "")
const dirName = TARGET.split("/").filter(Boolean).pop() ?? ""
check("README 卡片渲染（目录内 README.md 优先）", /README\.md/i.test(readmeTitle) && (readmeH1 || "").length > 0, `title="${readmeTitle}" h1="${readmeH1}"`)

// --- 网格 ---
await modeBtn("网格").click()
await page.waitForTimeout(600)
const gridCards = await page.locator(".ol-grid-card").count()
check(`网格视图渲染 ${PAGE_SIZE} 张卡（分页后）`, gridCards === PAGE_SIZE, `cards=${gridCards}`)
const dirFirst = await page.locator(".ol-grid-card .ol-grid-name").first().textContent()
check("网格里目录排在前面", !/^file-/.test(dirFirst || ""), `first="${dirFirst}"`)
await page.screenshot({ path: `${SHOT}/v2-grid.png` })

// --- 图墙 ---
await modeBtn("图墙").click()
await page.waitForTimeout(600)
const imageCards = await page.locator(".ol-grid-card").count()
const imageThumbs = await page.locator(".ol-grid-thumb img").count()
check("图墙只显示目录 + 图片（装置无图片）", imageCards === 2 && imageThumbs === 0, `cards=${imageCards} imgs=${imageThumbs}`)
await page.screenshot({ path: `${SHOT}/v2-image.png` })

// --- 过滤（图墙下输入框一直可见）---
await modeBtn("列表").click()
await page.waitForTimeout(500)
const searchInput = page.locator("input[placeholder='在当前目录筛选']")
check("过滤输入框存在", (await searchInput.count()) > 0)
if (await searchInput.count()) {
  await searchInput.fill("file-1")
  await page.waitForTimeout(500)
  const rowsFiltered = await page.locator(".el-table__body tr").count()
  const tagFiltered = await page.locator(".ol-files-bar .el-tag").last().textContent().catch(() => "")
  check("过滤命中 file-1* 10 条（file-10..file-19）", rowsFiltered === 10, `rows=${rowsFiltered}`)
  check(
    "过滤时标签切换口径",
    new RegExp(`显示 \\d+ / ${TOTAL} 项`).test(tagFiltered || ""),
    `tag="${tagFiltered}"`,
  )

  await searchInput.fill("zzz-not-exist")
  await page.waitForTimeout(500)
  const emptyMsg = await page.locator(".el-empty__description").first().textContent().catch(() => "")
  check("过滤无命中显示空态", /没有匹配/.test(emptyMsg || ""), `msg=${emptyMsg}`)
  await searchInput.fill("")
  await page.waitForTimeout(400)
}

// --- 目录树折叠 / 展开 ---
const asideBefore = await page.locator(".ol-home-aside").count()
await page.locator(".ol-bar-left button").first().click()
await page.waitForTimeout(400)
const asideAfter = await page.locator(".ol-home-aside").count()
check("目录树可折叠", asideBefore === 1 && asideAfter === 0, `${asideBefore} -> ${asideAfter}`)
await page.locator(".ol-bar-left button").first().click()
await page.waitForTimeout(400)

// --- 树导航：点根目录再点回来 ---
// 树的根节点由 ElTree 内部承担（不渲染占位节点），第一层就是挂载点。
const treeLabels = await page.locator(".ol-tree .el-tree-node__label").allTextContents()
const mountKey = TARGET.split("/").filter(Boolean)[0]
check("树渲染根下挂载点", treeLabels.includes(mountKey), JSON.stringify(treeLabels))

// 深链：直接落到某个深层目录时，父链要自动展开并高亮当前节点。
const currentLabel = await page
  .locator(".ol-tree .el-tree-node.is-current > .el-tree-node__content .el-tree-node__label")
  .first()
  .textContent()
  .catch(() => "")
check("深链自动展开并高亮当前目录", (currentLabel || "").includes(dirName), `current="${currentLabel}"`)

const beforeNav = page.url()
await page.locator(".ol-tree .el-tree-node__label").filter({ hasText: "子目录A" }).first().click()
await page.waitForTimeout(1000)
check("点击树节点导航", page.url() !== beforeNav, `${beforeNav} -> ${page.url()}`)
await page.screenshot({ path: `${SHOT}/v2-root.png` })

check("控制台无错误", errors.length === 0, errors.slice(0, 3).join(" | "))

await browser.close()

const failed = results.filter((r) => !r.ok)
console.log("\n==== SUMMARY ====")
for (const r of results) console.log(`${r.ok ? "PASS" : "FAIL"} ${r.name} ${r.extra || ""}`)
console.log(`\n${results.length - failed.length}/${results.length} passed`)
process.exit(failed.length ? 1 : 0)
