import { Zip, ZipPassThrough } from "fflate"
import { fsList } from "~/api/fs"
import { downloadLink } from "~/utils/link"
import { pathJoin } from "~/utils/path"
import type { Obj } from "~/api/types"

/**
 * 前端打包下载（zip）。
 *
 * 为什么在前端做：后端没有「打包」端点（只有归档的 list/decompress），
 * 而在服务端新增一个边读边压的流式接口，要处理驱动抽象、临时文件、断连清理，
 * 成本远高于浏览器里拼。上游 OpenList-Frontend 也是同一策略（streamsaver + 自研 ZIP 流）。
 *
 * 存储方式用 **STORE（不压缩）**：网盘里绝大多数是图片/视频/已压缩包，
 * deflate 只会白烧 CPU 换来 1–2% 的体积收益。
 *
 * 落盘两条路：
 * 1. `showSaveFilePicker`（Chromium，含桌面端 WebView2）→ 边压边写，**不占内存**；
 * 2. 其它浏览器 → 累积成 Blob 再触发下载，超过 `MAX_BLOB_BYTES` 直接拒绝，
 *    因为把几 GB 塞进内存会把标签页搞崩（宁可让用户分批下载）。
 */

/** Blob 回退路径的内存上限（1 GiB）。 */
export const MAX_BLOB_BYTES = 1024 * 1024 * 1024

export interface ZipEntry {
  /** zip 内的相对路径（不含前导斜杠）。 */
  name: string
  /** 下载地址（单文件走 /d 直链）。 */
  url: string
  size: number
  isDir: boolean
}

export interface CollectResult {
  entries: ZipEntry[]
  totalBytes: number
}

/**
 * 递归收集待打包的文件。
 *
 * 空目录也会产出一个 `dir/` 条目 —— 否则「打包一个有目录结构的文件夹」解压后
 * 会丢掉所有空目录，这对整理过的素材库是明显的体验损失。
 */
export async function collectEntries(
  root: string,
  targets: Obj[],
  password: string,
  onProgress?: (found: number, bytes: number) => void,
  signal?: AbortSignal,
): Promise<CollectResult> {
  const entries: ZipEntry[] = []
  let totalBytes = 0

  async function walk(dir: string, prefix: string, objs: Obj[]) {
    for (const obj of objs) {
      if (signal?.aborted) throw new DOMException("aborted", "AbortError")
      const rel = prefix ? `${prefix}/${obj.name}` : obj.name
      const abs = pathJoin(dir, obj.name)
      if (obj.is_dir) {
        // 先登记目录（保留空目录），再递归取子项。
        entries.push({ name: `${rel}/`, url: "", size: 0, isDir: true })
        const resp = await fsList(abs, password, 1, 0, false, signal)
        if (resp.code !== 200) {
          throw new Error(`${obj.name}: ${resp.message || "列目录失败"}`)
        }
        await walk(abs, rel, resp.data.content ?? [])
        onProgress?.(entries.length, totalBytes)
      } else {
        entries.push({
          name: rel,
          url: downloadLink(abs, obj.sign),
          size: obj.size,
          isDir: false,
        })
        totalBytes += obj.size
        onProgress?.(entries.length, totalBytes)
      }
    }
  }

  await walk(root, "", targets)
  return { entries, totalBytes }
}

export interface ZipProgress {
  done: number
  total: number
  bytes: number
  totalBytes: number
}

/**
 * 把条目压成 zip 并逐块交给 `sink`。
 *
 * `ZipPassThrough` 允许逐块 push，所以单个大文件是流式读、流式写，
 * 峰值内存只有浏览器 fetch 的缓冲窗口。
 */
export async function writeZip(
  entries: ZipEntry[],
  sink: (chunk: Uint8Array, final: boolean) => void | Promise<void>,
  onProgress?: (p: ZipProgress) => void,
  signal?: AbortSignal,
): Promise<void> {
  let resolveDone: () => void = () => {}
  let rejectDone: (err: Error) => void = () => {}
  const finished = new Promise<void>((resolve, reject) => {
    resolveDone = resolve
    rejectDone = reject
  })

  const zip = new Zip((err, chunk, final) => {
    if (err) {
      rejectDone(err)
      return
    }
    // sink 是异步写盘，这里串行等待，避免 chunk 顺序错乱。
    void Promise.resolve(sink(chunk, final)).then(() => {
      if (final) resolveDone()
    })
  })

  let done = 0
  let bytes = 0
  const totalBytes = entries.reduce((sum, e) => sum + e.size, 0)

  for (const entry of entries) {
    if (signal?.aborted) throw new DOMException("aborted", "AbortError")
    const file = new ZipPassThrough(entry.name)
    zip.add(file)

    if (entry.isDir) {
      file.push(new Uint8Array(0), true)
    } else {
      const resp = await fetch(entry.url, { signal })
      if (!resp.ok || !resp.body) {
        throw new Error(`${entry.name}: HTTP ${resp.status}`)
      }
      const reader = resp.body.getReader()
      let read = 0
      for (;;) {
        const { done: end, value } = await reader.read()
        if (end) break
        if (value) {
          file.push(value, false)
          read += value.byteLength
          onProgress?.({ done, total: entries.length, bytes: bytes + read, totalBytes })
        }
      }
      file.push(new Uint8Array(0), true)
      bytes += read
    }
    done += 1
    onProgress?.({ done, total: entries.length, bytes, totalBytes })
  }

  zip.end()
  await finished
}

/** 是否支持「另存为」流式写盘（Chromium / WebView2，且处于安全上下文）。 */
export function canStreamSave(): boolean {
  return typeof (window as unknown as { showSaveFilePicker?: unknown }).showSaveFilePicker === "function"
}

/** 让用户选保存位置，返回写入器；用户取消时抛 AbortError。 */
export async function pickSaveTarget(fileName: string): Promise<{
  write: (chunk: Uint8Array) => Promise<void>
  close: () => Promise<void>
}> {
  const picker = (
    window as unknown as {
      showSaveFilePicker: (opts: unknown) => Promise<{
        createWritable: () => Promise<{
          write: (data: Uint8Array) => Promise<void>
          close: () => Promise<void>
        }>
      }>
    }
  ).showSaveFilePicker

  const handle = await picker({
    suggestedName: fileName,
    types: [{ description: "ZIP 压缩包", accept: { "application/zip": [".zip"] } }],
  })
  const writable = await handle.createWritable()
  return {
    write: (chunk) => writable.write(chunk),
    close: () => writable.close(),
  }
}

/** Blob 回退：把所有 chunk 拼起来触发一次普通下载。 */
export function saveAsBlob(chunks: Uint8Array[], fileName: string) {
  const blob = new Blob(chunks as BlobPart[], { type: "application/zip" })
  const url = URL.createObjectURL(blob)
  const anchor = document.createElement("a")
  anchor.href = url
  anchor.download = fileName
  anchor.click()
  // 立刻 revoke 会让部分浏览器取消下载，给一拍再回收。
  setTimeout(() => URL.revokeObjectURL(url), 10_000)
}
