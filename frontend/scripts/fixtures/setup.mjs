/**
 * 回归前置：给隔离后端建挂载 / 建媒体库并跑完一次扫描。
 *
 * 这些脚本里有一半自己会 `POST /admin/storage/create`（preview-extra / media-manage /
 * dimensions），另一半默认「挂载已经存在，库已经扫好」——本文件补的就是后者，好让
 * `run-regression.sh` 每一轮都从同一份干净状态起步，而不是靠人手点。
 *
 * 用法：
 *   BASE=http://127.0.0.1:5001 node scripts/fixtures/setup.mjs mount /e2e G:/Share/ol-v2-prev
 *   BASE=http://127.0.0.1:5001 node scripts/fixtures/setup.mjs library "E2E Movies" /e2e
 */

const BASE = process.env.BASE || "http://127.0.0.1:5001"
const PASS = process.env.ADMIN_PASS || "e2e12345"
const [command, ...args] = process.argv.slice(2)

const log = (...a) => console.log("[setup]", ...a)

// `Connection: close` 不是洁癖：Windows 上 undici 的 keep-alive 句柄若在进程退出时还挂着，
// 会撞 `Assertion failed: !(handle->flags & UV_HANDLE_CLOSING)`（libuv 的已知断言），
// 进程以非 0 退出码结束 —— 调用方会以为「建挂载失败」，而实际上一切正常。
const NO_KEEPALIVE = { Connection: "close" }

const token = await fetch(`${BASE}/api/auth/login`, {
  method: "POST",
  headers: { "Content-Type": "application/json", ...NO_KEEPALIVE },
  body: JSON.stringify({ username: "admin", password: PASS }),
})
  .then((r) => r.json())
  .then((body) => {
    if (!body?.data?.token) throw new Error(`login failed: ${JSON.stringify(body)}`)
    return body.data.token
  })

async function api(method, path, body) {
  const resp = await fetch(`${BASE}/api${path}`, {
    method,
    headers: { "Content-Type": "application/json", Authorization: token, ...NO_KEEPALIVE },
    body: body === undefined ? undefined : JSON.stringify(body),
  })
  const text = await resp.text()
  try {
    return JSON.parse(text)
  } catch {
    throw new Error(`${method} ${path} -> ${resp.status} ${text.slice(0, 120)}`)
  }
}

function expectOk(body, what) {
  if (body?.code !== 200) throw new Error(`${what} failed: ${JSON.stringify(body).slice(0, 200)}`)
  return body.data
}

/** 幂等建挂载：同名 mount_path 先删后建（Local 驱动的 addition 是**字符串化 JSON**）。 */
async function mount(mountPath, rootFolder) {
  const list = expectOk(await api("get", "/admin/storage/list"), "storage/list")
  for (const storage of list.content ?? []) {
    if (storage.mount_path === mountPath) {
      expectOk(await api("post", `/admin/storage/delete?id=${storage.id}`), "storage/delete")
      log(`removed stale mount ${mountPath}`)
    }
  }
  expectOk(
    await api("post", "/admin/storage/create", {
      mount_path: mountPath,
      driver: "Local",
      addition: JSON.stringify({ root_folder_path: rootFolder }),
      order: 0,
      remark: "e2e",
    }),
    "storage/create",
  )

  // 立刻回读一次：create 返回 200 只证明「请求被接受」，不证明挂载真的可读。
  // 这一步把「挂载没落库 / 根目录写错 / 驱动拒绝」变成这里的一句明确报错，
  // 而不是让后续脚本在空目录上跑出 20 个莫名其妙的 FAIL。
  const readBack = await api("post", "/fs/list", { path: mountPath, page: 1, per_page: 5 })
  if (readBack?.code !== 200) {
    throw new Error(`mount ${mountPath} is not readable: ${JSON.stringify(readBack).slice(0, 200)}`)
  }
  log(`mount ${mountPath} -> ${rootFolder} (${readBack.data?.total ?? 0} entries, readable)`)
}

/** 幂等建媒体库并等待扫描结束（扫描是 fire-and-forget，用 /media/tasks 判终止）。 */
async function library(name, vfsPath) {
  const libraries = expectOk(await api("get", "/media/libraries"), "media/libraries")
  for (const lib of libraries ?? []) {
    if (lib.name === name) {
      expectOk(await api("post", `/media/libraries/${lib.id}/delete`), "libraries/delete")
      log(`removed stale library ${name}`)
    }
  }
  const created = expectOk(
    await api("post", "/media/libraries", { name, path: vfsPath, type: 2, auto_scan: false }),
    "libraries/create",
  )
  expectOk(await api("post", `/media/libraries/${created.id}/scan`), "libraries/scan")

  for (let i = 0; i < 600; i += 1) {
    const tasks = expectOk(await api("get", "/media/tasks"), "media/tasks")
    const task = (tasks ?? []).find((t) => t.library_id === created.id)
    if (task && !task.is_running && task.state !== "idle") {
      if (task.state !== "completed") throw new Error(`scan ended as ${task.state}`)
      const items = expectOk(
        await api("get", `/media/items?library_id=${created.id}&per_page=1`),
        "media/items",
      )
      log(`library ${name} (#${created.id}) scanned: ${items.total} items`)
      return
    }
    await new Promise((r) => setTimeout(r, 100))
  }
  throw new Error("scan did not finish in 60s")
}

try {
  switch (command) {
    case "mount":
      await mount(args[0], args[1])
      break
    case "library":
      await library(args[0], args[1])
      break
    default:
      console.error("usage: setup.mjs mount <mountPath> <rootFolder> | library <name> <vfsPath>")
      process.exit(2)
  }
} catch (error) {
  console.error(`[setup] ${error.message ?? error}`)
  process.exitCode = 1
}
