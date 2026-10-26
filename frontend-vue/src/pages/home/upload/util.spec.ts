import { describe, expect, it } from "vitest"
import { asyncPool, File2Upload } from "./util"

describe("File2Upload", () => {
  it("从 File 构造初始行数据", () => {
    const file = new File(["hello"], "a.txt", { type: "text/plain" })
    const row = File2Upload(file)
    expect(row.name).toBe("a.txt")
    expect(row.path).toBe("a.txt")
    expect(row.size).toBe(5)
    expect(row.progress).toBe(0)
    expect(row.status).toBe("pending")
  })

  it("目录上传时把反斜杠路径归一为斜杠", () => {
    const file = new File(["x"], "dir\\sub\\b.txt")
    const row = File2Upload(file)
    expect(row.path).toBe("dir/sub/b.txt")
  })
})

describe("asyncPool", () => {
  it("全部执行且保持并发上限", async () => {
    let active = 0
    let peak = 0
    const seen: number[] = []
    for await (const r of asyncPool(2, [1, 2, 3, 4, 5], async (n) => {
      active++
      peak = Math.max(peak, active)
      await new Promise((res) => setTimeout(res, 5))
      active--
      seen.push(n)
      return n * 10
    })) {
      expect(r).toBeTypeOf("number")
    }
    expect(seen.sort((a, b) => a - b)).toEqual([1, 2, 3, 4, 5])
    expect(peak).toBeLessThanOrEqual(2)
  })

  it("mapper 抛异常时不会卡死整池", async () => {
    const results: unknown[] = []
    for await (const r of asyncPool(2, [1, 2, 3], async (n) => {
      if (n === 2) throw new Error("boom")
      return n
    })) {
      results.push(r)
    }
    // 1、3 正常完成并产出；2 的异常被池吞掉，其余任务照常
    expect(results).toEqual([1, 3])
  })
})
