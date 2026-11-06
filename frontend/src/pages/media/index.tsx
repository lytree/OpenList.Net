import { computed, defineComponent, onMounted, ref, watch } from "vue"
import { useRoute, useRouter } from "vue-router"
import { ElButton, ElEmpty, ElInput, ElOption, ElSelect, ElTag } from "element-plus"
import { Search } from "@element-plus/icons-vue"
import {
  mediaBatchStates,
  mediaFacets,
  mediaListItems,
  mediaListLibraries,
  mediaUpdateState,
  type MediaFacets,
  type MediaItem,
  type MediaItemQuery,
  type MediaLibrary,
} from "~/api/media"
import MediaGrid from "~/components/MediaGrid"
import FilePager from "~/pages/home/Pager"
import MediaNav from "~/pages/media/MediaNav"
import { useUserStore } from "~/stores/user"
import { t } from "~/i18n"

/**
 * 媒体库浏览页（M4 第一片）。
 *
 * 数据面很薄：库列表 + 条目分页查询（筛选/排序/搜索）。
 * 库的管理（建/删/扫描）在管理台，这里只做**看**。
 *
 * **媒体首页（推荐聚合）是另一个界面**（`pages/media/Home.tsx`，`#/media`）：
 * 继续观看 / 最近入库 / 最近收藏都在那边，这一页只负责「按库、题材、年份、评分找条目」。
 * 本页在 `#/media/library`，两者共用 `MediaNav` 的入口条，见 docs/unified-media-product-plan.md 第 48 节。
 *
 * **游客也能进这一页**（只读端点匿名开放，`/` 默认落这里）。凡是「按用户」的东西
 * （收藏星标、「播放列表/口味」入口）只在登录时渲染 —— 否则游客会连发几个 401，
 * 被全局 401 处理踢回登录页。
 *
 * 海报一律走 `/api/media/items/{id}/image` 专用端点 ——
 * 一页 30 张图逐条 fs/get 取签名不可接受，见 `api/media.ts` 里的注释。
 */

const PAGE_SIZE = 30

/** 排序键直接复用 API 查询类型，避免两处联合类型漂移（后端新增排序键时只改 api/media.ts）。 */
type MediaSort = NonNullable<MediaItemQuery["sort"]>

/**
 * 个人状态筛选（value 对应后端 `state` 参数；空串 = 不筛）。
 *
 * 媒体首页的「继续观看 / 最近收藏」段用「查看全部 →」带 `?state=in-progress|favorite`
 * 跳到这里，所以本页必须认这两个值 —— 否则点进来看到的是未筛选的全量列表。
 * 其余状态（fresh/played/flagged/later）没有入口，先不暴露。
 */
const STATES: { value: string; labelKey: string }[] = [
  { value: "", labelKey: "media.filter_all" },
  { value: "favorite", labelKey: "media.favorite" },
  { value: "in-progress", labelKey: "media.continue_watching" },
]

/** 排序选项（value 对应后端 sort 参数）。 */
const SORTS: { value: MediaSort; labelKey: string; desc: boolean }[] = [
  { value: "created", labelKey: "media.sort_added", desc: true },
  // 按当前用户收藏时间倒序：与首页「最近收藏」同序（未收藏的不参与，筛选收藏时才有意义）
  { value: "favorited", labelKey: "media.sort_favorited", desc: true },
  { value: "rating", labelKey: "media.sort_rating", desc: true },
  { value: "year", labelKey: "media.sort_year", desc: true },
  { value: "title", labelKey: "media.sort_title", desc: false },
]

export default defineComponent({
  name: "MediaPage",
  setup() {
    const route = useRoute()
    const router = useRouter()
    const user = useUserStore()

    /** 收藏这类「按用户」的能力只在登录后才有（游客没有个人状态）。 */
    const canPersonalize = computed(() => user.isLogin)

    const libraries = ref<MediaLibrary[]>([])
    const libraryId = ref(0) // 0 = 全部库
    const keyword = ref("")
    /** 初始排序/状态可来自 URL（媒体首页「查看全部」预置筛选），非法值一律退回默认。 */
    const urlState = String(route.query.state ?? "")
    const state = ref(STATES.some((item) => item.value === urlState) ? urlState : "")
    const urlSort = String(route.query.sort ?? "")
    // 收藏筛选的默认排序是「按收藏时间」，与首页「最近收藏」同序；URL 显式给了 sort 则以 URL 为准
    const sort = ref<MediaSort>(
      SORTS.some((item) => item.value === urlSort)
        ? (urlSort as MediaSort)
        : state.value === "favorite"
          ? "favorited"
          : "created",
    )
    const page = ref(1)
    const items = ref<MediaItem[]>([])
    const total = ref(0)
    const loading = ref(false)
    /** 筛选条数据源（题材/年份聚合，只取可见库）。 */
    const facets = ref<MediaFacets | null>(null)
    /** 当前筛选：空串/0 表示不筛。年份精确匹配靠 year_from = year_to。 */
    const genre = ref("")
    const year = ref(0)
    const ratingMin = ref(0)
    /** 收藏态缓存：网格卡片上直接点星切换，不用进详情页。 */
    const favorites = ref<Set<number>>(new Set())

    /** URL 记住当前库与筛选，刷新/返回不掉状态（排序/状态也回写，便于分享当前视图）。 */
    const syncQuery = () => {
      void router.replace({
        path: route.path,
        query: {
          ...route.query,
          library: libraryId.value ? String(libraryId.value) : undefined,
          state: state.value || undefined,
          sort: sort.value === "created" ? undefined : sort.value,
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
          state: state.value || undefined,
        })
        if (resp.code === 200) {
          items.value = resp.data?.content ?? []
          total.value = resp.data?.total ?? 0
        } else {
          items.value = []
          total.value = 0
        }
        await loadFavoriteStates()
      } finally {
        loading.value = false
      }
    }

    /**
     * 收藏态不复用「首页聚合」里的 recently_favorited（那是媒体首页的数据，且只有十几条）。
     * 这里按**当前页**的条目问一次批量状态端点：一次请求，页面里每颗星都是真的。
     */
    async function loadFavoriteStates() {
      if (!user.isLogin || items.value.length === 0) return
      const resp = await mediaBatchStates(items.value.map((item) => item.id))
      if (resp.code !== 200) return
      const set = new Set<number>()
      for (const state of resp.data ?? []) {
        if (state.favorite) set.add(state.item_id)
      }
      favorites.value = set
    }

    /** facets 跟着库切换刷新：换了库，题材/年份的可选项本来就不同。 */
    async function loadFacets() {
      const resp = await mediaFacets(libraryId.value || undefined)
      if (resp.code === 200) facets.value = resp.data
    }

    onMounted(() => {
      void loadLibraries()
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
    /**
     * 状态筛选与库无关（收藏/在追跨库），切换库时保留，不做清空。
     * 收藏筛选默认按收藏时间排序（与首页「最近收藏」同序）；离开收藏时退回默认入库排序。
     * 排序真的变了就交给 `sort` 的 watcher 统一处理，避免一次切换发两轮请求。
     */
    watch(state, () => {
      const wanted: MediaSort =
        state.value === "favorite"
          ? sort.value === "created" ? "favorited" : sort.value
          : sort.value === "favorited" ? "created" : sort.value
      if (wanted !== sort.value) {
        sort.value = wanted
        return
      }
      page.value = 1
      syncQuery()
      void loadItems()
    })
    watch(page, () => void loadItems())

    const pages = computed(() => Math.max(1, Math.ceil(total.value / PAGE_SIZE)))

    /** 题材/年份聚合为空时不渲染那两行（与现状一致）。 */
    const showFacets = computed(
      () => Boolean(facets.value && (facets.value.genres.length > 0 || facets.value.years.length > 0)),
    )

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

    return () => (
      <div class="ol-media">
        <MediaNav />

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

        {/* 筛选条：状态行来自首页「查看全部」的入口，题材/年份行来自 /media/facets */}
        {showFacets.value || canPersonalize.value ? (
          <div class="ol-media-facets">
            {canPersonalize.value ? (
              <div class="ol-media-facet-row">
                <span class="ol-media-facet-label">{t("media.filter_state")}</span>
                {STATES.map((item) => (
                  <ElTag
                    key={item.value || "all"}
                    size="small"
                    class={state.value === item.value ? "ol-media-chip-tag is-on" : "ol-media-chip-tag"}
                    onClick={() => (state.value = item.value)}
                  >
                    {t(item.labelKey)}
                  </ElTag>
                ))}
              </div>
            ) : null}
            {showFacets.value && facets.value ? (
              <>
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
                      <ElOption
                        key={value}
                        label={value === 0 ? t("media.filter_all") : `≥ ${value}`}
                        value={value}
                      />
                    ))}
                  </ElSelect>
                </div>
              </>
            ) : null}
          </div>
        ) : null}

        {libraries.value.length === 0 && !loading.value ? (
          <ElEmpty description={t("media.no_libraries")} />
        ) : (
          <>
            <MediaGrid
              items={items.value}
              favoriteIds={canPersonalize.value ? [...favorites.value] : []}
              showFavorite={canPersonalize.value}
              emptyText={t("media.no_items")}
              onOpen={openItem}
              onFavorite={(item: MediaItem) => void toggleFavorite(item)}
            />

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
