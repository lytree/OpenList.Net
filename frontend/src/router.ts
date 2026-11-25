import {
  createRouter,
  createWebHistory,
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
 * **history 模式**：URL 就是真实路径（`/media/library`、`/files?path=/a`），没有 `#`。
 * 服务端已把未命中的路径回落到 `index.html`（Program.cs 的 `MapFallback("{*path}")`），
 * 所以刷新 / 直接粘贴深链都成立；桌面端（Avalonia）同样是核心起在本机端口上再被
 * WebView 加载，走的是同一条回落，不需要自定义协议的特判。
 *
 * 站点的两个门面是**两棵互相独立的树**，各自一个布局组件、各自一套守卫：
 *
 * - `/media/**` → `MediaLayout`（公开门面，默认落地页）。游客默认可进
 *   （`media_guest_access=true`）；关掉之后连海报都要登录。
 * - `/files/**` → `FilesLayout`（私人门面：文件树属于「找文件 / 管理」）。
 *   默认要求登录（`home_guest_access=false`）；管理员显式放开后游客可只读浏览。
 *
 * 拆成两棵树而不是「一棵树 + 条件渲染」，是因为两者需要的外壳根本不同：媒体要的是
 * 海报墙的宽幅容器与内容导航，文件要的是面包屑与操作条。挤在同一个布局里，
 * 每一处都要写 `route.path.startsWith("/media")` 的分支，游客还会在公开页面上
 * 看到自己进不去的「文件」入口。
 */

/**
 * 老地址兼容（两代）。
 *
 * 1. **hash 模式**（`/#/media/item/1`、`/#/@login?redirect=/`）：旧前端书签、文档、
 *    第三方面板里到处是这种写法。直接换成 history 后这些地址的 pathname 是 `/`，
 *    打开只会落回首页 —— 看起来「收藏的详情页全丢了」。这里就地改写 URL
 *    （`replaceState`，不产生一条多余的历史记录），后续 router 按正常路径解析。
 * 2. **更老的文件深链**（`/?path=/a/b`）不需要在这里管：根路由的 `redirect`
 *    会把它转给 `/files`。
 */
function migrateLegacyHashLink() {
  if (typeof window === "undefined") return
  const { hash } = window.location
  if (!hash.startsWith("#/")) return

  const target = hash.slice(1) // "#/media?x=1" -> "/media?x=1"
  const prefix = basePath.endsWith("/") ? basePath.slice(0, -1) : basePath
  window.history.replaceState(null, "", `${prefix}${target}`)
}

migrateLegacyHashLink()

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

  // ---------------------------------------------------------------- 媒体门面
  {
    path: "/media",
    component: () => import("~/layouts/MediaLayout"),
    meta: { guestSwitch: GuestSwitch.media },
    children: [
      {
        // 媒体首页：继续观看 / 想看 / 最近入库 / 最近收藏。
        // 它是站点的**默认落地页**（`/` 就落在这里），也是对游客开放的那一面。
        path: "",
        name: "media",
        component: () => import("~/pages/media/Home"),
        meta: { title: "media.tab_home", wide: true },
      },
      {
        // 媒体库：按库 / 题材 / 年份 / 评分筛选浏览。与首页是**两个界面**：
        // 这一页回答「找什么」，首页回答「看什么」（见方案文档第 48 节）。
        path: "library",
        name: "media-library",
        component: () => import("~/pages/media"),
        meta: { title: "media.tab_libraries", wide: true },
      },
      {
        // 兼容：媒体首页刚拆出来时短期用过 `/media/home`
        path: "home",
        redirect: { name: "media" },
      },
      {
        path: "item/:id(\\d+)",
        name: "media-item",
        component: () => import("~/pages/media/ItemDetail"),
        meta: { title: "media.title", wide: true },
      },
      // ---- 维度页 ----
      // 片商/系列共用一个列表组件与一个详情组件，靠 meta.kind 区分维度
      {
        path: "persons",
        name: "media-persons",
        component: () => import("~/pages/media/PersonList"),
        meta: { title: "media.persons", wide: true },
      },
      {
        path: "persons/:id(\\d+)",
        name: "media-person",
        component: () => import("~/pages/media/PersonDetail"),
        meta: { title: "media.persons", wide: true },
      },
      {
        path: "studios",
        name: "media-studios",
        component: () => import("~/pages/media/TaxonomyList"),
        meta: { title: "media.studios", kind: "studios", wide: true },
      },
      {
        path: "studios/:id(\\d+)",
        name: "media-studio",
        component: () => import("~/pages/media/TaxonomyDetail"),
        meta: { title: "media.studios", kind: "studios", wide: true },
      },
      {
        path: "series",
        name: "media-series",
        component: () => import("~/pages/media/TaxonomyList"),
        meta: { title: "media.series", kind: "series", wide: true },
      },
      {
        path: "series/:id(\\d+)",
        name: "media-series-detail",
        component: () => import("~/pages/media/TaxonomyDetail"),
        meta: { title: "media.series", kind: "series", wide: true },
      },
      // ---- 按用户的界面 ----
      // 想看 / 历史 / 播放列表 / 口味都是「按用户」的数据，游客没有，所以这些入口
      // 保持 requiresAuth：游客点进去先去登录，而不是看一个空页面。
      {
        path: "watch-later",
        name: "media-watch-later",
        component: () => import("~/pages/media/WatchLater"),
        meta: { title: "media.tab_later", wide: true, requiresAuth: true },
      },
      // 关注更新是**可公开读**的（游客能看更新流，私有库人物/条目自动过滤），
      // 但关注/取关/检查更新要登录；入口本身只给登录用户看，避免空页面。
      {
        path: "follow",
        name: "media-follow",
        component: () => import("~/pages/media/Follow"),
        meta: { title: "media.tab_follow", wide: true },
      },
      // 待下载 = 库外作品清单 + 提交离线下载，纯个人数据。
      {
        path: "wants",
        name: "media-wants",
        component: () => import("~/pages/media/Wants"),
        meta: { title: "media.tab_wants", wide: true, requiresAuth: true },
      },
      {
        path: "history",
        name: "media-history",
        component: () => import("~/pages/media/History"),
        meta: { title: "media.tab_history", wide: true, requiresAuth: true },
      },
      {
        path: "playlists",
        name: "media-playlists",
        component: () => import("~/pages/media/Playlists"),
        meta: { title: "media.tab_playlists", wide: true, requiresAuth: true },
      },
      {
        path: "playlists/:id(\\d+)",
        name: "media-playlist",
        component: () => import("~/pages/media/PlaylistDetail"),
        meta: { title: "media.tab_playlists", wide: true, requiresAuth: true },
      },
      {
        path: "taste",
        name: "media-taste",
        component: () => import("~/pages/media/Taste"),
        meta: { title: "media.tab_taste", wide: true, requiresAuth: true },
      },
      // ---- 任务中心 ----
      // 扫描 / 刮削的进度原来只在管理台可见；媒体侧也需要「我刚点的扫描跑到哪了」，
      // 这一页复用同一批端点，只读。
      {
        path: "tasks",
        name: "media-tasks",
        component: () => import("~/pages/media/Tasks"),
        meta: { title: "media.tab_tasks", wide: true, requiresAuth: true },
      },
    ],
  },

  // ---------------------------------------------------------------- 文件门面
  {
    path: "/files",
    component: () => import("~/layouts/FilesLayout"),
    // 文件树默认要登录；管理员在「访问控制」里放开 `home_guest_access` 后游客可只读浏览
    // （写操作照旧对游客隐藏：`/fs/list` 返回的 `write` 为 false）。
    // 这里**不写** `requiresAuth`：那样会把「开关放开」这条出口焊死。默认要登录是靠
    // `home_guest_access` 的默认值 false 得到的，与后端 `IGuestAccessService` 同源。
    meta: { guestSwitch: GuestSwitch.home },
    children: [
      {
        // 路径仍然走 query（`/files?path=/a/b`）：目录名里什么字符都可能有
        // （`#`、`?`、中文、空格），塞进 path 段会让编码规则与后端 VFS 的匹配
        // 口径产生第二套真相。query 由 `useFileList` 单一驱动，前进/后退天然正确。
        path: "",
        name: "files",
        component: () => import("~/pages/home"),
        meta: { title: "global.nav_home" },
      },
    ],
  },

  // ---------------------------------------------------------------- 落地页
  {
    // `/` 落媒体首页（公开门面）。老的文件深链 `/??path=/a/b` 转给文件树 ——
    // 游客会被访客策略拦到登录页，登录用户则正常浏览。
    path: "/",
    redirect: (to) =>
      to.query.path ? { name: "files", query: { path: to.query.path } } : { name: "media" },
  },

  // ---------------------------------------------------------------- 管理台
  {
    // 管理台是**独立整页布局**（顶部标题栏 + 图标侧栏 + 内容区），不套在浏览外壳里。
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
        path: "users",
        name: "users",
        component: () => import("~/pages/manage/users/Users"),
        meta: { title: "manage.sidemenu.users" },
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
        path: "metas",
        name: "metas",
        component: () => import("~/pages/manage/metas/Metas"),
        meta: { title: "manage.sidemenu.metas" },
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
        // 清理页从媒体库页进（侧栏「媒体」只保留一个入口），所以不占侧栏位。
        path: "media-cleanup",
        name: "media-cleanup",
        component: () => import("~/pages/manage/media/MediaCleanup"),
        meta: { title: "media.cleanup.title" },
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

  // 其余一律回媒体首页（history 模式下用户可能手改出一个不存在的路径）。
  { path: "/:pathMatch(.*)*", redirect: { name: "media" } },
]

const history: RouterHistory = createWebHistory(basePath || "/")

const router = createRouter({
  history,
  routes,
  // 媒体页都是长列表：从详情返回列表时恢复原滚动位置，新页面从顶部开始。
  scrollBehavior: (_to, _from, savedPosition) => savedPosition ?? { top: 0 },
})

declare module "vue-router" {
  interface RouteMeta {
    title?: string
    /** 仅管理员可进。 */
    admin?: boolean
    /** 需要登录（与访客开关无关）。 */
    requiresAuth?: boolean
    /**
     * 访客放行开关（公开设置里的键名），**登录与否由它决定**：
     * - `media_guest_access`（默认 true）：关掉后游客连媒体首页都进不去；
     * - `home_guest_access`（默认 false）：文件树默认要登录，管理员放开后游客可只读浏览。
     *
     * 与 `requiresAuth` 的分工：只写 `requiresAuth` 表示「与开关无关，必须登录」
     * （想看 / 历史 / 播放列表 / 口味 / 任务中心 / 管理台）；两者都写表示
     * 「默认要登录，但开关可放开」（文件树）。
     */
    guestSwitch?: string
    layout?: string
    /** 内容区放宽到 1280px（媒体库这类多列页面），默认按 OpenList 的 980px 居中列。 */
    wide?: boolean
  }
}

/**
 * 登录回跳参数。
 *
 * 必须**自己再编码一次**（router 序列化时还会编一遍，所以地址栏里看到的是 `%252F`）：
 * vue-router 序列化 query 时会把 `%3F` / `%3D` / `%26` 还原成 `?` `=` `&`（为了让 URL
 * 可读），于是 `redirect=/files?path=/a` 会被浏览器解析成 `redirect=/files` + `path=/a`
 * 两个参数 —— 登录后就丢了目录。
 *
 * 现在的链路是「自己编一次 → router 编一次 → 浏览器解析 → router 解一次 → 登录页
 * `safeDecode` 解一次」，最终回到完整的 `/files?path=/a`。代价是 URL 不好看，
 * 换来的是带查询串的深链（文件目录、媒体筛选）登录后不会被吞掉。
 */
function loginRedirect(fullPath: string): string {
  return encodeURIComponent(fullPath)
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
    return { name: "login", query: { redirect: loginRedirect(to.fullPath) } }
  }

  // 管理台：单用户产品下「登录即内置管理员」，因此只按登录态收口 ——
  // 未登录先去登录页（带上回跳）。
  if (to.meta.admin && !user.isLogin) {
    return { name: "login", query: { redirect: loginRedirect(to.fullPath) } }
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
  void router.replace({ name: "login", query: { redirect: loginRedirect(current) } })
})

export default router
