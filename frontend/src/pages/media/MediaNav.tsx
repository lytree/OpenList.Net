import { computed, defineComponent, h, type Component } from "vue"
import { RouterLink, useRoute } from "vue-router"
import { ElIcon } from "element-plus"
import {
  Clock,
  Collection,
  Film,
  HomeFilled,
  List,
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
 * 入口分级：**浏览类**（首页 / 媒体库 / 演员 / 片商 / 系列）恒在；**按用户**的想看 / 历史
 * 只在登录后出现 —— 给游客一个点进去就被弹回登录页的假入口，比没有入口更让人困惑。
 *
 * 播放列表 / 口味 / 任务中心放在**第二组**（按用户、低频）：它们原来是右上角账户菜单里的
 * 条目，账户区拿掉之后必须有地方接住，否则三个页面变成只能手打地址。分组用的是另一个
 * 容器（`.olm-nav-extra`）而不是同一组后面追加 —— 顶层的「浏览」与「我的」在语义上是两栏。
 */
export default defineComponent({
  name: "MediaNav",
  setup() {
    const route = useRoute()
    const user = useUserStore()

    /**
     * 每项带一个 `prefix` 而不是依赖 RouterLink 的 activeClass：
     * 演员详情（`/media/persons/12`）与演员列表（`/media/persons`）是不同的路由记录，
     * 靠 RouterLink 的匹配规则，详情页上「演员」不会高亮。前缀判断把「列表 + 详情」
     * 当成同一个栏目，符合导航的语义。
     */
    const browse = computed<NavEntry[]>(() => [
      { prefix: "/media", label: t("media.tab_home"), icon: HomeFilled, exact: true },
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
        <nav class="olm-nav">{browse.value.map(link)}</nav>

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
