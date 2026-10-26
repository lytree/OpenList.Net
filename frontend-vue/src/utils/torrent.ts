/**
 * .torrent 解析（纯前端）。
 *
 * 为什么在浏览器里解析而不是调 `/api/fs/torrent/parse`：
 *
 * 1. 那个端点要登录，而公开目录/分享页里的种子同样需要预览；
 * 2. 预览器拿到的就是文件原始字节（`props.src`），再让服务端读一遍纯属多余往返；
 * 3. 后端解析要求 base64 上传整个文件，大种子（含 CAS 信息的常有几十 MB 的 pieces）会翻倍放大。
 *
 * 字段与后端 `TorrentParsedResp` 对齐，方便将来把「秒传」接到同一份数据上。
 */
import {
  BencodeError,
  decodeBencode,
  dictGet,
  nodeBytes,
  nodeInt,
  nodeList,
  nodeText,
  type BencodeNode,
} from "./bencode"
import { sha1Hex } from "./sha1"

/** 种子内的一个文件。单文件种子也会包装成一条（path 即种子名）。 */
export interface TorrentFileEntry {
  /** 相对路径，多文件模式带目录（`S01/E01.mkv`），单文件模式就是文件名。 */
  path: string
  size: number
}

/** 天翼云盘 CAS 秒传扩展（`x-cas` 字典）。 */
export interface TorrentCasInfo {
  fileMd5: string
  sliceMd5: string
  sliceSize: number
  cloud: string
}

export interface ParsedTorrent {
  name: string
  totalSize: number
  pieceLength: number
  pieceCount: number
  /** 小写十六进制 info_hash。 */
  infoHash: string
  files: TorrentFileEntry[]
  /** `announce` + 拍平后的 `announce-list`，去重后按出现顺序。 */
  trackers: string[]
  comment: string
  createdBy: string
  /** Unix 秒；0 表示未填。 */
  creationDate: number
  /** `private=1`（私有种子，只能靠 tracker 找 peer）。 */
  isPrivate: boolean
  cas: TorrentCasInfo | null
}

/** 解析失败。`message` 是给「技术细节」那一行用的，UI 另有本地化文案。 */
export class TorrentParseError extends Error {
  constructor(message: string) {
    super(message)
    this.name = "TorrentParseError"
  }
}

/**
 * 解析 .torrent 字节。
 *
 * 只做**结构**校验（必须有 info 字典、名称、以及 files/length 之一）；
 * 分片哈希是否自洽不做校验 —— 那要重算整份数据，预览阶段得不偿失。
 */
export function parseTorrent(bytes: Uint8Array): ParsedTorrent {
  let root: BencodeNode
  try {
    root = decodeBencode(bytes)
  } catch (error) {
    if (error instanceof BencodeError) {
      throw new TorrentParseError(`not a valid bencode file: ${error.message}`)
    }
    throw error
  }

  if (root.type !== "dict") throw new TorrentParseError("root is not a dictionary")
  const info = dictGet(root, "info")
  if (!info || info.type !== "dict") throw new TorrentParseError("missing info dictionary")

  const name = nodeText(info.entries.get("name")).trim()
  if (!name) throw new TorrentParseError("missing torrent name")

  const pieceLength = nodeInt(info.entries.get("piece length"))
  const pieces = info.entries.get("pieces")
  const pieceCount =
    pieces?.type === "bytes" ? Math.floor(pieces.value.length / 20) : 0

  // ---- 文件列表 -------------------------------------------------------
  // 单/多文件的判定看**键是否存在**，而不是 `files` 是否非空：
  // 空 `files` 数组是坏种子（不是「单文件种子」），当成单文件会凭空造出一个 0 字节条目。
  const files: TorrentFileEntry[] = []
  const fileNodes = nodeList(info.entries.get("files"))
  const hasFilesKey = info.entries.has("files")
  const hasLengthKey = info.entries.has("length")
  if (hasFilesKey) {
    for (const node of fileNodes) {
      if (node.type !== "dict") continue
      const size = nodeInt(node.entries.get("length"))
      // `path.utf-8` 是 BEP 3 的补充字段，优先用它（老种子的 `path` 可能不是 UTF-8）
      const pathParts = nodeList(node.entries.get("path.utf-8") ?? node.entries.get("path"))
        .map((part) => nodeText(part))
        .filter((part) => part.length > 0)
      if (pathParts.length === 0) continue
      files.push({ path: pathParts.join("/"), size })
    }
  } else if (hasLengthKey) {
    // 单文件模式
    files.push({ path: name, size: nodeInt(info.entries.get("length")) })
  }
  if (files.length === 0) throw new TorrentParseError("torrent contains no files")

  const totalSize = files.reduce((sum, file) => sum + file.size, 0)

  // ---- info_hash：对 info 字典的**原始字节**做 SHA-1 -------------------
  const infoHash = sha1Hex(nodeBytes(bytes, info))

  // ---- tracker --------------------------------------------------------
  const trackers: string[] = []
  const push = (url: string) => {
    const trimmed = url.trim()
    if (trimmed && !trackers.includes(trimmed)) trackers.push(trimmed)
  }
  push(nodeText(root.entries.get("announce")))
  for (const tier of nodeList(root.entries.get("announce-list"))) {
    // 标准是「列表的列表」，但老种子/手改过的文件里也有直接给字符串的
    if (tier.type === "list") tier.items.forEach((item) => push(nodeText(item)))
    else push(nodeText(tier))
  }

  // ---- CAS 扩展 -------------------------------------------------------
  const casNode = dictGet(root, "x-cas")
  let cas: TorrentCasInfo | null = null
  if (casNode) {
    const fileMd5 = nodeText(dictGet(casNode, "file_md5"))
    const sliceMd5 = nodeText(dictGet(casNode, "slice_md5"))
    // 两个 MD5 缺一不可，否则不是可用的秒传信息（与后端 HasCasInfo 判定一致）
    if (fileMd5 && sliceMd5) {
      cas = {
        fileMd5,
        sliceMd5,
        sliceSize: nodeInt(dictGet(casNode, "slice_size")),
        cloud: nodeText(dictGet(casNode, "cloud")),
      }
    }
  }

  return {
    name,
    totalSize,
    pieceLength,
    pieceCount,
    infoHash,
    files,
    trackers,
    comment: nodeText(root.entries.get("comment")).trim(),
    createdBy: nodeText(root.entries.get("created by")).trim(),
    creationDate: nodeInt(root.entries.get("creation date")),
    isPrivate: nodeInt(info.entries.get("private")) === 1,
    cas,
  }
}

/**
 * 生成磁力链接。
 *
 * `dn`（显示名）与 `tr`（tracker）都要 encodeURIComponent：种子里带中文或
 * `&`/`#` 的名称直接拼进 query 会被下载器截断。
 */
export function torrentMagnet(torrent: ParsedTorrent): string {
  const parts = [`xt=urn:btih:${torrent.infoHash}`]
  if (torrent.name) parts.push(`dn=${encodeURIComponent(torrent.name)}`)
  for (const tracker of torrent.trackers) {
    parts.push(`tr=${encodeURIComponent(tracker)}`)
  }
  return `magnet:?${parts.join("&")}`
}

/** 文件名是否是种子（`.torrent` 后缀，大小写不敏感）。 */
export function isTorrentName(name: string): boolean {
  return /\.torrent$/i.test(name ?? "")
}
