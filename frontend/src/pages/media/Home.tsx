import { computed, defineComponent, onMounted, ref } from "vue"
import { useRouter } from "vue-router"
import { ElButton, ElEmpty } from "element-plus"
import {
  mediaBatchStates,
  mediaHome,
  mediaListWatchLater,
  mediaUpdateState,
  type MediaItem,
} from "~/api/media"
import MediaGrid from "~/components/MediaGrid"
import { useUserStore } from "~/stores/user"
import { t } from "~/i18n"

/**
 * 媒体首页（聚合推荐）—— 站点的默认落地页。
 *
 * 路由 `/media`（媒体库在 `/media/library`）。与「媒体库」（`pages/media/index.tsx`）
 * 是**两个界面**：这一页回答「看什么」（继续观看 / 想看 / 最近入库 / 最近收藏），
 * 那一页回答「找什么」（按库、题材、年份、评分筛选）。
 *
 * 排列顺序参照 Peach：**接着看**永远在最上面（它是唯一带「未完成」语义的列表），
 * 然后是「想看」（自己攒的清单），最后才是「最近入库 / 最近收藏」这类发现型内容。
 *
 * 游客也能进（`GET /media/home` 匿名开放），但「继续观看 / 想看 / 最近收藏」是按用户的
 * 数据，对游客天然是空数组 —— sections 空就不渲染，不需要特判登录态。
 */

/** 每个 section 取多少条：横滑条 12 条够用，网格比库浏览页的 30 少一档。 */
const LIMIT = 18

export default defineComponent({
  name: "MediaHomePage",
  setup() {
    const router = useRouter()
    const user = useUserStore()

    const continueWatching = ref<MediaItem[]>([])
    const recentlyAdded = ref<MediaItem[]>([])
    const recentlyFavorited = ref<MediaItem[]>([])
    const watchLater = ref<MediaItem[]>([])
    const loading = ref(true)
    /** 收藏态缓存：各 section 共用一份，任一入口切换后同步。 */
    const favoriteIds = ref<Set<number>>(new Set())

    /**
     * 收藏态要单独问一次 `/media/items/states`：
     * `/media/home` 只给「最近收藏」这一个列表，标题里没提到收藏的条目（继续观看、
     * 最近入库）就算已被收藏也会顶着一颗空心星 —— 点一下等于重复收藏。
     */
    async function loadFavoriteStates(ids: number[]) {
      if (!user.isLogin || ids.length === 0) return
      const resp = await mediaBatchStates(ids)
      if (resp.code !== 200) return
      const set = new Set(favoriteIds.value)
      for (const state of resp.data ?? []) {
        if (state.favorite) set.add(state.item_id)
      }
      favoriteIds.value = set
    }

    async function load() {
      loading.value = true
      try {
        const resp = await mediaHome(LIMIT)
        if (resp.code === 200) {
          continueWatching.value = resp.data?.continue_watching ?? []
          recentlyAdded.value = resp.data?.recently_added ?? []
          recentlyFavorited.value = resp.data?.recently_favorited ?? []
        }

        // 「想看」是独立端点（`/media/watch-later`），游客没有这个概念，直接跳过，
        // 免得先发一个 401 再被全局处理器弹去登录页。
        if (user.isLogin) {
          const later = await mediaListWatchLater(1, LIMIT)
          if (later.code === 200) watchLater.value = later.data?.content ?? []
        }

        const ids = new Set<number>()
        for (const list of [
          continueWatching.value,
          recentlyAdded.value,
          recentlyFavorited.value,
          watchLater.value,
        ]) {
          for (const item of list) ids.add(item.id)
        }
        await loadFavoriteStates([...ids])
      } finally {
        loading.value = false
      }
    }

    onMounted(() => void load())

    function openItem(item: MediaItem) {
      void router.push({ name: "media-item", params: { id: item.id } })
    }

    async function toggleFavorite(item: MediaItem) {
      // 乐观更新：星标是纯 UI 状态，失败了再弹回
      const next = !favoriteIds.value.has(item.id)
      const set = new Set(favoriteIds.value)
      if (next) set.add(item.id)
      else set.delete(item.id)
      favoriteIds.value = set
      const resp = await mediaUpdateState(item.id, { favorite: next })
      if (resp.code !== 200) {
        const rollback = new Set(favoriteIds.value)
        if (next) rollback.delete(item.id)
        else rollback.add(item.id)
        favoriteIds.value = rollback
      }
    }

    const favoriteIdList = computed(() => [...favoriteIds.value])
    const hasContent = computed(
      () =>
        continueWatching.value.length > 0 ||
        recentlyAdded.value.length > 0 ||
        recentlyFavorited.value.length > 0 ||
        watchLater.value.length > 0,
    )

    /** 收藏是按用户的状态，游客只读，不渲染星标。 */
    const canFavorite = user.isLogin

    /**
     * 「查看全部」的三种去向：按用户的两条去自己的页面（历史 / 想看），
     * 发现型的两条去媒体库并预置筛选（该页只认 `state` / `sort` 查询参数）。
     */
    function openLibrary(query: Record<string, string>) {
      void router.push({ name: "media-library", query })
    }

    function renderSection(
      title: string,
      items: MediaItem[],
      layout: "grid" | "strip",
      onMore?: () => void,
    ) {
      if (items.length === 0) return null
      return (
        <section class="ol-media-section">
          <div class="ol-media-section-head">
            <div class="ol-media-section-title">{title}</div>
            {onMore ? (
              <ElButton size="small" text={true} onClick={onMore}>
                {t("media.view_all")}
              </ElButton>
            ) : null}
          </div>
          <MediaGrid
            items={items}
            layout={layout}
            favoriteIds={favoriteIdList.value}
            showFavorite={canFavorite}
            onOpen={openItem}
            onFavorite={(item: MediaItem) => void toggleFavorite(item)}
          />
        </section>
      )
    }

    return () => (
      <div class="ol-media">
        {!loading.value && !hasContent.value ? (
          <ElEmpty description={t("media.home_empty")} />
        ) : (
          <>
            {/* 「继续观看」用横滑条：它是「接着上次看」的临时状态，不该占满整屏 */}
            {renderSection(
              t("media.continue_watching"),
              continueWatching.value,
              "strip",
              () => void router.push({ name: "media-history" }),
            )}
            {user.isLogin &&
              renderSection(t("media.tab_later"), watchLater.value, "strip", () =>
                void router.push({ name: "media-watch-later" }),
              )}
            {renderSection(t("media.home_recent"), recentlyAdded.value, "grid", () =>
              openLibrary({ sort: "created" }),
            )}
            {renderSection(t("media.home_favorites"), recentlyFavorited.value, "grid", () =>
              openLibrary({ state: "favorite" }),
            )}
          </>
        )}
      </div>
    )
  },
})
