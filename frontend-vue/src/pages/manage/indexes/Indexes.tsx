import { defineComponent, ref, computed, onMounted, onUnmounted } from "vue"
import {
  ElAlert,
  ElButton,
  ElCard,
  ElForm,
  ElFormItem,
  ElIcon,
  ElInput,
  ElInputNumber,
  ElMessage,
  ElMessageBox,
  ElTag,
} from "element-plus"
import { Refresh, VideoPause, VideoPlay } from "@element-plus/icons-vue"
import {
  buildIndex,
  clearIndex,
  getIndexProgress,
  getScanProgress,
  startScan,
  stopIndex,
  stopScan,
  updateIndex,
} from "~/api/admin"
import type { IndexProgress, ScanProgress } from "~/api/types"
import { formatTime } from "~/utils/format"
import { t } from "~/i18n"

/**
 * 搜索索引 + 手动扫描（M2）。
 *
 * 两个进度接口都把后端对象压成 `{ obj_count, is_done }`（索引另有 last_done_time/error），
 * 所以前端只能做「完成 / 未完成」二态展示，没有百分比 —— 不要去猜 total。
 *
 * 轮询策略：只在 `is_done === false` 时挂 2s 定时器，完成即自动停，避免长期空转。
 */
const POLL_MS = 2000

export default defineComponent({
  name: "IndexesPage",
  setup() {
    const index = ref<IndexProgress>({ obj_count: 0, is_done: true })
    const scan = ref<ScanProgress>({ obj_count: 0, is_done: true })
    const paths = ref("/")
    const maxDepth = ref(20)
    const scanPath = ref("/")
    const busy = ref("")

    let indexTimer: ReturnType<typeof setInterval> | null = null
    let scanTimer: ReturnType<typeof setInterval> | null = null

    function stopIndexPolling() {
      if (indexTimer) clearInterval(indexTimer)
      indexTimer = null
    }
    function stopScanPolling() {
      if (scanTimer) clearInterval(scanTimer)
      scanTimer = null
    }

    async function loadIndex() {
      const resp = await getIndexProgress()
      if (resp.code !== 200) {
        stopIndexPolling()
        ElMessage.error(resp.message)
        return
      }
      index.value = resp.data
      if (resp.data.is_done) stopIndexPolling()
      else if (!indexTimer) indexTimer = setInterval(loadIndex, POLL_MS)
    }

    async function loadScan() {
      const resp = await getScanProgress()
      if (resp.code !== 200) {
        stopScanPolling()
        ElMessage.error(resp.message)
        return
      }
      scan.value = resp.data
      if (resp.data.is_done) stopScanPolling()
      else if (!scanTimer) scanTimer = setInterval(loadScan, POLL_MS)
    }

    onMounted(() => {
      void loadIndex()
      void loadScan()
    })
    onUnmounted(() => {
      stopIndexPolling()
      stopScanPolling()
    })

    /** `/a` `/b` 这类换行输入 → 路径数组；空则回落到根目录。 */
    function parsePaths(): string[] {
      const list = paths.value
        .split("\n")
        .map((s) => s.trim())
        .filter(Boolean)
      return list.length ? list : ["/"]
    }

    async function runIndex(kind: "build" | "update" | "clear" | "stop") {
      if (kind === "build") {
        try {
          await ElMessageBox.confirm(t("indexes.rebuild_confirm"), t("indexes.rebuild"), {
            type: "warning",
          })
        } catch {
          return
        }
      }
      if (kind === "clear") {
        try {
          await ElMessageBox.confirm(t("indexes.clear_confirm"), t("indexes.clear"), {
            type: "warning",
          })
        } catch {
          return
        }
      }
      busy.value = kind
      const resp =
        kind === "build"
          ? await buildIndex({ paths: parsePaths(), max_depth: maxDepth.value })
          : kind === "update"
            ? await updateIndex({ paths: parsePaths(), max_depth: maxDepth.value })
            : kind === "clear"
              ? await clearIndex()
              : await stopIndex()
      busy.value = ""
      if (resp.code !== 200) {
        ElMessage.error(resp.message)
        return
      }
      ElMessage.success(t("indexes.action_success"))
      await loadIndex()
      if (!indexTimer) indexTimer = setInterval(loadIndex, POLL_MS)
    }

    async function onScan(action: "start" | "stop") {
      busy.value = `scan-${action}`
      const resp =
        action === "start" ? await startScan({ path: scanPath.value || "/", limit: 0 }) : await stopScan()
      busy.value = ""
      if (resp.code !== 200) {
        ElMessage.error(resp.message)
        return
      }
      ElMessage.success(t("indexes.action_success"))
      await loadScan()
      if (!scanTimer) scanTimer = setInterval(loadScan, POLL_MS)
    }

    const indexRunning = computed(() => !index.value.is_done)
    const scanRunning = computed(() => !scan.value.is_done)

    return () => (
      <div class="ol-manage-page">
        <ElCard shadow="never">
          <div class="ol-manage-toolbar">
            <h2 class="ol-manage-h2">{t("indexes.index_header")}</h2>
            <div class="ol-manage-actions">
              <ElButton size="small" loading={busy.value === "refresh"} onClick={loadIndex}>
                <ElIcon>
                  <Refresh />
                </ElIcon>
                {t("global.refresh")}
              </ElButton>
            </div>
          </div>

          <div class="ol-stat-row">
            <div class="ol-stat">
              <span class="ol-stat-label">{t("indexes.task_status")}</span>
              <ElTag size="small" type={indexRunning.value ? "warning" : "success"}>
                {indexRunning.value ? t("indexes.running") : t("indexes.idle")}
              </ElTag>
            </div>
            <div class="ol-stat">
              <span class="ol-stat-label">{t("indexes.obj_count")}</span>
              <span class="ol-stat-value">{index.value.obj_count.toLocaleString()}</span>
            </div>
            <div class="ol-stat">
              <span class="ol-stat-label">{t("indexes.last_done_time")}</span>
              <span class="ol-stat-value">
                {index.value.last_done_time
                  ? formatTime(index.value.last_done_time, t("indexes.unknown"))
                  : t("indexes.unknown")}
              </span>
            </div>
          </div>

          {index.value.error && (
            <ElAlert
              type="error"
              showIcon={true}
              closable={false}
              title={`${t("indexes.error")}: ${index.value.error}`}
            />
          )}

          <ElForm labelWidth="180px" class="ol-manage-form">
            <ElFormItem label={t("indexes.paths_to_update")}>
              {{
                default: () => (
                  <ElInput
                    type="textarea"
                    rows={3}
                    modelValue={paths.value}
                    placeholder={"/\n/foo\n/bar"}
                    onUpdate:modelValue={(v: string) => (paths.value = v)}
                  />
                ),
              }}
            </ElFormItem>
            <ElFormItem label={t("indexes.max_depth")}>
              {{
                default: () => (
                  <ElInputNumber
                    modelValue={maxDepth.value}
                    min={1}
                    max={100}
                    onUpdate:modelValue={(v: number | undefined) => (maxDepth.value = v ?? 20)}
                  />
                ),
              }}
            </ElFormItem>
          </ElForm>

          <div class="ol-manage-form-actions">
            <ElButton
              type="primary"
              loading={busy.value === "build"}
              disabled={indexRunning.value}
              onClick={() => runIndex("build")}
            >
              {t("indexes.rebuild")}
            </ElButton>
            <ElButton
              loading={busy.value === "update"}
              disabled={indexRunning.value}
              onClick={() => runIndex("update")}
            >
              {t("indexes.update")}
            </ElButton>
            <ElButton loading={busy.value === "stop"} disabled={!indexRunning.value} onClick={() => runIndex("stop")}>
              {t("indexes.stop")}
            </ElButton>
            <ElButton type="danger" plain={true} loading={busy.value === "clear"} disabled={indexRunning.value} onClick={() => runIndex("clear")}>
              {t("indexes.clear")}
            </ElButton>
          </div>
        </ElCard>

        <ElCard shadow="never" class="ol-manage-card-gap">
          <div class="ol-manage-toolbar">
            <h2 class="ol-manage-h2">{t("indexes.scan_header")}</h2>
          </div>

          <div class="ol-stat-row">
            <div class="ol-stat">
              <span class="ol-stat-label">{t("indexes.task_status")}</span>
              <ElTag size="small" type={scanRunning.value ? "warning" : "success"}>
                {scanRunning.value ? t("indexes.running") : t("indexes.idle")}
              </ElTag>
            </div>
            <div class="ol-stat">
              <span class="ol-stat-label">{t("indexes.obj_count")}</span>
              <span class="ol-stat-value">{scan.value.obj_count.toLocaleString()}</span>
            </div>
          </div>

          <ElForm labelWidth="180px" class="ol-manage-form">
            <ElFormItem label={t("indexes.path_to_scan")}>
              {{
                default: () => (
                  <ElInput
                    modelValue={scanPath.value}
                    placeholder="/"
                    onUpdate:modelValue={(v: string) => (scanPath.value = v)}
                  />
                ),
              }}
            </ElFormItem>
          </ElForm>

          <div class="ol-manage-form-actions">
            <ElButton
              type="primary"
              loading={busy.value === "scan-start"}
              disabled={scanRunning.value}
              onClick={() => onScan("start")}
            >
              <ElIcon>
                <VideoPlay />
              </ElIcon>
              {t("indexes.start")}
            </ElButton>
            <ElButton
              loading={busy.value === "scan-stop"}
              disabled={!scanRunning.value}
              onClick={() => onScan("stop")}
            >
              <ElIcon>
                <VideoPause />
              </ElIcon>
              {t("indexes.stop")}
            </ElButton>
          </div>
        </ElCard>
      </div>
    )
  },
})
