import { computed, defineComponent, onMounted, ref, watch } from "vue"
import { useRouter } from "vue-router"
import { ElButton, ElEmpty, ElIcon, ElOption, ElSelect, ElTag } from "element-plus"
import { ArrowLeft } from "@element-plus/icons-vue"
import {
  mediaListLibraries,
  mediaStats,
  mediaTaste,
  type MediaFacetCount,
  type MediaLibrary,
  type MediaStats,
  type TasteProfile,
} from "~/api/media"
import { hoursOf, nameInitial, percentOf, topBuckets } from "~/utils/mediaCatalog"
import { humanSize } from "~/utils/format"
import { t } from "~/i18n"

/**
 * 口味画像 —— `/media/taste`。
 *
 * 两套数据叠在一页，但语义不同，别混：
 * - `GET /media/stats`：**库的**客观统计（数量/体积/时长/分布），按可见库过滤，
 *   所有用户看到的同一库数字相同；
 * - `GET /media/taste`：**本人的**主观偏好（收藏/看过/评分加权），每人不同。
 *
 * 库级统计支持切库（`library_id`），个人口味不支持 —— 偏好是跨库累积的，
 * 按库切会得到「这个库里我爱看什么」，那是另一个问题。
 */

/** 条形图最多展示的桶数：再多就成噪音，且纵向空间不够。 */
const TOP_N = 12

export default defineComponent({
  name: "MediaTaste",
  setup() {
    const router = useRouter()

    const libraries = ref<MediaLibrary[]>([])
    const libraryId = ref(0)
    const stats = ref<MediaStats | null>(null)
    const taste = ref<TasteProfile | null>(null)
    const loading = ref(true)

    async function loadLibraries() {
      const resp = await mediaListLibraries()
      if (resp.code === 200) libraries.value = resp.data ?? []
    }

    async function loadStats() {
      loading.value = true
      try {
        const resp = await mediaStats(libraryId.value || undefined)
        stats.value = resp.code === 200 ? resp.data : null
      } finally {
        loading.value = false
      }
    }

    async function loadTaste() {
      const resp = await mediaTaste()
      taste.value = resp.code === 200 ? resp.data : null
    }

    onMounted(() => {
      void loadLibraries()
      void loadStats()
      void loadTaste()
    })
    watch(libraryId, () => void loadStats())

    /** 条形图的相对宽度以最大桶为 100%，否则小库的条形全都贴地看不见。 */
    const maxOf = (buckets: MediaFacetCount[]) => Math.max(1, ...buckets.map((b) => b.count))

    const genres = computed(() => topBuckets(stats.value?.by_genre ?? [], TOP_N))
    const years = computed(() => topBuckets(stats.value?.by_year ?? [], TOP_N))
    const ratings = computed(() => stats.value?.ratings ?? [])
    const tasteGenres = computed(() => topBuckets(
      (taste.value?.genres ?? []).map((genre) => ({ value: genre.genre, count: genre.weight })),
      TOP_N,
    ))
    const tastePersons = computed(() => (taste.value?.persons ?? []).slice(0, TOP_N))

    const hasTaste = computed(
      () =>
        (taste.value?.favoriteCount ?? 0) > 0 ||
        (taste.value?.watchedCount ?? 0) > 0 ||
        (taste.value?.genres.length ?? 0) > 0,
    )

    function renderBar(value: string, count: number, max: number) {
      return (
        <div key={value} class="ol-media-bar-row">
          <span class="ol-media-bar-label" title={value}>
            {value}
          </span>
          <div class="ol-media-bar-track">
            <div class="ol-media-bar-fill" style={{ width: `${percentOf(count, max)}%` }} />
          </div>
          <span class="ol-media-bar-value">{count}</span>
        </div>
      )
    }

    return () => {
      const overview = stats.value
      return (
        <div class="ol-media">
          <div class="ol-media-toolbar">
            <div class="ol-media-title-row">
              <ElButton size="small" text={true} onClick={() => router.push({ name: "media" })}>
                <ElIcon style={{ marginRight: "4px" }}>
                  <ArrowLeft />
                </ElIcon>
                {t("media.back_to_library")}
              </ElButton>
              <span class="ol-media-page-title">{t("media.tab_taste")}</span>
            </div>
            <div class="ol-media-tools">
              <ElSelect
                modelValue={libraryId.value}
                size="small"
                style={{ width: "160px" }}
                onUpdate:modelValue={(value: number) => (libraryId.value = Number(value))}
              >
                <ElOption label={t("media.all_libraries")} value={0} />
                {libraries.value.map((library) => (
                  <ElOption key={library.id} label={library.name} value={library.id} />
                ))}
              </ElSelect>
            </div>
          </div>

          {!overview && !loading.value ? (
            <ElEmpty description={t("media.no_taste_data")} />
          ) : (
            <>
              <div class="ol-media-stat-cards">
                <div class="ol-media-stat-card">
                  <span class="ol-media-stat-value">{overview?.item_count ?? 0}</span>
                  <span class="ol-media-stat-label">{t("media.stat_items")}</span>
                </div>
                <div class="ol-media-stat-card">
                  <span class="ol-media-stat-value">{overview?.file_count ?? 0}</span>
                  <span class="ol-media-stat-label">{t("media.stat_files")}</span>
                </div>
                <div class="ol-media-stat-card">
                  <span class="ol-media-stat-value">
                    {humanSize(overview?.total_bytes ?? 0)}
                  </span>
                  <span class="ol-media-stat-label">{t("media.stat_size")}</span>
                </div>
                <div class="ol-media-stat-card">
                  <span class="ol-media-stat-value">
                    {t("media.hours_value", { hours: hoursOf(overview?.total_duration_seconds ?? 0) })}
                  </span>
                  <span class="ol-media-stat-label">{t("media.stat_duration")}</span>
                </div>
                <div class="ol-media-stat-card">
                  <span class="ol-media-stat-value">{overview?.watched_count ?? 0}</span>
                  <span class="ol-media-stat-label">{t("media.watched_count")}</span>
                </div>
                <div class="ol-media-stat-card">
                  <span class="ol-media-stat-value">{overview?.favorite_count ?? 0}</span>
                  <span class="ol-media-stat-label">{t("media.favorite_count")}</span>
                </div>
              </div>

              <div class="ol-media-two-col">
                <div class="ol-media-panel">
                  <div class="ol-media-section-title">{t("media.stat_by_genre")}</div>
                  {genres.value.length === 0 ? (
                    <div class="ol-dialog-hint">{t("media.no_taste_data")}</div>
                  ) : (
                    genres.value.map((bucket) => renderBar(bucket.value, bucket.count, maxOf(genres.value)))
                  )}
                </div>
                <div class="ol-media-panel">
                  <div class="ol-media-section-title">{t("media.stat_by_year")}</div>
                  {years.value.length === 0 ? (
                    <div class="ol-dialog-hint">{t("media.no_taste_data")}</div>
                  ) : (
                    years.value.map((bucket) => renderBar(bucket.value, bucket.count, maxOf(years.value)))
                  )}
                </div>
                <div class="ol-media-panel">
                  <div class="ol-media-section-title">{t("media.stat_ratings")}</div>
                  {ratings.value.length === 0 ? (
                    <div class="ol-dialog-hint">{t("media.no_ratings")}</div>
                  ) : (
                    <div class="ol-media-bar-list">
                      {ratings.value.map((bucket) => (
                        <div key={bucket.label} class="ol-media-bar-row">
                          <span class="ol-media-bar-label">★ {bucket.label}</span>
                          <div class="ol-media-bar-track">
                            <div
                              class="ol-media-bar-fill"
                              style={{
                                width: `${percentOf(bucket.count, Math.max(1, ...ratings.value.map((b) => b.count)))}%`,
                              }}
                            />
                          </div>
                          <span class="ol-media-bar-value">{bucket.count}</span>
                        </div>
                      ))}
                    </div>
                  )}
                </div>
                <div class="ol-media-panel">
                  <div class="ol-media-section-title">{t("media.genre_weights")}</div>
                  {!hasTaste.value ? (
                    <div class="ol-dialog-hint">{t("media.no_taste_data")}</div>
                  ) : (
                    <>
                      <div class="ol-media-taste-summary">
                        <ElTag size="small" type="warning">
                          {t("media.favorite_count")}: {taste.value?.favoriteCount ?? 0}
                        </ElTag>
                        <ElTag size="small" type="success">
                          {t("media.watched_count")}: {taste.value?.watchedCount ?? 0}
                        </ElTag>
                        <ElTag size="small" type="info">
                          {t("media.hours_value", {
                            hours: hoursOf(taste.value?.totalWatchSeconds ?? 0),
                          })}
                        </ElTag>
                      </div>
                      {tasteGenres.value.length === 0 ? (
                        <div class="ol-dialog-hint">{t("media.no_taste_data")}</div>
                      ) : (
                        tasteGenres.value.map((bucket) =>
                          renderBar(bucket.value, bucket.count, maxOf(tasteGenres.value)),
                        )
                      )}
                      {tastePersons.value.length > 0 ? (
                        <>
                          <div class="ol-media-section-title" style={{ marginTop: "12px" }}>
                            {t("media.person_weights")}
                          </div>
                          <div class="ol-media-person-chips">
                            {tastePersons.value.map((person) => (
                              <span
                                key={person.personId}
                                class="ol-media-chip"
                                onClick={() =>
                                  router.push({ name: "media-person", params: { id: person.personId } })
                                }
                              >
                                <span class="ol-media-chip-avatar">{nameInitial(person.name)}</span>
                                {person.name}
                                <span class="ol-media-chip-count">{person.weight}</span>
                              </span>
                            ))}
                          </div>
                        </>
                      ) : null}
                    </>
                  )}
                </div>
              </div>
            </>
          )}
        </div>
      )
    }
  },
})
