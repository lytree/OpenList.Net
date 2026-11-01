/**
 * WebDAV（`/dav`）写路径验收。
 *
 * 存在的理由：`WebDavEndpointsTests` 里那条 `Delete_RemovesFile` 被 `[Skip]` 挂了很久，
 * 理由写的是「首个 WebDAV 请求无限挂起」，于是 `/dav` 的写入路径**一直没有端到端覆盖**——
 * 单测是 in-proc TestServer，`verify-*` 里又没有 WebDAV 轮，等于三类客户端（Finder /
 * Windows 资源管理器 / rclone）真正走的这条链路没人守。2026-11-01 复核发现真实服务器一点
 * 问题没有（DELETE 204、文件真的没了、事后 GET 404），所以这里把它钉成回归。
 *
 * 与其它轮的区别：不开浏览器。WebDAV 客户端本来就只讲 HTTP + Basic 认证，用 fetch 打
 * 更贴近真实调用方，也更快。
 *
 * 每个请求都带 `AbortSignal.timeout`：这类接口历史上出过「不返回也不报错」的挂起，
 * 没有超时的话一轮回归会卡到 CI 全局超时，症状是「什么都没输出」。
 *
 * 用法：
 *   BASE=http://127.0.0.1:5001 ADMIN_PASS=e2e12345 node scripts/verify-webdav.mjs
 *   自行建挂载：MOUNT=/e2e-webdav MOUNT_ROOT=<本机目录>（不存在会自动创建）
 */
import { existsSync, mkdirSync, rmSync, writeFileSync } from "node:fs"
import { join } from "node:path"

const BASE = process.env.BASE ?? "http://127.0.0.1:5001"
const ADMIN = process.env.ADMIN_USER ?? "admin"
const PASS = process.env.ADMIN_PASS ?? "e2e12345"
const MOUNT = process.env.MOUNT ?? "/e2e-webdav"
const ROOT = process.env.MOUNT_ROOT ?? "F:/Code/github/OpenList.Net/.e2e/webdav/storage"
const REQ_TIMEOUT = Number(process.env.REQ_TIMEOUT_MS ?? 15000)

const results = []
function check(name, ok, detail = "") {
  results.push({ name, ok, detail })
  console.log(`${ok ? "  ok  " : "  FAIL"} ${name}${detail ? ` — ${detail}` : ""}`)
}

const log = (...a) => console.log("[webdav]", ...a)

/** 建挂载要用的管理端 API（与 fixtures/setup.mjs 同一套约定）。 */
const NO_KEEPALIVE = { Connection: "close" }

async function api(method, path, body, token) {
  const resp = await fetch(`${BASE}/api${path}`, {
    method,
    headers: {
      "Content-Type": "application/json",
      ...(token ? { Authorization: token } : {}),
      ...NO_KEEPALIVE,
    },
    body: body === undefined ? undefined : JSON.stringify(body),
    signal: AbortSignal.timeout(REQ_TIMEOUT),
  })
  return resp.json()
}

async function ensureMount() {
  mkdirSync(ROOT, { recursive: true })
  const login = await api("post", "/auth/login", { username: ADMIN, password: PASS })
  if (!login?.data?.token) throw new Error(`login failed: ${JSON.stringify(login).slice(0, 200)}`)
  const token = login.data.token

  const list = await api("get", "/admin/storage/list", undefined, token)
  for (const storage of list?.data?.content ?? []) {
    if (storage.mount_path === MOUNT) {
      await api("post", `/admin/storage/delete?id=${storage.id}`, undefined, token)
    }
  }
  const created = await api(
    "post",
    "/admin/storage/create",
    {
      mount_path: MOUNT,
      driver: "Local",
      addition: JSON.stringify({ root_folder_path: ROOT }),
      order: 0,
      remark: "webdav-e2e",
    },
    token,
  )
  if (created?.code !== 200) throw new Error(`storage/create failed: ${JSON.stringify(created).slice(0, 200)}`)
  log(`mount ${MOUNT} -> ${ROOT}`)
}

const BASIC = `Basic ${Buffer.from(`${ADMIN}:${PASS}`).toString("base64")}`

/** 发一个 WebDAV 请求；超时会被记成失败而不是让整轮挂住。 */
async function dav(method, path, { body, headers = {}, auth = BASIC } = {}) {
  const started = Date.now()
  try {
    const resp = await fetch(`${BASE}/dav${path}`, {
      method,
      headers: { ...(auth ? { Authorization: auth } : {}), ...NO_KEEPALIVE, ...headers },
      body,
      signal: AbortSignal.timeout(REQ_TIMEOUT),
    })
    const text = await resp.text().catch(() => "")
    return { status: resp.status, headers: resp.headers, text, ms: Date.now() - started }
  } catch (error) {
    return { status: 0, headers: new Headers(), text: "", ms: Date.now() - started, error: String(error) }
  }
}

/** VFS 路径 → 本机路径（挂载点之外的东西不该被这个脚本碰到）。 */
function onDisk(rel) {
  return join(ROOT, ...rel.split("/").filter(Boolean))
}

const slow = []
function noteTiming(what, ms) {
  slow.push({ what, ms })
}

await ensureMount()

// 装置：一个文件 + 一个目录（目录里再放一个文件，用来验递归删除）
rmSync(onDisk("work"), { recursive: true, force: true })
mkdirSync(onDisk("work/nested"), { recursive: true })
writeFileSync(onDisk("work/nested/inner.txt"), "inner")
writeFileSync(onDisk("work/keep.txt"), "keep")

// --- 1. 能力协商 / 认证 ---

{
  const r = await dav("OPTIONS", MOUNT)
  noteTiming("OPTIONS", r.ms)
  const davHeader = r.headers.get("DAV") ?? ""
  check("OPTIONS 返回 200", r.status === 200, `code=${r.status}`)
  check("DAV 头通告 1 与 2", davHeader.includes("1") && davHeader.includes("2"), davHeader)
  const allow = r.headers.get("Allow") ?? ""
  check(
    "Allow 列出写路径方法",
    ["DELETE", "MOVE", "COPY", "PROPFIND", "PUT", "MKCOL"].every((m) => allow.includes(m)),
    allow.slice(0, 80),
  )

  const anon = await dav("GET", `${MOUNT}/work/keep.txt`, { auth: null })
  check("匿名 GET 被拒绝（401）", anon.status === 401, `code=${anon.status}`)

  const wrong = await dav("GET", `${MOUNT}/work/keep.txt`, {
    auth: `Basic ${Buffer.from(`${ADMIN}:wrong-password`).toString("base64")}`,
  })
  check("错误口令被拒绝（401）", wrong.status === 401, `code=${wrong.status}`)
}

// --- 2. PUT / GET / HEAD ---

{
  const put = await dav("PUT", `${MOUNT}/work/put.txt`, { body: "dav-upload" })
  noteTiming("PUT", put.ms)
  check("PUT 新建文件成功", [200, 201, 204].includes(put.status), `code=${put.status}`)
  check("PUT 落到了磁盘", existsSync(onDisk("work/put.txt")), onDisk("work/put.txt"))

  const get = await dav("GET", `${MOUNT}/work/put.txt`)
  noteTiming("GET", get.ms)
  check("GET 内容一致", get.status === 200 && get.text === "dav-upload", `code=${get.status} body=${get.text}`)

  const head = await dav("HEAD", `${MOUNT}/work/put.txt`)
  check(
    "HEAD 返回正确长度",
    head.status === 200 && head.headers.get("Content-Length") === String("dav-upload".length),
    `code=${head.status} len=${head.headers.get("Content-Length")}`,
  )

  const putDir = await dav("PUT", `${MOUNT}/work/nested/uploaded.txt`, { body: "nested-upload" })
  check("PUT 到子目录成功", [200, 201, 204].includes(putDir.status), `code=${putDir.status}`)
}

// --- 3. PROPFIND ---

{
  const depth0 = await dav("PROPFIND", `${MOUNT}/work/put.txt`, { headers: { Depth: "0" } })
  check("PROPFIND Depth:0 返回 207", depth0.status === 207, `code=${depth0.status}`)
  check("Depth:0 只含自身", depth0.text.includes("put.txt") && !depth0.text.includes("keep.txt"), "")

  const depth1 = await dav("PROPFIND", `${MOUNT}/work`, { headers: { Depth: "1" } })
  check("PROPFIND Depth:1 返回 207", depth1.status === 207, `code=${depth1.status}`)
  check(
    "Depth:1 含子项",
    ["keep.txt", "nested", "put.txt"].every((n) => depth1.text.includes(n)) && depth1.text.includes("multistatus"),
    "",
  )
}

// --- 4. MKCOL ---

{
  const mkcol = await dav("MKCOL", `${MOUNT}/work/created`)
  check("MKCOL 建目录成功", [200, 201, 204].includes(mkcol.status), `code=${mkcol.status}`)
  check("MKCOL 目录真的创建了", existsSync(onDisk("work/created")), onDisk("work/created"))
}

// --- 5. MOVE / COPY ---

{
  const move = await dav("MOVE", `${MOUNT}/work/keep.txt`, {
    headers: { Destination: `${BASE}/dav${MOUNT}/work/moved.txt` },
  })
  check("MOVE 成功", [200, 201, 204].includes(move.status), `code=${move.status}`)
  const oldGone = await dav("GET", `${MOUNT}/work/keep.txt`)
  const newThere = await dav("GET", `${MOUNT}/work/moved.txt`)
  check("MOVE 后源路径消失", oldGone.status === 404, `code=${oldGone.status}`)
  check("MOVE 后目标可读", newThere.status === 200 && newThere.text === "keep", `code=${newThere.status}`)

  const copy = await dav("COPY", `${MOUNT}/work/moved.txt`, {
    headers: { Destination: `${BASE}/dav${MOUNT}/work/copied.txt` },
  })
  check("COPY 成功", [200, 201, 204].includes(copy.status), `code=${copy.status}`)
  const both = [await dav("GET", `${MOUNT}/work/moved.txt`), await dav("GET", `${MOUNT}/work/copied.txt`)]
  check("COPY 后源与目标都在", both.every((r) => r.status === 200), both.map((r) => r.status).join("/"))
}

// --- 6. LOCK / UNLOCK ---

{
  const lock = await dav("LOCK", `${MOUNT}/work/put.txt`, {
    headers: { Timeout: "Second-60" },
    body: `<?xml version="1.0" encoding="utf-8"?>
<D:lockinfo xmlns:D="DAV:">
  <D:lockscope><D:exclusive/></D:lockscope>
  <D:locktype><D:write/></D:locktype>
  <D:owner><D:href>e2e</D:href></D:owner>
</D:lockinfo>`,
  })
  const token = lock.headers.get("Lock-Token") ?? ""
  check("LOCK 返回 Lock-Token", lock.status === 207 && token.startsWith("<"), `code=${lock.status} token=${token}`)

  const unlock = await dav("UNLOCK", `${MOUNT}/work/put.txt`, { headers: { "Lock-Token": token } })
  check("UNLOCK 成功", [200, 204].includes(unlock.status), `code=${unlock.status}`)
}

// --- 7. DELETE（这条是被 [Skip] 过的那条用例在真实服务器上的版本）---

{
  const del = await dav("DELETE", `${MOUNT}/work/put.txt`)
  noteTiming("DELETE", del.ms)
  check("DELETE 文件返回 204", del.status === 204, `code=${del.status}`)
  const after = await dav("GET", `${MOUNT}/work/put.txt`)
  check("DELETE 后 GET 为 404", after.status === 404, `code=${after.status}`)
  check("DELETE 后磁盘文件消失", !existsSync(onDisk("work/put.txt")), onDisk("work/put.txt"))

  const delDir = await dav("DELETE", `${MOUNT}/work`)
  check("DELETE 非空目录返回 204", delDir.status === 204, `code=${delDir.status}`)
  check("DELETE 目录递归清空磁盘", !existsSync(onDisk("work")), onDisk("work"))

  const missing = await dav("DELETE", `${MOUNT}/work/not-exists.txt`)
  check("DELETE 不存在的路径不悬挂（4xx）", missing.status >= 400 && missing.status < 500, `code=${missing.status}`)
}

// --- 8. 防悬挂护栏 ---

{
  const worst = slow.reduce((a, b) => (b.ms > a.ms ? b : a), { what: "-", ms: 0 })
  check("最慢请求 < 5s（慢挂起也算失败）", worst.ms < 5000, `${worst.what} ${worst.ms}ms`)
  const failures = results.filter((r) => !r.ok).length
  console.log(`\n${results.length - failures}/${results.length} 项通过`)
  process.exit(failures === 0 ? 0 : 1)
}
