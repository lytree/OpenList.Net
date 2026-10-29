import { chromium } from "playwright"

/**
 * 站内信（M6）的真机验收：管理台发信 + 用户侧收件箱 + 归属校验。
 *
 * 用法：`BASE=http://127.0.0.1:5016 ADMIN_PASS=e2e12345 node scripts/verify-messenger.mjs`
 * 前置：隔离后端（临时库 + AdminPassword + DistDir=frontend/dist）。
 *
 * 为什么要第二个账号：站内信是「管理员 → 指定用户」，只用 admin 自测永远看不出
 * 收件人隔离是否成立。脚本自建 `carol`（幂等：已存在就复用），并清空两个账号的历史消息，
 * 好让「未读数」这类绝对值断言有意义。
 *
 * 越权那一项用 `page.request` 直接打接口而不是点 UI：走页面会触发浏览器为 404
 * 记一条 console error，把最后的「零错误」断言搅浑。
 */

const BASE = process.env.BASE || "http://127.0.0.1:5016"
const PASS = process.env.ADMIN_PASS || "e2e12345"
const CAROL = "carol"
const CAROL_PASS = "carol12345"

const log = (...args) => console.log("[messenger]", ...args)
const results = []
function check(name, ok, extra = "") {
  results.push({ name, ok, extra })
  log(ok ? "PASS" : "FAIL", name, extra)
}

const browser = await chromium.launch({ channel: "msedge", headless: true })

const errors = []
function watch(page, tag) {
  page.on("console", (msg) => {
    if (msg.type() === "error") errors.push(`${tag}: ${msg.text()}`)
  })
  page.on("pageerror", (e) => errors.push(`${tag}: ${e}`))
}

/** 登录并返回一个已注入 token 的 page。 */
async function signIn(username, password, tag) {
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 }, locale: "en-US" })
  watch(page, tag)
  const token = await page.request
    .post(`${BASE}/api/auth/login`, { data: { username, password } })
    .then((r) => r.json())
    .then((b) => b.data?.token)
  if (!token) throw new Error(`login failed for ${username}`)
  await page.addInitScript((v) => localStorage.setItem("token", v), token)
  return { page, token }
}

const admin = await signIn("admin", PASS, "admin")
const apiAs = (token) => async (method, path, data) =>
  admin.page.request[method](`${BASE}/api${path}`, {
    data,
    headers: { Authorization: token },
  }).then((r) => r.json())
const api = apiAs(admin.token)

// ---------- 0. 装置：建 carol（幂等）+ 清空两边消息 ----------
let users = await api("get", "/admin/user/list")
let carol = (users.data?.content ?? []).find((u) => u.username === CAROL)
if (!carol) {
  await api("post", "/admin/user/create", {
    username: CAROL,
    password: CAROL_PASS,
    role: 1,
    permission: 0,
  })
  users = await api("get", "/admin/user/list")
  carol = (users.data?.content ?? []).find((u) => u.username === CAROL)
  log("created user", CAROL, carol?.id)
} else {
  log("reusing user", CAROL, carol.id)
}
check("测试账号就绪", !!carol?.id, `id=${carol?.id}`)

const adminUser = (users.data?.content ?? []).find((u) => u.is_built_in)
check("找到内置管理员", !!adminUser?.id, `id=${adminUser?.id}`)

for (const uid of [adminUser.id, carol.id]) {
  const list = await api("get", `/admin/message/list?user_id=${uid}`)
  for (const m of list.data ?? []) await api("post", `/admin/message/delete?id=${m.id}`)
}
const cleanCarol = await api("get", `/admin/message/list?user_id=${carol.id}`)
check("历史消息已清空", (cleanCarol.data ?? []).length === 0)

// ---------- 1. 管理台页面 ----------
const { page: apage } = admin
await apage.goto(`${BASE}/#/@manage/messenger`, { waitUntil: "networkidle" })
await apage.waitForTimeout(1500)

const h2 = await apage.locator(".ol-manage-h2").textContent().catch(() => "")
check("导航进入 Messages 页", h2?.trim() === "Messages", h2 || "(无)")

const navActive = await apage.locator(".ol-manage-menu .is-active", { hasText: "Messages" }).count()
check("侧边导航 Messages 高亮且未禁用", navActive === 1, `count=${navActive}`)

const carolBtn = apage.locator(".ol-msg-user", { hasText: CAROL })
check("用户选择器列出 carol", (await carolBtn.count()) === 1)
check("内置管理员带 built-in 标记",
  (await apage.locator(".ol-msg-user", { hasText: "built-in" }).count()) >= 1)

// 过滤框
await apage.locator(".ol-msg-users input").first().fill(CAROL)
await apage.waitForTimeout(300)
check("过滤后只剩目标用户", (await apage.locator(".ol-msg-user").count()) === 1,
  `count=${await apage.locator(".ol-msg-user").count()}`)
await apage.locator(".ol-msg-users input").first().fill("")
await apage.waitForTimeout(300)

await carolBtn.click()
await apage.waitForTimeout(600)
const recipient = await apage.locator(".ol-msg-recipient").textContent().catch(() => "")
check("右侧切到 carol", /carol/.test(recipient || ""), recipient?.trim() || "")

// ---------- 2. 空标题被前端挡下 ----------
await apage.locator(".ol-msg-compose textarea").fill("只填正文")
await apage.locator(".ol-msg-compose-actions button").click()
await apage.waitForTimeout(700)
const warn = await apage.locator(".el-message--warning").first().textContent().catch(() => "")
check("标题为空时给出提示且不提交", /required/i.test(warn || ""), warn?.trim() || "(无)")
const stillEmpty = await api("get", `/admin/message/list?user_id=${carol.id}`)
check("空标题没有落库", (stillEmpty.data ?? []).length === 0)

// ---------- 3. 正常发信 ----------
const TITLE = "维护通知"
const BODY = "今晚 22:00 起停止上传 30 分钟。"
await apage.locator(".ol-msg-compose input").first().fill(TITLE)
await apage.locator(".ol-msg-compose textarea").fill(BODY)
await apage.locator(".ol-msg-compose-actions button").click()
await apage.waitForTimeout(1200)

const successToast = await apage.locator(".el-message--success").first().textContent().catch(() => "")
check("发送成功提示", /carol/.test(successToast || ""), successToast?.trim() || "(无)")
check("消息出现在列表", (await apage.locator(".ol-msg-item").count()) === 1)
const itemText = await apage.locator(".ol-msg-item").first().textContent().catch(() => "")
check("列表显示标题与正文", itemText?.includes(TITLE) && itemText?.includes(BODY), itemText?.trim() || "")
check("新消息标为未读（左侧色条 + 圆点）",
  (await apage.locator(".ol-msg-item.is-unread .ol-msg-dot").count()) === 1)
const bodyInput = await apage.locator(".ol-msg-compose textarea").inputValue()
check("发送后清空正文、保留标题", bodyInput === "" &&
  (await apage.locator(".ol-msg-compose input").first().inputValue()) === TITLE, `body="${bodyInput}"`)

// ---------- 4. 收件人侧：未读数与收件箱 ----------
const carolSession = await signIn(CAROL, CAROL_PASS, "carol")
const cpage = carolSession.page
const carolApi = apiAs(carolSession.token)

const unread = await carolApi("get", "/me/message/unread")
check("carol 未读数 = 1", unread.data === 1, `unread=${unread.data}`)

await cpage.goto(`${BASE}/#/`, { waitUntil: "networkidle" })
await cpage.waitForTimeout(1500)
const badge = await cpage.locator(".ol-inbox-badge .el-badge__content").textContent().catch(() => "")
check("顶栏徽标显示 1", badge?.trim() === "1", badge?.trim() || "(无)")

await cpage.locator(".ol-inbox-badge button").click()
await cpage.waitForTimeout(900)
check("收件箱打开", (await cpage.locator(".el-dialog__title", { hasText: "Inbox" }).count()) === 1)
check("收件箱里 1 条消息", (await cpage.locator(".ol-inbox-item").count()) === 1)
const inboxText = await cpage.locator(".ol-inbox-item").first().textContent().catch(() => "")
check("收件箱内容与发件一致", inboxText?.includes(TITLE) && inboxText?.includes(BODY), inboxText?.trim() || "")

// ---------- 5. 标为已读 ----------
await cpage.locator(".ol-inbox-item").first().getByText("Mark as read").click()
await cpage.waitForTimeout(900)
const unreadAfter = await carolApi("get", "/me/message/unread")
check("标记已读后未读归零（服务端）", unreadAfter.data === 0, `unread=${unreadAfter.data}`)
check("已读后不再显示 Mark as read",
  (await cpage.locator(".ol-inbox-item").first().getByText("Mark as read").count()) === 0)
const badgeAfter = await cpage.locator(".ol-inbox-badge .el-badge__content").count()
check("徽标消失", badgeAfter === 0, `count=${badgeAfter}`)

// ---------- 6. 归属校验：carol 不能动 admin 的消息 ----------
await api("post", "/admin/message/create", {
  userId: adminUser.id,
  title: "只给管理员",
  content: "carol 不该能把它标成已读",
})
const adminMsgs = await api("get", `/admin/message/list?user_id=${adminUser.id}`)
const adminMsgId = adminMsgs.data?.[0]?.id
check("管理员侧存在一条未读", adminMsgId !== undefined && adminMsgs.data[0].read === false,
  `id=${adminMsgId}`)

const hijack = await carolApi("post", "/me/message/read", { id: adminMsgId })
check("越权标记被拒绝（404）", hijack.code === 404, `code=${hijack.code}`)
const adminMsgsAfter = await api("get", `/admin/message/list?user_id=${adminUser.id}`)
check("管理员消息仍为未读", adminMsgsAfter.data?.[0]?.read === false,
  `read=${adminMsgsAfter.data?.[0]?.read}`)
check("carol 的列表里看不到别人的消息",
  ((await carolApi("get", "/me/message/list")).data ?? []).every((m) => m.userId === carol.id))

// ---------- 7. 管理台删除 ----------
await apage.locator(".ol-msg-item").first().locator("button").click()
await apage.waitForTimeout(600)
const confirmText = await apage.locator(".el-message-box__message").textContent().catch(() => "")
check("删除确认框带上标题", confirmText?.includes(TITLE) === true, confirmText?.trim() || "")
await apage.locator(".el-message-box__btns .el-button--primary").click()
await apage.waitForTimeout(1000)
check("删除后列表为空", (await apage.locator(".ol-msg-item").count()) === 0)
const carolAfterDelete = await carolApi("get", "/me/message/list")
check("服务端已删除", (carolAfterDelete.data ?? []).length === 0, JSON.stringify(carolAfterDelete.data ?? []))

// 但 admin 自己那条不该被连带删掉
const adminStillThere = await api("get", `/admin/message/list?user_id=${adminUser.id}`)
check("删除只作用于该用户", (adminStillThere.data ?? []).length === 1)

// ---------- 8. 控制台干净 ----------
check("零控制台错误", errors.length === 0, errors.slice(0, 3).join(" | "))

await browser.close()

const failed = results.filter((r) => !r.ok)
console.log(`\n[messenger] SUMMARY ${results.length - failed.length}/${results.length} passed`)
if (failed.length) {
  for (const f of failed) console.log("  FAIL:", f.name, f.extra)
  process.exit(1)
}
