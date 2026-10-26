import { computed, defineComponent, onMounted, ref, watch } from "vue"
import { useRoute, useRouter } from "vue-router"
import { ElButton, ElEmpty, ElIcon, ElTag } from "element-plus"
import { ArrowLeft } from "@element-plus/icons-vue"
import {
  mediaListItems,
  mediaListTaxonomyItems,
  mediaUpdateState,
  type MediaItem,
  type MediaTaxonomyKind,
} from "~/api/media"
import MediaGrid from "~/components/MediaGrid"
import FilePager from "~/pages/home/Pager"
import { t } from "~/i18n"

/**
 * 片商 / 系列详情页 —— `/media/studios/:id` 与 `/media/series/:id`。
 *
 * 只有条目列表接口（`/studios/{id}/items`、`/series/{id}/items`），没有单条详情，
 * 所以标题从 query 的 `name` 来（列表页跳转时带上）；直接输 URL 进来则退化为
 * 「片商 #id」，这是当前后端契约下能做到的最诚实的展示。
 */

const PAGE_SIZE = 30

export default defineComponent({
  name: "MediaTaxonomyDetail",
  setup() {
    const route = useRoute()
    const router = useRouter()

    const kind = computed<MediaTaxonomyKind>(() =>
      route.meta.kind === "series" ? "series" : "studios",
    )
    const labelKey = computed(() => (kind.value === "series" ? "media.series" : "media.studio"))
    const name = computed(() => String(route.query.name ?? ""))

    const items = ref<MediaItem[]>([])
    const total = ref(0)
    const page = ref(1)
    const loading = ref(true)
    const favorites = ref<Set<number>>(new Set())

    async function loadItems() {
      loading.value = true
      try {
        const id = Number(route.params.id)
        const resp = await mediaListTaxonomyItems(kind.value, id, page.value, PAGE_SIZE)
        if (resp.code === 200) {
          items.value = resp.data?.content ?? []
          total.value = resp.data?.total ?? 0
        } else {
          items.value = []
          total.value = 0
        }
      } finally {
        loading.value = false
      }
    }

    async function loadFavorites() {
      const resp = await mediaListItems({ state: "favorite", page: 1, per_page: 100 })
      if (resp.code === 200) {
        favorites.value = new Set((resp.data?.content ?? []).map((item) => item.id))
      }
    }

    onMounted(() => {
      void loadItems()
      void loadFavorites()
    })
    watch([() => route.params.id, kind], () => {
      page.value = 1
      void loadItems()
    })
    watch(page, () => void loadItems())

    const pages = computed(() => Math.max(1, Math.ceil(total.value / PAGE_SIZE)))

    async function toggleFavorite(item: MediaItem) {
      const next = !favorites.value.has(item.id)
      const set = new Set(favorites.value)
      if (next) set.add(item.id)
      else set.delete(item.id)
      favorites.value = set
      const resp = await mediaUpdateState(item.id, { favorite: next })
      if (resp.code !== 200) {
        const rollback = new Set(favorites.value)
        if (next) rollback.delete(item.id)
        else rollback.add(item.id)
        favorites.value = rollback
      }
    }

    const backToList = () =>
      void router.push({ name: kind.value === "series" ? "media-series" : "media-studios" })

    return () => (
      <div class="ol-media">
        <div class="ol-media-toolbar">
          <div class="ol-media-title-row">
            <ElButton size="small" text={true} onClick={backToList}>
              <ElIcon style={{ marginRight: "4px" }}>
                <ArrowLeft />
              </ElIcon>
              {t(kind.value === "series" ? "media.back_to_series" : "media.back_to_studios")}
            </ElButton>
            <span class="ol-media-page-title">
              {name.value || `${t(labelKey.value)} #${route.params.id}`}
            </span>
            <ElTag size="small" type="info">
              {t("media.item_count", { count: total.value })}
            </ElTag>
          </div>
        </div>

        {items.value.length === 0 && !loading.value ? (
          <ElEmpty description={t("media.no_items")} />
        ) : (
          <MediaGrid
            items={items.value}
            favoriteIds={[...favorites.value]}
            onOpen={(item: MediaItem) => router.push({ name: "media-item", params: { id: item.id } })}
            onFavorite={(item: MediaItem) => void toggleFavorite(item)}
          />
        )}

        <FilePager
          page={page.value}
          pageSize={PAGE_SIZE}
          total={total.value}
          pages={pages.value}
          onUpdate:page={(p: number) => (page.value = p)}
        />
      </div>
    )
  },
})
