import { computed, defineComponent, onMounted, ref } from "vue"
import { useRouter } from "vue-router"
import { ElEmpty } from "element-plus"
import {
  mediaBatchStates,
  mediaHome,
  mediaUpdateState,
  type MediaItem,
} from "~/api/media"
import MediaGrid from "~/components/MediaGrid"
import MediaNav from "~/pages/media/MediaNav"
import { useUserStore } from "~/stores/user"
import { t } from "~/i18n"

/**
 * 媒体首页（聚合推荐）。
 *
 * 路由 `#/media`（媒体库在 `#/media/library`）。
 * 与「媒体库」（`pages/media/index.tsx`）是**两个界面**：这一页回答「看什么」
 * （继续观看 / 最近入库 / 最近收藏），那一页回答「找什么」（按库、题材、年份、评分筛选）。
 * 之前聚合内容挂在库浏览页顶部，一屏里既有推荐条又有筛选网格，两边都不清爽。
 *
 * 游客也能进（`GET /media/home` 匿名开放），但「继续观看 / 最近收藏」是按用户的数据，
 * 对游客天然是空数组 —— sections 空就不渲染，不需要特判登录态。
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
    const loading = ref(true)
    /** 收藏态缓存：三个 section 共用一份，任一入口切换后同步。 */
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
        if (resp.code !== 200) return
        continueWatching.value = resp.data?.continue_watching ?? []
        recentlyAdded.value = resp.data?.recently_added ?? []
        recentlyFavorited.value = resp.data?.recently_favorited ?? []
        const ids = new Set<number>()
        for (const list of [continueWatching.value, recentlyAdded.value, recentlyFavorited.value]) {
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
        recentlyFavorited.value.length > 0,
    )

    /** 收藏是按用户的状态，游客只读，不渲染星标。 */
    const canFavorite = user.isLogin

    function renderSection(title: string, items: MediaItem[], layout: "grid" | "strip") {
      if (items.length === 0) return null
      return (
        <section class="ol-media-section">
          <div class="ol-media-section-title">{title}</div>
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
        <MediaNav />

        {!loading.value && !hasContent.value ? (
          <ElEmpty description={t("media.home_empty")} />
        ) : (
          <>
            {/* 「继续观看」用横滑条：它是「接着上次看」的临时状态，不该占满整屏 */}
            {renderSection(t("media.continue_watching"), continueWatching.value, "strip")}
            {renderSection(t("media.home_recent"), recentlyAdded.value, "grid")}
            {renderSection(t("media.home_favorites"), recentlyFavorited.value, "grid")}
          </>
        )}
      </div>
    )
  },
})
