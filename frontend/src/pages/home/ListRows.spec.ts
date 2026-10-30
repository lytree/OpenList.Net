import { describe, expect, it } from "vitest"
import { mount } from "@vue/test-utils"
import { ObjType, type Obj } from "~/api/types"
import FileListRows from "~/pages/home/ListRows"
import type { RowAction } from "~/pages/home/rowActions"

/**
 * 手机卡片行的渲染与事件契约。
 *
 * 卡片行是「列表模式在 <=767px 的替代结构」，必须与表格保持同一套语义：
 * 点行=打开、点勾选框=选中（且不打开）、更多菜单项由父组件给、
 * 只读时不出现勾选框。这几条错一条，手机上就会「点了没反应」或「误打开」。
 */

const rows: Obj[] = [
  {
    name: "Movies",
    size: 0,
    is_dir: true,
    created: "2026-01-01T00:00:00Z",
    modified: "2026-01-02T03:04:00Z",
    thumb: "",
    type: ObjType.FOLDER,
  },
  {
    name: "note.txt",
    size: 2048,
    is_dir: false,
    created: "2026-01-01T00:00:00Z",
    modified: "2026-01-02T03:04:00Z",
    thumb: "",
    type: ObjType.TEXT,
  },
]

const actions = (obj: Obj): RowAction[] => [{ command: "open", label: `打开 ${obj.name}` }]

function mountRows(over: Record<string, unknown> = {}) {
  return mount(FileListRows, {
    props: { objs: rows, selected: [], selectable: true, actions, ...over },
  })
}

describe("FileListRows", () => {
  it("每个条目渲染一行，名称原样显示", () => {
    const wrapper = mountRows()
    const items = wrapper.findAll(".ol-row")
    expect(items).toHaveLength(2)
    expect(items[0]!.text()).toContain("Movies")
    expect(items[1]!.text()).toContain("note.txt")
  })

  it("目录副信息写「文件夹」，文件写大小", () => {
    const wrapper = mountRows()
    const metas = wrapper.findAll(".ol-row-meta").map((node) => node.text())
    expect(metas[0]).toContain("文件夹")
    expect(metas[1]).toContain("KB")
    // 时间在副信息里，两种条目都有
    expect(metas[1]).toContain("2026")
  })

  it("点行发出 open", async () => {
    const wrapper = mountRows()
    await wrapper.findAll(".ol-row")[1]!.trigger("click")
    const emitted = wrapper.emitted("open")
    expect(emitted).toHaveLength(1)
    expect((emitted![0]![0] as Obj).name).toBe("note.txt")
  })

  it("点勾选框只发 toggle，不触发打开", async () => {
    const wrapper = mountRows()
    await wrapper.findAll(".ol-row-check input")[1]!.setValue(true)
    expect(wrapper.emitted("open")).toBeUndefined()
    const toggled = wrapper.emitted("toggle")
    expect(toggled).toHaveLength(1)
    expect((toggled![0]![0] as Obj).name).toBe("note.txt")
    expect(toggled![0]![1]).toBe(true)
  })

  it("只读时不渲染勾选框", () => {
    const wrapper = mountRows({ selectable: false })
    expect(wrapper.findAll(".ol-row-check")).toHaveLength(0)
  })

  it("已选中行带 is-selected，并回显勾选状态", () => {
    const wrapper = mountRows({ selected: ["Movies"] })
    const items = wrapper.findAll(".ol-row")
    expect(items[0]!.classes()).toContain("is-selected")
    expect(items[1]!.classes()).not.toContain("is-selected")
    expect((wrapper.findAll(".ol-row-check input")[0]!.element as HTMLInputElement).checked).toBe(true)
  })

  it("「更多」按钮不会把点击透传给行", async () => {
    const wrapper = mountRows()
    await wrapper.findAll(".ol-row-more")[0]!.trigger("click")
    expect(wrapper.emitted("open")).toBeUndefined()
  })

  it("右键发出 contextmenu 并带出坐标事件", async () => {
    const wrapper = mountRows()
    await wrapper.findAll(".ol-row")[0]!.trigger("contextmenu")
    const emitted = wrapper.emitted("contextmenu")
    expect(emitted).toHaveLength(1)
    expect((emitted![0]![0] as Obj).name).toBe("Movies")
  })
})
