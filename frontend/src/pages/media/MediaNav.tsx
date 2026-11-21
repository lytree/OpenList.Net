import { computed, defineComponent, h, type Component } from "vue"
import { RouterLink, useRoute, useRouter } from "vue-router"
import { ElIcon } from "element-plus"
import {
  Clock,
  Collection,
  Film,
  FolderOpened,
  HomeFilled,
  List,
  Lock,
  Odometer,
  OfficeBuilding,
  Star,
  User,
} from "@element-plus/icons-vue"
import { t } from "~/i18n"
import { useUserStore } from "~/stores/user"

interface NavEntry {
  /** 高亮判定用的路径前缀；`exact` 表示必须完全相等（首页）。 */
  prefix: string
  label: string
  icon: Component
  exact?: boolean
}

/**
 * 媒体门面的主导航：**竖排**，住在左栏（`layouts/MediaLayout`）里。
 *
 * 版式参照 Peach 的侧栏：图标 + 文字、一行一项、选中项是一块圆角胶囊。横排导航在栏目
 * 长到十几项时会退化成「一条要横向滑动的截断列表」，而媒体侧恰恰是栏目最多的一侧；
 * 竖排在桌面还能一直挂着分类，不占内容区的上部宽度。
 *
 * 入口分级：**浏览类**（首页 / 资源库 / 媒体库 / 演员 / 片商 / 系列）恒在，其中资源库
 * 只在登录后出现 —— 它指向 `/files`，而文件门面默认要登录（`home_guest_access=false`），
 * 给游客一个点进去就被弹回登录页的假入口比没有入口更让人困惑。**按用户**的想看 / 历史
 * 也只在登录后出现。
 *
 * **「资源库」为什么排在首页之后、媒体库之前**：首页是落地页，出去看内容的第一跳是它；
 * 资源库（磁盘原貌）与媒体库（刮削后的条目）是同一批内容的两种视角，必须相邻。三者
 * 排成「首页 → 资源库 → 媒体库」，两个约束同时满足。
 *
 * 资源库是**唯一一个跨门面的入口**（`/files`，整个外壳换成 `FilesLayout`），这里只做
 * 导航转发、不内嵌渲染：文件树自带面包屑 / 批量操作 / 上传浮条，塞进媒体外壳会让两套
 * 版式互相将就。代价是点进去之后左栏消失，回媒体靠浏览器后退。
 *
 * 左栏**底部**只放「管理」（`layouts/MediaLayout` 的 `.olm-side-foot`）：那是去改配置，
 * 与「去看内容」不同类，所以不和栏目混成一列。
 *
 * 播放列表 / 口味 / 任务中心放在**第二组**（按用户、低频）：它们原来是右上角账户菜单里的
 * 条目，账户区拿掉之后必须有地方接住，否则三个页面变成只能手打地址。分组用的是另一个
 * 容器（`.olm-nav-extra`）而不是同一组后面追加 —— 顶层的「浏览」与「我的」在语义上是两栏。
 *
 * **公开 / 私有**：媒体内容按「库」分区，公开库对所有人（含游客）默认显示，私有库
 * （`MediaLibrary.Private`）只对登录用户可见 —— 后端在 `MediaLibraryService.CanSeeLibrary`
 * 一处收口，所以游客这里的列表里根本不会出现私有库的条目。游客态额外给一个带锁的
 * 「私有资源」入口：它不是假入口，而是一句明确的「这边要登录」，点了直接去登录页并回跳。
 */
export default defineComponent({
  name: "MediaNav",
  setup() {
    const route = useRoute()
    const router = useRouter()
    const user = useUserStore()

    /**
     * 每项带一个 `prefix` 而不是依赖 RouterLink 的 activeClass：
     * 演员详情（`/media/persons/12`）与演员列表（`/media/persons`）是不同的路由记录，
     * 靠 RouterLink 的匹配规则，详情页上「演员」不会高亮。前缀判断把「列表 + 详情」
     * 当成同一个栏目，符合导航的语义。
     */
    const browse = computed<NavEntry[]>(() => [
      { prefix: "/media", label: t("media.tab_home"), icon: HomeFilled, exact: true },
      // 资源库：紧跟首页（出去看内容的第一跳），又紧邻媒体库（两种视角）。登录后才渲染。
      ...(user.isLogin
        ? [{ prefix: "/files", label: t("media.tab_resources"), icon: FolderOpened }]
        : []),
      { prefix: "/media/library", label: t("media.tab_libraries"), icon: Film },
      { prefix: "/media/persons", label: t("media.persons"), icon: User },
      { prefix: "/media/studios", label: t("media.studios"), icon: OfficeBuilding },
      { prefix: "/media/series", label: t("media.series"), icon: Collection },
      ...(user.isLogin
        ? [
            { prefix: "/media/watch-later", label: t("media.tab_later"), icon: Star },
            { prefix: "/media/history", label: t("media.tab_history"), icon: Clock },
          ]
        : []),
    ])

    const mine = computed<NavEntry[]>(() =>
      user.isLogin
        ? [
            { prefix: "/media/playlists", label: t("media.tab_playlists"), icon: List },
            { prefix: "/media/taste", label: t("media.tab_taste"), icon: Odometer },
            { prefix: "/media/tasks", label: t("media.tab_tasks"), icon: Clock },
          ]
        : [],
    )

    const active = (prefix: string, exact?: boolean) =>
      exact ? route.path === prefix : route.path === prefix || route.path.startsWith(`${prefix}/`)

    const link = (entry: NavEntry) => {
      const icon = h(entry.icon)
      return (
        <RouterLink
          key={entry.prefix}
          to={entry.prefix}
          class={["olm-nav-link", active(entry.prefix, entry.exact) && "is-active"]}
          title={entry.label}
        >
          <ElIcon class="olm-nav-icon">{icon}</ElIcon>
          <span class="olm-nav-label">{entry.label}</span>
        </RouterLink>
      )
    }

    return () => (
      <>
        <nav class="olm-nav">
          {browse.value.map(link)}

          {/* 游客态：私有资源不是「点进去会被弹回登录页的假入口」，而是一个明确的
              「这里需要登录」提示 —— 锁图标 + 悬停说明，点了直接去登录页并回跳媒体首页。
              登录后这一项消失（私有库自己会出现在下面的列表里）。 */}
          {!user.isLogin && (
            <button
              type="button"
              class="olm-nav-link olm-nav-locked"
              title={t("media.private_login_hint")}
              onClick={() => void router.push({ name: "login", query: { redirect: "/media" } })}
            >
              <ElIcon class="olm-nav-icon">
                <Lock />
              </ElIcon>
              <span class="olm-nav-label">{t("media.tab_private")}</span>
            </button>
          )}
        </nav>

        {mine.value.length > 0 && (
          <>
            {/* 分组只用一条细线，不加标题：三个条目已经能说明这一栏是「我的」。 */}
            <div class="olm-nav-sep" />
            <nav class="olm-nav-extra">{mine.value.map(link)}</nav>
          </>
        )}
      </>
    )
  },
})
