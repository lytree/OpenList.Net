import { computed, defineComponent, onMounted, ref, watch } from "vue"
import { useRoute, useRouter } from "vue-router"
import { ElButton, ElEmpty, ElIcon, ElInput } from "element-plus"
import { ArrowLeft, Search } from "@element-plus/icons-vue"
import {
  mediaListTaxonomies,
  type MediaTaxonomyKind,
  type MediaTaxonomySummary,
} from "~/api/media"
import FilePager from "~/pages/home/Pager"
import { t } from "~/i18n"

/**
 * 片商 / 系列列表页 —— `/media/studios` 与 `/media/series` 共用。
 *
 * 两个维度在数据层完全同构（都是 `{id, name, item_count}` + 同名 items 端点），
 * 差别只有文案与排序语义，所以用一个组件 + 路由 meta 上的 `kind` 区分，
 * 而不是复制两份只改了 i18n 键的页面。
 */

const PAGE_SIZE = 30

export default defineComponent({
  name: "MediaTaxonomyList",
  setup() {
    const route = useRoute()
    const router = useRouter()

    /** 维度：来自路由 meta（`studios` | `series`）。 */
    const kind = computed<MediaTaxonomyKind>(() =>
      route.meta.kind === "series" ? "series" : "studios",
    )
    const labelKey = computed(() => (kind.value === "series" ? "media.series" : "media.studios"))

    const keyword = ref("")
    const page = ref(1)
    const rows = ref<MediaTaxonomySummary[]>([])
    const total = ref(0)
    const loading = ref(false)

    async function load() {
      loading.value = true
      try {
        const resp = await mediaListTaxonomies(
          kind.value,
          keyword.value.trim() || undefined,
          page.value,
          PAGE_SIZE,
        )
        if (resp.code === 200) {
          rows.value = resp.data?.content ?? []
          total.value = resp.data?.total ?? 0
        } else {
          rows.value = []
          total.value = 0
        }
      } finally {
        loading.value = false
      }
    }

    onMounted(() => void load())
    // 同一组件在 studios / series 之间切换时组件实例会被复用，必须重新取数
    watch(kind, () => {
      keyword.value = ""
      page.value = 1
      rows.value = []
      void load()
    })

    let keywordTimer: ReturnType<typeof setTimeout> | null = null
    watch(keyword, () => {
      if (keywordTimer) clearTimeout(keywordTimer)
      keywordTimer = setTimeout(() => {
        page.value = 1
        void load()
      }, 350)
    })
    watch(page, () => void load())

    const pages = computed(() => Math.max(1, Math.ceil(total.value / PAGE_SIZE)))

    /**
     * 打开某个片商/系列。
     *
     * 后端没有 `GET /studios/{id}` 单条接口（只有 items），名称取不到；
     * 因此把列表里已知的名字放进 query 带过去，详情页直接用它做标题，
     * 刷新页面丢了 query 就退化成「片商 #id」。
     */
    const open = (row: MediaTaxonomySummary) =>
      void router.push({
        name: kind.value === "series" ? "media-series-detail" : "media-studio",
        params: { id: row.id },
        query: { name: row.name },
      })

    return () => (
      <div class="ol-media">
        <div class="ol-media-toolbar">
          <div class="ol-media-title-row">
            <ElButton size="small" text={true} onClick={() => router.push({ name: "media" })}>
              <ElIcon style={{ marginRight: "4px" }}>
                <ArrowLeft />
              </ElIcon>
              {t("media.back_to_library")}
            </ElButton>
            <span class="ol-media-page-title">{t(labelKey.value)}</span>
            <span class="ol-media-page-sub">{t("media.taxonomy_count", { count: total.value })}</span>
          </div>
          <div class="ol-media-tools">
            <ElInput
              modelValue={keyword.value}
              size="small"
              clearable={true}
              placeholder={t(labelKey.value)}
              prefixIcon={Search}
              style={{ width: "200px" }}
              onUpdate:modelValue={(value: string) => (keyword.value = value)}
            />
          </div>
        </div>

        {rows.value.length === 0 && !loading.value ? (
          <ElEmpty
            description={kind.value === "series" ? t("media.no_series") : t("media.no_studios")}
          />
        ) : (
          <>
            <div class="ol-media-tag-cloud">
              {rows.value.map((row) => (
                <div key={row.id} class="ol-media-taxonomy-chip" onClick={() => open(row)}>
                  <span class="ol-media-taxonomy-name" title={row.name}>
                    {row.name}
                  </span>
                  <span class="ol-media-taxonomy-count">{row.item_count}</span>
                </div>
              ))}
            </div>

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
