/**
 * 归档目录树构建。
 *
 * 后端 `/fs/archive/list` 返回的是**扁平**条目（`name` 是归档内完整相对路径），
 * 而且 zip 里常见「有 `a/b.txt` 但没有 `a/` 目录条目」的情况。
 * 因此这里按需补齐隐式目录，前端一次请求拿到整棵树，
 * 之后面包屑导航都是纯本地操作（不需要按目录再请求后端）。
 */

export interface FlatArchiveEntry {
  name: string
  size: number
  is_dir: boolean
  modified: string
}

/**
 * 归档扩展名白名单（不含点）。
 *
 * 与后端 `SettingService.GetArchiveExtensionsAsync` 返回的集合保持一致：
 * 驱动层的 SharpCompress 读不了的类型不该在 UI 上露出「归档预览」入口。
 */
export const ARCHIVE_EXTS = ["zip", "tar", "gz", "tgz", "bz2", "7z", "rar"]

/** 文件名是否是后端可读的归档类型。 */
export function isArchiveName(name: string): boolean {
  const lower = (name ?? "").toLowerCase()
  return ARCHIVE_EXTS.some((extension) => lower.endsWith(`.${extension}`))
}

export interface ArchiveNode {
  /** 显示名（最后一段）。 */
  name: string
  /** 归档内完整相对路径，不带前导斜杠。 */
  path: string
  isDir: boolean
  size: number
  modified: string
  children: ArchiveNode[]
  /** 该目录是否由隐式路径补出来的（没有任何显式目录条目）。 */
  implicit?: boolean
}

function makeNode(name: string, path: string, isDir: boolean, size = 0, modified = ""): ArchiveNode {
  return { name, path, isDir, size, modified, children: [] }
}

/** 目录在前，再按名称自然序（`file-2` 排在 `file-10` 前）。 */
export function sortArchiveNodes(nodes: ArchiveNode[]): ArchiveNode[] {
  return [...nodes].sort((a, b) => {
    if (a.isDir !== b.isDir) return a.isDir ? -1 : 1
    return a.name.localeCompare(b.name, undefined, { numeric: true, sensitivity: "base" })
  })
}

/**
 * 把扁平条目列表构建成树，返回根层的子节点（已排序）。
 *
 * 同一路径的显式条目与隐式目录合并：显式条目的 `size`/`modified` 覆盖隐式值。
 */
export function buildArchiveTree(entries: FlatArchiveEntry[]): ArchiveNode[] {
  const root: ArchiveNode = makeNode("", "", true)
  /** 路径 → 节点。用 Map 而不是在 children 里线性找，几千条目的归档也不会卡。 */
  const index = new Map<string, ArchiveNode>([["", root]])

  const ensureDir = (segments: string[]): ArchiveNode => {
    let current = root
    let path = ""
    for (const segment of segments) {
      path = path ? `${path}/${segment}` : segment
      let node = index.get(path)
      if (!node) {
        node = makeNode(segment, path, true)
        node.implicit = true
        index.set(path, node)
        current.children.push(node)
      }
      current = node
    }
    return current
  }

  for (const entry of entries) {
    const clean = (entry.name ?? "").replace(/\\/g, "/").replace(/^\/+|\/+$/g, "")
    if (!clean) continue
    const segments = clean.split("/").filter(Boolean)
    const leaf = segments.pop()!
    const parent = ensureDir(segments)

    const existing = index.get(clean)
    if (existing) {
      // 已由隐式路径建过目录：补上真实元数据，标记为显式
      existing.implicit = false
      existing.isDir = existing.isDir || entry.is_dir
      existing.size = entry.size
      existing.modified = entry.modified
      continue
    }

    const node = makeNode(leaf, clean, entry.is_dir, entry.size, entry.modified)
    index.set(clean, node)
    parent.children.push(node)
  }

  const sortRecursive = (node: ArchiveNode) => {
    node.children = sortArchiveNodes(node.children)
    for (const child of node.children) if (child.isDir) sortRecursive(child)
  }
  sortRecursive(root)

  return root.children
}

/** 按归档内路径找到节点；找不到返回 null。 */
export function findArchiveNode(tree: ArchiveNode[], path: string): ArchiveNode | null {
  const segments = path.split("/").filter(Boolean)
  let list = tree
  let node: ArchiveNode | null = null
  for (const segment of segments) {
    node = list.find((item) => item.name === segment) ?? null
    if (!node) return null
    list = node.children
  }
  return node
}

/** 归档内条目总数（递归，含目录）。 */
export function countArchiveNodes(tree: ArchiveNode[]): number {
  let total = 0
  for (const node of tree) {
    total += 1
    if (node.isDir) total += countArchiveNodes(node.children)
  }
  return total
}
