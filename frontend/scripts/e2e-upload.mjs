/**
 * 上传管线 e2e（API 级）。
 *
 * 单脚本完成：启动后端（CWD 必须是 src/OpenList.Server，DB 按相对路径
 * `data/data.db` 解析）→ 登录 → 建隔离目录 → 依次验证
 *   1) /fs/put 流式上传（小文件）
 *   2) /fs/multipart/* 分片上传（>10MB，含分片顺序、状态轮询语义）
 *   3) /fs/form 表单上传
 *   4) 分片续传：init 后丢弃一个分片再 complete 之外的补发
 * → fs/get 校验大小 → 清理隔离目录 → 杀后端。
 *
 * 用法：
 *   node scripts/e2e-upload.mjs                      # 自起后端（CWD=src/OpenList.Server，用其 data/data.db）
 *   BASE=http://127.0.0.1:5055 node scripts/e2e-upload.mjs   # 打一个已在跑的隔离后端（M5 回归用这条）
 *
 * 给了 BASE 就**不再自己起后端**：回归编排器每个轮次都有自己的临时库，
 * 这个脚本若照旧 `spawn` 一个 5244，会写进开发库（src/OpenList.Server/data）。
 */
import { spawn } from "node:child_process"
import crypto from "node:crypto"

const EXTERNAL_BASE = process.env.BASE || ""
const BASE = EXTERNAL_BASE || "http://127.0.0.1:5244"
const ADMIN_PASS = process.env.ADMIN_PASS || "admin"
const SERVER_DIR = "F:/Code/Dotnet/OpenList.Net/src/OpenList.Server"

let failures = 0
const ok = (cond, label) => {
  console.log(`${cond ? "PASS" : "FAIL"}  ${label}`)
  if (!cond && typeof cond === "object") console.log("      detail:", cond)
  if (!cond) failures++
}

const api = async (method, url, { token, headers = {}, body } = {}) => {
  const resp = await fetch(`${BASE}/api${url}`, {
    method,
    headers: {
      // 字符串 body 一律是 JSON；二进制用 Uint8Array，表单用 FormData（fetch 自动设）
      ...(typeof body === "string" ? { "Content-Type": "application/json" } : {}),
      ...(token ? { Authorization: token } : {}),
      ...headers,
    },
    body,
  })
  const text = await resp.text()
  try {
    return JSON.parse(text)
  } catch {
    console.error(`NON-JSON from ${method} /api${url} [${resp.status}]:`, text.slice(0, 300))
    return { code: -2, message: `non-json response (${resp.status})` }
  }
}

const sleep = (ms) => new Promise((r) => setTimeout(r, r))

async function waitBackend(proc) {
  for (let i = 0; i < 120; i++) {
    try {
      const r = await fetch(`${BASE}/api/public/settings`)
      if (r.ok) return
    } catch { /* not yet */ }
    if (proc.exitCode !== null) throw new Error("backend exited early")
    await new Promise((r) => setTimeout(r, 500))
  }
  throw new Error("backend did not start in time")
}

async function main() {
  // ---- 启动后端（除非调用方已经给了一个）----
  // 直接跑 DLL 不读 launchSettings，必须显式给 URL（dotnet run 才会用 5244）
  const backend = EXTERNAL_BASE
    ? null
    : spawn("dotnet", ["bin/Debug/net10.0/OpenList.Server.dll"], {
        cwd: SERVER_DIR,
        env: {
          ...process.env,
          ASPNETCORE_ENVIRONMENT: "Development",
          ASPNETCORE_URLS: "http://127.0.0.1:5244",
        },
        stdio: ["ignore", "pipe", "pipe"],
      })
  backend?.stdout.on("data", (d) => process.stdout.write(`[be] ${d}`))
  backend?.stderr.on("data", (d) => process.stderr.write(`[be] ${d}`))
  try {
    if (backend) {
      await waitBackend(backend)
    } else {
      const r = await fetch(`${BASE}/api/public/settings`)
      ok(r.ok, `external backend reachable at ${BASE}`)
    }

    // ---- 登录 ----
    const login = await api("POST", "/auth/login", {
      body: JSON.stringify({ username: "admin", password: ADMIN_PASS }),
    })
    ok(login.code === 200 && login.data?.token, "login admin")
    const token = login.data.token

    // ---- 找一个本地存储挂载目录 ----
    const storages = await api("GET", "/admin/storage/list", { token })
    ok(storages.code === 200, "storage list")
    const local = (storages.data?.content ?? []).find(
      (s) => s.driver === "Local" || s.driver?.includes("Local"),
    )
    ok(!!local, `found local storage: ${local?.mount_path ?? "none"}`)

    // ---- 建隔离目录 ----
    const testDir = `${local.mount_path.replace(/\/$/, "")}/e2e-upload-${Date.now()}`
    const mk = await api("POST", "/fs/mkdir", { token, body: JSON.stringify({ path: testDir }) })
    ok(mk.code === 200, `mkdir ${testDir}`)

    // ---- 1) 流式上传小文件 ----
    const small = crypto.randomBytes(64 * 1024)
    const putResp = await api("PUT", "/fs/put", {
      token,
      headers: {
        "File-Path": encodeURIComponent(`${testDir}/small.bin`),
        "Content-Type": "application/octet-stream",
        Overwrite: "false",
      },
      body: small,
    })
    ok(putResp.code === 200, "stream upload /fs/put small file")

    // ---- 2) 表单上传 ----
    const form = new FormData()
    form.append("file", new Blob([crypto.randomBytes(32 * 1024)]), "form.txt")
    const formResp = await api("PUT", "/fs/form", {
      token,
      headers: {
        "File-Path": encodeURIComponent(`${testDir}/form.txt`),
        Overwrite: "false",
      },
      body: form,
    })
    ok(formResp.code === 200, "form upload /fs/form")

    // ---- 3) 分片上传 25MB（3 片：10+10+5）----
    const big = crypto.randomBytes(25 * 1024 * 1024)
    const bigHash = crypto.createHash("md5").update(big).digest("hex")
    const chunkSize = 10 * 1024 * 1024
    const init = await api("POST", "/fs/multipart/init", {
      token,
      headers: {
        "File-Path": encodeURIComponent(`${testDir}/big.bin`),
        "X-File-Size": String(big.length),
        "X-Chunk-Size": String(chunkSize),
        "Content-Type": "application/octet-stream",
        Overwrite: "false",
      },
    })
    ok(init.code === 200 && init.data?.upload_id, `multipart init -> ${JSON.stringify(init).slice(0, 200)}`)
    const session = init.data
    ok(session.chunk_size === chunkSize, `chunk_size echoed (${session.chunk_size})`)
    ok(session.total_chunks === 3, `total_chunks = 3 (${session.total_chunks})`)

    // 传分片 0、1；跳过分片 2 再验证 status/received 语义
    for (const idx of [0, 1]) {
      const blob = big.subarray(idx * chunkSize, (idx + 1) * chunkSize)
      const r = await api("PUT", "/fs/multipart/chunk", {
        token,
        headers: {
          "X-Upload-Id": session.upload_id,
          "X-Chunk-Index": String(idx),
          "Content-Type": "application/octet-stream",
        },
        body: blob,
      })
      ok(r.code === 200, `chunk ${idx} accepted -> ${JSON.stringify(r).slice(0, 200)}`)
    }
    const mid = await api("GET", `/fs/multipart/status?upload_id=${session.upload_id}`, { token })
    ok(
      mid.code === 200 && JSON.stringify(mid.data.received) === "[[0,1]]",
      `status received=[[0,1]] (${JSON.stringify(mid.data?.received)})`,
    )

    // 补发最后一个分片
    const tail = big.subarray(2 * chunkSize)
    const r2 = await api("PUT", "/fs/multipart/chunk", {
      token,
      headers: {
        "X-Upload-Id": session.upload_id,
        "X-Chunk-Index": "2",
        "Content-Type": "application/octet-stream",
      },
      body: tail,
    })
    ok(r2.code === 200, "chunk 2 accepted")

    // ---- 4) complete + 轮询到完成 ----
    let completed = false
    const complete = await api("POST", "/fs/multipart/complete", {
      token,
      headers: { "X-Upload-Id": session.upload_id },
    })
    ok(complete.code === 200, "multipart complete accepted")
    if (complete.data?.state === "completed") {
      completed = true
    } else {
      for (let i = 0; i < 60; i++) {
        await new Promise((r) => setTimeout(r, 1000))
        const st = await api("GET", `/fs/multipart/status?upload_id=${session.upload_id}`, { token })
        if (st.code === 200 && st.data?.state === "completed") { completed = true; break }
        if (st.code === 404) { completed = true; break } // 完成后回收
        if (st.code === 200 && st.data?.state?.startsWith("failed")) break
      }
    }
    ok(completed, "multipart session reached completed")

    // ---- 校验落盘 ----
    const getBig = await api("POST", "/fs/get", { token, body: JSON.stringify({ path: `${testDir}/big.bin` }) })
    ok(getBig.code === 200 && getBig.data?.size === big.length, `big.bin size=${getBig.data?.size}`)
    const getSmall = await api("POST", "/fs/get", { token, body: JSON.stringify({ path: `${testDir}/small.bin` }) })
    ok(getSmall.code === 200 && getSmall.data?.size === small.length, "small.bin size ok")
    const getForm = await api("POST", "/fs/get", { token, body: JSON.stringify({ path: `${testDir}/form.txt` }) })
    ok(getForm.code === 200 && getForm.data?.size === 32 * 1024, "form.txt size ok")

    // md5 校验走 raw_url 下载（也顺便验证下载直链可用）
    if (getBig.data?.raw_url) {
      const dl = await fetch(getBig.data.raw_url)
      const buf = Buffer.from(await dl.arrayBuffer())
      ok(
        crypto.createHash("md5").update(buf).digest("hex") === bigHash,
        `big.bin md5 matches after download (${buf.length} bytes)`,
      )
    }

    // ---- 重复 init 幂等/续传语义：同 path+size 应复用或正常新建 ----
    const reinit = await api("POST", "/fs/multipart/init", {
      token,
      headers: {
        "File-Path": encodeURIComponent(`${testDir}/another.bin`),
        "X-File-Size": String(30 * 1024 * 1024),
        "X-Chunk-Size": String(chunkSize),
      },
    })
    ok(reinit.code === 200 && reinit.data?.upload_id, "re-init new session ok")
    await api("POST", "/fs/multipart/abort", {
      token,
      headers: { "X-Upload-Id": reinit.data.upload_id },
    })

    // ---- 清理 ----
    const rm = await api("POST", "/fs/remove", {
      token,
      body: JSON.stringify({ dir: local.mount_path, names: [testDir.split("/").pop()] }),
    })
    ok(rm.code === 200, "cleanup test dir")
  } finally {
    backend?.kill()
  }

  console.log(failures === 0 ? "\nALL PASS" : `\n${failures} FAILURES`)
  process.exit(failures === 0 ? 0 : 1)
}

main().catch((e) => {
  console.error(e)
  process.exit(1)
})
