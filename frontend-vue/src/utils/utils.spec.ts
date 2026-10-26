import { describe, expect, it } from "vitest"
import {
  baseName,
  encodePath,
  ext,
  pathBase,
  pathDir,
  pathJoin,
  pathSegments,
  standardizePath,
} from "./path"
import { formatTime, humanSize } from "./format"

describe("path", () => {
  it("standardizePath 补前导斜杠、去尾斜杠", () => {
    expect(standardizePath("a/b")).toBe("/a/b")
    expect(standardizePath("/a/b/")).toBe("/a/b")
    expect(standardizePath("/")).toBe("/")
    // 根目录单独处理：分享/写操作需要空串表示「无子路径」
    expect(standardizePath("/", true)).toBe("")
  })

  it("pathJoin 合并重复斜杠", () => {
    expect(pathJoin("/a/", "/b/", "c")).toBe("/a/b/c")
    expect(pathJoin("", "/", "x")).toBe("/x")
  })

  it("pathBase / pathDir / ext / baseName", () => {
    expect(pathBase("/a/b/c.txt")).toBe("c.txt")
    expect(pathDir("/a/b/c.txt")).toBe("/a/b")
    // 根的父目录仍是根，否则「上一级」会变成空路径
    expect(pathDir("/a")).toBe("/")
    expect(ext("/a/b.TAR.GZ")).toBe("gz")
    // 隐藏文件不应被当成「无扩展名但去掉首字符」
    expect(ext("/a/.gitignore")).toBe("gitignore")
    expect(baseName("archive.tar.gz")).toBe("archive.tar")
    expect(baseName("noext")).toBe("noext")
  })

  it("encodePath 只转义破坏 URL 的字符", () => {
    expect(encodePath("/a b/c#d?e")).toBe("/a%20b/c%23d%3Fe")
    // 中文保持原样，后端按原文匹配
    expect(encodePath("/电影/片名.mp4")).toBe("/电影/片名.mp4")
    expect(encodePath("/电影 名", true)).toBe("/%E7%94%B5%E5%BD%B1%20%E5%90%8D")
  })

  it("pathSegments 生成面包屑且末项路径等于原路径", () => {
    const segs = pathSegments("/a/b/c", "首页")
    expect(segs.map((s) => s.name)).toEqual(["首页", "a", "b", "c"])
    expect(segs.map((s) => s.path)).toEqual(["/", "/a", "/a/b", "/a/b/c"])
    expect(pathSegments("/", "首页")).toEqual([{ name: "首页", path: "/" }])
  })
})

describe("format", () => {
  it("humanSize 走 1024 进制", () => {
    expect(humanSize(0)).toBe("0 B")
    expect(humanSize(512)).toBe("512 B")
    expect(humanSize(1024)).toBe("1.00 KB")
    expect(humanSize(1536)).toBe("1.50 KB")
    expect(humanSize(1024 ** 3)).toBe("1.00 GB")
    // 目录不给 size 时显示占位符，而不是 0 B
    expect(humanSize(undefined)).toBe("-")
    expect(humanSize(-1)).toBe("-")
  })

  it("formatTime 输出本地时间，无效输入原样返回", () => {
    expect(formatTime("")).toBe("-")
    expect(formatTime("not-a-date")).toBe("not-a-date")
    expect(formatTime("2026-10-25T09:05:00Z")).toMatch(/^\d{4}-\d{2}-\d{2} \d{2}:\d{2}$/)
  })
})
