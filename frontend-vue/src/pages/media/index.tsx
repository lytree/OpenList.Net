import { computed, defineComponent, onMounted, ref, watch } from "vue"
import { useRoute, useRouter } from "vue-router"
import { ElButton, ElEmpty, ElIcon, ElInput, ElOption, ElSelect, ElTag } from "element-plus"
import { Search, Star, StarFilled, VideoCamera } from "@element-plus/icons-vue"
import {
  MediaItemStatus,
  mediaFacets,
  mediaHome,
  mediaImageLink,
  mediaListItems,
  mediaListLibraries,
  mediaUpdateState,
  type MediaFacets,
  type MediaItem,
  type MediaLibrary,
} from "~/api/media"
import FilePager from "~/pages/home/Pager"
import { t } from "~/i18n"

/**
 * 媒体库浏览页（M4 第一片）。
 *
 * 数据面很薄：库列表 + 条目分页查询 + Home 聚合里的「继续观看」。
 * 库的管理（建/删/扫描）在管理台，这里只做**看与播**。
 *
 * 海报一律走 `/api/media/items/{id}/image` 专用端点 ——
 * 一页 30 张图逐条 fs/get 取签名不可接受，见 `api/media.ts` 里的注释。
 */

const PAGE_SIZE = 30

type MediaSort = "created" | "updated" | "title" | "rating" | "year"

/** 排序选项（value 对应后端 sort 参数）。 */
const SORTS: { value: MediaSort; labelKey: string; desc: boolean }[] = [
  { value: "created", labelKey: "media.sort_added", desc: true },
  { value: "rating", labelKey: "media.sort_rating", desc: true },
  { value: "year", labelKey: "media.sort_year", desc: true },
  { value: "title", labelKey: "media.sort_title", desc: false },
]

export default defineComponent({
  name: "MediaPage",
  setup() {
    const route = useRoute()
    const router = useRouter()

    const libraries = ref<MediaLibrary[]>([])
    const libraryId = ref(0) // 0 = 全部库
    const keyword = ref("")
    const sort = ref<MediaSort>("created")
    const page = ref(1)
    const items = ref<MediaItem[]>([])
    const total = ref(0)
    const loading = ref(false)
    const continueWatching = ref<MediaItem[]>([])
    /** 筛选条数据源（题材/年份聚合，只取可见库）。 */
    const facets = ref<MediaFacets | null>(null)
    /** 当前筛选：空串/0 表示不筛。年份精确匹配靠 year_from = year_to。 */
    const genre = ref("")
    const year = ref(0)
    const ratingMin = ref(0)
    /** 收藏态缓存：网格卡片上直接点星切换，不用进详情页。 */
    const favorites = ref<Set<number>>(new Set())
    /** 海报加载失败的条目：换成占位块而不是裂图。 */
    const brokenPosters = ref<Set<number>>(new Set())

    /** URL `?library=` 记住当前库，刷新/返回不掉状态。 */
    const syncQuery = () => {
      void router.replace({
        path: route.path,
        query: {
          ...route.query,
          library: libraryId.value ? String(libraryId.value) : undefined,
        },
      })
    }

    async function loadLibraries() {
      const resp = await mediaListLibraries()
      if (resp.code === 200) libraries.value = resp.data ?? []
      const fromUrl = Number(route.query.library ?? 0)
      libraryId.value = libraries.value.some((lib) => lib.id === fromUrl) ? fromUrl : 0
      void loadFacets()
      void loadItems()
    }

    async function loadItems() {
      loading.value = true
      try {
        const resp = await mediaListItems({
          library_id: libraryId.value || undefined,
          page: page.value,
          per_page: PAGE_SIZE,
          keyword: keyword.value.trim() || undefined,
          sort: sort.value,
          desc: SORTS.find((item) => item.value === sort.value)?.desc,
          genre: genre.value || undefined,
          // 年份是精确匹配：后端只有区间参数，两端设成同一年即可
          year_from: year.value || undefined,
          year_to: year.value || undefined,
          rating_min: ratingMin.value || undefined,
        })
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

    /** facets 跟着库切换刷新：换了库，题材/年份的可选项本来就不同。 */
    async function loadFacets() {
      const resp = await mediaFacets(libraryId.value || undefined)
      if (resp.code === 200) facets.value = resp.data
    }

    onMounted(() => {
      void loadLibraries()
      void mediaHome(12).then((resp) => {
        if (resp.code === 200) {
          continueWatching.value = resp.data?.continue_watching ?? []
          // 继续观看卡片直接反映收藏态，免去详情页往返
          favorites.value = new Set([
            ...favorites.value,
            ...(resp.data?.recently_favorited ?? []).map((item) => item.id),
          ])
        }
      })
    })

    watch([libraryId, sort], () => {
      page.value = 1
      // 换库时清掉库相关的筛选：上一个库才有的题材留在 URL 里会得到空结果
      genre.value = ""
      year.value = 0
      syncQuery()
      void loadFacets()
      void loadItems()
    })
    // 关键词防抖：输入停顿后才真正发请求
    let keywordTimer: ReturnType<typeof setTimeout> | null = null
    watch(keyword, () => {
      if (keywordTimer) clearTimeout(keywordTimer)
      keywordTimer = setTimeout(() => {
        page.value = 1
        void loadItems()
      }, 350)
    })
    watch([genre, year, ratingMin], () => {
      page.value = 1
      void loadItems()
    })
    watch(page, () => void loadItems())

    const pages = computed(() => Math.max(1, Math.ceil(total.value / PAGE_SIZE)))

    function openItem(item: MediaItem) {
      void router.push({ name: "media-item", params: { id: item.id } })
    }

    async function toggleFavorite(item: MediaItem) {
      // 乐观更新：星标是纯 UI 状态，失败了再弹回
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

    const statusTag = (item: MediaItem) => {
      if (item.status === MediaItemStatus.Pending) return t("media.status_pending")
      if (item.status === MediaItemStatus.Unmatched) return t("media.status_unmatched")
      return ""
    }

    function renderCard(item: MediaItem, mode: "grid" | "strip") {
      const broken = brokenPosters.value.has(item.id)
      const fav = favorites.value.has(item.id)
      return (
        <div
          key={`${mode}-${item.id}`}
          class={mode === "grid" ? "ol-media-card" : "ol-media-card is-strip"}
          onClick={() => openItem(item)}
        >
          <div class="ol-media-poster">
            {item.poster_path && !broken ? (
              <img
                src={mediaImageLink(item.id)}
                loading="lazy"
                alt={item.title}
                onError={() => {
                  const set = new Set(brokenPosters.value)
                  set.add(item.id)
                  brokenPosters.value = set
                }}
              />
            ) : (
              <div class="ol-media-poster-fallback">
                <ElIcon><VideoCamera /></ElIcon>
                <span>{t("media.no_poster")}</span>
              </div>
            )}
            {item.rating ? <span class="ol-media-rating">★ {item.rating.toFixed(1)}</span> : null}
            <span
              class={fav ? "ol-media-fav is-on" : "ol-media-fav"}
              title={fav ? t("media.unfavorite") : t("media.favorite")}
              onClick={(event: MouseEvent) => {
                event.stopPropagation()
                void toggleFavorite(item)
              }}
            >
              <ElIcon>{fav ? <StarFilled /> : <Star />}</ElIcon>
            </span>
          </div>
          <div class="ol-media-card-title" title={item.title}>
            {item.title}
          </div>
          <div class="ol-media-card-sub">
            {item.year ? <span>{item.year}</span> : null}
            {statusTag(item) ? <ElTag size="small" type="warning">{statusTag(item)}</ElTag> : null}
          </div>
        </div>
      )
    }

    return () => (
      <div class="ol-media">
        <div class="ol-media-toolbar">
          <div class="ol-media-libs">
            <ElButton
              size="small"
              type={libraryId.value === 0 ? "primary" : "default"}
              onClick={() => (libraryId.value = 0)}
            >
              {t("media.all_libraries")}
            </ElButton>
            {libraries.value.map((lib) => (
              <ElButton
                key={lib.id}
                size="small"
                type={libraryId.value === lib.id ? "primary" : "default"}
                onClick={() => (libraryId.value = lib.id)}
              >
                {lib.name}
              </ElButton>
            ))}
          </div>
          <div class="ol-media-tools">
            <ElInput
              modelValue={keyword.value}
              size="small"
              clearable={true}
              placeholder={t("media.search_placeholder")}
              prefixIcon={Search}
              style={{ width: "200px" }}
              onUpdate:modelValue={(value: string) => (keyword.value = value)}
            />
            <ElSelect
              modelValue={sort.value}
              size="small"
              style={{ width: "130px" }}
              onUpdate:modelValue={(value: string) => (sort.value = value as MediaSort)}
            >
              {SORTS.map((item) => (
                <ElOption key={item.value} label={t(item.labelKey)} value={item.value} />
              ))}
            </ElSelect>
          </div>
        </div>

        {/*
          维度入口：人物 / 片商 / 系列 / 口味 / 播放列表各自独立成页。
          做成按钮而不是 tab，是因为它们是**浏览视图**而不是库内筛选；
          tab 会让人以为切换后还在同一个列表里。
        */}
        <div class="ol-media-dim-nav">
          {[
            { name: "media-persons", label: t("media.persons") },
            { name: "media-studios", label: t("media.studios") },
            { name: "media-series", label: t("media.series") },
            { name: "media-playlists", label: t("media.tab_playlists") },
            { name: "media-taste", label: t("media.tab_taste") },
          ].map((entry) => (
            <ElButton
              key={entry.name}
              size="small"
              plain={true}
              onClick={() => router.push({ name: entry.name })}
            >
              {entry.label}
            </ElButton>
          ))}
        </div>

        {/* 筛选条：数据来自 /media/facets（只含当前库可见条目的聚合） */}
        {facets.value && (facets.value.genres.length > 0 || facets.value.years.length > 0) ? (
          <div class="ol-media-facets">
            <div class="ol-media-facet-row">
              <span class="ol-media-facet-label">{t("media.filter_genre")}</span>
              <ElTag
                size="small"
                class={genre.value === "" ? "ol-media-chip-tag is-on" : "ol-media-chip-tag"}
                onClick={() => (genre.value = "")}
              >
                {t("media.filter_all")}
              </ElTag>
              {facets.value.genres.slice(0, 16).map((bucket) => (
                <ElTag
                  key={bucket.value}
                  size="small"
                  class={genre.value === bucket.value ? "ol-media-chip-tag is-on" : "ol-media-chip-tag"}
                  onClick={() => (genre.value = genre.value === bucket.value ? "" : bucket.value)}
                >
                  {bucket.value} <span class="ol-media-facet-count">{bucket.count}</span>
                </ElTag>
              ))}
            </div>
            <div class="ol-media-facet-row">
              <span class="ol-media-facet-label">{t("media.filter_year")}</span>
              <ElTag
                size="small"
                class={year.value === 0 ? "ol-media-chip-tag is-on" : "ol-media-chip-tag"}
                onClick={() => (year.value = 0)}
              >
                {t("media.filter_all")}
              </ElTag>
              {facets.value.years.slice(0, 16).map((bucket) => {
                const value = Number(bucket.value)
                return (
                  <ElTag
                    key={bucket.value}
                    size="small"
                    class={year.value === value ? "ol-media-chip-tag is-on" : "ol-media-chip-tag"}
                    onClick={() => (year.value = year.value === value ? 0 : value)}
                  >
                    {bucket.value} <span class="ol-media-facet-count">{bucket.count}</span>
                  </ElTag>
                )
              })}
              <span class="ol-media-facet-label" style={{ marginLeft: "12px" }}>
                {t("media.filter_rating")}
              </span>
              <ElSelect
                modelValue={ratingMin.value}
                size="small"
                style={{ width: "90px" }}
                onUpdate:modelValue={(value: number) => (ratingMin.value = Number(value))}
              >
                {[0, 6, 7, 8, 9].map((value) => (
                  <ElOption key={value} label={value === 0 ? t("media.filter_all") : `≥ ${value}`} value={value} />
                ))}
              </ElSelect>
            </div>
          </div>
        ) : null}

        {libraries.value.length === 0 && !loading.value ? (
          <ElEmpty description={t("media.no_libraries")} />
        ) : (
          <>
            {continueWatching.value.length > 0 && !keyword.value && (
              <div class="ol-media-section">
                <div class="ol-media-section-title">{t("media.continue_watching")}</div>
                <div class="ol-media-strip">
                  {continueWatching.value.map((item) => renderCard(item, "strip"))}
                </div>
              </div>
            )}

            {items.value.length === 0 && !loading.value ? (
              <ElEmpty description={t("media.no_items")} />
            ) : (
              <div class="ol-media-grid">{items.value.map((item) => renderCard(item, "grid"))}</div>
            )}

            <FilePager
              page={page.value}
              pageSize={PAGE_SIZE}
              total={total.value}
              pages={pages.value}
              onUpdate:page={(p: number) => (page.value = p)}
            />
          </>
        )}
      </div>
    )
  },
})
