import { computed, defineComponent, onMounted, ref } from "vue"
import { useRouter } from "vue-router"
import { ElEmpty, ElMessage } from "element-plus"
import { mediaBatchStates, mediaListWatchLater, mediaUpdateState, type MediaItem } from "~/api/media"
import MediaGrid from "~/components/MediaGrid"
import FilePager from "~/pages/home/Pager"
import { t } from "~/i18n"

/**
 * 想看清单 —— `/media/watch-later`（需登录）。
 *
 * 数据就是个人状态行的 `watch_later` 标记（条目详情页的「稍后观看」写的就是它）。
 * 与「媒体库 + `state=later`」的区别在于**语义**：这里是一份自己攒下来的待看队列，
 * 所以卡片上带「移除」而不是只有星标，翻页也不带任何筛选条件。
 *
 * 参照 Peach 的 want list：清单里点开条目直接进详情（进播放器），移除是卡片上的
 * 一次性动作，不要求先打开条目再取消勾选。
 */

const PAGE_SIZE = 30

export default defineComponent({
  name: "MediaWatchLaterPage",
  setup() {
    const router = useRouter()

    const items = ref<MediaItem[]>([])
    const favoriteIds = ref<Set<number>>(new Set())
    const total = ref(0)
    const page = ref(1)
    const loading = ref(false)

    async function load() {
      loading.value = true
      try {
        const resp = await mediaListWatchLater(page.value, PAGE_SIZE)
        if (resp.code !== 200) {
          items.value = []
          total.value = 0
          return
        }
        items.value = resp.data?.content ?? []
        total.value = resp.data?.total ?? 0

        // 星标状态单独问：清单里的条目可能早已被收藏，空心的星会让人以为没收藏过。
        const states = await mediaBatchStates(items.value.map((item) => item.id))
        if (states.code === 200) {
          favoriteIds.value = new Set(
            (states.data ?? []).filter((state) => state.favorite).map((state) => state.item_id),
          )
        }
      } finally {
        loading.value = false
      }
    }

    onMounted(() => void load())

    const pages = computed(() => Math.max(1, Math.ceil(total.value / PAGE_SIZE)))

    async function toggleFavorite(item: MediaItem) {
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

    /**
     * 移出清单：先在本地删掉再打接口，失败就整页重取。
     * 成功后如果当前页空了且不是第一页，退一页 —— 否则停在空白页上看着像坏了。
     */
    async function removeFromList(item: MediaItem) {
      const before = items.value
      items.value = items.value.filter((row) => row.id !== item.id)
      total.value = Math.max(0, total.value - 1)
      const resp = await mediaUpdateState(item.id, { watch_later: false })
      if (resp.code !== 200) {
        items.value = before
        total.value = before.length
        ElMessage.error(resp.message)
        return
      }
      if (items.value.length === 0 && page.value > 1) page.value -= 1
      else await load()
    }

    return () => (
      <div class="ol-media">
        <div class="ol-media-toolbar">
          <div class="ol-media-title-row">
            <span class="ol-media-page-title">{t("media.tab_later")}</span>
            <span class="ol-media-page-sub">{t("media.item_count", { count: total.value })}</span>
          </div>
        </div>

        {items.value.length === 0 && !loading.value ? (
          <ElEmpty description={t("media.no_later")} />
        ) : (
          <>
            <MediaGrid
              items={items.value}
              favoriteIds={[...favoriteIds.value]}
              removeLabel={t("media.remove")}
              onOpen={(item: MediaItem) =>
                void router.push({ name: "media-item", params: { id: item.id } })
              }
              onFavorite={(item: MediaItem) => void toggleFavorite(item)}
              onRemove={(item: MediaItem) => void removeFromList(item)}
            />
            <FilePager
              page={page.value}
              pageSize={PAGE_SIZE}
              total={total.value}
              pages={pages.value}
              onUpdate:page={(p: number) => {
                page.value = p
                void load()
              }}
            />
          </>
        )}
      </div>
    )
  },
})
