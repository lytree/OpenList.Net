import { computed, defineComponent, onMounted, onUnmounted, ref } from "vue"
import { useRouter } from "vue-router"
import { ElButton, ElEmpty, ElIcon, ElProgress, ElTag, ElTooltip } from "element-plus"
import { Refresh } from "@element-plus/icons-vue"
import {
  mediaListScanTasks,
  mediaListScrapeTasks,
  type MediaScanTask,
  type MediaScrapeStatus,
} from "~/api/media"
import { formatTime } from "~/utils/format"
import { t } from "~/i18n"

/**
 * 媒体任务中心 —— `/media/tasks`（需登录）。
 *
 * 媒体侧原来只有「点扫描 → 转圈」这一层反馈，进度去哪看？只能去管理台。这一页把
 * 两件**常驻后台任务**摊开：
 *
 * 1. **扫描**（`GET /media/tasks`）：每个库一行，跑着的显示当前路径与进度；
 * 2. **刮削**（`GET /media/scrape/tasks`）：每个库的待刮 / 已刮 / 失败计数。
 *
 * 只读：启动与取消仍在管理台（`/@manage/media`）。理由与 Peach 的任务中心一致 ——
 * 这一页回答「刚才点的东西跑完没有」，不是运维面板；给普通用户摆一排「取消」按钮
 * 只会让批量任务的管理变得模糊。
 *
 * 轮询 2.5 秒，与媒体库管理页同一节奏；页面不可见时不轮询（切到后台标签页不烧 CPU）。
 */

const POLL_MS = 2500

export default defineComponent({
  name: "MediaTasksPage",
  setup() {
    const router = useRouter()

    const scans = ref<MediaScanTask[]>([])
    const scrapes = ref<MediaScrapeStatus[]>([])
    const loading = ref(false)
    const updatedAt = ref("")

    async function load() {
      loading.value = true
      try {
        const [scanResp, scrapeResp] = await Promise.all([
          mediaListScanTasks(),
          mediaListScrapeTasks(),
        ])
        if (scanResp.code === 200) scans.value = scanResp.data ?? []
        if (scrapeResp.code === 200) scrapes.value = scrapeResp.data ?? []
        updatedAt.value = formatTime(new Date().toISOString())
      } finally {
        loading.value = false
      }
    }

    let timer: ReturnType<typeof setInterval> | null = null

    function stopPolling() {
      if (timer) clearInterval(timer)
      timer = null
    }

    function startPolling() {
      stopPolling()
      timer = setInterval(() => {
        if (document.visibilityState === "visible") void load()
      }, POLL_MS)
    }

    onMounted(() => {
      void load()
      startPolling()
    })
    onUnmounted(stopPolling)

    const anyRunning = computed(
      () => scans.value.some((scan) => scan.is_running) || scrapes.value.some((s) => s.running),
    )

    /** 扫描进度百分比：总数未知时给「不确定」形态（striped + 不定值）。 */
    function scanPercent(scan: MediaScanTask): number {
      if (!scan.found || scan.found <= 0) return 0
      return Math.min(100, Math.round((scan.added / scan.found) * 100))
    }

    function scanStateTag(scan: MediaScanTask) {
      if (scan.is_running) return { type: "warning" as const, text: t("media.tasks_running") }
      if (scan.error) return { type: "danger" as const, text: t("media.tasks_failed") }
      if (scan.state === "completed")
        return { type: "success" as const, text: t("media.tasks_completed") }
      return { type: "info" as const, text: scan.state || t("media.tasks_idle") }
    }

    return () => (
      <div class="ol-media">
        <div class="ol-media-toolbar">
          <div class="ol-media-title-row">
            <span class="ol-media-page-title">{t("media.tab_tasks")}</span>
            <span class="ol-media-page-sub">
              {t("media.tasks_updated", { time: updatedAt.value })}
            </span>
          </div>
          <div class="ol-media-tools">
            <ElTag size="small" type={anyRunning.value ? "warning" : "info"}>
              {anyRunning.value ? t("media.tasks_busy") : t("media.tasks_idle")}
            </ElTag>
            <ElButton size="small" text={true} loading={loading.value} onClick={() => void load()}>
              <ElIcon style={{ marginRight: "4px" }}>
                <Refresh />
              </ElIcon>
              {t("global.refresh")}
            </ElButton>
            <ElButton size="small" text={true} onClick={() => router.push("/@manage/media")}>
              {t("media.tasks_manage")}
            </ElButton>
          </div>
        </div>

        <section class="ol-media-section">
          <div class="ol-media-section-head">
            <div class="ol-media-section-title">{t("media.tasks_scan")}</div>
          </div>
          {scans.value.length === 0 ? (
            <ElEmpty description={t("media.tasks_empty")} />
          ) : (
            <div class="olm-task-list">
              {scans.value.map((scan) => {
                const tag = scanStateTag(scan)
                return (
                  <div key={`scan-${scan.library_id}`} class="olm-task-row">
                    <div class="olm-task-main">
                      <div class="olm-task-title">
                        {scan.library_name}
                        <ElTag size="small" type={tag.type}>
                          {tag.text}
                        </ElTag>
                      </div>
                      <div class="olm-task-sub">
                        {scan.is_running
                          ? scan.current_path || scan.path
                          : t("media.tasks_stats", {
                              found: scan.found,
                              added: scan.added,
                              updated: scan.updated,
                              removed: scan.removed,
                            })}
                      </div>
                      {scan.is_running ? (
                        <ElProgress
                          percentage={scanPercent(scan)}
                          strokeWidth={4}
                          showText={false}
                          status="warning"
                        />
                      ) : null}
                    </div>
                    <div class="olm-task-times">
                      <div>{formatTime(scan.started_at)}</div>
                      <div class="olm-task-muted">{formatTime(scan.finished_at)}</div>
                    </div>
                  </div>
                )
              })}
            </div>
          )}
        </section>

        <section class="ol-media-section">
          <div class="ol-media-section-head">
            <div class="ol-media-section-title">{t("media.tasks_scrape")}</div>
          </div>
          {scrapes.value.length === 0 ? (
            <ElEmpty description={t("media.tasks_empty")} />
          ) : (
            <div class="olm-task-list">
              {scrapes.value.map((status) => (
                <div key={`scrape-${status.library_id}`} class="olm-task-row">
                  <div class="olm-task-main">
                    <div class="olm-task-title">
                      {status.library_name}
                      {status.running ? (
                        <ElTag size="small" type="warning">
                          {t("media.tasks_running")}
                        </ElTag>
                      ) : null}
                    </div>
                    <div class="olm-task-sub">
                      {t("media.tasks_scrape_stats", {
                        pending: status.pending,
                        matched: status.matched,
                        unmatched: status.unmatched,
                      })}
                    </div>
                    {status.running && status.batch_size > 0 ? (
                      <ElProgress
                        percentage={Math.min(
                          100,
                          Math.round((status.batch_done / status.batch_size) * 100),
                        )}
                        strokeWidth={4}
                        status={status.failed > 0 ? "exception" : undefined}
                      />
                    ) : null}
                  </div>
                  {status.error ? (
                    <ElTooltip content={status.error} placement="left">
                      <span class="olm-task-error">{t("media.tasks_failed_count", { count: status.failed })}</span>
                    </ElTooltip>
                  ) : (
                    <div class="olm-task-times">
                      <div class="olm-task-muted">
                        {t("media.tasks_failed_count", { count: status.failed })}
                      </div>
                    </div>
                  )}
                </div>
              ))}
            </div>
          )}
        </section>
      </div>
    )
  },
})
