import { describe, expect, it } from "vitest"
import {
  expandTimePlaceholders,
  planFindReplace,
  planRegexRename,
  planSequentialRename,
  validateName,
} from "./rename"
import type { RenameSource } from "./rename"

const file = (name: string, extra: Partial<RenameSource> = {}): RenameSource => ({
  name,
  is_dir: false,
  created: "2024-03-05T10:20:30Z",
  modified: "2024-04-06T11:22:33Z",
  ...extra,
})

describe("validateName", () => {
  it("拒绝空名、点目录与非法字符", () => {
    expect(validateName("")).not.toBe("")
    expect(validateName("  ")).not.toBe("")
    expect(validateName(".")).not.toBe("")
    expect(validateName("..")).not.toBe("")
    expect(validateName("a/b.txt")).not.toBe("")
    expect(validateName("a:b.txt")).not.toBe("")
  })

  it("正常名字放行", () => {
    expect(validateName("report 2024.txt")).toBe("")
    expect(validateName("中文名.mp4")).toBe("")
  })
})

describe("expandTimePlaceholders", () => {
  it("展开创建/修改时间，月与日补零", () => {
    const source = file("a.txt")
    // 用 UTC 构造，避免本机时区影响断言
    const created = new Date(source.created!)
    const text = expandTimePlaceholders("{created_year}-{created_month}-{created_date}", source)
    expect(text).toBe(
      `${created.getFullYear()}-${String(created.getMonth() + 1).padStart(2, "0")}-${String(created.getDate()).padStart(2, "0")}`,
    )
  })

  it("没有对应时间时保留占位符原文", () => {
    expect(expandTimePlaceholders("{modified_year}", { name: "a", is_dir: false })).toBe("{modified_year}")
  })
})

describe("planRegexRename", () => {
  it("按正则替换并保留反向引用", () => {
    const plan = planRegexRename([file("IMG_001.jpg"), file("IMG_002.jpg"), file("note.txt")], "^IMG_(\\d+)", "photo-$1")
    expect(plan.changed.map((i) => i.new_name)).toEqual(["photo-001.jpg", "photo-002.jpg"])
    expect(plan.items.find((i) => i.src_name === "note.txt")?.unchanged).toBe(true)
    expect(plan.problems).toEqual([])
  })

  it("支持时间占位符", () => {
    const plan = planRegexRename([file("a.txt")], "^(.*)\\.txt$", "$1-{created_year}.txt")
    expect(plan.changed[0].new_name).toMatch(/^a-\d{4}\.txt$/)
  })

  it("非法正则给出问题而不是抛异常", () => {
    const plan = planRegexRename([file("a.txt")], "([", "x")
    expect(plan.changed).toHaveLength(0)
    expect(plan.problems[0]).toContain("正则表达式不合法")
  })
})

describe("planSequentialRename", () => {
  it("默认保留扩展名并补零", () => {
    const plan = planSequentialRename([file("b.jpg"), file("a.png"), file("c")], "{number}", 1, 3)
    expect(plan.changed.map((i) => i.new_name)).toEqual(["001.jpg", "002.png", "003"])
  })

  it("{number} 可放在任意位置", () => {
    const plan = planSequentialRename([file("x.mp4")], "ep{number}", 5, 2)
    expect(plan.changed[0].new_name).toBe("ep05.mp4")
  })

  it("模板自带扩展名时不重复拼接", () => {
    const plan = planSequentialRename([file("x.mp4")], "clip{number}.webm", 1, 0)
    expect(plan.changed[0].new_name).toBe("clip1.webm")
  })

  it("{ext} 展开为原扩展名", () => {
    const plan = planSequentialRename([file("x.mp4")], "v{number}.{ext}", 1, 0)
    expect(plan.changed[0].new_name).toBe("v1.mp4")
  })

  it("目录不拆分扩展名", () => {
    const plan = planSequentialRename([file("v1.2", { is_dir: true })], "dir{number}", 1, 0)
    expect(plan.changed[0].new_name).toBe("dir1")
  })
})

describe("planFindReplace", () => {
  it("替换全部出现位置", () => {
    const plan = planFindReplace([file("a-a-a.txt")], "a", "b")
    expect(plan.changed[0].new_name).toBe("b-b-b.txt")
  })

  it("未命中的条目保持原名", () => {
    const plan = planFindReplace([file("keep.txt")], "zzz", "x")
    expect(plan.changed).toHaveLength(0)
    expect(plan.items[0].unchanged).toBe(true)
  })

  it("空查找词不产生任何变化", () => {
    const plan = planFindReplace([file("a.txt")], "", "x")
    expect(plan.changed).toHaveLength(0)
  })
})

describe("收尾校验", () => {
  it("撞名被拦下（两条改成同一个名字）", () => {
    const plan = planFindReplace([file("a1.txt"), file("a2.txt")], "1", "")
    // a1.txt -> a.txt，a2.txt -> a2.txt（未命中）；不冲突
    expect(plan.problems).toEqual([])

    const plan2 = planRegexRename([file("a1.txt"), file("a2.txt")], "a\\d", "same")
    expect(plan2.problems.some((p) => p.includes("重复"))).toBe(true)
  })

  it("新名字含非法字符时阻断", () => {
    const plan = planFindReplace([file("a.txt")], "a", "b/c")
    expect(plan.problems.some((p) => p.includes("不合法"))).toBe(true)
    expect(plan.changed).toHaveLength(0)
  })

  it("改成和同批另一项原名相同也算冲突（逐条改名没法交换）", () => {
    const plan = planFindReplace([file("a.txt"), file("b.txt")], "a", "b")
    expect(plan.problems.some((p) => p.includes("重复"))).toBe(true)
  })
})
