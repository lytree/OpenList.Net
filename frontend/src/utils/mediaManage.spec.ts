import { describe, expect, it } from "vitest"
import type { MediaScanTask, MediaScrapeStatus } from "~/api/media"
import {
  formatScanCounts,
  libraryTypeKey,
  normalizeScanState,
  scanLogSummary,
  scanTagType,
  scrapeOfLibrary,
  scrapeProgress,
  taskOfLibrary,
} from "./mediaManage"

describe("normalizeScanState", () => {
  it("已知状态原样返回", () => {
    expect(normalizeScanState("running")).toBe("running")
    expect(normalizeScanState("completed")).toBe("completed")
    expect(normalizeScanState("failed")).toBe("failed")
    expect(normalizeScanState("cancelled")).toBe("cancelled")
    expect(normalizeScanState("idle")).toBe("idle")
  })

  it("未知值兜底 idle（与后端 ApplySort 认不出按 title 处理同口径）", () => {
    expect(normalizeScanState("weird")).toBe("idle")
    expect(normalizeScanState("")).toBe("idle")
    expect(normalizeScanState(null)).toBe("idle")
    expect(normalizeScanState(undefined)).toBe("idle")
  })
})

describe("scanTagType", () => {
  it("running 黄、completed 绿、failed 红、其余中性", () => {
    expect(scanTagType("running")).toBe("warning")
    expect(scanTagType("completed")).toBe("success")
    expect(scanTagType("failed")).toBe("danger")
    expect(scanTagType("idle")).toBe("info")
    expect(scanTagType("cancelled")).toBe("info")
    expect(scanTagType("???")).toBe("info")
  })
})

describe("libraryTypeKey", () => {
  it("枚举值映射 i18n 键尾，未知归 mixed", () => {
    expect(libraryTypeKey(0)).toBe("movie")
    expect(libraryTypeKey(1)).toBe("tv")
    expect(libraryTypeKey(2)).toBe("mixed")
    expect(libraryTypeKey(99)).toBe("mixed")
  })
})

describe("formatScanCounts", () => {
  it("只拼非零段，全零显示 -", () => {
    expect(formatScanCounts({ added: 3, updated: 0, removed: 0 })).toBe("+3")
    expect(formatScanCounts({ added: 1, updated: 2, removed: 0 })).toBe("+1 ~2")
    expect(formatScanCounts({ added: 0, updated: 0, removed: 4 })).toBe("-4")
    expect(formatScanCounts({ added: 1, updated: 2, removed: 3 })).toBe("+1 ~2 -3")
    expect(formatScanCounts({ added: 0, updated: 0, removed: 0 })).toBe("-")
  })
})

describe("scanLogSummary", () => {
  it("失败行优先显示错误并截断 80 字符", () => {
    expect(scanLogSummary({ error: "boom", added: 1, updated: 0, removed: 0 })).toBe("boom")
    const long = "x".repeat(120)
    const out = scanLogSummary({ error: long, added: 0, updated: 0, removed: 0 })
    expect(out.length).toBe(81) // 80 + 省略号
    expect(out.endsWith("…")).toBe(true)
  })

  it("成功行显示计数摘要", () => {
    expect(scanLogSummary({ error: null, added: 2, updated: 1, removed: 0 })).toBe("+2 ~1")
    expect(scanLogSummary({ error: undefined, added: 0, updated: 0, removed: 0 })).toBe("-")
  })
})

describe("taskOfLibrary", () => {
  const task = (id: number): MediaScanTask => ({
    library_id: id,
    library_name: `L${id}`,
    path: "/x",
    state: "idle",
    found: 0,
    added: 0,
    updated: 0,
    removed: 0,
    is_running: false,
  })

  it("按 library_id 命中，缺失返回 null", () => {
    const tasks = [task(1), task(7)]
    expect(taskOfLibrary(tasks, 7)?.library_name).toBe("L7")
    expect(taskOfLibrary(tasks, 3)).toBeNull()
    expect(taskOfLibrary([], 1)).toBeNull()
  })
})

describe("scrapeOfLibrary / scrapeProgress", () => {
  const status = (patch: Partial<MediaScrapeStatus> = {}): MediaScrapeStatus => ({
    library_id: 3,
    library_name: "L3",
    pending: 0,
    unmatched: 0,
    matched: 0,
    running: false,
    batch_size: 0,
    batch_done: 0,
    failed: 0,
    error: null,
    ...patch,
  })

  it("按 library_id 命中，缺失返回 null", () => {
    const list = [status({ library_id: 1 }), status({ library_id: 3 })]
    expect(scrapeOfLibrary(list, 3)?.library_name).toBe("L3")
    expect(scrapeOfLibrary(list, 9)).toBeNull()
    expect(scrapeOfLibrary([], 1)).toBeNull()
  })

  it("未运行或批大小为 0 时进度为 0", () => {
    expect(scrapeProgress(null)).toBe(0)
    // 上一轮的 batch 字段后端会清零，此时不该残留一个 100%
    expect(scrapeProgress(status({ batch_size: 0, batch_done: 0 }))).toBe(0)
    expect(scrapeProgress(status({ running: true }))).toBe(0)
  })

  it("运行中按 batch_done / batch_size 算，且夹在 [0,100]", () => {
    expect(scrapeProgress(status({ running: true, batch_size: 200, batch_done: 50 }))).toBe(25)
    // 后端已夹取，这里再夹一次：脏数据不该让进度条溢出
    expect(scrapeProgress(status({ running: true, batch_size: 100, batch_done: 250 }))).toBe(100)
    expect(scrapeProgress(status({ running: true, batch_size: 100, batch_done: -5 }))).toBe(0)
  })

  it("分母是单轮上限而不是库里总数", () => {
    // 3000 条待刮削、单轮只做 200 个：进度按 200 算，不会永远停在 6%
    const s = status({ running: true, pending: 2800, batch_size: 200, batch_done: 100 })
    expect(scrapeProgress(s)).toBe(50)
  })
})
