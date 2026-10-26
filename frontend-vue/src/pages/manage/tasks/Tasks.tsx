import { defineComponent, ref, computed, onMounted, onUnmounted, watch } from "vue"
import {
  ElAlert,
  ElButton,
  ElCard,
  ElIcon,
  ElMessage,
  ElMessageBox,
  ElProgress,
  ElRadioButton,
  ElRadioGroup,
  ElSelect,
  ElOption,
  ElSwitch,
  ElTable,
  ElTableColumn,
  ElTag,
} from "element-plus"
import { Delete, Refresh, RefreshRight, VideoPause } from "@element-plus/icons-vue"
import {
  TASK_TYPES,
  listTasks,
  taskAction,
  taskBulkAction,
  type TaskListState,
  type TaskTypeKey,
} from "~/api/tasks"
import type { TaskInfo } from "~/api/types"
import { formatTime, humanSize } from "~/utils/format"
import { t } from "~/i18n"

/**
 * 任务管理（M2）。
 *
 * 三个后端约定必须照顾到：
 * 1. `GET /task/{type}/{done}` 返回**裸数组**，不是 `{ content, total }`。
 * 2. `{type}` 只能是 snake_case 别名（`offline_download`），C# 枚举名会被拒。
 * 3. `state` 是数值枚举，文案在 `tasks.state.{n}`；2 成功、4 取消、5/7 失败/出错，
 *    其余按「进行中」展示。
 *
 * 轮询只在「进行中」页签开启（1.5s），已完成页签不轮询，避免无意义请求。
 */
const POLL_MS = 1500

/** 成功 / 取消 / 失败 三类的具体 state 值（见 `TaskState` 枚举）。 */
const STATE_SUCCEEDED = 2
const STATE_CANCELED = 4
const STATE_ERROR = new Set([5, 6, 7])

function stateType(state: number): "success" | "info" | "warning" | "danger" {
  if (state === STATE_SUCCEEDED) return "success"
  if (state === STATE_CANCELED) return "info"
  if (STATE_ERROR.has(state)) return "danger"
  return "warning"
}

export default defineComponent({
  name: "TasksPage",
  setup() {
    const type = ref<TaskTypeKey>("offline_download")
    const listState = ref<TaskListState>("undone")
    const rows = ref<TaskInfo[]>([])
    const loading = ref(false)
    const error = ref("")
    const autoRefresh = ref(true)
    const busy = ref("")

    let timer: ReturnType<typeof setInterval> | null = null

    function stopPolling() {
      if (timer) clearInterval(timer)
      timer = null
    }

    async function load() {
      loading.value = true
      error.value = ""
      const resp = await listTasks(type.value, listState.value)
      loading.value = false
      if (resp.code !== 200) {
        error.value = resp.message
        stopPolling()
        return
      }
      rows.value = resp.data ?? []
      if (listState.value === "done" || !autoRefresh.value) stopPolling()
      else if (!timer) timer = setInterval(load, POLL_MS)
    }

    function restartPolling() {
      stopPolling()
      if (listState.value === "undone" && autoRefresh.value) timer = setInterval(load, POLL_MS)
    }

    watch([type, listState, autoRefresh], () => {
      void load()
      restartPolling()
    })

    onMounted(load)
    onUnmounted(stopPolling)

    async function onSingle(row: TaskInfo, action: "cancel" | "delete" | "retry") {
      if (action === "delete") {
        try {
          await ElMessageBox.confirm(
            t("global.delete_confirm", { name: row.name }),
            t("global.delete"),
            { type: "warning" },
          )
        } catch {
          return
        }
      }
      busy.value = row.id
      const resp = await taskAction(type.value, action, row.id)
      busy.value = ""
      if (resp.code !== 200) {
        ElMessage.error(resp.message)
        return
      }
      ElMessage.success(t("indexes.action_success"))
      await load()
    }

    async function onBulk(action: "clear_succeeded" | "retry_failed") {
      if (action === "clear_succeeded") {
        try {
          await ElMessageBox.confirm(t("tasks.clear_confirm"), t("tasks.clear_succeeded"), {
            type: "warning",
          })
        } catch {
          return
        }
      }
      busy.value = action
      const resp = await taskBulkAction(type.value, action)
      busy.value = ""
      if (resp.code !== 200) {
        ElMessage.error(resp.message)
        return
      }
      ElMessage.success(t("indexes.action_success"))
      await load()
    }

    function percent(row: TaskInfo): number {
      if (!row.total_bytes) return row.state === STATE_SUCCEEDED ? 100 : 0
      return Math.min(100, Math.round((row.progress / row.total_bytes) * 100))
    }

    const typeLabel = (key: string) => {
      const mapped = t(`tasks.${key}`)
      return mapped === `tasks.${key}` ? key : mapped
    }

    const emptyText = computed(() =>
      listState.value === "done" ? t("tasks.empty_done") : t("tasks.empty_undone"),
    )

    return () => (
      <div class="ol-manage-page">
        <ElCard shadow="never">
          <div class="ol-manage-toolbar">
            <h2 class="ol-manage-h2">{t("manage.sidemenu.tasks")}</h2>
            <div class="ol-manage-actions">
              <ElSelect
                modelValue={type.value}
                size="small"
                style={{ width: "220px" }}
                onUpdate:modelValue={(v: string | number | boolean) =>
                  (type.value = String(v) as TaskTypeKey)
                }
              >
                {TASK_TYPES.map((key) => (
                  <ElOption key={key} label={typeLabel(key)} value={key} />
                ))}
              </ElSelect>
              <ElRadioGroup
                size="small"
                modelValue={listState.value}
                onUpdate:modelValue={(v: string | number | boolean | undefined) =>
                  (listState.value = String(v) as TaskListState)
                }
              >
                <ElRadioButton value="undone" label={t("tasks.undone")} />
                <ElRadioButton value="done" label={t("tasks.done")} />
              </ElRadioGroup>
              <span class="ol-manage-inline">
                <ElSwitch
                  size="small"
                  modelValue={autoRefresh.value}
                  onUpdate:modelValue={(v: string | number | boolean) =>
                    (autoRefresh.value = Boolean(v))
                  }
                />
                <span class="ol-field-help">{t("tasks.auto_refresh")}</span>
              </span>
              <ElButton size="small" loading={loading.value} onClick={load}>
                <ElIcon>
                  <Refresh />
                </ElIcon>
                {t("global.refresh")}
              </ElButton>
            </div>
          </div>

          {error.value && (
            <ElAlert type="error" showIcon={true} title={error.value} closable={false} />
          )}

          <div class="ol-manage-actions ol-manage-bulk">
            <ElButton
              size="small"
              loading={busy.value === "retry_failed"}
              onClick={() => onBulk("retry_failed")}
            >
              <ElIcon>
                <RefreshRight />
              </ElIcon>
              {t("tasks.retry_failed")}
            </ElButton>
            <ElButton
              size="small"
              type="danger"
              plain={true}
              loading={busy.value === "clear_succeeded"}
              onClick={() => onBulk("clear_succeeded")}
            >
              <ElIcon>
                <Delete />
              </ElIcon>
              {t("tasks.clear_succeeded")}
            </ElButton>
          </div>

          <ElTable data={rows.value} size="small" style={{ width: "100%" }} emptyText={emptyText.value}>
            <ElTableColumn label={t("tasks.attr.name")} minWidth={220} showOverflowTooltip={true}>
              {{
                default: ({ row }) => {
                  const r = row as unknown as TaskInfo
                  return (
                    <div class="ol-task-name">
                      <span>{r.name}</span>
                      <span class="ol-mono ol-muted">{r.id}</span>
                    </div>
                  )
                },
              }}
            </ElTableColumn>
            <ElTableColumn label={t("tasks.attr.creator")} prop="creator" width={110} />
            <ElTableColumn label={t("tasks.attr.state")} width={120}>
              {{
                default: ({ row }) => {
                  const r = row as unknown as TaskInfo
                  return (
                    <ElTag size="small" type={stateType(r.state)}>
                      {t(`tasks.state.${r.state}`)}
                    </ElTag>
                  )
                },
              }}
            </ElTableColumn>
            <ElTableColumn label={t("tasks.attr.progress")} minWidth={200}>
              {{
                default: ({ row }) => {
                  const r = row as unknown as TaskInfo
                  return (
                    <div class="ol-task-progress">
                      <ElProgress
                        percentage={percent(r)}
                        strokeWidth={6}
                        status={stateType(r.state) === "danger" ? "exception" : undefined}
                        showText={false}
                      />
                      <span class="ol-mono ol-field-help">
                        {humanSize(r.progress)} / {humanSize(r.total_bytes)}
                      </span>
                    </div>
                  )
                },
              }}
            </ElTableColumn>
            <ElTableColumn label={t("tasks.attr.time_elapsed")} width={170}>
              {{
                default: ({ row }) => {
                  const r = row as unknown as TaskInfo
                  return (
                    <span class="ol-field-help">
                      {formatTime(r.start_time)} → {formatTime(r.end_time)}
                    </span>
                  )
                },
              }}
            </ElTableColumn>
            <ElTableColumn label={t("tasks.attr.err")} minWidth={160} showOverflowTooltip={true}>
              {{
                default: ({ row }) => {
                  const r = row as unknown as TaskInfo
                  return r.error ? (
                    <span class="ol-text-danger">{r.error}</span>
                  ) : (
                    <span class="ol-muted">-</span>
                  )
                },
              }}
            </ElTableColumn>
            <ElTableColumn label={t("tasks.attr.operation")} width={200} align="center">
              {{
                default: ({ row }) => {
                  const r = row as unknown as TaskInfo
                  const done = listState.value === "done"
                  return (
                    <div class="ol-manage-row-ops">
                      {!done && (
                        <ElButton
                          size="small"
                          text={true}
                          loading={busy.value === r.id}
                          onClick={() => onSingle(r, "cancel")}
                        >
                          <ElIcon>
                            <VideoPause />
                          </ElIcon>
                          {t("global.cancel")}
                        </ElButton>
                      )}
                      {done && (
                        <ElButton
                          size="small"
                          text={true}
                          type="primary"
                          loading={busy.value === r.id}
                          onClick={() => onSingle(r, "retry")}
                        >
                          <ElIcon>
                            <RefreshRight />
                          </ElIcon>
                          {t("tasks.retry")}
                        </ElButton>
                      )}
                      <ElButton
                        size="small"
                        text={true}
                        type="danger"
                        onClick={() => onSingle(r, "delete")}
                      >
                        <ElIcon>
                          <Delete />
                        </ElIcon>
                        {t("global.delete")}
                      </ElButton>
                    </div>
                  )
                },
              }}
            </ElTableColumn>
          </ElTable>
        </ElCard>
      </div>
    )
  },
})
