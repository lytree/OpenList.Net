/**
 * Bencode 解码（只读，够解析 .torrent 用）。
 *
 * 关键设计：每个节点都带 `[start, end)` 的**原始字节区间**。
 * 这不是为了好看 —— torrent 的 `info_hash` 是「info 字典的 bencoded 原始字节」的 SHA-1，
 * 一旦解析后又重新编码，字节顺序/整数格式稍有不同（例如 `i0e` 编码成 `i0e` 但字典键序被重排）
 * 就会算出完全不同的哈希，进而生成一个下不动的磁力链接。
 * 保留区间后直接 `slice` 原缓冲区，哈希天然正确。
 *
 * 为什么不用现成的 bencode 库：只需要「解一次 + 取区间」，而这个逻辑不到 200 行；
 * 引入依赖省不了多少代码，却多一份供应链与体积成本。
 */

/** 解码失败（格式错误、越界、嵌套过深）。 */
export class BencodeError extends Error {
  constructor(message: string) {
    super(message)
    this.name = "BencodeError"
  }
}

export interface BencodeInt {
  type: "int"
  start: number
  end: number
  value: number
}

export interface BencodeBytes {
  type: "bytes"
  start: number
  end: number
  /** 指向输入缓冲区的视图，不做拷贝（`pieces` 可能有几十 MB）。 */
  value: Uint8Array
}

export interface BencodeList {
  type: "list"
  start: number
  end: number
  items: BencodeNode[]
}

export interface BencodeDict {
  type: "dict"
  start: number
  end: number
  entries: Map<string, BencodeNode>
}

export type BencodeNode = BencodeInt | BencodeBytes | BencodeList | BencodeDict

const COLON = 0x3a
const LOWER_E = 0x65
const LOWER_I = 0x69
const LOWER_L = 0x6c
const LOWER_D = 0x64

/** 把「字节串」节点的内容按 UTF-8 解出来（容忍非法字节，用替换字符）。 */
const utf8 = new TextDecoder("utf-8")

/**
 * 解码一段 bencode 数据，返回根节点。
 *
 * @param maxDepth 最大嵌套层数。恶意文件可以用几千层 `l`/`d` 把递归打爆，
 *                 这里默认 64（torrent 实际嵌套不超过 5 层）。
 */
export function decodeBencode(data: Uint8Array, maxDepth = 64): BencodeNode {
  let pos = 0

  function peek(): number {
    if (pos >= data.length) throw new BencodeError(`unexpected end of data at ${pos}`)
    return data[pos]
  }

  /** 读 `<len>:<bytes>` 形式的字节串，返回其内容视图（同时记录整体区间）。 */
  function readBytes(): BencodeBytes {
    const start = pos
    while (pos < data.length && data[pos] !== COLON) {
      const c = data[pos]
      if (c < 0x30 || c > 0x39) {
        throw new BencodeError(`invalid string length at ${pos}`)
      }
      pos += 1
    }
    if (pos >= data.length) throw new BencodeError(`unterminated string length at ${start}`)
    const length = Number(utf8.decode(data.subarray(start, pos)))
    if (!Number.isSafeInteger(length) || length < 0) {
      throw new BencodeError(`invalid string length "${length}" at ${start}`)
    }
    pos += 1 // 跳过 ':'
    const valueStart = pos
    const valueEnd = valueStart + length
    if (valueEnd > data.length) {
      throw new BencodeError(`string of length ${length} overflows data at ${start}`)
    }
    pos = valueEnd
    return { type: "bytes", start, end: valueEnd, value: data.subarray(valueStart, valueEnd) }
  }

  function readNode(depth: number): BencodeNode {
    if (depth > maxDepth) throw new BencodeError(`nesting deeper than ${maxDepth}`)
    const start = pos
    const c = peek()

    if (c === LOWER_I) {
      pos += 1
      const digitsStart = pos
      while (pos < data.length && data[pos] !== LOWER_E) pos += 1
      if (pos >= data.length) throw new BencodeError(`unterminated integer at ${start}`)
      const raw = utf8.decode(data.subarray(digitsStart, pos))
      pos += 1
      if (!/^-?\d+$/.test(raw)) throw new BencodeError(`invalid integer "${raw}" at ${start}`)
      const value = Number(raw)
      if (!Number.isSafeInteger(value)) throw new BencodeError(`integer out of range at ${start}`)
      return { type: "int", start, end: pos, value }
    }

    if (c === LOWER_L) {
      pos += 1
      const items: BencodeNode[] = []
      while (peek() !== LOWER_E) items.push(readNode(depth + 1))
      pos += 1
      return { type: "list", start, end: pos, items }
    }

    if (c === LOWER_D) {
      pos += 1
      const entries = new Map<string, BencodeNode>()
      while (peek() !== LOWER_E) {
        const key = readBytes()
        // 键必须是字节串；重复键按 bencode 惯例「后者覆盖」
        entries.set(utf8.decode(key.value), readNode(depth + 1))
      }
      pos += 1
      return { type: "dict", start, end: pos, entries }
    }

    if (c >= 0x30 && c <= 0x39) return readBytes()

    throw new BencodeError(`unexpected byte 0x${c.toString(16)} at ${pos}`)
  }

  const root = readNode(0)
  if (pos !== data.length) {
    throw new BencodeError(`${data.length - pos} trailing bytes after root value`)
  }
  return root
}

/** 节点对应的原始字节（用于计算 info_hash 等「按原文」的哈希）。 */
export function nodeBytes(data: Uint8Array, node: BencodeNode): Uint8Array {
  return data.subarray(node.start, node.end)
}

/** 取字节串的文本；节点类型不对或缺失时返回空串。 */
export function nodeText(node: BencodeNode | undefined): string {
  return node?.type === "bytes" ? utf8.decode(node.value) : ""
}

/** 取整数；节点类型不对或缺失时返回 `fallback`。 */
export function nodeInt(node: BencodeNode | undefined, fallback = 0): number {
  return node?.type === "int" ? node.value : fallback
}

/** 取字典的某个键。 */
export function dictGet(node: BencodeNode | undefined, key: string): BencodeNode | undefined {
  return node?.type === "dict" ? node.entries.get(key) : undefined
}

/** 取列表节点；类型不对时返回空数组。 */
export function nodeList(node: BencodeNode | undefined): BencodeNode[] {
  return node?.type === "list" ? node.items : []
}
