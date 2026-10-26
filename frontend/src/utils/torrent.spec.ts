import { createHash } from "node:crypto"
import { describe, expect, it } from "vitest"
import { BencodeError, decodeBencode, dictGet, nodeInt, nodeText } from "./bencode"
import {
  TorrentParseError,
  isTorrentName,
  parseTorrent,
  torrentMagnet,
  type ParsedTorrent,
} from "./torrent"

// ---------------------------------------------------------------------------
// 测试用的最小 bencode 编码器（仅测试需要「造」字节，生产代码只读不写）
// ---------------------------------------------------------------------------

type Encodable = number | string | Uint8Array | Encodable[] | { [key: string]: Encodable }

const encoder = new TextEncoder()

function encode(value: Encodable): Uint8Array {
  if (typeof value === "number") return encoder.encode(`i${Math.trunc(value)}e`)
  if (typeof value === "string") return encode(encoder.encode(value))
  // 用 isView 而不是 `instanceof Uint8Array`：jsdom 环境下 TextEncoder 来自 Node realm，
  // 与测试作用域里的全局构造函数不是同一个，instanceof 会失效并掉进字典分支递归爆炸。
  if (ArrayBuffer.isView(value)) {
    return concat([encoder.encode(`${value.length}:`), value])
  }
  if (Array.isArray(value)) {
    return concat([encoder.encode("l"), ...value.map(encode), encoder.encode("e")])
  }
  // 字典键必须按字节序升序（规范要求；解码器兼容乱序，但夹具应当合法）
  const keys = Object.keys(value).sort()
  return concat([
    encoder.encode("d"),
    ...keys.flatMap((key) => [encode(key), encode(value[key])]),
    encoder.encode("e"),
  ])
}

function concat(chunks: Uint8Array[]): Uint8Array {
  const total = chunks.reduce((sum, chunk) => sum + chunk.length, 0)
  const out = new Uint8Array(total)
  let offset = 0
  for (const chunk of chunks) {
    out.set(chunk, offset)
    offset += chunk.length
  }
  return out
}

/** 3 个分片、两个文件的种子；pieces 用递增字节（内容不校验，只要长度对）。 */
function multiFileInfo(): { [key: string]: Encodable } {
  const pieces = new Uint8Array(60)
  for (let i = 0; i < pieces.length; i += 1) pieces[i] = (i * 7) & 0xff
  return {
    name: "ol-v2-torrent",
    "piece length": 16384,
    pieces,
    files: [
      { length: 100, path: ["a.txt"] },
      { length: 2048, path: ["sub", "b.txt"] },
    ],
    private: 1,
  }
}

function multiFileTorrent(): Uint8Array {
  return encode({
    announce: "http://tracker.example/announce",
    "announce-list": [
      ["http://tracker.example/announce"],
      ["udp://tracker2.example:6969"],
    ],
    comment: "ol 测试种子",
    "created by": "ol-test",
    "creation date": 1730000000,
    info: multiFileInfo(),
    "x-cas": {
      file_md5: "AABBCCDD",
      slice_md5: "EEFF0011",
      slice_size: 1048576,
      cloud: "tianyi",
    },
  })
}

// ---------------------------------------------------------------------------

describe("decodeBencode", () => {
  it("解整数 / 字节串 / 列表 / 字典", () => {
    const node = decodeBencode(encode({ a: 42, b: ["x", "yy"], c: { d: -7 } }))
    expect(nodeInt(dictGet(node, "a"))).toBe(42)
    const list = dictGet(node, "b")
    expect(list?.type).toBe("list")
    if (list?.type === "list") expect(list.items.map(nodeText)).toEqual(["x", "yy"])
    expect(nodeInt(dictGet(dictGet(node, "c"), "d"))).toBe(-7)
  })

  it("记录节点的原始字节区间（info_hash 依赖它）", () => {
    const data = encode({ info: { name: "x" } })
    const node = decodeBencode(data)
    const info = dictGet(node, "info")
    expect(info).toBeDefined()
    if (!info) throw new Error("no info")
    // 区间必须正好是 `d4:name1:xe`
    expect(new TextDecoder().decode(data.subarray(info.start, info.end))).toBe("d4:name1:xe")
  })

  it("格式错误抛 BencodeError", () => {
    expect(() => decodeBencode(encoder.encode("i12"))).toThrow(BencodeError)
    expect(() => decodeBencode(encoder.encode("3:ab"))).toThrow(BencodeError)
    expect(() => decodeBencode(encoder.encode("d1:ae"))).toThrow(BencodeError)
    expect(() => decodeBencode(encoder.encode("i1eX"))).toThrow(BencodeError)
    expect(() => decodeBencode(encoder.encode("x"))).toThrow(BencodeError)
  })

  it("嵌套过深直接拒绝（不把栈打爆）", () => {
    const deep = concat([
      encoder.encode("l".repeat(200)),
      encoder.encode("e".repeat(200)),
    ])
    expect(() => decodeBencode(deep, 64)).toThrow(/nesting deeper/)
  })
})

describe("parseTorrent", () => {
  it("多文件种子的字段", () => {
    const torrent = parseTorrent(multiFileTorrent())
    expect(torrent.name).toBe("ol-v2-torrent")
    expect(torrent.files).toEqual([
      { path: "a.txt", size: 100 },
      { path: "sub/b.txt", size: 2048 },
    ])
    expect(torrent.totalSize).toBe(2148)
    expect(torrent.pieceLength).toBe(16384)
    expect(torrent.pieceCount).toBe(3)
    expect(torrent.trackers).toEqual([
      "http://tracker.example/announce",
      "udp://tracker2.example:6969",
    ])
    expect(torrent.comment).toBe("ol 测试种子")
    expect(torrent.createdBy).toBe("ol-test")
    expect(torrent.creationDate).toBe(1730000000)
    expect(torrent.isPrivate).toBe(true)
    expect(torrent.cas).toEqual({
      fileMd5: "AABBCCDD",
      sliceMd5: "EEFF0011",
      sliceSize: 1048576,
      cloud: "tianyi",
    })
  })

  it("info_hash 等于 info 字典原文的 SHA-1（用 Node crypto 交叉验证）", () => {
    const bytes = multiFileTorrent()
    const torrent = parseTorrent(bytes)
    const expected = createHash("sha1").update(encode(multiFileInfo())).digest("hex")
    expect(torrent.infoHash).toBe(expected)
    expect(torrent.infoHash).toMatch(/^[0-9a-f]{40}$/)
  })

  it("单文件种子包装成一条，且不含 CAS 时 cas 为 null", () => {
    const bytes = encode({
      announce: "http://t/announce",
      info: { name: "movie.iso", length: 4096, "piece length": 1024, pieces: new Uint8Array(80) },
    })
    const torrent = parseTorrent(bytes)
    expect(torrent.files).toEqual([{ path: "movie.iso", size: 4096 }])
    expect(torrent.totalSize).toBe(4096)
    expect(torrent.pieceCount).toBe(4)
    expect(torrent.isPrivate).toBe(false)
    expect(torrent.cas).toBeNull()
  })

  it("只有 file_md5 没有 slice_md5 不算 CAS（与后端 HasCasInfo 一致）", () => {
    const bytes = encode({
      info: { name: "x", length: 1, "piece length": 1, pieces: new Uint8Array(20) },
      "x-cas": { file_md5: "AA" },
    })
    expect(parseTorrent(bytes).cas).toBeNull()
  })

  it("缺 info / 缺 name / 空文件列表都报错", () => {
    expect(() => parseTorrent(encode({ announce: "x" }))).toThrow(TorrentParseError)
    expect(() => parseTorrent(encode({ info: { length: 1 } }))).toThrow(/missing torrent name/)
    expect(() => parseTorrent(encode({ info: { name: "n", files: [] } }))).toThrow(
      /no files|missing torrent name/,
    )
    expect(() => parseTorrent(encoder.encode("not bencode"))).toThrow(TorrentParseError)
  })
})

describe("torrentMagnet", () => {
  const sample: ParsedTorrent = {
    name: "ol 种子 & nice#1",
    totalSize: 0,
    pieceLength: 0,
    pieceCount: 0,
    infoHash: "0123456789abcdef0123456789abcdef01234567",
    files: [],
    trackers: ["http://t/announce", "udp://t2:6969"],
    comment: "",
    createdBy: "",
    creationDate: 0,
    isPrivate: false,
    cas: null,
  }

  it("拼接 xt / dn / tr 并转义特殊字符", () => {
    const magnet = torrentMagnet(sample)
    expect(magnet).toBe(
      "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567" +
        `&dn=${encodeURIComponent("ol 种子 & nice#1")}` +
        `&tr=${encodeURIComponent("http://t/announce")}` +
        `&tr=${encodeURIComponent("udp://t2:6969")}`,
    )
    // 转义后不应残留裸 & / #（会被下载器切断）
    expect(magnet.split("&dn=")[1].split("&tr=")[0]).not.toContain("#")
  })

  it("没有 tracker 时只出 xt（DHT 仍可用）", () => {
    expect(torrentMagnet({ ...sample, trackers: [] })).toBe(
      `magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=${encodeURIComponent(sample.name)}`,
    )
  })
})

describe("isTorrentName", () => {
  it("只认 .torrent 后缀", () => {
    expect(isTorrentName("a.torrent")).toBe(true)
    expect(isTorrentName("A.TORRENT")).toBe(true)
    expect(isTorrentName("a.torrent.bak")).toBe(false)
    expect(isTorrentName("torrent")).toBe(false)
  })
})
