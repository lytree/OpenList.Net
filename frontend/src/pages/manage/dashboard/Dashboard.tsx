import { computed, defineComponent, onMounted, ref } from "vue"
import {
  ElAlert,
  ElButton,
  ElEmpty,
  ElIcon,
  ElSkeleton,
  ElTag,
  ElTooltip,
} from "element-plus"
import { VexTable, VexColumn } from "~/components/table"
import { Refresh } from "@element-plus/icons-vue"
import { getIndexProgress, getScanProgress, listStorages, listUsers } from "~/api/admin"
import { listShares } from "~/api/shares"
import {
  mediaListScrapeTasks,
  mediaStats,
  type MediaScrapeStatus,
  type MediaStats,
} from "~/api/media"
import type {
  AdminUser,
  IndexProgress,
  PageResp,
  Resp,
  ScanProgress,
  ShareInfo,
  StorageWithDriver,
} from "~/api/types"
import {
  libraryHealth,
  libraryMatchRate,
  libraryState,
  libraryStateTagType,
  shareStats,
  storageStats,
  userStats,
} from "~/utils/dashboard"
import { hoursOf, topBuckets } from "~/utils/mediaCatalog"
import { formatTime, humanSize } from "~/utils/format"
import { t } from "~/i18n"

/**
 * 管理台仪表盘 —— `/@manage/dashboard`。
 *
 * 后端**没有**统计端点（承方案 §32.8：上游也没有，仪表盘是各页各取一份）。
 * 所以这里并发拉 7 个既有端点，在本地做计数/分组。几条约定：
 *
 * 1. **不引图表库**：只有条形分布和计数，用 div 画即可；为一张图引 echarts
 *    （300 KB+）不划算，而且暗色变量还要单独适配一遍。
 * 2. **数据源失败不整体爆掉**：7 个请求里有 1 个失败（例如委派管理员没有
 *    `manage_media`，刮削状态就 403），其余面板照常显示，顶部用一条 warning
 *    列出失败的来源 —— 比「整页白屏 + 一个 toast」可诊断得多。
 * 3. **口径以 `utils/dashboard.ts` 为准**：计数逻辑全在那个纯函数文件里，便于单测；
 *    这里只负责取值、拼文案、渲染。
 * 4. 不做自动轮询：仪表盘是「看一眼现状」的页面，进来拉一次 + 手动刷新就够了。
 *    真要盯进度，媒体库管理页与索引页本来就有轮询。
 */

/** 失败来源在界面上显示的名字（也用于拼 warning 文案）。 */
const SOURCES = ["users", "storages", "shares", "stats", "scrape", "index", "scan"] as const
type SourceName = (typeof SOURCES)[number]

/** 分布图最多展示的桶数：再多就成噪音。 */
const TOP_N = 8

export default defineComponent({
  name: "DashboardPage",
  setup() {
    const users = ref<AdminUser[]>([])
    const storages = ref<StorageWithDriver[]>([])
    const shares = ref<ShareInfo[]>([])
    const stats = ref<MediaStats | null>(null)
    const scrape = ref<MediaScrapeStatus[]>([])
    const index = ref<IndexProgress | null>(null)
    const scan = ref<ScanProgress | null>(null)

    const loading = ref(false)
    /** 首次加载完成前不渲染面板，避免「先是一排 0 再跳成真实数字」。 */
    const firstLoad = ref(true)
    const loadedAt = ref("")
    const failed = ref<SourceName[]>([])

    async function load() {
      loading.value = true
      const failures: SourceName[] = []

      // 统一收口：业务失败（code != 200）与网络层异常都只记来源，不抛给调用方，
      // 否则 Promise.all 会在第一个失败处短路，剩下的面板永远拿不到数据。
      async function settle<T>(name: SourceName, run: () => Promise<Resp<T>>): Promise<T | null> {
        try {
          const resp = await run()
          if (resp.code === 200) return (resp.data ?? null) as T | null
        } catch {
          /* 网络层失败：与业务失败同样处理 */
        }
        failures.push(name)
        return null
      }

      const [userPage, storagePage, sharePage, statsData, scrapeRows, indexData, scanData] =
        await Promise.all([
          settle<PageResp<AdminUser>>("users", () => listUsers()),
          settle<PageResp<StorageWithDriver>>("storages", () => listStorages()),
          settle<PageResp<ShareInfo>>("shares", () => listShares()),
          settle<MediaStats>("stats", () => mediaStats()),
          settle<MediaScrapeStatus[]>("scrape", () => mediaListScrapeTasks()),
          settle<IndexProgress>("index", () => getIndexProgress()),
          settle<ScanProgress>("scan", () => getScanProgress()),
        ])

      // 列表类端点都返回 `{ content, total }` 信封，仪表盘只需要 content（total 与长度一致）
      users.value = userPage?.content ?? []
      storages.value = storagePage?.content ?? []
      shares.value = sharePage?.content ?? []
      stats.value = statsData
      scrape.value = scrapeRows ?? []
      index.value = indexData
      scan.value = scanData

      failed.value = failures
      loadedAt.value = new Date().toISOString()
      loading.value = false
      firstLoad.value = false
    }

    onMounted(() => void load())

    const user = computed(() => userStats(users.value))
    const storage = computed(() => storageStats(storages.value))
    const share = computed(() => shareStats(shares.value))
    const health = computed(() => libraryHealth(scrape.value))
    const genres = computed(() => topBuckets(stats.value?.by_genre ?? [], TOP_N))
    const maxDriver = computed(() => Math.max(1, ...storage.value.byDriver.map((b) => b.count)))
    const maxGenre = computed(() => Math.max(1, ...genres.value.map((b) => b.count)))

    /** 一行「索引 / 扫描 / 刮削」的状态展示，避免三块重复 JSX。 */
    function renderSysRow(label: string, running: boolean | null, note: string, error?: string) {
      return (
        <div class="ol-dash-sys-row">
          <span class="ol-dash-sys-label">{label}</span>
          {running === null ? (
            <ElTag size="small" type="info">
              {t("manage.dashboard.unknown")}
            </ElTag>
          ) : (
            <ElTag size="small" type={running ? "primary" : "success"}>
              {running ? t("manage.dashboard.running") : t("manage.dashboard.idle")}
            </ElTag>
          )}
          <span class="ol-dash-sys-note">{note}</span>
          {error ? (
            <ElTooltip content={error} placement="top">
              <span class="ol-dash-err">{t("manage.dashboard.has_error")}</span>
            </ElTooltip>
          ) : null}
        </div>
      )
    }

    return () => {
      const media = stats.value
      const cards: { key: string; value: string | number; label: string; sub: string }[] = [
        {
          key: "users",
          value: user.value.total,
          label: t("manage.dashboard.card_users"),
          sub: t("manage.dashboard.sub_users", {
            admins: user.value.admins,
            disabled: user.value.disabled,
          }),
        },
        {
          key: "storages",
          value: storage.value.total,
          label: t("manage.dashboard.card_storages"),
          sub: t("manage.dashboard.sub_storages", {
            enabled: storage.value.enabled,
            disabled: storage.value.disabled,
          }),
        },
        {
          key: "shares",
          value: share.value.total,
          label: t("manage.dashboard.card_shares"),
          sub: t("manage.dashboard.sub_shares", {
            enabled: share.value.enabled,
            accessed: share.value.accessed,
          }),
        },
        {
          key: "libraries",
          value: media?.library_count ?? 0,
          label: t("manage.dashboard.card_libraries"),
          sub: t("manage.dashboard.sub_libraries", { count: media?.item_count ?? 0 }),
        },
        {
          key: "capacity",
          value: humanSize(media?.total_bytes ?? 0),
          label: t("manage.dashboard.card_capacity"),
          sub: t("manage.dashboard.sub_capacity", {
            hours: hoursOf(media?.total_duration_seconds ?? 0),
            files: media?.file_count ?? 0,
          }),
        },
        {
          key: "index",
          value: (index.value?.obj_count ?? 0).toLocaleString(),
          label: t("manage.dashboard.card_index"),
          sub:
            index.value && !index.value.is_done
              ? t("manage.dashboard.sub_index_running")
              : t("manage.dashboard.sub_index", {
                  time: formatTime(index.value?.last_done_time),
                }),
        },
      ]

      return (
        <div class="ol-manage-page">
          <div class="ol-manage-toolbar">
            <h2 class="ol-manage-h2">{t("manage.dashboard.title")}</h2>
            <div class="ol-manage-actions">
              {loadedAt.value ? (
                <span class="ol-dash-stamp">
                  {t("manage.dashboard.updated_at", { time: formatTime(loadedAt.value) })}
                </span>
              ) : null}
              <ElButton size="small" loading={loading.value} onClick={() => void load()}>
                <ElIcon>
                  <Refresh />
                </ElIcon>
                {t("manage.dashboard.refresh")}
              </ElButton>
            </div>
          </div>

          {failed.value.length > 0 ? (
            <ElAlert
              class="ol-dash-alert"
              type="warning"
              showIcon={true}
              closable={false}
              title={t("manage.dashboard.partial_failed", {
                names: failed.value
                  .map((name) => t(`manage.dashboard.src_${name}`))
                  .join(" / "),
              })}
            />
          ) : null}

          {firstLoad.value ? (
            <ElSkeleton rows={10} animated={true} />
          ) : (
            <>
              <div class="ol-dash-cards">
                {cards.map((card) => (
                  <div class="ol-dash-card" key={card.key}>
                    <span class="ol-dash-card-label">{card.label}</span>
                    <span class="ol-dash-card-value">{card.value}</span>
                    <span class="ol-dash-card-sub">{card.sub}</span>
                  </div>
                ))}
              </div>

              <section class="ol-dash-panel">
                <div class="ol-dash-panel-head">
                  <h3 class="ol-dash-panel-title">{t("manage.dashboard.library_health")}</h3>
                  <span class="ol-dash-panel-note">
                    {t("manage.dashboard.library_health_sub", {
                      matched: health.value.matched,
                      total: health.value.matched + health.value.pending + health.value.unmatched,
                      pending: health.value.pending,
                      unmatched: health.value.unmatched,
                    })}
                  </span>
                </div>
                {scrape.value.length === 0 ? (
                  <ElEmpty description={t("manage.dashboard.no_library")} imageSize={60} />
                ) : (
                  <VexTable data={scrape.value} size="small" rowKey="library_id" border={false}>
                    <VexColumn label={t("manage.dashboard.col_library")} minWidth={160}>
                      {{
                        default: ({ row }) => {
                          const r = row as MediaScrapeStatus
                          return <span title={r.library_name}>{r.library_name}</span>
                        },
                      }}
                    </VexColumn>
                    <VexColumn label={t("manage.dashboard.col_rate")} width={150}>
                      {{
                        default: ({ row }) => {
                          const rate = libraryMatchRate(row as MediaScrapeStatus)
                          return (
                            <div class="ol-dash-meter">
                              <div class="ol-dash-meter-track">
                                <div
                                  class="ol-dash-meter-fill"
                                  style={{ width: `${rate}%` }}
                                  data-rate={rate}
                                />
                              </div>
                              <span class="ol-dash-meter-value">{rate}%</span>
                            </div>
                          )
                        },
                      }}
                    </VexColumn>
                    <VexColumn
                      prop="matched"
                      label={t("manage.dashboard.col_matched")}
                      width={90}
                    />
                    <VexColumn
                      prop="pending"
                      label={t("manage.dashboard.col_pending")}
                      width={90}
                    />
                    <VexColumn
                      prop="unmatched"
                      label={t("manage.dashboard.col_unmatched")}
                      width={100}
                    />
                    <VexColumn label={t("manage.dashboard.col_state")} minWidth={150}>
                      {{
                        default: ({ row }) => {
                          const r = row as MediaScrapeStatus
                          const state = libraryState(r)
                          const tag = (
                            <ElTag size="small" type={libraryStateTagType(state)}>
                              {t(`manage.dashboard.state_${state}`)}
                            </ElTag>
                          )
                          // 运行中补一条「本轮进度」；失败补错误原文的 tooltip。
                          // 其余状态不需要额外信息 —— 表格左侧的计数就是全部内容。
                          if (state === "running") {
                            return (
                              <span class="ol-dash-state">
                                {tag}
                                <span class="ol-dash-state-note">
                                  {r.batch_done}/{r.batch_size}
                                </span>
                              </span>
                            )
                          }
                          if (state === "failed") {
                            return (
                              <ElTooltip content={r.error || "-"} placement="top">
                                <span class="ol-dash-state">{tag}</span>
                              </ElTooltip>
                            )
                          }
                          return <span class="ol-dash-state">{tag}</span>
                        },
                      }}
                    </VexColumn>
                  </VexTable>
                )}
              </section>

              <div class="ol-dash-grid">
                <section class="ol-dash-panel">
                  <div class="ol-dash-panel-head">
                    <h3 class="ol-dash-panel-title">{t("manage.dashboard.driver_dist")}</h3>
                    <span class="ol-dash-panel-note">
                      {t("manage.dashboard.driver_dist_sub", { count: storage.value.enabled })}
                    </span>
                  </div>
                  {storage.value.byDriver.length === 0 ? (
                    <ElEmpty description={t("manage.dashboard.no_data")} imageSize={50} />
                  ) : (
                    <div class="ol-dash-bars">
                      {storage.value.byDriver.map((bucket) => (
                        <div class="ol-dash-bar-row" key={bucket.name}>
                          <span class="ol-dash-bar-label" title={bucket.name}>
                            {bucket.name}
                          </span>
                          <div class="ol-dash-meter-track">
                            <div
                              class="ol-dash-meter-fill"
                              style={{ width: `${(bucket.count / maxDriver.value) * 100}%` }}
                            />
                          </div>
                          <span class="ol-dash-bar-value">{bucket.count}</span>
                        </div>
                      ))}
                    </div>
                  )}
                </section>

                <section class="ol-dash-panel">
                  <div class="ol-dash-panel-head">
                    <h3 class="ol-dash-panel-title">{t("manage.dashboard.genre_dist")}</h3>
                    <span class="ol-dash-panel-note">
                      {t("manage.dashboard.genre_dist_sub", { count: media?.item_count ?? 0 })}
                    </span>
                  </div>
                  {genres.value.length === 0 ? (
                    <ElEmpty description={t("manage.dashboard.no_data")} imageSize={50} />
                  ) : (
                    <div class="ol-dash-bars">
                      {genres.value.map((bucket) => (
                        <div class="ol-dash-bar-row" key={bucket.value}>
                          <span class="ol-dash-bar-label" title={bucket.value}>
                            {bucket.value}
                          </span>
                          <div class="ol-dash-meter-track">
                            <div
                              class="ol-dash-meter-fill"
                              style={{ width: `${(bucket.count / maxGenre.value) * 100}%` }}
                            />
                          </div>
                          <span class="ol-dash-bar-value">{bucket.count}</span>
                        </div>
                      ))}
                    </div>
                  )}
                </section>
              </div>

              <section class="ol-dash-panel">
                <div class="ol-dash-panel-head">
                  <h3 class="ol-dash-panel-title">{t("manage.dashboard.system")}</h3>
                </div>
                {renderSysRow(
                  t("manage.dashboard.sys_index"),
                  index.value ? !index.value.is_done : null,
                  [
                    t("manage.dashboard.objects", { count: index.value?.obj_count ?? 0 }),
                    formatTime(index.value?.last_done_time),
                  ].join(" · "),
                  index.value?.error,
                )}
                {renderSysRow(
                  t("manage.dashboard.sys_scan"),
                  scan.value ? !scan.value.is_done : null,
                  t("manage.dashboard.objects", { count: scan.value?.obj_count ?? 0 }),
                )}
                {renderSysRow(
                  t("manage.dashboard.sys_scrape"),
                  scrape.value.some((row) => row.running) ? true : health.value.libraries > 0 ? false : null,
                  health.value.libraries > 0
                    ? t("manage.dashboard.scrape_running_libs", {
                        count: health.value.running,
                        total: health.value.libraries,
                      })
                    : t("manage.dashboard.no_library"),
                )}
              </section>
            </>
          )}
        </div>
      )
    }
  },
})
