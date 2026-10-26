import { computed, ref, watch, type Ref } from "vue"

/**
 * 文本类预览器的取数逻辑（markdown / text / html 共用）。
 *
 * 关键点是**不整份下载**：
 * - 小文件全量读；
 * - 大文件读流的前 `MAX_BYTES` 就 `cancel()`，即使后端忽略 Range 也不会把
 *   几百 MB 的日志拉进内存；
 * - 超过 `HARD_LIMIT` 干脆不读，直接把「请下载查看」交给调用方。
 *
 * 编码用浏览器原生 `TextDecoder`：utf-8/gbk/big5/shift_jis 都是内置支持，
 * 不引入 iconv，编译体积零增长（旧前端为此引入了 jschardet + iconv-lite）。
 */

export type TextEncoding = "utf-8" | "gbk" | "big5" | "shift_jis"

export const TEXT_ENCODINGS: { value: TextEncoding; label: string }[] = [
  { value: "utf-8", label: "UTF-8" },
  { value: "gbk", label: "GBK / GB18030" },
  { value: "big5", label: "Big5" },
  { value: "shift_jis", label: "Shift_JIS" },
]

/** 全量读取上限：再大改走「只读开头」。 */
const FULL_READ_LIMIT = 4 * 1024 * 1024
/** 大文件只读开头这么多字节。 */
const PARTIAL_BYTES = 1024 * 1024
/** 超过这个体积不做任何读取，直接提示下载。 */
export const HARD_LIMIT = 64 * 1024 * 1024

async function readUpTo(url: string, maxBytes: number): Promise<{ bytes: Uint8Array; truncated: boolean }> {
  const resp = await fetch(url)
  if (!resp.ok) throw new Error(`${resp.status} ${resp.statusText}`)

  if (!resp.body) {
    const all = new Uint8Array(await resp.arrayBuffer())
    return all.length > maxBytes
      ? { bytes: all.slice(0, maxBytes), truncated: true }
      : { bytes: all, truncated: false }
  }

  const reader = resp.body.getReader()
  const chunks: Uint8Array[] = []
  let received = 0
  let truncated = false
  for (;;) {
    const { done, value } = await reader.read()
    if (done) break
    if (!value) continue
    chunks.push(value)
    received += value.length
    if (received >= maxBytes) {
      truncated = true
      await reader.cancel().catch(() => undefined)
      break
    }
  }

  const total = Math.min(received, maxBytes)
  const bytes = new Uint8Array(total)
  let offset = 0
  for (const chunk of chunks) {
    if (offset >= total) break
    const slice = chunk.length > total - offset ? chunk.subarray(0, total - offset) : chunk
    bytes.set(slice, offset)
    offset += slice.length
  }
  return { bytes, truncated }
}

export function useTextContent(src: Ref<string>, size: Ref<number>) {
  const text = ref("")
  const loading = ref(false)
  const error = ref("")
  const truncated = ref(false)
  const tooLarge = ref(false)
  const encoding = ref<TextEncoding>("utf-8")

  /** 每次加载的序号：编码快速切换时丢弃过期响应，避免旧结果覆盖新结果。 */
  let seq = 0
  /**
   * 已下载的字节缓存：切换编码只重新 decode，不重复请求。
   * 键是 URL，文件切换（URL 变化）自然失效。
   */
  let cache: { url: string; bytes: Uint8Array; truncated: boolean } | null = null

  async function load() {
    const current = ++seq
    error.value = ""
    tooLarge.value = false

    if (!src.value) {
      text.value = ""
      error.value = "empty src"
      return
    }
    if (size.value > HARD_LIMIT) {
      text.value = ""
      tooLarge.value = true
      return
    }

    if (cache && cache.url === src.value) {
      text.value = new TextDecoder(encoding.value).decode(cache.bytes)
      truncated.value = cache.truncated
      return
    }

    text.value = ""
    truncated.value = false
    loading.value = true
    try {
      const limit = size.value > FULL_READ_LIMIT ? PARTIAL_BYTES : Number.MAX_SAFE_INTEGER
      const result = await readUpTo(src.value, limit)
      if (current !== seq) return
      cache = { url: src.value, bytes: result.bytes, truncated: result.truncated }
      text.value = new TextDecoder(encoding.value).decode(result.bytes)
      truncated.value = result.truncated
    } catch (e) {
      if (current !== seq) return
      error.value = String((e as Error)?.message ?? e)
    } finally {
      if (current === seq) loading.value = false
    }
  }

  watch([src, encoding], () => void load(), { immediate: true })

  return {
    text,
    loading,
    error,
    truncated,
    tooLarge,
    encoding,
    reload: load,
    /** 已加载部分的字符数，用于底部统计。 */
    chars: computed(() => text.value.length),
  }
}
