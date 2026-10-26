import { describe, expect, it } from "vitest"
import { ObjType, type Obj } from "~/api/types"
import { filterObjs, pageList, paginate } from "./list"

function obj(name: string, isDir = false, type = ObjType.UNKNOWN): Obj {
  return {
    name,
    size: isDir ? 0 : 100,
    is_dir: isDir,
    created: "2026-01-01T00:00:00Z",
    modified: "2026-01-01T00:00:00Z",
    thumb: "",
    type,
  }
}

describe("filterObjs", () => {
  const objs = [obj("Pic", true), obj("README.md"), obj("readme.txt"), obj("a.MP4")]

  it("空关键字原样返回（同一个引用，避免无谓重排）", () => {
    expect(filterObjs(objs, "")).toBe(objs)
    expect(filterObjs(objs, "   ")).toBe(objs)
  })

  it("大小写不敏感", () => {
    expect(filterObjs(objs, "readme").map((o) => o.name)).toEqual(["README.md", "readme.txt"])
    expect(filterObjs(objs, "mp4").map((o) => o.name)).toEqual(["a.MP4"])
  })

  it("首尾空格被忽略", () => {
    expect(filterObjs(objs, "  pic ").map((o) => o.name)).toEqual(["Pic"])
  })

  it("无匹配返回空数组", () => {
    expect(filterObjs(objs, "zzz")).toEqual([])
  })
})

describe("paginate", () => {
  const items = Array.from({ length: 25 }, (_, i) => i)

  it("pageSize<=0 表示不分页", () => {
    const r = paginate(items, 3, 0)
    expect(r.items).toHaveLength(25)
    expect(r.pages).toBe(1)
    expect(r.page).toBe(1)
  })

  it("正常切片", () => {
    const r = paginate(items, 2, 10)
    expect(r.items).toEqual([10, 11, 12, 13, 14, 15, 16, 17, 18, 19])
    expect(r.total).toBe(25)
    expect(r.pages).toBe(3)
    expect(r.page).toBe(2)
  })

  it("最后一页不满", () => {
    expect(paginate(items, 3, 10).items).toEqual([20, 21, 22, 23, 24])
  })

  it("页码越界被夹回合法区间", () => {
    expect(paginate(items, 99, 10).page).toBe(3)
    expect(paginate(items, 99, 10).items).toEqual([20, 21, 22, 23, 24])
    expect(paginate(items, 0, 10).page).toBe(1)
    expect(paginate(items, -5, 10).page).toBe(1)
    // 非整数页码（分页组件偶尔给字符串）退化为第 1 页而不是 NaN 切片
    expect(paginate(items, Number.NaN, 10).page).toBe(1)
  })

  it("空列表也有 1 页", () => {
    const r = paginate<number>([], 1, 10)
    expect(r.items).toEqual([])
    expect(r.pages).toBe(1)
    expect(r.total).toBe(0)
  })
})

describe("pageList", () => {
  it("只有一页时返回 [1]", () => {
    expect(pageList(1, 1)).toEqual([1])
    expect(pageList(1, 0)).toEqual([1])
  })

  it("页数少时全部列出，不插省略号", () => {
    expect(pageList(1, 5)).toEqual([1, 2, 3, 4, 5])
    expect(pageList(3, 5)).toEqual([1, 2, 3, 4, 5])
  })

  it("页数多时首末页恒在，当前页 ±1，中间插省略号", () => {
    expect(pageList(1, 20)).toEqual([1, 2, "…", 20])
    expect(pageList(10, 20)).toEqual([1, "…", 9, 10, 11, "…", 20])
    expect(pageList(20, 20)).toEqual([1, "…", 19, 20])
  })

  it("越界页码被夹回区间内", () => {
    expect(pageList(99, 20)).toEqual([1, "…", 19, 20])
    expect(pageList(0, 20)).toEqual([1, 2, "…", 20])
  })
})
