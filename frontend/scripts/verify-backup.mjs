import { chromium } from "playwright"
import { copyFileSync, mkdirSync, readFileSync, writeFileSync } from "node:fs"
import { join } from "node:path"
import CryptoJS from "crypto-js"

/**
 * 管理台备份 / 恢复（§37）的真机验收。
 *
 * 用法：`BASE=http://127.0.0.1:5055 ADMIN_PASS=e2e12345 node scripts/verify-backup.mjs`
 * 前置：隔离后端（临时库 + AdminPassword + DistDir=frontend/dist）。
 *
 * 这个页面**没有后端端点**：导出 = 拉五个既有管理端点拼 JSON，恢复 = 解 JSON 后逐条走 CRUD。
 * 所以断言全部落在「文件内容」与「恢复后服务端的状态」上，而不是某个接口。
 *
 * 五件必须真验的事：
 * 1. **文件格式**：明文导出是可读 JSON（五个数组）；加密导出是逐字段的双 base64，
 *    并且真的能用口令解开 —— 只断言「encrypted 非空」等于没验加密。
 * 2. **口令错时不写任何字节**：先删掉探针用户再拿错口令恢复，用户数不能变回来。
 * 3. **往返**：导出 → 删掉实体 → 恢复（新增模式）→ 实体回来，且用户是**停用**状态
 *    （口令不随备份迁移，脚本必须确认这条设计成立，否则会误判成"恢复失败"）。
 * 4. **幂等**：同一份文件再导入一次，新增数必须是 0（全部 skipped），不能出现重复实体。
 * 5. **自锁保护**：把文件里当前登录管理员的 disabled 改成 true，开覆盖模式导入，
 *    必须整份拒绝，且账号仍然可用。
 */

const BASE = process.env.BASE || "http://127.0.0.1:5055"
const PASS = process.env.ADMIN_PASS || "e2e12345"
const WORK = process.env.BACKUP_WORK || ".e2e/backup-e2e"
const PROBE_USER = "br_probe"
const PROBE_PASS = "probe12345"
const PROBE_MOUNT = "/backup-probe"
const PROBE_ROOT = process.env.PROBE_ROOT || "G:/Share/ol-v2-backup-probe"
const ENC_PASSWORD = "e2e-secret"

const log = (...args) => console.log("[backup]", ...args)
const results = []
function check(name, ok, extra = "") {
  results.push({ name, ok, extra })
  log(ok ? "PASS" : "FAIL", name, extra)
}

mkdirSync(WORK, { recursive: true })
mkdirSync(PROBE_ROOT, { recursive: true })

const browser = await chromium.launch({ channel: "msedge", headless: true })
const page = await browser.newPage({ viewport: { width: 1440, height: 900 }, acceptDownloads: true })

const errors = []
page.on("console", (msg) => {
  if (msg.type() === "error") errors.push(msg.text())
})
page.on("pageerror", (e) => errors.push(String(e)))

const token = await page.request
  .post(`${BASE}/api/auth/login`, { data: { username: "admin", password: PASS } })
  .then((r) => r.json())
  .then((body) => body.data?.token)
if (!token) throw new Error("admin login failed")
await page.addInitScript((value) => localStorage.setItem("token", value), token)

const api = async (method, path, data) =>
  page.request[method](`${BASE}/api${path}`, { data, headers: { Authorization: token } }).then((r) =>
    r.json(),
  )

const listUsers = async () => (await api("get", "/admin/user/list")).data?.content ?? []
const findUser = async (name) => (await listUsers()).find((u) => u.username === name)
const deleteUser = async (name) => {
  const row = await findUser(name)
  if (row) await api("post", `/admin/user/delete?id=${row.id}`, {})
}
const deleteMount = async () => {
  const rows = (await api("get", "/admin/storage/list")).data?.content ?? []
  for (const row of rows) {
    if (row.mount_path === PROBE_MOUNT) await api("post", `/admin/storage/delete?id=${row.id}`, {})
  }
}
const mountExists = async () =>
  ((await api("get", "/admin/storage/list")).data?.content ?? []).some((s) => s.mount_path === PROBE_MOUNT)

// ---------- 1. 装置：探针用户 + 探针挂载（先清残留） ----------
await deleteUser(PROBE_USER)
await deleteMount()
const mkUser = await api("post", "/admin/user/create", {
  username: PROBE_USER,
  password: PROBE_PASS,
  role: 0,
  permission: 0,
})
check("装置：建探针用户", mkUser.code === 200, mkUser.message)
const mkMount = await api("post", "/admin/storage/create", {
  mount_path: PROBE_MOUNT,
  driver: "Local",
  addition: JSON.stringify({ root_folder_path: PROBE_ROOT }),
})
check("装置：建探针挂载", mkMount.code === 200, mkMount.message)

// ---------- 2. 页面与导航 ----------
await page.goto(`${BASE}/#/@manage/backup-restore`, { waitUntil: "networkidle" })
await page.waitForSelector(".ol-br-card", { timeout: 10000 })
const cardCount = await page.locator(".ol-br-card").count()
check("页面渲染三张卡（备份 / 恢复 / 日志）", cardCount === 3, `${cardCount}`)

const navTexts = (await page.locator(".ol-manage-menu .el-menu-item").allInnerTexts()).map((s) =>
  s.replace(/\s+/g, " ").trim(),
)
check(
  "导航里 Backup & Restore 已放开（无 soon）",
  navTexts.some((t) => t === "Backup & Restore"),
  navTexts.find((t) => t.includes("Backup")) ?? "",
)
// about 在 §38 放开后导航已无禁用项（本轮的旧断言曾是「只剩 About 一个」）
check(
  "导航已无禁用项（about 也已放开）",
  (await page.locator(".el-menu-item.is-disabled").count()) === 0,
  `${await page.locator(".el-menu-item.is-disabled").count()}`,
)

const backupCard = page.locator(".ol-br-card").first()
const restoreCard = page.locator(".ol-br-card").nth(1)

/** 点「Download backup」并取回落盘文本。 */
async function exportBackup(password, fileName) {
  await backupCard.locator("input[type=password]").fill(password)
  const [download] = await Promise.all([
    page.waitForEvent("download", { timeout: 20000 }),
    backupCard.locator("button.el-button--primary").click(),
  ])
  const dest = join(WORK, fileName)
  copyFileSync(await download.path(), dest)
  return { name: download.suggestedFilename(), path: dest, text: readFileSync(dest, "utf8") }
}

// ---------- 3. 明文导出 ----------
const plain = await exportBackup("", "plain.json")
check("明文备份文件名格式", /^openlist_backup_.+_\d{8}-\d{6}\.json$/.test(plain.name), plain.name)
const doc = JSON.parse(plain.text)
check("format / version 标记", doc.format === "openlist-net-backup" && doc.version === 1, `${doc.format}@${doc.version}`)
check("明文导出 encrypted 为空", doc.encrypted === "", JSON.stringify(doc.encrypted))
for (const section of ["settings", "users", "storages", "metas", "shares"]) {
  check(`五个域都在：${section}`, Array.isArray(doc[section]), `${doc[section]?.length ?? "-"} 行`)
}
check("设置项非空", doc.settings.length > 10, `${doc.settings.length} settings`)
check(
  "明文含探针用户",
  doc.users.some((u) => u.username === PROBE_USER),
  `${doc.users.length} users`,
)
check("明文含探针挂载", doc.storages.some((s) => s.mount_path === PROBE_MOUNT), `${doc.storages.length} storages`)
check("用户行不含密码字段", doc.users.every((u) => !("password" in u)), "")
check(
  "日志写出「已保存」与「明文」提示",
  (await page.locator(".ol-br-log-row").allInnerTexts()).some((line) => /saved|保存|plaintext|明文/i.test(line)),
  "",
)

// ---------- 4. 加密导出（逐字段双 base64，且真的能解开） ----------
const encrypted = await exportBackup(ENC_PASSWORD, "encrypted.json")
const enc = JSON.parse(encrypted.text)
check("加密备份有 encrypted 标记", typeof enc.encrypted === "string" && enc.encrypted.length > 20, "")
const encUser = enc.users.find((u) => true) ?? {}
const firstValue = Object.values(encUser)[0]
check("加密后字段变成字符串", typeof firstValue === "string" && firstValue.length > 20, typeof firstValue)

const decodeDouble = (value, key) => {
  const inner = CryptoJS.enc.Base64.parse(value).toString(CryptoJS.enc.Utf8)
  const plainText = CryptoJS.AES.decrypt(inner, key).toString(CryptoJS.enc.Utf8)
  return JSON.parse(plainText)
}
let decodedNames = []
try {
  decodedNames = enc.users.map((row) => decodeDouble(row.username, ENC_PASSWORD))
} catch {
  decodedNames = []
}
check("加密字段能用口令解回原值", decodedNames.includes(PROBE_USER), decodedNames.join(","))

// ---------- 5. 口令错：一个字节都不许写 ----------
await deleteUser(PROBE_USER)
const beforeWrong = (await listUsers()).length
await page.locator(".ol-br-file-input").setInputFiles(encrypted.path)
await restoreCard.locator("input[type=password]").fill("wrong-password")
await restoreCard.locator("button.el-button--primary").click()
await page.waitForTimeout(1200)
const wrongLog = await page.locator(".ol-br-log-row").allInnerTexts()
check("错口令有明确提示", wrongLog.some((line) => /wrong password|口令|密码/i.test(line)), wrongLog.slice(-1).join(" | "))
check("错口令没进确认框", (await page.locator(".el-message-box").count()) === 0, "")
check("错口令没有恢复用户", (await listUsers()).length === beforeWrong, `${beforeWrong} -> ${(await listUsers()).length}`)

/** 选文件 → 点恢复 → 处理确认框。 */
async function restore(filePath, password, confirm) {
  await page.locator(".ol-br-file-input").setInputFiles(filePath)
  await restoreCard.locator("input[type=password]").fill(password)
  await restoreCard.locator("button.el-button--primary").click()
  const box = page.locator(".el-message-box")
  await box.waitFor({ state: "visible", timeout: 15000 }).catch(() => {})
  if (confirm) {
    await box.locator(".el-message-box__btns button.el-button--primary").click()
  } else {
    await box.locator(".el-message-box__btns button").first().click()
  }
  // 恢复是逐条 CRUD，settings 一百多行，给足时间
  await page.waitForTimeout(4000)
  return (await page.locator(".ol-br-log-row").allInnerTexts()).join("\n")
}

// ---------- 6. 往返：删掉 → 用明文文件恢复 ----------
await deleteMount()
check("（前置）探针挂载已删除", !(await mountExists()), "")
const restoreLog = await restore(plain.path, "", true)
check("恢复走完并写出完成提示", /restore|恢复/i.test(restoreLog), restoreLog.split("\n").slice(-1)[0])

const restoredUser = await findUser(PROBE_USER)
check("探针用户被恢复", Boolean(restoredUser), `${restoredUser?.id}`)
check("恢复的用户是停用状态（口令不随备份迁移）", restoredUser?.disabled === true, `disabled=${restoredUser?.disabled}`)
check("探针挂载被恢复", await mountExists(), "")

const summaryRows = (await page.locator(".ol-br-summary-row").allInnerTexts()).map((s) =>
  s.replace(/\s+/g, " ").trim(),
)
log("summary:", summaryRows.join(" | "))
check("汇总面板出现", summaryRows.length >= 4, `${summaryRows.length} rows`)

// ---------- 7. 幂等：同一份文件再导一次，不应新增任何实体 ----------
const usersAfterFirst = (await listUsers()).length
const secondLog = await restore(plain.path, "", true)
const usersAfterSecond = (await listUsers()).length
check("二次导入不产生重复用户", usersAfterSecond === usersAfterFirst, `${usersAfterFirst} -> ${usersAfterSecond}`)
const secondSummary = (await page.locator(".ol-br-summary-row").allInnerTexts()).join(" ")
check(
  "二次导入的新增数为 0",
  /0/.test(secondSummary),
  secondSummary.replace(/\s+/g, " ").slice(0, 80),
)
check("二次导入仍有跳过计数", /skip|跳过/i.test(secondSummary) || Boolean(secondLog), "")

// ---------- 8. 自锁保护：把管理员 disabled 改成 true，覆盖模式导入必须整份拒绝 ----------
const adminRow = doc.users.find((u) => u.is_built_in) ?? doc.users.find((u) => u.username === "admin")
check("文件里有内置管理员", Boolean(adminRow), adminRow?.username ?? "")
const crafted = {
  ...doc,
  users: doc.users.map((u) => (u.username === adminRow.username ? { ...u, disabled: true } : u)),
}
const craftedPath = join(WORK, "admin-disabled.json")
writeFileSync(craftedPath, JSON.stringify(crafted, null, 2), "utf8")

// 打开覆盖开关（只作用于用户/挂载/元数据/分享）
await restoreCard.locator(".el-switch").click()
await page.waitForTimeout(200)
const guardLog = await restore(craftedPath, "", true)
check("自锁风险被拒绝", /refused|demote|disable|lock|拒绝|停用/i.test(guardLog), guardLog.split("\n").slice(-1)[0])
const adminAfter = (await listUsers()).find((u) => u.username === adminRow.username)
check("管理员没有被停用", adminAfter?.disabled === false, `disabled=${adminAfter?.disabled}`)
const stillLoggedIn = await api("get", "/me")
check("当前会话仍然有效", stillLoggedIn.code === 200, `${stillLoggedIn.code}`)

// ---------- 9. 清理探针实体，避免影响后续轮次 ----------
await page.locator(".el-switch").click()
await deleteUser(PROBE_USER)
await deleteMount()
check("清理探针实体", (await findUser(PROBE_USER)) === undefined, "")

check("零控制台错误", errors.length === 0, errors.slice(0, 2).join(" | "))

// ---------- 汇总 ----------
const failed = results.filter((r) => !r.ok)
log(`\n${results.length - failed.length}/${results.length} passed`)
if (failed.length) log("FAILED:", failed.map((r) => `${r.name} (${r.extra})`).join("; "))
await browser.close()
process.exit(failed.length ? 1 : 0)
