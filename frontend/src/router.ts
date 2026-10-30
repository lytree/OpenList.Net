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
import { GuestSwitch, guestBlocked } from "~/utils/access"

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
      // 站点有两个**独立门面**，各有自己的访客策略（设置 → 访问控制）：
      // - 媒体（`/media`）：默认**对游客开放**（`media_guest_access=true`），是站点的公开门面，
      //   落地页就是它；媒体域只读端点匿名开放（见 MediaEndpoints）。
      // - 首页（`/files`）：默认**需要登录**（`home_guest_access=false`）—— 文件树属于
      //   「找文件 / 管理」场景。管理员放开这个开关后，游客也能只读浏览（写操作照旧按
      //   权限位隐藏：`/fs/list` 返回的 `write` 为 false）。
      //
      // 媒体域内部再分两层界面：`/media` 是**媒体首页**（继续观看 / 最近入库 /
      // 最近收藏的聚合推荐），`/media/library` 是**媒体库**（按库、题材、年份、评分筛选浏览）。
      // 两者曾经挤在同一页，见 docs/unified-media-product-plan.md 第 48 节。
      //
      // 旧书签/文档里的文件深链是 `/?path=/a/b`（没有 /files 前缀），这类地址照旧
      // 转给首页：游客会被访客策略拦到登录页，登录用户则正常浏览。
      {
        path: "",
        redirect: (to) => (to.query.path ? { name: "home", query: to.query } : { name: "media" }),
      },
      {
        path: "files",
        name: "home",
        component: () => import("~/pages/home"),
        meta: { title: "global.nav_home", requiresAuth: true, guestSwitch: GuestSwitch.home },
      },
      // 媒体域的两个界面：`/media` 是**媒体首页**（聚合推荐，点「媒体」先看到它），
      // `/media/library` 是**媒体库**（按库/题材/年份/评分筛选浏览）。
      // 两者共用 `MediaNav` 的入口条，见 docs/unified-media-product-plan.md 第 48 节。
      {
        path: "media",
        name: "media",
        component: () => import("~/pages/media/Home"),
        meta: { title: "media.tab_home", wide: true, guestSwitch: GuestSwitch.media },
      },
      {
        path: "media/library",
        name: "media-library",
        component: () => import("~/pages/media"),
        meta: { title: "media.tab_libraries", wide: true, guestSwitch: GuestSwitch.media },
      },
      {
        // 兼容：媒体首页刚拆出来时短期用过 `/media/home`
        path: "media/home",
        redirect: { name: "media" },
      },
      {
        path: "media/item/:id",
        name: "media-item",
        component: () => import("~/pages/media/ItemDetail"),
        meta: { title: "media.title", wide: true, guestSwitch: GuestSwitch.media },
      },
      // ---- 媒体域维度页（M4 第三片）----
      // 片商/系列共用一个列表组件与一个详情组件，靠 meta.kind 区分维度
      {
        path: "media/persons",
        name: "media-persons",
        component: () => import("~/pages/media/PersonList"),
        meta: { title: "media.persons", wide: true, guestSwitch: GuestSwitch.media },
      },
      {
        path: "media/persons/:id(\\d+)",
        name: "media-person",
        component: () => import("~/pages/media/PersonDetail"),
        meta: { title: "media.persons", wide: true, guestSwitch: GuestSwitch.media },
      },
      {
        path: "media/studios",
        name: "media-studios",
        component: () => import("~/pages/media/TaxonomyList"),
        meta: { title: "media.studios", kind: "studios", wide: true, guestSwitch: GuestSwitch.media },
      },
      {
        path: "media/studios/:id(\\d+)",
        name: "media-studio",
        component: () => import("~/pages/media/TaxonomyDetail"),
        meta: { title: "media.studios", kind: "studios", wide: true, guestSwitch: GuestSwitch.media },
      },
      {
        path: "media/series",
        name: "media-series",
        component: () => import("~/pages/media/TaxonomyList"),
        meta: { title: "media.series", kind: "series", wide: true, guestSwitch: GuestSwitch.media },
      },
      {
        path: "media/series/:id(\\d+)",
        name: "media-series-detail",
        component: () => import("~/pages/media/TaxonomyDetail"),
        meta: { title: "media.series", kind: "series", wide: true, guestSwitch: GuestSwitch.media },
      },
      // 播放列表与口味画像都是**按用户**的数据（个人片单、观看口味），游客没有，
      // 因此这两个入口保持 requiresAuth：游客点进去先去登录，而不是看空页面。
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
    /**
     * 访客放行开关（公开设置里的键名）。
     *
     * 语义：**未登录能不能进这一页，由这个设置项决定**。
     * - `media_guest_access`（默认 true）：媒体关闭后游客进不去，只能去登录；
     * - `home_guest_access`（默认 false）：首页默认要登录，管理员放开后游客可只读浏览。
     *
     * 与 `requiresAuth` 的分工：只写 `requiresAuth` 表示「与开关无关，必须登录」
     * （播放列表、口味画像、管理台）；两者都写表示「默认要登录，但开关可放开」（首页）。
     */
    guestSwitch?: string
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

  // 访客策略：两个门面各自一个开关（设置 → 访问控制），判定逻辑收在 `utils/access.ts`
  // 里（纯函数，有单测），口径与后端 `IGuestAccessService` 一致 —— 否则会出现
  // 「页面能进、接口 401」或者反过来「接口能进、页面被弹走」。
  if (!user.isLogin && guestBlocked(to.meta, settings.getBool)) {
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
