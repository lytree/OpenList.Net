import { computed, defineComponent } from "vue"
import { RouterLink, useRoute } from "vue-router"
import { t } from "~/i18n"
import { useUserStore } from "~/stores/user"

/**
 * 媒体门面的主导航：首页 / 媒体库 / 演员 / 片商 / 系列 +（登录后）想看 / 历史。
 *
 * 位置从页面挪到了**布局**（`layouts/MediaLayout`）。原先这条入口条只在媒体首页与
 * 媒体库两页渲染，人物/片商/系列/播放列表页面上根本没有导航 —— 进到深处只能靠浏览器
 * 后退。导航属于外壳，不属于某一页。
 *
 * 入口分级参照 Peach：**浏览类**（首页 / 媒体库 / 演员 / 片商 / 系列）恒在，
 * **按用户的收藏类**（想看 / 历史 / 片单 / 口味）只在登录后出现。给游客留一个
 * 点进去就被弹到登录页的假入口，比没有入口更让人困惑。
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
    const entries = computed(() => [
      { prefix: "/media", label: t("media.tab_home"), exact: true },
      { prefix: "/media/library", label: t("media.tab_libraries") },
      { prefix: "/media/persons", label: t("media.persons") },
      { prefix: "/media/studios", label: t("media.studios") },
      { prefix: "/media/series", label: t("media.series") },
      ...(user.isLogin
        ? [
            { prefix: "/media/watch-later", label: t("media.tab_later") },
            { prefix: "/media/history", label: t("media.tab_history") },
          ]
        : []),
    ])

    const active = (prefix: string, exact?: boolean) =>
      exact ? route.path === prefix : route.path === prefix || route.path.startsWith(`${prefix}/`)

    return () => (
      <nav class="olm-nav">
        {entries.value.map((entry) => (
          <RouterLink
            key={entry.prefix}
            to={entry.prefix}
            class={["olm-nav-link", active(entry.prefix, entry.exact) && "is-active"]}
          >
            {entry.label}
          </RouterLink>
        ))}
      </nav>
    )
  },
})
