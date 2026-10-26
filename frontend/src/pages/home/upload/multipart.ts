import { http, type Envelope } from "~/api/client"
import { useSettingsStore } from "~/stores/settings"
import type { SetUpload, Upload } from "./types"
import { calculateHash } from "./util"
import { StreamUpload } from "./stream"

/**
 * 分片上传客户端（对应旧前端 multipart.ts，协议语义保持一致）。
 *
 * 后端：`/api/fs/multipart/*`（init / chunk / status / complete / abort）。
 * 要点（全部镜像旧实现，勿随手“优化”掉）：
 * - init 幂等：同 path+size 的会话可续传，`received` 告知跳过哪些分片。
 * - 流控（429/409）不是失败：服务端窗口满时已替我们泊车等待，重试要有耐心。
 * - 传输层失败（信封外）返回 undefined 交给上层探测，绝不与服务端裁决混淆。
 * - complete 可能被 CDN 掐断：之后轮询 status 到 404（完成后会话即回收）。
 */

type MultipartState =
  | "receiving"
  | "completed"
  | "failed_retriable"
  | "failed_permanent"
  | "aborted"

export interface MultipartSnapshot {
  upload_id: string
  state: MultipartState
  attempt: number
  path: string
  size: number
  chunk_size: number
  total_chunks: number
  received: [number, number][]
  received_bytes: number
  frontier: number
  storage_progress: number
  error?: string
}

type SnapResp = Envelope & { code: number; message: string; data?: MultipartSnapshot }
type InitResp = SnapResp & { data?: MultipartSnapshot & { resumed?: boolean } }

// 并发分片数：服务端缓冲窗口是 8，3 个在途升序上传几乎不会触发流控
const INFLIGHT = 3
// 流控不算失败：服务端已泊车等待 ~10s，重试只在存储上行真的落后时发生，
// 宁可耐心也不要把大上传判死
const MAX_FLOW_RETRIES = 400

const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms))

// http 客户端把传输失败归一成 {code, message}（无 data 键），服务端信封
// 恒为 HTTP 200 且带 data 键——用「有没有 data 键」区分两种世界。
const mpRequest = async <T extends SnapResp>(req: Promise<unknown>): Promise<T | undefined> => {
  let resp: unknown
  try {
    resp = await req
  } catch {
    return undefined
  }
  const r = resp as Partial<Envelope> | undefined
  if (r && typeof r.code === "number" && "data" in r) {
    return r as T
  }
  if (r?.code === -1) {
    throw new Error(r.message || "canceled")
  }
  return undefined
}

export const MultipartUpload: Upload = async (
  uploadPath,
  file,
  setUpload,
  opts,
) => {
  const { password, overwrite, rapid } = opts
  // 建议后端按 multipart_chunk_size 设置切片；单分片上传要走 3 个请求
  // 而 Stream 只要 1 个，小文件静默回退
  const settings = useSettingsStore()
  const fallbackThreshold =
    Math.max(1, settings.getNumber("multipart_chunk_size", 10)) * 1024 * 1024
  if (file.size <= fallbackThreshold) {
    return StreamUpload(uploadPath, file, setUpload, opts)
  }

  const initHeaders: Record<string, string | number> = {
    "File-Path": encodeURIComponent(uploadPath),
    "X-File-Size": file.size,
    // 服务端会夹到 [1MB, 管理员上限] 并把实际 chunk_size 回显
    "X-Chunk-Size": fallbackThreshold,
    "Content-Type": file.type || "application/octet-stream",
    "Last-Modified": file.lastModified,
    Password: password,
    Overwrite: String(overwrite),
  }
  if (rapid) {
    setUpload("status", "hashing")
    setUpload("progress", 0)
    const { md5, sha1, sha256 } = await calculateHash(file, (p) => {
      setUpload("progress", p | 0)
    })
    initHeaders["X-File-Md5"] = md5
    initHeaders["X-File-Sha1"] = sha1
    initHeaders["X-File-Sha256"] = sha256
  }

  setUpload("status", "uploading")
  setUpload("progress", 0)
  // init 透明续传同 path+size 的中断会话；瞬时网络失败安全重试
  let initResp: InitResp | undefined
  for (let i = 0; ; i++) {
    initResp = await mpRequest<InitResp>(
      http.post("/fs/multipart/init", undefined, { headers: initHeaders }),
    )
    if (initResp) break
    if (i >= 2) throw new Error("multipart init failed: network error")
    await sleep(1000 * (i + 1))
  }
  if (initResp!.code !== 200) {
    throw new Error(initResp!.message || "multipart init failed")
  }
  const session = initResp!.data
  // 后端不支持分片（驱动无会话上传能力）返回 data:null —— 回退流式
  if (!session) {
    return StreamUpload(uploadPath, file, setUpload, opts)
  }
  const uploadId = session.upload_id
  const chunkSize = session.chunk_size
  const total = session.total_chunks
  const chunkLen = (idx: number) =>
    idx === total - 1 ? file.size - idx * chunkSize : chunkSize

  if (session.state === "completed") {
    // 秒传：一个分片没发就完成了
    setUpload("progress", 100)
    return
  }
  const have = new Set<number>()
  for (const [lo, hi] of session.received ?? []) {
    for (let i = lo; i <= hi; i++) have.add(i)
  }
  const missing: number[] = []
  for (let i = 0; i < total; i++) {
    if (!have.has(i)) missing.push(i)
  }

  let ackedBytes = session.received_bytes ?? 0
  const inflightLoaded: Record<number, number> = {}
  // 被拒分片的在途字节从总和里扣除直到重发；上报高水位让进度只会平台期
  // 不会回跳，速度也永不为负
  let peakDone = ackedBytes
  let lastTime = Date.now()
  let lastBytes = ackedBytes
  const report = () => {
    let inflight = 0
    for (const v of Object.values(inflightLoaded)) inflight += v
    peakDone = Math.max(peakDone, Math.min(ackedBytes + inflight, file.size))
    setUpload("progress", ((peakDone / file.size) * 100) | 0)
    const now = Date.now()
    if (now - lastTime >= 1000) {
      setUpload("speed", ((peakDone - lastBytes) / (now - lastTime)) * 1000)
      lastTime = now
      lastBytes = peakDone
    }
  }

  let completedEarly = false // 秒传在服务端把会话提前收尾了
  let fatal: Error | undefined

  const sendChunk = async (idx: number) => {
    const blob = file.slice(idx * chunkSize, idx * chunkSize + chunkLen(idx))
    let flowRetries = 0
    for (;;) {
      if (completedEarly || fatal) return
      const resp = await mpRequest<SnapResp>(
        http.put("/fs/multipart/chunk", blob, {
          headers: {
            "X-Upload-Id": uploadId,
            "X-Chunk-Index": idx,
            "Content-Type": "application/octet-stream",
          },
          onUploadProgress: (e) => {
            inflightLoaded[idx] = e.loaded ?? 0
            report()
          },
        }),
      )
      delete inflightLoaded[idx]
      if (resp && resp.code === 200) {
        ackedBytes += chunkLen(idx)
        if (resp.data?.state === "completed") {
          completedEarly = true
        }
        report()
        return
      }
      if (resp && resp.code !== 429 && resp.code !== 409) {
        throw new Error(resp.message || `chunk ${idx} rejected`)
      }
      if (!resp) {
        // 秒传可能在这个分片在途时完成了会话，早退响应在浏览器侧表现为
        // 网络错误——先问会话状态再认定网络真的挂了
        const st = await mpRequest<SnapResp>(
          http.get(`/fs/multipart/status?upload_id=${encodeURIComponent(uploadId)}`),
        )
        if (
          (st?.code === 200 && st.data?.state === "completed") ||
          st?.code === 404 // 完成的会话在成功后即被回收
        ) {
          completedEarly = true
          return
        }
        if (st?.code === 200 && st.data?.state.startsWith("failed")) {
          throw new Error(st.data.error || `upload failed (${st.data.state})`)
        }
      }
      flowRetries++
      if (flowRetries > MAX_FLOW_RETRIES) {
        throw new Error(`chunk ${idx} stalled: the storage cannot keep up`)
      }
      await sleep(resp ? 800 : Math.min(1200 * flowRetries, 4800))
    }
  }

  // refill 协议：failed_retriable 会话只在分片 0 到达时重生，先单发它——
  // 并发兄弟请求会与重生竞争被拒
  let startFrom = 0
  if (session.state === "failed_retriable" && missing.length > 0) {
    await sendChunk(missing[0])
    startFrom = 1
  }

  // 升序发送让服务端窗口持续流动，无需背压
  let nextIdx = startFrom
  const worker = async () => {
    for (;;) {
      if (completedEarly || fatal) return
      const i = nextIdx++
      if (i >= missing.length) return
      try {
        await sendChunk(missing[i])
      } catch (e) {
        fatal = e instanceof Error ? e : new Error(String(e))
        return
      }
    }
  }
  await Promise.all(
    Array.from({ length: Math.min(INFLIGHT, missing.length) || 1 }, worker),
  )
  if (fatal) {
    throw fatal
  }

  // 分片全部到齐，驱动还在往存储写
  setUpload("status", "backending")
  setUpload("speed", 0)
  let settled = false
  const completePromise: Promise<SnapResp | undefined> = mpRequest<SnapResp>(
    http.post("/fs/multipart/complete", undefined, {
      headers: { "X-Upload-Id": uploadId },
    }),
  )
    .catch(() => undefined)
    .finally(() => {
      settled = true
    })

  while (!settled) {
    await sleep(2000)
    if (settled) break
    const st = await mpRequest<SnapResp>(
      http.get(`/fs/multipart/status?upload_id=${encodeURIComponent(uploadId)}`),
    )
    if (st?.code === 200 && st.data?.state === "receiving") {
      setUpload("progress", st.data.storage_progress | 0)
    }
  }
  const fin = await completePromise
  if (fin) {
    if (fin.code !== 200) {
      throw new Error(fin.message || "multipart complete failed")
    }
    setUpload("progress", 100)
    return
  }
  // complete 请求没有回来——轮询会话直到终态
  for (;;) {
    await sleep(2000)
    const st = await mpRequest<SnapResp>(
      http.get(`/fs/multipart/status?upload_id=${encodeURIComponent(uploadId)}`),
    )
    if (!st) {
      continue // 驱动收尾期间的瞬时抖动——继续轮询
    }
    if (st.code === 404) {
      // 完成的会话在回收后立即移除；分片全部确认时这意味着上传成功
      setUpload("progress", 100)
      return
    }
    if (st.code !== 200) {
      throw new Error(st.message || "multipart status failed")
    }
    switch (st.data?.state) {
      case "completed":
        setUpload("progress", 100)
        return
      case "receiving":
        setUpload("progress", st.data.storage_progress | 0)
        continue
      default:
        throw new Error(st.data?.error || `upload failed (${st.data?.state})`)
    }
  }
}
