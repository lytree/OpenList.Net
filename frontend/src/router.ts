import { createRouter, createWebHashHistory, type RouteRecordRaw } from "vue-router"
import { onUnauthorized } from "~/api"
import { t } from "~/i18n"
import { pinia } from "~/stores"
import { useSettingsStore } from "~/stores/settings"
import { useUserStore } from "~/stores/user"

/**
 * 路由。
 *
 * 用 hash 模式：桌面端（Avalonia）与反代场景下 dist 可能挂在任意前缀/自定义协议上，
 * history 模式需要服务端 rewrite 配合，hash 模式零配置。旧前端同样用 hash。
 */
const routes: RouteRecordRaw[] = [
  {
    path: "/@login",
    name: "login",
    component: () => import("~/pages/login"),
    meta: { layout: "blank", title: "login.login" },
  },
  {
    // 公开分享页：不带应用外壳、不带登录态，仅凭分享码访问（见 api/sharePublic.ts）
    path: "/s/:code",
    name: "share",
    component: () => import("~/pages/share"),
    meta: { layout: "blank", title: "shares.title" },
  },
  {
    path: "/",
    component: () => import("~/layouts/BasicLayout"),
    children: [
      {
        path: "",
        name: "home",
        component: () => import("~/pages/home"),
        meta: { title: "global.nav_home" },
      },
      {
        path: "media",
        name: "media",
        component: () => import("~/pages/media"),
        meta: { title: "media.title" },
      },
      {
        path: "media/item/:id",
        name: "media-item",
        component: () => import("~/pages/media/ItemDetail"),
        meta: { title: "media.title" },
      },
      // ---- 媒体域维度页（M4 第三片）----
      // 片商/系列共用一个列表组件与一个详情组件，靠 meta.kind 区分维度
      {
        path: "media/persons",
        name: "media-persons",
        component: () => import("~/pages/media/PersonList"),
        meta: { title: "media.persons" },
      },
      {
        path: "media/persons/:id(\\d+)",
        name: "media-person",
        component: () => import("~/pages/media/PersonDetail"),
        meta: { title: "media.persons" },
      },
      {
        path: "media/studios",
        name: "media-studios",
        component: () => import("~/pages/media/TaxonomyList"),
        meta: { title: "media.studios", kind: "studios" },
      },
      {
        path: "media/studios/:id(\\d+)",
        name: "media-studio",
        component: () => import("~/pages/media/TaxonomyDetail"),
        meta: { title: "media.studios", kind: "studios" },
      },
      {
        path: "media/series",
        name: "media-series",
        component: () => import("~/pages/media/TaxonomyList"),
        meta: { title: "media.series", kind: "series" },
      },
      {
        path: "media/series/:id(\\d+)",
        name: "media-series-detail",
        component: () => import("~/pages/media/TaxonomyDetail"),
        meta: { title: "media.series", kind: "series" },
      },
      {
        path: "media/playlists",
        name: "media-playlists",
        component: () => import("~/pages/media/Playlists"),
        meta: { title: "media.tab_playlists" },
      },
      {
        path: "media/playlists/:id(\\d+)",
        name: "media-playlist",
        component: () => import("~/pages/media/PlaylistDetail"),
        meta: { title: "media.tab_playlists" },
      },
      {
        path: "media/taste",
        name: "media-taste",
        component: () => import("~/pages/media/Taste"),
        meta: { title: "media.tab_taste" },
      },
      {
        path: "@manage",
        component: () => import("~/layouts/ManageLayout"),
        meta: { title: "manage.title", admin: true },
        children: [
          { path: "", redirect: { name: "storages" } },
          {
            path: "storages",
            name: "storages",
            component: () => import("~/pages/manage/storages/Storages"),
            meta: { title: "manage.sidemenu.storages" },
          },
          {
            path: "storages/edit/:id?",
            name: "storage-edit",
            component: () => import("~/pages/manage/storages/StorageEdit"),
            meta: { title: "manage.sidemenu.storages" },
          },
          {
            path: "users",
            name: "users",
            component: () => import("~/pages/manage/users/Users"),
            meta: { title: "manage.sidemenu.users" },
          },
          {
            path: "metas",
            name: "metas",
            component: () => import("~/pages/manage/metas/Metas"),
            meta: { title: "manage.sidemenu.metas" },
          },
          {
            path: "shares",
            name: "shares",
            component: () => import("~/pages/manage/shares/Shares"),
            meta: { title: "manage.sidemenu.shares" },
          },          {
            path: "indexes",
            name: "indexes",
            component: () => import("~/pages/manage/indexes/Indexes"),
            meta: { title: "manage.sidemenu.indexes" },
          },
          {
            path: "tasks",
            name: "tasks",
            component: () => import("~/pages/manage/tasks/Tasks"),
            meta: { title: "manage.sidemenu.tasks" },
          },
          {
            path: "media",
            name: "media-libraries",
            component: () => import("~/pages/manage/media/MediaLibraries"),
            meta: { title: "media.manage.title" },
          },
          {
            path: "settings",
            name: "settings",
            component: () => import("~/pages/manage/settings/Settings"),
            meta: { title: "manage.sidemenu.settings" },
          },
        ],
      },
      {
        path: "spike",
        name: "spike",
        component: () => import("~/pages/spike"),
        meta: { title: "互操作回归页" },
      },
    ],
  },
  { path: "/:pathMatch(.*)*", redirect: "/" },
]

const router = createRouter({
  history: createWebHashHistory(),
  routes,
})

declare module "vue-router" {
  interface RouteMeta {
    title?: string
    /** 仅管理员可进。 */
    admin?: boolean
    /** 需要登录（默认允许游客浏览，写操作由按钮级权限控制）。 */
    requiresAuth?: boolean
    layout?: string
  }
}

router.beforeEach(async (to) => {
  const settings = useSettingsStore(pinia)
  const user = useUserStore(pinia)

  // 站点设置：登录页也要用（site_title / ldap / webauthn 开关）。
  // load() 内部有幂等保护，重复调用只会在真正未加载时发一次请求。
  await settings.load()

  // 有 token 但还没有用户资料时补一次；token 失效会在 loadMe 内部被清掉。
  if (user.isLogin && !user.me) {
    await user.loadMe()
  }

  if (to.meta.requiresAuth && !user.isLogin) {
    return { path: "/@login", query: { redirect: to.fullPath } }
  }

  if (to.meta.admin && !user.isAdmin) {
    return { path: "/" }
  }

  return true
})

router.afterEach((to) => {
  const settings = useSettingsStore(pinia)
  const page = to.meta.title ? t(to.meta.title) : ""
  const site = settings.siteTitle || "OpenList"
  document.title = page && page !== site ? `${page} - ${site}` : site
})

/**
 * 全局 401 处理：清 token + 回登录页，并带上 redirect。
 * 放在这里而不是 client.ts 里，是为了让 HTTP 层不依赖路由（便于单测）。
 */
onUnauthorized(() => {
  const user = useUserStore(pinia)
  user.clear()
  const current = router.currentRoute.value.fullPath
  if (current.startsWith("/@login")) return
  void router.replace({ path: "/@login", query: { redirect: current } })
})

export default router
