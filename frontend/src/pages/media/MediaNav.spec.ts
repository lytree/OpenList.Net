import { beforeEach, describe, expect, it } from "vitest"
import { flushPromises, mount } from "@vue/test-utils"
import { createPinia, setActivePinia } from "pinia"
import { createMemoryHistory, createRouter, type Router } from "vue-router"
import { defineComponent } from "vue"
import { setLocale } from "~/i18n"
import { useUserStore } from "~/stores/user"
import MediaNav from "~/pages/media/MediaNav"

/**
 * 媒体左栏导航的条目集合。
 *
 * 守两件事：
 * 1. **「资源库」只在登录后出现，且紧挨首页**。它指向 `/files`，文件门面默认要登录；
 *    位置在首页之后、媒体库之前 —— 既满足"出去看内容的第一跳就在首页旁边"，也保证
 *    它与媒体库（同一批内容的两种视角）仍然相邻。
 * 2. **登录后才出现按用户的栏目**。想看 / 历史 / 播放列表 / 口味 / 任务都要求登录；
 *    给游客摆一个点进去就被弹回登录页的入口，比没有入口更让人困惑。
 *
 * 这里只断言「渲染了哪些条目、跳到哪」，不断言样式 —— 版式归 e2e 管。
 */

const Blank = defineComponent({ render: () => null })

function makeRouter(): Router {
  return createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: "/media", component: Blank },
      { path: "/@login", name: "login", component: Blank },
      { path: "/files", component: Blank },
      { path: "/:pathMatch(.*)*", component: Blank },
    ],
  })
}

/** 每次挂载用的 router（登录跳转那条用例要断言它落到了哪个路由）。 */
let router: Router

async function mountNav(loggedIn: boolean) {
  if (loggedIn) useUserStore().token = "test-token"
  router = makeRouter()
  await router.push("/media")
  return mount(MediaNav, { global: { plugins: [router] } })
}

const labelsOf = (wrapper: Awaited<ReturnType<typeof mountNav>>) =>
  wrapper.findAll(".olm-nav a.olm-nav-link").map((link) => link.text())

describe("媒体左栏导航", () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    setLocale("en")
  })

  it("游客：只有五个浏览入口，没有资源库", async () => {
    const wrapper = await mountNav(false)
    const labels = labelsOf(wrapper)

    expect(labels).toEqual(["Media home", "Libraries", "People", "Studios", "Series"])
    expect(labels).not.toContain("File library")
    expect(wrapper.findAll(".olm-nav-extra")).toHaveLength(0)

    // 私有资源对游客只有一个「要登录」的锁入口，不是可点进去的栏目。
    const locked = wrapper.findAll(".olm-nav-locked")
    expect(locked).toHaveLength(1)
    expect(locked[0]!.text()).toBe("Private")
  })

  it("登录后不再显示锁入口（私有库直接出现在列表里）", async () => {
    const wrapper = await mountNav(true)
    expect(wrapper.findAll(".olm-nav-locked")).toHaveLength(0)
  })

  it("游客点锁入口去登录页，并回跳媒体首页", async () => {
    const wrapper = await mountNav(false)
    await wrapper.find(".olm-nav-locked").trigger("click")
    await flushPromises()

    expect(router.currentRoute.value.name).toBe("login")
    expect(router.currentRoute.value.query.redirect).toBe("/media")
  })

  it("登录后：资源库紧挨首页，排在媒体库之前，指向 /files", async () => {
    const wrapper = await mountNav(true)
    const labels = labelsOf(wrapper)

    expect(labels).toEqual([
      "Media home",
      "File library",
      "Libraries",
      "People",
      "Studios",
      "Series",
      "Watch later",
      "Following",
      "History",
    ])

    const links = wrapper.findAll(".olm-nav a.olm-nav-link")
    expect(links[1]!.attributes("href")).toBe("/files")
  })

  it("登录后第二组是「我的」四项（待下载 / 播放列表 / 口味 / 任务）", async () => {
    const wrapper = await mountNav(true)
    const extra = wrapper.findAll(".olm-nav-extra .olm-nav-link").map((link) => link.text())

    expect(extra).toEqual(["Wanted", "Playlists", "Taste", "Tasks"])
  })
})
