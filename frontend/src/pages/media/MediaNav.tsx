import { computed, defineComponent } from "vue"
import { useRoute, useRouter } from "vue-router"
import { ElButton } from "element-plus"
import { t } from "~/i18n"
import { useUserStore } from "~/stores/user"

/**
 * 媒体域的页面导航条：首页 / 媒体库 / 人物 / 片商 / 系列 /（播放列表 / 口味）。
 *
 * 媒体首页（`/media`，聚合推荐）与媒体库（`/media/library`，库内浏览）是**两个界面**，
 * 各自只是这套导航里的一个入口 —— 之前它们挤在同一页（库浏览页上方还挂着继续观看横滑条），看着像一件事，
 * 其实一个是「看什么」、一个是「找什么」。见 docs/unified-media-product-plan.md 第 48 节。
 *
 * 播放列表与口味画像都是按用户的数据（个人片单、观看口味），游客不显示入口 ——
 * 它们本身也 requiresAuth，给游客留个假按钮只会在点进去时被弹到登录页。
 */
export default defineComponent({
  name: "MediaNav",
  setup() {
    const route = useRoute()
    const router = useRouter()
    const user = useUserStore()

    const entries = computed(() => [
      { name: "media", label: t("media.tab_home") },
      { name: "media-library", label: t("media.tab_libraries") },
      { name: "media-persons", label: t("media.persons") },
      { name: "media-studios", label: t("media.studios") },
      { name: "media-series", label: t("media.series") },
      ...(user.isLogin
        ? [
            { name: "media-playlists", label: t("media.tab_playlists") },
            { name: "media-taste", label: t("media.tab_taste") },
          ]
        : []),
    ])

    return () => (
      <div class="ol-media-nav">
        {entries.value.map((entry) => {
          const active = route.name === entry.name
          return (
            <ElButton
              key={entry.name}
              size="small"
              type={active ? "primary" : "default"}
              plain={!active}
              onClick={() => {
                if (!active) void router.push({ name: entry.name })
              }}
            >
              {entry.label}
            </ElButton>
          )
        })}
      </div>
    )
  },
})
