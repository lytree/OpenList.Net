import { describe, expect, it } from "vitest"
import { buildArchiveTree, countArchiveNodes, findArchiveNode, sortArchiveNodes } from "./archive"
import type { FlatArchiveEntry } from "./archive"

const entry = (name: string, is_dir = false, size = 0): FlatArchiveEntry => ({
  name,
  size,
  is_dir,
  modified: "2026-01-01T00:00:00Z",
})

describe("buildArchiveTree", () => {
  it("根层文件与显式目录按目录优先排序", () => {
    const tree = buildArchiveTree([entry("b.txt"), entry("a.txt"), entry("sub", true)])
    expect(tree.map((n) => n.name)).toEqual(["sub", "a.txt", "b.txt"])
    expect(tree[0].isDir).toBe(true)
  })

  it("没有目录条目时补出隐式目录", () => {
    const tree = buildArchiveTree([entry("a/b/c.txt")])
    expect(tree).toHaveLength(1)
    const a = tree[0]
    expect(a.name).toBe("a")
    expect(a.isDir).toBe(true)
    expect(a.implicit).toBe(true)
    expect(a.children[0].name).toBe("b")
    expect(a.children[0].children[0].name).toBe("c.txt")
    expect(a.children[0].children[0].path).toBe("a/b/c.txt")
  })

  it("显式目录元数据覆盖隐式目录", () => {
    const tree = buildArchiveTree([
      entry("a/b.txt"),
      entry("a/", true),
    ])
    expect(tree[0].implicit).toBe(false)
  })

  it("反斜杠与前后斜杠都能归一化", () => {
    const tree = buildArchiveTree([entry("\\win\\file.txt"), entry("/lead/x.txt")])
    expect(tree.map((n) => n.name)).toEqual(["lead", "win"])
  })

  it("名称按自然序：file-2 在 file-10 之前", () => {
    const tree = buildArchiveTree([entry("file-10.txt"), entry("file-2.txt")])
    expect(tree.map((n) => n.name)).toEqual(["file-2.txt", "file-10.txt"])
  })
})

describe("findArchiveNode / countArchiveNodes", () => {
  const tree = buildArchiveTree([entry("a/b/c.txt"), entry("a/d.txt"), entry("root.txt")])

  it("按路径取节点", () => {
    expect(findArchiveNode(tree, "a/b")?.name).toBe("b")
    expect(findArchiveNode(tree, "a/b/c.txt")?.name).toBe("c.txt")
    expect(findArchiveNode(tree, "a/nope")).toBeNull()
    // 空路径代表根，根不在 tree 里（tree 是根的子节点），返回 null
    expect(findArchiveNode(tree, "")).toBeNull()
  })

  it("统计递归条目数", () => {
    // a, a/b, a/b/c.txt, a/d.txt, root.txt
    expect(countArchiveNodes(tree)).toBe(5)
  })
})

describe("sortArchiveNodes", () => {
  it("目录恒在前，大小写不敏感", () => {
    const sorted = sortArchiveNodes([
      { name: "B.txt", path: "B.txt", isDir: false, size: 0, modified: "", children: [] },
      { name: "a", path: "a", isDir: true, size: 0, modified: "", children: [] },
      { name: "a.txt", path: "a.txt", isDir: false, size: 0, modified: "", children: [] },
    ])
    expect(sorted.map((n) => n.name)).toEqual(["a", "a.txt", "B.txt"])
  })
})
