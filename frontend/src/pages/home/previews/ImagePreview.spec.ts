import { describe, expect, it } from "vitest"
import { mount } from "@vue/test-utils"
import { ObjType, type FsGetData, type Obj } from "~/api/types"
import ImagePreview from "~/pages/home/previews/ImagePreview"

/**
 * 图片预览的「上一张 / 下一张」契约。
 *
 * 播放列表由宿主注入（同目录图片），组件只负责按 index 回调 `playlistPlay`，
 * 路径拼接与保持预览器都在宿主里做 —— 这里守的是「点按钮回的下标对不对」，
 * 以及列表里找不到当前文件时不冒出一堆无效按钮。
 */

function image(name: string): Obj {
  return {
    name,
    size: 1024,
    is_dir: false,
    created: "2026-01-01T00:00:00Z",
    modified: "2026-01-01T00:00:00Z",
    thumb: "",
    type: ObjType.IMAGE,
  }
}

function fsGet(name: string): FsGetData {
  return { ...image(name), raw_url: `/p/${name}`, readme: "", header: "", provider: "", related: [] }
}

const playlist = [image("a.png"), image("b.png"), image("c.png")]

function mountPreview(props: Record<string, unknown> = {}) {
  return mount(ImagePreview, {
    props: { obj: fsGet("b.png"), src: "/p/b.png", playlist, ...props },
  })
}

/** 工具栏第二组按钮就是切图组（第一组是缩放/旋转/复位）。 */
function navButtons(wrapper: ReturnType<typeof mountPreview>) {
  return wrapper.findAll(".el-button-group")[1]!.findAll("button")
}

describe("ImagePreview", () => {
  it("下一张按列表下标回调，结尾回卷到第一张", async () => {
    const calls: number[] = []
    const wrapper = mountPreview({ playlistPlay: (index: number) => calls.push(index) })

    await navButtons(wrapper)[1]!.trigger("click")
    expect(calls).toEqual([2])

    // 当前是最后一张时再点下一张，回到第一张
    await wrapper.setProps({ obj: fsGet("c.png") })
    await navButtons(wrapper)[1]!.trigger("click")
    expect(calls).toEqual([2, 0])
  })

  it("上一张按列表下标回调，第一张回卷到最后一张", async () => {
    const calls: number[] = []
    const wrapper = mountPreview({ playlistPlay: (index: number) => calls.push(index) })

    await navButtons(wrapper)[0]!.trigger("click")
    expect(calls).toEqual([0])

    await wrapper.setProps({ obj: fsGet("a.png") })
    await navButtons(wrapper)[0]!.trigger("click")
    expect(calls).toEqual([0, 2])
  })

  it("显示当前位置", () => {
    const wrapper = mountPreview()
    expect(wrapper.text()).toContain("2 / 3")
  })

  it("播放列表只有一张（或没有当前文件）时不渲染切图按钮", () => {
    const single = mountPreview({ obj: fsGet("b.png"), playlist: [image("b.png")] })
    expect(single.findAll(".el-button-group")).toHaveLength(1)

    const missing = mountPreview({ obj: fsGet("d.png") })
    expect(missing.findAll(".el-button-group")).toHaveLength(1)
  })
})
