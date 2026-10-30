import { describe, expect, it } from "vitest"
import { ObjType, type Obj } from "~/api/types"
import { rowActionsFor } from "~/pages/home/rowActions"

/**
 * 行内菜单规则测试。
 *
 * 这里守的是「桌面表格」与「手机卡片行」共用的那一份规则：以前菜单项直接写在表格模板里，
 * 加一个视图就得抄一遍。改成纯函数后，最容易跑偏的三类组合（文件/目录 × 可写/只读）
 * 都能被断言锁定。
 */

function obj(over: Partial<Obj> = {}): Obj {
  return {
    name: "a.txt",
    size: 1024,
    is_dir: false,
    created: "2026-01-01T00:00:00Z",
    modified: "2026-01-02T03:04:00Z",
    thumb: "",
    type: ObjType.TEXT,
    ...over,
  }
}

const commands = (items: { command: string }[]) => items.map((item) => item.command)

describe("rowActionsFor", () => {
  it("只读时文件只有打开 / 下载 / 复制直链", () => {
    expect(commands(rowActionsFor(obj(), { writable: false }))).toEqual([
      "open",
      "download",
      "copy_link",
    ])
  })

  it("只读时目录没有字节流相关的操作", () => {
    expect(commands(rowActionsFor(obj({ is_dir: true }), { writable: false }))).toEqual(["open"])
  })

  it("可写时目录追加改名 / 移动 / 复制 / 删除", () => {
    expect(commands(rowActionsFor(obj({ is_dir: true }), { writable: true }))).toEqual([
      "open",
      "rename",
      "move",
      "copy",
      "delete",
    ])
  })

  it("可写时文件在下载项之后接写操作", () => {
    expect(commands(rowActionsFor(obj(), { writable: true }))).toEqual([
      "open",
      "download",
      "copy_link",
      "rename",
      "move",
      "copy",
      "delete",
    ])
  })

  it("删除单独分组并标红，改名与它同属「写操作」分组", () => {
    const items = rowActionsFor(obj(), { writable: true })
    const del = items.find((item) => item.command === "delete")
    const rename = items.find((item) => item.command === "rename")
    expect(del?.danger).toBe(true)
    expect(del?.divided).toBe(true)
    expect(rename?.divided).toBe(true)
    // 只读时不该有任何 danger 项
    expect(rowActionsFor(obj(), { writable: false }).some((item) => item.danger)).toBe(false)
  })
})
