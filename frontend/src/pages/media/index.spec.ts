import { beforeEach, describe, expect, it, vi } from "vitest"
import { mount } from "@vue/test-utils"
import { createPinia, setActivePinia } from "pinia"
import type { MediaItemQuery } from "~/api/media"
import { setLocale } from "~/i18n"
import MediaLibraryPage from "~/pages/media/index"

/**
 * 媒体库页对 URL 筛选参数的接收（媒体首页「查看全部 →」的落点）。
 *
 * 首页三段带 `?state=favorite|in-progress` 与 `?sort=created` 跳过来；
 * 本页必须把这两个参数转成 `/media/items` 的查询条件，否则「查看全部」
 * 只是跳了个页，看到的仍是全量列表。非法值要静默退回默认，不能让用户
 * 通过手改 URL 把任意字符串塞进后端。
 */

/** 当前挂载时的路由查询串（每个测试替换）。 */
const routeQuery = vi.hoisted(() => ({ value: {} as Record<string, string> }))

/** `/media/items` 收到的查询参数（逐次记录）。 */
const listCalls = vi.hoisted(() => ({ value: [] as Record<string, unknown>[] }))

vi.mock("vue-router", () => ({
  useRoute: () => ({ name: "media-library", path: "/media/library", query: routeQuery.value }),
  useRouter: () => ({ push: vi.fn(), replace: vi.fn() }),
}))

vi.mock("~/api/media", async (importOriginal) => {
  const actual = await importOriginal<typeof import("~/api/media")>()
  return {
    ...actual,
    mediaListLibraries: async () => ({ code: 200, message: "success", data: [] }),
    mediaFacets: async () => ({
      code: 200,
      message: "success",
      data: { total: 0, genres: [], years: [] },
    }),
    // 只要记录查询参数，内容留空（断言与渲染无关）
    mediaListItems: async (query: Record<string, unknown>) => {
      listCalls.value.push(query)
      return { code: 200, message: "success", data: { content: [], total: 0 } }
    },
    mediaBatchStates: async () => ({ code: 200, message: "success", data: [] }),
    mediaUpdateState: async () => ({ code: 200, message: "success", data: {} }),
    mediaImageLink: () => "about:blank",
  }
})

const flush = () => new Promise((resolve) => setTimeout(resolve, 0))
const firstQuery = () => listCalls.value[0] as MediaItemQuery

async function mountLibrary(query: Record<string, string>) {
  routeQuery.value = query
  const wrapper = mount(MediaLibraryPage)
  await flush()
  await flush()
  wrapper.unmount()
}

describe("媒体库页 接收首页预置筛选", () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    setLocale("en")
    listCalls.value = []
  })

  it("?state=favorite 透传给 /media/items", async () => {
    await mountLibrary({ state: "favorite" })
    expect(firstQuery()).toMatchObject({ state: "favorite", page: 1 })
  })

  it("?state=in-progress 透传给 /media/items", async () => {
    await mountLibrary({ state: "in-progress" })
    expect(firstQuery()).toMatchObject({ state: "in-progress" })
  })

  it("?sort=rating 恢复排序并带 descending", async () => {
    await mountLibrary({ sort: "rating" })
    expect(firstQuery()).toMatchObject({ sort: "rating", desc: true })
  })

  it("非法 state / sort 退回默认（不向主查询下传）", async () => {
    await mountLibrary({ state: "played; drop table", sort: "evil" })
    expect(firstQuery().state).toBeUndefined()
    expect(firstQuery().sort).toBe("created")
  })
})
