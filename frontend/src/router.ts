import {
  createRouter,
  createWebHashHistory,
  type RouteRecordRaw,
  type RouterHistory,
} from "vue-router"
import { onUnauthorized } from "~/api"
import { basePath } from "~/api/config"
import { t } from "~/i18n"
import { pinia } from "~/stores"
import { useInitStore } from "~/stores/init"
import { useSettingsStore } from "~/stores/settings"
import { useUserStore } from "~/stores/user"

/**
 * 路由。
 *
 * 用 hash 模式：桌面端（Avalonia）与反代场景下 dist 可能挂在任意前缀/自定义协议上，
 * history 模式需要服务端 rewrite 配合，hash 模式零配置。
 *
 * 但外部链接（旧前端书签、文档、第三方面板）写的是**路径式**深链
 * （`http://host/@login?redirect=/`）。hash 模式下这种地址的 pathname 会被忽略，
 * 打开后落在 `#/`（首页）—— 看起来「登录页/安装页根本进不去」。所以在建 router 之前
 * 先把它归一化成 `/#/@login?...`，两种写法就都能用了。
 */
function normalizePathStyleDeepLink() {
  if (typeof window === "undefined") return
  if (window.location.hash) return // 已经是 hash 路由，别动
  const prefix = basePath.endsWith("/") ? basePath.slice(0, -1) : basePath
  const { pathname, search } = window.location
  const rel = prefix && pathname.startsWith(prefix) ? pathname.slice(prefix.length) : pathname
  if (!rel.startsWith("/@")) return
  window.history.replaceState(null, "", `${prefix}/#${rel}${search}`)
}

normalizePathStyleDeepLink()

const routes: RouteRecordRaw[] = [
  {
    // 安装向导。没有管理员账号时全局守卫会把人强制送到这里（见下方 beforeEach）。
    path: "/@init",
    name: "init",
    component: () => import("~/pages/init"),
    meta: { layout: "blank", title: "init.title" },
  },
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
      // 站点默认落地页是**媒体库**；文件浏览（首页）降级成独立页面 `/files`。
      // 但旧书签/文档里的文件深链是 `/?path=/a/b`（没有 /files 前缀），这里按
      // 「带 path 就等于要去浏览文件」把这类地址转给首页，否则一刷新就被甩到媒体页。
      {
        path: "",
        // 落地页对**已登录**用户是媒体库；访客留在文件浏览。
        // 媒体域（/api/media/**）全部需要登录，把未登录访客送到 /media 只会让页面挂载时
        // 连发几个 401、被全局 401 处理踢回登录页 —— 而登录页的「以访客身份浏览」又回到
        // `/`，形成「点游客 → 登录页 → 点游客」的死循环。
        redirect: (to) => {
          if (to.query.path) return { name: "home", query: to.query }
          return useUserStore(pinia).isLogin ? { name: "media" } : { name: "home" }
        },
      },
      {
        path: "files",
        name: "home",
        component: () => import("~/pages/home"),
        meta: { title: "global.nav_home" },
      },
      {
        path: "media",
        name: "media",
        component: () => import("~/pages/media"),
        meta: { title: "media.title", wide: true, requiresAuth: true },
      },
      {
        path: "media/item/:id",
        name: "media-item",
        component: () => import("~/pages/media/ItemDetail"),
        meta: { title: "media.title", wide: true, requiresAuth: true },
      },
      // ---- 媒体域维度页（M4 第三片）----
      // 片商/系列共用一个列表组件与一个详情组件，靠 meta.kind 区分维度
      {
        path: "media/persons",
        name: "media-persons",
        component: () => import("~/pages/media/PersonList"),
        meta: { title: "media.persons", wide: true, requiresAuth: true },
      },
      {
        path: "media/persons/:id(\\d+)",
        name: "media-person",
        component: () => import("~/pages/media/PersonDetail"),
        meta: { title: "media.persons", wide: true, requiresAuth: true },
      },
      {
        path: "media/studios",
        name: "media-studios",
        component: () => import("~/pages/media/TaxonomyList"),
        meta: { title: "media.studios", kind: "studios", wide: true, requiresAuth: true },
      },
      {
        path: "media/studios/:id(\\d+)",
        name: "media-studio",
        component: () => import("~/pages/media/TaxonomyDetail"),
        meta: { title: "media.studios", kind: "studios", wide: true, requiresAuth: true },
      },
      {
        path: "media/series",
        name: "media-series",
        component: () => import("~/pages/media/TaxonomyList"),
        meta: { title: "media.series", kind: "series", wide: true, requiresAuth: true },
      },
      {
        path: "media/series/:id(\\d+)",
        name: "media-series-detail",
        component: () => import("~/pages/media/TaxonomyDetail"),
        meta: { title: "media.series", kind: "series", wide: true, requiresAuth: true },
      },
      {
        path: "media/playlists",
        name: "media-playlists",
        component: () => import("~/pages/media/Playlists"),
        meta: { title: "media.tab_playlists", wide: true, requiresAuth: true },
      },
      {
        path: "media/playlists/:id(\\d+)",
        name: "media-playlist",
        component: () => import("~/pages/media/PlaylistDetail"),
        meta: { title: "media.tab_playlists", wide: true, requiresAuth: true },
      },
      {
        path: "media/taste",
        name: "media-taste",
        component: () => import("~/pages/media/Taste"),
        meta: { title: "media.tab_taste", wide: true, requiresAuth: true },
      },
    ],
  },
  {
    // 管理台是**独立整页布局**（顶部标题栏 + 图标侧栏 + 内容区），不再套在浏览外壳里。
    // 对应旧前端 pages/manage/index.tsx —— 它同样是一套自成一体的 chrome。
    path: "/@manage",
    component: () => import("~/layouts/ManageLayout"),
    meta: { title: "manage.title", admin: true },
    children: [
      { path: "", redirect: { name: "dashboard" } },
      {
        path: "dashboard",
        name: "dashboard",
        component: () => import("~/pages/manage/dashboard/Dashboard"),
        meta: { title: "manage.sidemenu.dashboard" },
      },
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
      },
      {
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
        path: "messenger",
        name: "messenger",
        component: () => import("~/pages/manage/messenger/Messenger"),
        meta: { title: "manage.sidemenu.messenger" },
      },
      {
        path: "settings",
        name: "settings",
        component: () => import("~/pages/manage/settings/Settings"),
        meta: { title: "manage.sidemenu.settings" },
      },
      {
        path: "backup-restore",
        name: "backup-restore",
        component: () => import("~/pages/manage/backup/BackupRestore"),
        meta: { title: "manage.sidemenu.backup-restore" },
      },
      {
        path: "about",
        name: "about",
        component: () => import("~/pages/manage/about/About"),
        meta: { title: "manage.sidemenu.about" },
      },
    ],
  },
  { path: "/:pathMatch(.*)*", redirect: "/" },
]

const history: RouterHistory = createWebHashHistory()

const router = createRouter({ history, routes })

declare module "vue-router" {
  interface RouteMeta {
    title?: string
    /** 仅管理员可进。 */
    admin?: boolean
    /** 需要登录（默认允许游客浏览，写操作由按钮级权限控制）。 */
    requiresAuth?: boolean
    layout?: string
    /** 内容区放宽到 1280px（媒体库这类多列页面），默认按 OpenList 的 980px 居中列。 */
    wide?: boolean
  }
}

router.beforeEach(async (to) => {
  const settings = useSettingsStore(pinia)
  const user = useUserStore(pinia)
  const init = useInitStore(pinia)

  // 站点设置：登录页/安装页也要用（site_title / ldap / webauthn 开关）。
  // load() 内部有幂等保护，重复调用只会在真正未加载时发一次请求。
  await settings.load()

  // 没有管理员账号时，全站只允许停在安装向导上。这一步必须排在登录判断之前：
  // 否则用户会被送到登录页，而登录页既没有可用账号、也没有去创建的入口。
  if (!init.checked) await init.check()
  if (!init.initialized) {
    return to.name === "init" ? true : { name: "init" }
  }
  if (to.name === "init") return { name: "login" }

  // 有 token 但还没有用户资料时补一次；token 失效会在 loadMe 内部被清掉。
  if (user.isLogin && !user.me) {
    await user.loadMe()
  }

  if (to.meta.requiresAuth && !user.isLogin) {
    return { path: "/@login", query: { redirect: to.fullPath } }
  }

  if (to.meta.admin && !user.isAdmin) {
    // 未登录去登录页（带上回跳），已登录但非管理员回首页 —— 直接踹回首页会
    // 让人以为链接坏了，带 redirect 至少能在登录后回到原处。
    return user.isLogin
      ? { path: "/" }
      : { path: "/@login", query: { redirect: to.fullPath } }
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
