import { describe, expect, it } from "vitest"
import { mount } from "@vue/test-utils"
import { ObjType, type Obj } from "~/api/types"
import ThumbIcon from "~/components/ThumbIcon"

/**
 * 列表缩略图槽位的两条契约：
 * 1. 后端下发了 `thumb` 就画小图，且不额外请求原图；
 * 2. 没有 `thumb`（或图加载失败）就退回类型图标 —— 不能留空槽，也不能显示裂图。
 */

function obj(over: Partial<Obj> = {}): Obj {
  return {
    name: "a.jpg",
    size: 1024,
    is_dir: false,
    created: "2026-01-01T00:00:00Z",
    modified: "2026-01-02T03:04:00Z",
    thumb: "http://127.0.0.1/api/p?path=/a.jpg&type=thumb&w=40&h=40",
    type: ObjType.IMAGE,
    ...over,
  }
}

describe("ThumbIcon", () => {
  it("有 thumb 时渲染 img 并带上尺寸", () => {
    const wrapper = mount(ThumbIcon, { props: { obj: obj(), size: 40 } })
    const img = wrapper.find("img")
    expect(img.exists()).toBe(true)
    expect(img.attributes("src")).toContain("type=thumb")
    expect(img.attributes("loading")).toBe("lazy")
    expect(wrapper.find(".ol-thumb").attributes("style")).toContain("width: 40px")
  })

  it("thumb 为空时退图标，不渲染 img", () => {
    const wrapper = mount(ThumbIcon, { props: { obj: obj({ thumb: "" }) } })
    expect(wrapper.find("img").exists()).toBe(false)
    expect(wrapper.find(".ol-thumb.is-icon").exists()).toBe(true)
    expect(wrapper.find(".el-icon").exists()).toBe(true)
  })

  it("目录即使带 thumb 也不用图（目录恒图标）", () => {
    const wrapper = mount(ThumbIcon, { props: { obj: obj({ is_dir: true, type: ObjType.FOLDER }) } })
    expect(wrapper.find("img").exists()).toBe(false)
  })

  it("图加载失败后降级为图标", async () => {
    const wrapper = mount(ThumbIcon, { props: { obj: obj() } })
    await wrapper.find("img").trigger("error")
    expect(wrapper.find("img").exists()).toBe(false)
    expect(wrapper.find(".ol-thumb.is-icon").exists()).toBe(true)
  })

  it("切换到另一条记录时重置失败态（复用实例不会一直显示图标）", async () => {
    const wrapper = mount(ThumbIcon, { props: { obj: obj() } })
    await wrapper.find("img").trigger("error")
    expect(wrapper.find("img").exists()).toBe(false)

    await wrapper.setProps({ obj: obj({ name: "b.jpg" }) })
    await wrapper.vm.$nextTick()
    expect(wrapper.find("img").exists()).toBe(true)
  })
})
