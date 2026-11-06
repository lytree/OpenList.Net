import { beforeEach, describe, expect, it, vi } from "vitest"
import { mount } from "@vue/test-utils"
import { createPinia, setActivePinia } from "pinia"
import { MediaLibraryType, MediaItemStatus, type MediaItem } from "~/api/media"
import { setLocale } from "~/i18n"
import MediaHomePage from "~/pages/media/Home"

/**
 * 媒体首页三个 section 的「查看全部 →」入口。
 *
 * 三段都能下钻到媒体库页且**筛选已预置**（收藏 → `state=favorite`、
 * 在追 → `state=in-progress`、最近入库 → `sort=created`）。这条契约一旦错，
 * 用户点「查看全部」看到的是未筛选的全量列表，等于入口失效。
 * 空 section 本来就不渲染，因此也不该留下一个点了没用的链接。
 */

/** 首页聚合的假数据（每个测试按需替换）。 */
const state = vi.hoisted(() => ({
  home: {
    recently_added: [] as unknown[],
    continue_watching: [] as unknown[],
    recently_favorited: [] as unknown[],
  },
}))

/** `useRouter().push` 的桩，用来断言跳转目标与查询参数。 */
const push = vi.hoisted(() => vi.fn())

vi.mock("vue-router", () => ({
  useRouter: () => ({ push }),
  useRoute: () => ({ name: "media", query: {} }),
}))

vi.mock("~/api/media", async (importOriginal) => {
  const actual = await importOriginal<typeof import("~/api/media")>()
  return {
    ...actual,
    mediaHome: async () => ({ code: 200, message: "success", data: state.home }),
    mediaBatchStates: async () => ({ code: 200, message: "success", data: [] }),
    mediaUpdateState: async () => ({ code: 200, message: "success", data: {} }),
    mediaImageLink: () => "about:blank",
  }
})

function item(id: number, title: string): MediaItem {
  return {
    id,
    library_id: 1,
    type: MediaLibraryType.Movie,
    title,
    status: MediaItemStatus.Matched,
    directory_path: `/movies/${title}`,
    createdAt: "2026-01-01T00:00:00Z",
    updatedAt: "2026-01-01T00:00:00Z",
  }
}

/** 等待 `onMounted` 里的异步加载落定（挂载时 load() 会串两次 await）。 */
const flush = () => new Promise((resolve) => setTimeout(resolve, 0))

async function mountHome() {
  const wrapper = mount(MediaHomePage)
  await flush()
  await flush()
  return wrapper
}

describe("媒体首页 查看全部入口", () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    setLocale("en")
    push.mockReset()
  })

  it("三个 section 各有数据时都渲染查看全部链接", async () => {
    state.home = {
      continue_watching: [item(1, "Movie A")],
      recently_added: [item(2, "Movie B")],
      recently_favorited: [item(3, "Movie C")],
    }
    const wrapper = await mountHome()

    const heads = wrapper.findAll(".ol-media-section-head")
    expect(heads).toHaveLength(3)
    for (const head of heads) {
      expect(head.find(".ol-media-section-title").text()).not.toBe("")
      expect(head.find("button").text()).toBe("View all →")
    }
  })

  it("空 section 不渲染查看全部链接", async () => {
    state.home = {
      continue_watching: [],
      recently_added: [item(2, "Movie B")],
      recently_favorited: [],
    }
    const wrapper = await mountHome()

    const heads = wrapper.findAll(".ol-media-section-head")
    expect(heads).toHaveLength(1)
    expect(heads[0]!.text()).toContain("Recently added")
  })

  it("各段带正确的预置筛选：在追 / 最近入库 / 收藏", async () => {
    state.home = {
      continue_watching: [item(1, "Movie A")],
      recently_added: [item(2, "Movie B")],
      recently_favorited: [item(3, "Movie C")],
    }
    const wrapper = await mountHome()

    const buttons = wrapper.findAll(".ol-media-section-head button")
    await buttons[0]!.trigger("click")
    await buttons[1]!.trigger("click")
    await buttons[2]!.trigger("click")

    expect(push).toHaveBeenNthCalledWith(1, {
      name: "media-library",
      query: { state: "in-progress" },
    })
    expect(push).toHaveBeenNthCalledWith(2, {
      name: "media-library",
      query: { sort: "created" },
    })
    expect(push).toHaveBeenNthCalledWith(3, {
      name: "media-library",
      query: { state: "favorite" },
    })
  })
})
