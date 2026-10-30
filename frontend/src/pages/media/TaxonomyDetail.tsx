import { computed, defineComponent, onMounted, ref, watch } from "vue"
import { useRoute, useRouter } from "vue-router"
import { ElButton, ElEmpty, ElIcon, ElTag } from "element-plus"
import { ArrowLeft } from "@element-plus/icons-vue"
import {
  mediaListItems,
  mediaListTaxonomyItems,
  mediaTaxonomyDetail,
  mediaUpdateState,
  type MediaItem,
  type MediaTaxonomyKind,
  type MediaTaxonomySummary,
} from "~/api/media"
import MediaGrid from "~/components/MediaGrid"
import FilePager from "~/pages/home/Pager"
import { useUserStore } from "~/stores/user"
import { t } from "~/i18n"

/**
 * 片商 / 系列详情页 —— `/media/studios/:id` 与 `/media/series/:id`。
 *
 * 标题有两级来源：
 * 1. `GET /media/studios|series/{id}`（**真源**）：直接输地址、刷新、从历史记录回来
 *    都能拿到正确名字与条目数；
 * 2. URL 上的 `?name=`：列表页跳转时带过来的，只为**首屏立刻有字**，
 *    详情接口一回来就被覆盖（两者不一致时以接口为准）。
 *
 * 详情接口 404（id 不存在）时显示「找不到」，旧实现会编一个「片商 #99」出来，
 * 那是把「地址错了」伪装成「有这条数据」。
 */

const PAGE_SIZE = 30

export default defineComponent({
  name: "MediaTaxonomyDetail",
  setup() {
    const route = useRoute()
    const router = useRouter()
    const user = useUserStore()

    const kind = computed<MediaTaxonomyKind>(() =>
      route.meta.kind === "series" ? "series" : "studios",
    )
    const labelKey = computed(() => (kind.value === "series" ? "media.series" : "media.studio"))
    /** 列表页带来的名字：仅作首屏占位，接口回来即让位。 */
    const hintName = computed(() => String(route.query.name ?? ""))

    const items = ref<MediaItem[]>([])
    const total = ref(0)
    const page = ref(1)
    const loading = ref(true)
    const favorites = ref<Set<number>>(new Set())
    const detail = ref<MediaTaxonomySummary | null>(null)
    const missing = ref(false)

    const title = computed(() => {
      if (detail.value) return detail.value.name
      if (hintName.value) return hintName.value
      return missing.value ? t("media.not_found") : `${t(labelKey.value)} #${route.params.id}`
    })

    async function loadDetail() {
      const id = Number(route.params.id)
      const resp = await mediaTaxonomyDetail(kind.value, id)
      if (resp.code === 200 && resp.data) {
        detail.value = resp.data
        missing.value = false
      } else {
        detail.value = null
        // 只有 404 才是「真的没有」；其它（403/网络）留空让 hint 顶着，别谎报不存在
        missing.value = resp.code === 404
      }
    }

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
      void loadDetail()
      void loadItems()
      // 游客没有「我的收藏」，这个请求对 user_id=0 只会拿到空集，不如不发
      if (user.isLogin) void loadFavorites()
    })
    watch([() => route.params.id, kind], () => {
      page.value = 1
      detail.value = null
      missing.value = false
      void loadDetail()
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
            <span class="ol-media-page-title">{title.value}</span>
            <ElTag size="small" type="info">
              {t("media.item_count", { count: detail.value?.item_count ?? total.value })}
            </ElTag>
          </div>
        </div>

        {items.value.length === 0 && !loading.value ? (
          <ElEmpty description={t("media.no_items")} />
        ) : (
          <MediaGrid
            items={items.value}
            favoriteIds={[...favorites.value]}
            showFavorite={user.isLogin}
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
