import { defineComponent, computed, onMounted, onUnmounted, reactive, ref } from "vue"
import {
  ElAlert,
  ElButton,
  ElCard,
  ElDialog,
  ElForm,
  ElFormItem,
  ElIcon,
  ElInput,
  ElMessage,
  ElMessageBox,
  ElOption,
  ElSelect,
  ElSwitch,
  ElTable,
  ElTableColumn,
  ElTag,
} from "element-plus"
import { Delete, Edit, List, Plus, Refresh, VideoPause, VideoPlay } from "@element-plus/icons-vue"
import {
  mediaCreateLibrary,
  mediaDeleteLibrary,
  mediaListLibraries,
  mediaListScanLogs,
  mediaListScanTasks,
  mediaScanLibrary,
  mediaStopScan,
  mediaUpdateLibrary,
  type MediaLibrary,
  type MediaScanLog,
  type MediaScanTask,
} from "~/api/media"
import { useUserStore } from "~/stores/user"
import { formatTime } from "~/utils/format"
import {
  formatScanCounts,
  libraryTypeKey,
  normalizeScanState,
  scanLogSummary,
  scanTagType,
  taskOfLibrary,
} from "~/utils/mediaManage"
import { t } from "~/i18n"

/**
 * 媒体库管理台（M4 · manage）。
 *
 * 后端契约要点（都以 C# 为真源，别想当然）：
 * 1. 库 CRUD / 触发扫描需要 `manage_media` 权限位；扫描进度 `GET /libraries/{id}/scan`
 *    无鉴权（挂在库 id 粒度上）。行内状态徽标统一走跨库的 `GET /media/tasks`
 *    —— 一次取回全部库的运行进度 + 最近结果，避免逐行轮询。
 * 2. 重复触发同库扫描返回 409（Resp.code != 200），按普通失败提示即可，不需要特殊分支。
 * 3. 删除库会连带删条目/文件/个人状态，确认文案必须说清楚。
 */

interface LibraryForm {
  name: string
  path: string
  type: number
  autoScan: boolean
  language: string
}

function emptyForm(): LibraryForm {
  return { name: "", path: "", type: 2, autoScan: true, language: "" }
}

export default defineComponent({
  name: "MediaLibrariesPage",
  setup() {
    const user = useUserStore()
    const canManage = computed(() => user.can("manage_media"))

    const rows = ref<MediaLibrary[]>([])
    const tasks = ref<MediaScanTask[]>([])
    const loading = ref(false)
    const error = ref("")

    const dialogVisible = ref(false)
    const editing = ref<MediaLibrary | null>(null)
    const saving = ref(false)
    const form = reactive<LibraryForm>(emptyForm())

    const logsVisible = ref(false)
    const logsFor = ref<MediaLibrary | null>(null)
    const logs = ref<MediaScanLog[]>([])
    const logsLoading = ref(false)

    let timer: number | undefined

    async function load() {
      loading.value = true
      error.value = ""
      const resp = await mediaListLibraries()
      loading.value = false
      if (resp.code !== 200) {
        error.value = resp.message
        return
      }
      rows.value = resp.data
    }

    const prevRunning = new Set<number>()

    async function loadTasks() {
      const resp = await mediaListScanTasks()
      if (resp.code !== 200) return
      tasks.value = resp.data
      const nowRunning = new Set(resp.data.filter((t) => t.is_running).map((t) => t.library_id))
      // 有库从 running 走到结束（完成/失败/取消）：行内 last_scan_at 已过期，补一次列表刷新
      const finished = [...prevRunning].some((id) => !nowRunning.has(id))
      prevRunning.clear()
      nowRunning.forEach((id) => prevRunning.add(id))
      if (finished) void load()
    }

    onMounted(() => {
      void load()
      void loadTasks()
      // 扫描是后台任务：页面驻留期间低频轮询跨库任务卡，覆盖进度与结果刷新
      timer = window.setInterval(() => void loadTasks(), 2500)
    })
    onUnmounted(() => window.clearInterval(timer))

    function taskOf(row: MediaLibrary): MediaScanTask | null {
      return taskOfLibrary(tasks.value, row.id)
    }

    function openCreate() {
      editing.value = null
      Object.assign(form, emptyForm())
      dialogVisible.value = true
    }

    function openEdit(row: MediaLibrary) {
      editing.value = row
      Object.assign(form, {
        name: row.name,
        path: row.path,
        type: row.type,
        autoScan: row.auto_scan,
        language: row.metadata_language ?? "",
      })
      dialogVisible.value = true
    }

    async function onSave() {
      if (!form.name.trim()) {
        ElMessage.warning(t("media.name_required"))
        return
      }
      if (!form.path.startsWith("/")) {
        ElMessage.warning(t("media.path_required"))
        return
      }
      const payload = {
        name: form.name.trim(),
        type: form.type,
        auto_scan: form.autoScan,
        metadata_language: form.language.trim() || undefined,
      }
      saving.value = true
      const resp = editing.value
        ? await mediaUpdateLibrary(editing.value.id, payload)
        : await mediaCreateLibrary({ ...payload, path: form.path.trim() })
      saving.value = false
      if (resp.code !== 200) {
        ElMessage.error(resp.message)
        return
      }
      ElMessage.success(t(editing.value ? "media.saved" : "media.created"))
      dialogVisible.value = false
      await load()
    }

    async function onScan(row: MediaLibrary) {
      const resp = await mediaScanLibrary(row.id)
      if (resp.code !== 200) {
        ElMessage.error(resp.message)
        return
      }
      ElMessage.success(t("media.scan_started", { name: row.name }))
      await loadTasks()
    }

    async function onStop(row: MediaLibrary) {
      const resp = await mediaStopScan(row.id)
      if (resp.code !== 200) {
        ElMessage.error(resp.message)
        return
      }
      await loadTasks()
    }

    async function openLogs(row: MediaLibrary) {
      logsFor.value = row
      logsVisible.value = true
      logsLoading.value = true
      const resp = await mediaListScanLogs(row.id, 20)
      logsLoading.value = false
      logs.value = resp.code === 200 ? resp.data : []
    }

    async function onDelete(row: MediaLibrary) {
      try {
        await ElMessageBox.confirm(t("media.confirm_delete", { name: row.name }), t("media.delete"), {
          type: "warning",
        })
      } catch {
        return
      }
      const resp = await mediaDeleteLibrary(row.id)
      if (resp.code !== 200) {
        ElMessage.error(resp.message)
        return
      }
      ElMessage.success(t("media.deleted"))
      await Promise.all([load(), loadTasks()])
    }

    return () => (
      <div class="ol-manage-page">
        <ElCard shadow="never">
          <div class="ol-manage-toolbar">
            <h2 class="ol-manage-h2">{t("media.manage.title")}</h2>
            <div class="ol-manage-actions">
              <ElButton
                size="small"
                loading={loading.value}
                onClick={() => Promise.all([load(), loadTasks()])}
              >
                <ElIcon>
                  <Refresh />
                </ElIcon>
                {t("global.refresh")}
              </ElButton>
              {canManage.value && (
                <ElButton size="small" type="primary" onClick={openCreate}>
                  <ElIcon>
                    <Plus />
                  </ElIcon>
                  {t("media.new_library")}
                </ElButton>
              )}
            </div>
          </div>

          <ElAlert
            type="info"
            showIcon={true}
            closable={false}
            class="ol-manage-tip"
            title={t("media.manage.hint")}
          />

          {error.value && <ElAlert type="error" showIcon={true} title={error.value} closable={false} />}
          {!error.value && !loading.value && rows.value.length === 0 && (
            <ElAlert type="warning" showIcon={true} closable={false} title={t("media.no_libraries")} />
          )}

          <ElTable data={rows.value} size="small" style={{ width: "100%" }}>
            <ElTableColumn label={t("media.field_name")} minWidth={140}>
              {{
                default: ({ row }) => {
                  const r = row as MediaLibrary
                  return <strong>{r.name}</strong>
                },
              }}
            </ElTableColumn>
            <ElTableColumn label={t("media.field_path")} minWidth={180} showOverflowTooltip={true}>
              {{
                default: ({ row }) => {
                  const r = row as MediaLibrary
                  return <span class="ol-mono">{r.path}</span>
                },
              }}
            </ElTableColumn>
            <ElTableColumn label={t("media.field_type")} width={90}>
              {{
                default: ({ row }) => {
                  const r = row as MediaLibrary
                  return <ElTag size="small">{t(`media.type_${libraryTypeKey(r.type)}`)}</ElTag>
                },
              }}
            </ElTableColumn>
            <ElTableColumn label={t("media.manage.col_state")} minWidth={200}>
              {{
                default: ({ row }) => {
                  const r = row as MediaLibrary
                  const task = taskOf(r)
                  const state = normalizeScanState(task?.state)
                  return (
                    <div class="ol-media-manage-state">
                      <ElTag size="small" type={scanTagType(task?.state)}>
                        {t(`media.manage.state_${state}`)}
                      </ElTag>
                      {state === "running" && (
                        <span class="ol-field-help" title={task?.current_path ?? ""}>
                          {task?.found ?? 0} …
                        </span>
                      )}
                      {state !== "running" && task && (task.added || task.updated || task.removed) > 0 && (
                        <span class="ol-field-help">{formatScanCounts(task)}</span>
                      )}
                      {task?.state === "failed" && task.error && (
                        <span class="ol-field-help" title={task.error}>
                          {task.error}
                        </span>
                      )}
                    </div>
                  )
                },
              }}
            </ElTableColumn>
            <ElTableColumn label={t("media.last_scan")} width={140}>
              {{
                default: ({ row }) => {
                  const r = row as MediaLibrary
                  return r.last_scan_at ? (
                    <span class="ol-field-help">{formatTime(r.last_scan_at)}</span>
                  ) : (
                    <span class="ol-muted">-</span>
                  )
                },
              }}
            </ElTableColumn>
            <ElTableColumn label={t("global.operations")} width={168} align="center">
              {{
                default: ({ row }) => {
                  const r = row as MediaLibrary
                  if (!canManage.value) return <span class="ol-muted">-</span>
                  const running = taskOf(r)?.state === "running"
                  return (
                    <div class="ol-manage-row-ops">
                      {running ? (
                        <span title={t("media.manage.stop_scan")}>
                          <ElButton size="small" text={true} type="warning" onClick={() => onStop(r)}>
                            <ElIcon>
                              <VideoPause />
                            </ElIcon>
                          </ElButton>
                        </span>
                      ) : (
                        <span title={t("media.scan")}>
                          <ElButton size="small" text={true} type="primary" onClick={() => onScan(r)}>
                            <ElIcon>
                              <VideoPlay />
                            </ElIcon>
                          </ElButton>
                        </span>
                      )}
                      <span title={t("media.manage.logs")}>
                        <ElButton size="small" text={true} onClick={() => openLogs(r)}>
                          <ElIcon>
                            <List />
                          </ElIcon>
                        </ElButton>
                      </span>
                      <span title={t("global.edit")}>
                        <ElButton size="small" text={true} type="primary" onClick={() => openEdit(r)}>
                          <ElIcon>
                            <Edit />
                          </ElIcon>
                        </ElButton>
                      </span>
                      <span title={t("media.delete")}>
                        <ElButton size="small" text={true} type="danger" onClick={() => onDelete(r)}>
                          <ElIcon>
                            <Delete />
                          </ElIcon>
                        </ElButton>
                      </span>
                    </div>
                  )
                },
              }}
            </ElTableColumn>
          </ElTable>
        </ElCard>

        {/* 建/编对话框：编辑时后端不允许改 path（UpdateMediaLibraryReq 没有该字段），输入框置灰 */}
        <ElDialog
          modelValue={dialogVisible.value}
          title={t(editing.value ? "media.manage.edit_title" : "media.manage.create_title")}
          width="560px"
          onUpdate:modelValue={(v: boolean) => (dialogVisible.value = v)}
        >
          {{
            default: () => (
              <ElForm labelWidth="160px">
                <ElFormItem label={t("media.field_name")} required={true}>
                  {{
                    default: () => (
                      <ElInput
                        modelValue={form.name}
                        placeholder={t("media.field_name_placeholder")}
                        onUpdate:modelValue={(v: string) => (form.name = v)}
                      />
                    ),
                  }}
                </ElFormItem>
                <ElFormItem label={t("media.field_path")} required={!editing.value}>
                  {{
                    default: () => (
                      <div class="ol-field-col">
                        <ElInput
                          modelValue={form.path}
                          disabled={!!editing.value}
                          placeholder="/media/movies"
                          onUpdate:modelValue={(v: string) => (form.path = v)}
                        />
                        <span class="ol-field-help">
                          {t(editing.value ? "media.manage.edit_path_locked" : "media.field_path_hint")}
                        </span>
                      </div>
                    ),
                  }}
                </ElFormItem>
                <ElFormItem label={t("media.field_type")}>
                  {{
                    default: () => (
                      <ElSelect modelValue={form.type} onUpdate:modelValue={(v: number) => (form.type = v)}>
                        <ElOption label={t("media.type_movie")} value={0} />
                        <ElOption label={t("media.type_tv")} value={1} />
                        <ElOption label={t("media.type_mixed")} value={2} />
                      </ElSelect>
                    ),
                  }}
                </ElFormItem>
                <ElFormItem label={t("media.manage.field_auto_scan")}>
                  {{
                    default: () => (
                      <div class="ol-field-col">
                        <ElSwitch
                          modelValue={form.autoScan}
                          onUpdate:modelValue={(v: boolean | string | number) => (form.autoScan = !!v)}
                        />
                        <span class="ol-field-help">{t("media.manage.field_auto_scan_hint")}</span>
                      </div>
                    ),
                  }}
                </ElFormItem>
                <ElFormItem label={t("media.manage.field_language")}>
                  {{
                    default: () => (
                      <div class="ol-field-col">
                        <ElInput
                          modelValue={form.language}
                          placeholder="zh-CN"
                          onUpdate:modelValue={(v: string) => (form.language = v)}
                        />
                        <span class="ol-field-help">{t("media.manage.field_language_hint")}</span>
                      </div>
                    ),
                  }}
                </ElFormItem>
              </ElForm>
            ),
            footer: () => (
              <div class="ol-manage-form-actions">
                <ElButton onClick={() => (dialogVisible.value = false)}>{t("media.cancel")}</ElButton>
                <ElButton type="primary" loading={saving.value} onClick={onSave}>
                  {t(editing.value ? "global.save" : "media.create")}
                </ElButton>
              </div>
            ),
          }}
        </ElDialog>

        {/* 扫描历史 */}
        <ElDialog
          modelValue={logsVisible.value}
          title={t("media.manage.logs_title", { name: logsFor.value?.name ?? "" })}
          width="720px"
          onUpdate:modelValue={(v: boolean) => (logsVisible.value = v)}
        >
          {{
            default: () => (
              <div>
                {logs.value.length === 0 && !logsLoading.value && (
                  <ElAlert type="info" showIcon={true} closable={false} title={t("media.manage.no_logs")} />
                )}
                <ElTable data={logs.value} size="small" style={{ width: "100%" }} v-loading={logsLoading.value}>
                  <ElTableColumn label={t("media.manage.col_started")} width={150}>
                    {{
                      default: ({ row }) => {
                        const r = row as MediaScanLog
                        return <span class="ol-field-help">{formatTime(r.started_at)}</span>
                      },
                    }}
                  </ElTableColumn>
                  <ElTableColumn label={t("media.manage.col_finished")} width={150}>
                    {{
                      default: ({ row }) => {
                        const r = row as MediaScanLog
                        return r.finished_at ? (
                          <span class="ol-field-help">{formatTime(r.finished_at)}</span>
                        ) : (
                          <span class="ol-muted">…</span>
                        )
                      },
                    }}
                  </ElTableColumn>
                  <ElTableColumn label={t("media.manage.col_result")} width={120}>
                    {{
                      default: ({ row }) => {
                        const r = row as MediaScanLog
                        return <span class="ol-mono">{scanLogSummary(r)}</span>
                      },
                    }}
                  </ElTableColumn>
                  <ElTableColumn label={t("media.manage.col_error")} minWidth={180} showOverflowTooltip={true}>
                    {{
                      default: ({ row }) => {
                        const r = row as MediaScanLog
                        return r.error ? (
                          <span style={{ color: "var(--el-color-danger)" }}>{r.error}</span>
                        ) : (
                          <span class="ol-muted">-</span>
                        )
                      },
                    }}
                  </ElTableColumn>
                </ElTable>
              </div>
            ),
          }}
        </ElDialog>
      </div>
    )
  },
})
