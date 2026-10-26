import { describe, expect, it } from "vitest"
import {
  hoursOf,
  minutesOf,
  moveIdBy,
  moveInArray,
  nameInitial,
  percentOf,
  topBuckets,
} from "./mediaCatalog"

describe("moveInArray", () => {
  it("中间移动：插到目标位置，其余元素顺移", () => {
    expect(moveInArray(["a", "b", "c", "d"], 0, 2)).toEqual(["b", "c", "a", "d"])
    expect(moveInArray(["a", "b", "c", "d"], 3, 1)).toEqual(["a", "d", "b", "c"])
  })

  it("越界索引收敛到端点（不抛错、不产生空洞）", () => {
    expect(moveInArray(["a", "b", "c"], -5, 99)).toEqual(["b", "c", "a"])
    expect(moveInArray(["a", "b", "c"], 99, -5)).toEqual(["c", "a", "b"])
  })

  it("同位置与空数组返回等值新数组", () => {
    const src = ["a", "b"]
    const out = moveInArray(src, 1, 1)
    expect(out).toEqual(src)
    expect(out).not.toBe(src)
    expect(moveInArray([], 0, 1)).toEqual([])
  })
})

describe("moveIdBy", () => {
  const ids = [10, 20, 30]

  it("上移/下移一格", () => {
    expect(moveIdBy(ids, 30, -1)).toEqual([10, 30, 20])
    expect(moveIdBy(ids, 10, 1)).toEqual([20, 10, 30])
  })

  it("已在端点：返回原顺序副本，可直接交给 reorder", () => {
    const out = moveIdBy(ids, 10, -1)
    expect(out).toEqual(ids)
    expect(out).not.toBe(ids)
    expect(moveIdBy(ids, 30, 1)).toEqual(ids)
  })

  it("id 不存在同样返回副本", () => {
    expect(moveIdBy(ids, 999, 1)).toEqual(ids)
    expect(moveIdBy([], 1, 1)).toEqual([])
  })
})

describe("percentOf", () => {
  it("常规四舍五入", () => {
    expect(percentOf(1, 3)).toBe(33)
    expect(percentOf(2, 3)).toBe(67)
    expect(percentOf(3, 3)).toBe(100)
  })

  it("total 为 0 / 负数 / 非数时返回 0（条形图不出现 NaN 宽度）", () => {
    expect(percentOf(5, 0)).toBe(0)
    expect(percentOf(0, 0)).toBe(0)
    expect(percentOf(5, -1)).toBe(0)
    expect(percentOf(Number.NaN, 10)).toBe(0)
  })
})

describe("hoursOf / minutesOf", () => {
  it("小时保留一位小数", () => {
    expect(hoursOf(3600)).toBe(1)
    expect(hoursOf(5400)).toBe(1.5)
    expect(hoursOf(0)).toBe(0)
    expect(hoursOf(-10)).toBe(0)
  })

  it("分钟四舍五入且不足 1 分钟按 1 分钟算", () => {
    expect(minutesOf(59)).toBe(1)
    expect(minutesOf(90)).toBe(2)
    expect(minutesOf(0)).toBe(0)
  })
})

describe("nameInitial", () => {
  it("CJK 取首字、拉丁取大写首字母、空值兜底 ?", () => {
    expect(nameInitial("张国荣")).toBe("张")
    expect(nameInitial("brad pitt")).toBe("B")
    expect(nameInitial("  9 Lives")).toBe("9")
    expect(nameInitial("")).toBe("?")
    expect(nameInitial(null)).toBe("?")
    expect(nameInitial("   ")).toBe("?")
  })
})

describe("topBuckets", () => {
  it("按 count 降序取前 N，入参顺序不影响结果", () => {
    const buckets = [
      { value: "drama", count: 2 },
      { value: "action", count: 9 },
      { value: "comedy", count: 5 },
    ]
    expect(topBuckets(buckets, 2).map((b) => b.value)).toEqual(["action", "comedy"])
    // 不改动入参
    expect(buckets[0]?.value).toBe("drama")
  })

  it("n <= 0 返回空数组", () => {
    expect(topBuckets([{ count: 1 }], 0)).toEqual([])
  })
})
