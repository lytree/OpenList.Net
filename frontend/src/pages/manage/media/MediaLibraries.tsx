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
  ElProgress,
  ElSelect,
  ElSwitch,
  ElTag,
} from "element-plus"
import { VexTable, VexColumn } from "~/components/table"
import { Delete, Edit, List, MagicStick, Plus, Refresh, VideoPause, VideoPlay } from "@element-plus/icons-vue"
import {
  mediaCreateLibrary,
  mediaDeleteLibrary,
  mediaListLibraries,
  mediaListScanLogs,
  mediaListScanTasks,
  mediaListScrapeTasks,
  mediaScanLibrary,
  mediaScrapeLibrary,
  mediaStopScan,
  mediaUpdateLibrary,
  type MediaLibrary,
  type MediaScanLog,
  type MediaScanTask,
  type MediaScrapeStatus,
} from "~/api/media"
import { useUserStore } from "~/stores/user"
import { formatTime } from "~/utils/format"
import {
  formatScanCounts,
  libraryTypeKey,
  normalizeScanState,
  scanLogSummary,
  scanTagType,
  scrapeOfLibrary,
  scrapeProgress,
  taskOfLibrary,
} from "~/utils/mediaManage"
import { t } from "~/i18n"

/**
 * 媒体库管理台（M4 · manage）。
 *
 * 后端契约要点（都以 C# 为真源，别想当然）：
 * 1. 库 CRUD / 触发扫描 / 触发刮削需要 `manage_media` 权限位；扫描进度 `GET /libraries/{id}/scan`
 *    无鉴权（挂在库 id 粒度上）。行内状态徽标统一走跨库的 `GET /media/tasks`
 *    —— 一次取回全部库的运行进度 + 最近结果，避免逐行轮询。
 * 2. 重复触发同库扫描返回 409（Resp.code != 200），按普通失败提示即可，不需要特殊分支。
 *    刮削同理，但多一种码：400 + 「no pending items」表示没有待刮削条目（按钮此时已置灰，
 *    走到这里是并发竞态），409 表示已有一轮在跑。
 * 3. 删除库会连带删条目/文件/个人状态，确认文案必须说清楚。
 * 4. 整库刮削是 fire-and-forget：POST 立即返回，进度靠轮询 `GET /media/scrape/tasks`。
 *    它只吃 Pending 条目、单轮上限 2000、每条间隔 250ms；Unmatched 不会被再次触碰
 *    （重试只能逐条走详情页），所以两个计数必须分开显示。
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
    const scrapes = ref<MediaScrapeStatus[]>([])
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
      // 扫描与刮削各有一个「跨库聚合」接口，同一频率一起取：
      // 逐库请求的话，一个库两条，20 个库每 2.5s 就是 40 个请求。
      const [taskResp, scrapeResp] = await Promise.all([mediaListScanTasks(), mediaListScrapeTasks()])

      // 非管理员拿不到刮削状态（403），此时保持空数组，列显示 "-"、按钮整列不渲染。
      if (scrapeResp.code === 200) scrapes.value = scrapeResp.data

      if (taskResp.code !== 200) return
      tasks.value = taskResp.data
      const nowRunning = new Set(taskResp.data.filter((t) => t.is_running).map((t) => t.library_id))
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

    function scrapeOf(row: MediaLibrary): MediaScrapeStatus | null {
      return scrapeOfLibrary(scrapes.value, row.id)
    }

    /** 刮削按钮置灰条件：拿不到状态（无权限/首次加载）、正在跑、或没有待刮削条目。 */
    function scrapeDisabled(row: MediaLibrary): boolean {
      const status = scrapeOf(row)
      return !status || status.running || status.pending === 0
    }

    /** 置灰时给出原因，否则按钮是灰的又没有说明，只能靠猜。 */
    function scrapeTitle(row: MediaLibrary): string {
      const status = scrapeOf(row)
      if (!status) return t("media.manage.scrape")
      if (status.running) return t("media.manage.scrape_running")
      if (status.pending === 0) return t("media.manage.scrape_nothing")
      return t("media.manage.scrape_hint", { count: status.pending })
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

    async function onScrape(row: MediaLibrary) {
      const status = scrapeOf(row)
      // 按钮此时应当已置灰，这里兜一层：别人刚刮完的竞态下，不该弹出一个必然失败的确认框
      if (!status || status.pending === 0) {
        ElMessage.warning(t("media.manage.scrape_nothing"))
        return
      }
      try {
        await ElMessageBox.confirm(
          t("media.manage.scrape_confirm", { name: row.name, count: status.pending }),
          t("media.manage.scrape"),
          {
            type: "info",
            confirmButtonText: t("media.manage.scrape_start"),
            cancelButtonText: t("media.cancel"),
          },
        )
      } catch {
        return
      }
      const resp = await mediaScrapeLibrary(row.id)
      if (resp.code === 409) {
        // 已有一轮在跑：不是失败，改成提示并刷新，让进度列接管
        ElMessage.warning(t("media.manage.scrape_running"))
        await loadTasks()
        return
      }
      if (resp.code !== 200) {
        // provider 未配置时后端会带上要配哪个设置项（media_tmdb_api_key），
        // 原样透出比这里另造一句「刮削失败」有用得多
        ElMessage.error(resp.message)
        return
      }
      ElMessage.success(t("media.manage.scrape_started", { name: row.name }))
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

          <VexTable data={rows.value} size="small" style={{ width: "100%" }}>
            <VexColumn label={t("media.field_name")} minWidth={130}>
              {{
                default: ({ row }) => {
                  const r = row as MediaLibrary
                  return <strong>{r.name}</strong>
                },
              }}
            </VexColumn>
            <VexColumn label={t("media.field_path")} minWidth={170} showOverflowTooltip={true}>
              {{
                default: ({ row }) => {
                  const r = row as MediaLibrary
                  return <span class="ol-mono">{r.path}</span>
                },
              }}
            </VexColumn>
            <VexColumn label={t("media.field_type")} width={80}>
              {{
                default: ({ row }) => {
                  const r = row as MediaLibrary
                  return <ElTag size="small">{t(`media.type_${libraryTypeKey(r.type)}`)}</ElTag>
                },
              }}
            </VexColumn>
            <VexColumn label={t("media.manage.col_state")} minWidth={180}>
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
            </VexColumn>
            <VexColumn label={t("media.manage.col_metadata")} minWidth={150}>
              {{
                default: ({ row }) => {
                  const r = row as MediaLibrary
                  const s = scrapeOf(r)
                  if (!s) return <span class="ol-muted">-</span>
                  // 运行中只显示进度：此时 matched/pending 每次轮询都在变，
                  // 三个标签一起跳反而看不清是在推进还是卡住了。
                  if (s.running) {
                    return (
                      <div class="ol-media-scrape-progress">
                        <ElProgress percentage={scrapeProgress(s)} strokeWidth={6} showText={false} />
                        <span class="ol-field-help">
                          {scrapeProgress(s)}% ·{" "}
                          {t("media.manage.scrape_left", {
                            count: Math.max(s.batch_size - s.batch_done, 0),
                          })}
                        </span>
                      </div>
                    )
                  }
                  // 列宽有限（操作列已固定在右侧，中间的列仍要塞进约 900px 的可用宽度），
                  // 所以**不同时**展示三个计数：
                  //   - 还有待刮削 → 只关心「还剩多少」和「多少条要人工处理」
                  //   - 已刮完     → 这时才用 matched 表达「这个库齐了」
                  const showMatched = s.pending === 0 && s.matched > 0
                  const isEmpty = s.pending === 0 && s.matched === 0 && s.unmatched === 0
                  return (
                    <div class="ol-media-manage-state">
                      {showMatched && (
                        <ElTag size="small" type="success">
                          {t("media.manage.scrape_matched", { count: s.matched })}
                        </ElTag>
                      )}
                      {!showMatched && s.pending > 0 && (
                        <ElTag size="small" type="warning">
                          {t("media.manage.scrape_pending", { count: s.pending })}
                        </ElTag>
                      )}
                      {s.unmatched > 0 && (
                        <span title={t("media.manage.scrape_unmatched_hint")}>
                          <ElTag size="small" type="danger">
                            {t("media.manage.scrape_unmatched", { count: s.unmatched })}
                          </ElTag>
                        </span>
                      )}
                      {isEmpty && <span class="ol-muted">0</span>}
                      {/* failed 是「上一轮任务里 provider 报错」的条数，与 unmatched 不是一回事：
                          后者是 provider 正常响应但没匹配到。熔断中止后这里是唯一线索。 */}
                      {s.failed > 0 && (
                        <span class="ol-field-help" title={s.error ?? ""}>
                          {t("media.manage.scrape_failed", { count: s.failed })}
                        </span>
                      )}
                    </div>
                  )
                },
              }}
            </VexColumn>
            <VexColumn label={t("media.last_scan")} width={120}>
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
            </VexColumn>
            {/* 钉在右侧：7 列在 1440 宽窗口里放不下（内容区约 900px），
                不固定的话「操作」会被推到横向滚动区外面 —— 按钮看不见等于没有。 */}
            <VexColumn label={t("global.operations")} width={186} align="center" fixed="right">
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
                      <span title={scrapeTitle(r)}>
                        <ElButton
                          class="ol-manage-scrape"
                          size="small"
                          text={true}
                          type="primary"
                          disabled={scrapeDisabled(r)}
                          loading={scrapeOf(r)?.running === true}
                          onClick={() => void onScrape(r)}
                        >
                          <ElIcon>
                            <MagicStick />
                          </ElIcon>
                        </ElButton>
                      </span>
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
            </VexColumn>
          </VexTable>
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
                <VexTable data={logs.value} size="small" style={{ width: "100%" }} v-loading={logsLoading.value}>
                  <VexColumn label={t("media.manage.col_started")} width={150}>
                    {{
                      default: ({ row }) => {
                        const r = row as MediaScanLog
                        return <span class="ol-field-help">{formatTime(r.started_at)}</span>
                      },
                    }}
                  </VexColumn>
                  <VexColumn label={t("media.manage.col_finished")} width={150}>
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
                  </VexColumn>
                  <VexColumn label={t("media.manage.col_result")} width={120}>
                    {{
                      default: ({ row }) => {
                        const r = row as MediaScanLog
                        return <span class="ol-mono">{scanLogSummary(r)}</span>
                      },
                    }}
                  </VexColumn>
                  <VexColumn label={t("media.manage.col_error")} minWidth={180} showOverflowTooltip={true}>
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
                  </VexColumn>
                </VexTable>
              </div>
            ),
          }}
        </ElDialog>
      </div>
    )
  },
})
