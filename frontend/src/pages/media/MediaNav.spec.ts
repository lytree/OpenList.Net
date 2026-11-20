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
 * 1. **「资源库」只在登录后出现**。它指向 `/files`，而文件门面默认要登录
 *    （`home_guest_access=false`）；给游客摆一个点进去就被弹回登录页的入口，
 *    比没有入口更让人困惑。
 * 2. **它紧跟媒体库**。两者是同一批内容的两个视角（刮削后的条目 / 磁盘上的原貌），
 *    排在一起才不会被当成两个互不相干的板块。
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

  it("登录后：资源库排在媒体库之后，指向 /files", async () => {
    const wrapper = await mountNav(true)
    const labels = labelsOf(wrapper)

    expect(labels).toHaveLength(8)
    expect(labels.slice(0, 3)).toEqual(["Media home", "Libraries", "File library"])
    expect(labels.slice(3)).toEqual(["People", "Studios", "Series", "Watch later", "History"])

    const resources = wrapper.findAll(".olm-nav a.olm-nav-link")[2]!
    expect(resources.attributes("href")).toBe("/files")
  })

  it("登录后第二组只有按用户的三项", async () => {
    const wrapper = await mountNav(true)
    const extra = wrapper.findAll(".olm-nav-extra .olm-nav-link").map((link) => link.text())

    expect(extra).toEqual(["Playlists", "Taste", "Tasks"])
  })
})
