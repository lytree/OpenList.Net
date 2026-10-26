import { describe, expect, it, beforeEach, afterEach, vi } from "vitest"
import { strFromU8, strToU8, unzipSync } from "fflate"
import { canStreamSave, MAX_BLOB_BYTES, writeZip, type ZipEntry } from "./zip"

/**
 * `writeZip` 的验收方式：**用 fflate 自己的 unzipSync 反解**。
 * 断言「产出的字节能被标准实现读回来」，比断言内部 chunk 数量更贴近真实约束。
 */

/** 最小 fetch 替身：按 URL 返回一个单块 body 的响应。 */
function stubFetch(files: Record<string, string>) {
  globalThis.fetch = (async (input: unknown) => {
    const url = String(input)
    const content = files[url]
    if (content === undefined) return { ok: false, status: 404, body: null }
    const bytes = strToU8(content)
    let sent = false
    return {
      ok: true,
      status: 200,
      body: {
        getReader: () => ({
          read: async () => {
            if (sent) return { done: true, value: undefined }
            sent = true
            return { done: false, value: bytes }
          },
        }),
      },
    }
  }) as unknown as typeof fetch
}

function concat(chunks: Uint8Array[]): Uint8Array {
  const total = chunks.reduce((n, c) => n + c.length, 0)
  const out = new Uint8Array(total)
  let offset = 0
  for (const chunk of chunks) {
    out.set(chunk, offset)
    offset += chunk.byteLength
  }
  return out
}

describe("writeZip", () => {
  const originalFetch = globalThis.fetch

  beforeEach(() => {
    stubFetch({
      "/d/hello.txt": "hello world",
      "/d/sub/nested.md": "# nested",
    })
  })

  afterEach(() => {
    globalThis.fetch = originalFetch
  })

  it("产出可被 fflate 解回的 zip，且内容一致", async () => {
    const entries: ZipEntry[] = [
      { name: "hello.txt", url: "/d/hello.txt", size: 11, isDir: false },
      { name: "sub/", url: "", size: 0, isDir: true },
      { name: "sub/nested.md", url: "/d/sub/nested.md", size: 8, isDir: false },
    ]
    const chunks: Uint8Array[] = []
    await writeZip(entries, (chunk) => {
      chunks.push(chunk)
    })

    const files = unzipSync(concat(chunks))
    expect(Object.keys(files).sort()).toEqual(["hello.txt", "sub/", "sub/nested.md"])
    expect(strFromU8(files["hello.txt"])).toBe("hello world")
    expect(strFromU8(files["sub/nested.md"])).toBe("# nested")
    // 目录条目以 "/" 结尾且零字节（解压时据此还原空目录）
    expect(files["sub/"].length).toBe(0)
  })

  it("按条目顺序回调进度，终值等于条目数", async () => {
    const entries: ZipEntry[] = [
      { name: "a.txt", url: "/d/hello.txt", size: 11, isDir: false },
      { name: "b.txt", url: "/d/hello.txt", size: 11, isDir: false },
    ]
    const seen: number[] = []
    await writeZip(
      entries,
      () => {},
      (p) => seen.push(p.done),
    )
    expect(seen.length).toBeGreaterThan(0)
    expect(seen[seen.length - 1]).toBe(2)
  })

  it("单个文件 404 时抛出带文件名的错误（不让用户对着空包发呆）", async () => {
    const entries: ZipEntry[] = [
      { name: "missing.bin", url: "/d/missing.bin", size: 1, isDir: false },
    ]
    await expect(writeZip(entries, () => {})).rejects.toThrow(/missing\.bin/)
  })

  it("已取消的 signal 立刻抛 AbortError", async () => {
    const controller = new AbortController()
    controller.abort()
    await expect(
      writeZip(
        [{ name: "a.txt", url: "/d/hello.txt", size: 11, isDir: false }],
        () => {},
        undefined,
        controller.signal,
      ),
    ).rejects.toMatchObject({ name: "AbortError" })
  })
})

describe("环境能力探测", () => {
  it("jsdom 没有 showSaveFilePicker，走 Blob 回退", () => {
    expect(canStreamSave()).toBe(false)
  })

  it("Blob 上限是 1 GiB（避免把几 GB 塞进内存）", () => {
    expect(MAX_BLOB_BYTES).toBe(1024 * 1024 * 1024)
  })
})
