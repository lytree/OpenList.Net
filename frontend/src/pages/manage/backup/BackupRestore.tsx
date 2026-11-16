import { computed, defineComponent, ref } from "vue"
import {
  ElAlert,
  ElButton,
  ElCard,
  ElIcon,
  ElInput,
  ElMessage,
  ElMessageBox,
  ElSwitch,
} from "element-plus"
import { Download, Upload } from "@element-plus/icons-vue"
import {
  createStorage,
  listMetas,
  listSettings,
  listStorages,
  saveMeta,
  saveSettings,
  updateStorage,
} from "~/api/admin"
import type { Meta, SettingItem, StorageWithDriver } from "~/api/types"
import {
  backupFileName,
  countBackup,
  openBackup,
  reconcile,
  restorableSettings,
  sealBackup,
  serializeBackup,
  type BackupPayload,
  type BackupSection,
  type OpenFailure,
} from "~/utils/backup"
import { useSettingsStore } from "~/stores/settings"
import { t } from "~/i18n"

/**
 * 备份 / 恢复 —— `/@manage/backup-restore`（管理台最后一个就绪模块）。
 *
 * **没有后端端点**：和上游一致，备份 = 把三个既有管理端点拉下来拼成一个 JSON，
 * 恢复 = 解出 JSON 后逐条走既有 CRUD。好处是零新增接口、两套前端的文件互通；
 * 代价是恢复只能「新增 + 更新」，**永远不删**（删是另一个语义，得单独确认）。
 *
 * 两处和上游不同、但必须不同（理由都写进 §37）：
 *
 * 1. **设置项永远合并写入**，不受「覆盖」开关影响：设置是 upsert，不存在「跳过」；
 *    真按开关处理，在新实例上恢复会出现「几乎什么都没恢复」的假象。开关只作用于
 *    挂载 / 元数据这两类「对象」。
 * 2. **单用户产品不备份用户与分享**：旧备份文件里的 `users` / `shares` 数组会被忽略。
 */

type LogType = "success" | "error" | "info" | "warn"

interface LogRow {
  type: LogType
  msg: string
}

const LOG_ICON: Record<LogType, string> = { success: "✅", error: "❌", info: "ℹ️", warn: "⚠️" }

interface SectionResult {
  created: number
  updated: number
  skipped: number
  failed: number
}

const zeroResult = (): SectionResult => ({ created: 0, updated: 0, skipped: 0, failed: 0 })

const SECTION_LABEL: Record<BackupSection, string> = {
  settings: "backup.settings",
  storages: "manage.sidemenu.storages",
  metas: "manage.sidemenu.metas",
}

const FAILURE_TEXT: Record<OpenFailure, string> = {
  invalid_json: "backup.err_invalid_json",
  not_a_backup: "backup.err_not_a_backup",
  no_sections: "backup.err_no_sections",
  wrong_password: "backup.err_wrong_password",
  corrupted: "backup.err_corrupted",
}

export default defineComponent({
  name: "BackupRestorePage",
  setup() {
    const settingsStore = useSettingsStore()

    const password = ref("")
    const backingUp = ref(false)

    const restoreFile = ref<File | null>(null)
    const restorePassword = ref("")
    const override = ref(false)
    const restoring = ref(false)
    const dragging = ref(false)
    const fileInput = ref<HTMLInputElement | null>(null)

    const logs = ref<LogRow[]>([])
    const summary = ref<{ section: BackupSection; result: SectionResult }[]>([])

    function log(type: LogType, msg: string) {
      logs.value = [...logs.value, { type, msg }]
    }

    const fileInfo = computed(() => {
      const file = restoreFile.value
      return file ? `${file.name} · ${(file.size / 1024).toFixed(1)} KB` : ""
    })

    // ---------------------------------------------------------------------
    // 采集 / 备份
    // ---------------------------------------------------------------------

    /** 拉当前三个域；任一失败返回 null（备份不能缺块）。 */
    async function gather(): Promise<BackupPayload | null> {
      const [settings, storages, metas] = await Promise.all([
        listSettings(),
        listStorages(),
        listMetas(),
      ])
      const failed: string[] = []
      if (settings.code !== 200) failed.push(t("backup.settings"))
      if (storages.code !== 200) failed.push(t("manage.sidemenu.storages"))
      if (metas.code !== 200) failed.push(t("manage.sidemenu.metas"))
      if (failed.length > 0) {
        log("error", t("backup.gather_failed", { names: failed.join(" / ") }))
        return null
      }
      return {
        settings: settings.data ?? [],
        storages: storages.data?.content ?? [],
        metas: metas.data?.content ?? [],
      }
    }

    function download(filename: string, text: string) {
      const blob = new Blob([text], { type: "application/json" })
      const url = URL.createObjectURL(blob)
      const a = document.createElement("a")
      a.href = url
      a.download = filename
      a.click()
      // 立刻 revoke 会让部分浏览器来不及取数据，下一轮事件循环再释放
      setTimeout(() => URL.revokeObjectURL(url), 0)
    }

    async function doBackup() {
      if (backingUp.value) return
      backingUp.value = true
      logs.value = []
      summary.value = []
      try {
        log("info", t("backup.start_backup"))
        const payload = await gather()
        if (!payload) return
        log("success", t("backup.gathered", countBackup(payload)))

        const file = sealBackup(payload, {
          password: password.value,
          site: settingsStore.siteTitle,
        })
        const name = backupFileName(settingsStore.siteTitle)
        download(name, serializeBackup(file))
        log("success", t("backup.saved", { name }))
        log("info", password.value ? t("backup.encrypted_hint") : t("backup.plaintext_hint"))
      } finally {
        backingUp.value = false
      }
    }

    // ---------------------------------------------------------------------
    // 选文件
    // ---------------------------------------------------------------------

    function pickFile(file: File) {
      if (!/\.json$/i.test(file.name)) {
        ElMessage.warning(t("backup.only_json"))
        return
      }
      restoreFile.value = file
      log("info", t("backup.file_selected", { name: file.name }))
    }

    function onPickFile(event: Event) {
      const input = event.target as HTMLInputElement
      const file = input.files?.[0]
      if (file) pickFile(file)
      input.value = "" // 允许再次选同一个文件
    }

    function onDrop(event: DragEvent) {
      dragging.value = false
      const file = event.dataTransfer?.files?.[0]
      if (file) pickFile(file)
    }

    // ---------------------------------------------------------------------
    // 恢复
    // ---------------------------------------------------------------------

    async function doRestore() {
      if (restoring.value) return
      const file = restoreFile.value
      if (!file) {
        ElMessage.warning(t("backup.no_file"))
        return
      }
      restoring.value = true
      logs.value = []
      summary.value = []
      try {
        const opened = openBackup(await file.text(), restorePassword.value)
        if (!opened.ok) {
          log("error", t(FAILURE_TEXT[opened.reason]))
          return
        }
        const payload = opened.payload
        const counts = countBackup(payload)
        try {
          await ElMessageBox.confirm(
            t("backup.confirm_body", {
              ...counts,
              mode: t(override.value ? "backup.mode_override" : "backup.mode_add"),
            }),
            t("backup.confirm_title"),
            {
              type: "warning",
              confirmButtonText: t("backup.restore"),
              cancelButtonText: t("global.cancel"),
            },
          )
        } catch {
          log("info", t("backup.cancelled"))
          return
        }

        log("info", t(opened.encrypted ? "backup.start_restore_encrypted" : "backup.start_restore"))

        // 对账要按自然键比，更新还要用本实例的 id —— 先把当前状态拉齐
        const current = await gather()
        if (!current) {
          log("error", t("backup.aborted"))
          return
        }

        // 计划阶段：先算完三个域的对账，再执行 —— 全部成功前不写任何数据
        const storagePlan = reconcile(payload.storages, current.storages, (row) => row.mount_path, override.value)
        const metaPlan = reconcile(payload.metas, current.metas, (row) => row.path, override.value)

        // 1) 设置：合并写，不删；不受覆盖开关影响（见文件头注释）
        summary.value.push({ section: "settings", result: await applySettings(restorableSettings(payload.settings)) })
        // 2) 挂载
        summary.value.push({ section: "storages", result: await applyStorages(storagePlan, current.storages) })
        // 3) 元数据
        summary.value.push({ section: "metas", result: await applyMetas(metaPlan) })

        // 站点名/logo 等设置改完立刻反映到外壳上，否则要等下次刷新
        await settingsStore.load(true)
        log("success", t("backup.finish_restore"))
      } finally {
        restoring.value = false
      }
    }

    async function applySettings(rows: SettingItem[]): Promise<SectionResult> {
      const result = zeroResult()
      if (rows.length === 0) return result
      const resp = await saveSettings(rows)
      if (resp.code === 200) {
        result.created = rows.length
        log("success", t("backup.result_settings", { count: rows.length }))
      } else {
        result.failed = rows.length
        log("error", t("backup.result_failed", { names: t("backup.settings"), msg: resp.message }))
      }
      return result
    }

    /** 挂载：新建与更新都发同一套字段，只有 id 不同。 */
    function storageReq(row: StorageWithDriver, id: number) {
      return {
        id,
        mount_path: row.mount_path,
        driver: row.driver,
        addition: row.addition || "{}",
        order: row.order ?? 0,
        disabled: row.disabled ?? false,
        remark: row.remark ?? null,
        order_by: row.order_by ?? null,
        order_direction: row.order_direction ?? null,
        extract_folder: row.extract_folder ?? null,
        web_proxy: row.web_proxy ?? false,
        webdav_policy: row.webdav_policy ?? null,
        enable_sign: row.enable_sign ?? false,
      }
    }

    async function applyStorages(
      plan: { creates: StorageWithDriver[]; updates: StorageWithDriver[]; skipped: StorageWithDriver[] },
      current: StorageWithDriver[],
    ): Promise<SectionResult> {
      const result = zeroResult()
      result.skipped = plan.skipped.length
      const byPath = new Map(current.map((row) => [row.mount_path, row]))

      for (const row of plan.creates) {
        const resp = await createStorage(storageReq(row, 0))
        if (resp.code === 200) result.created += 1
        else {
          result.failed += 1
          log("error", t("backup.result_failed", { names: row.mount_path, msg: resp.message }))
        }
      }
      for (const row of plan.updates) {
        const target = byPath.get(row.mount_path)
        if (!target) {
          result.failed += 1
          continue
        }
        const resp = await updateStorage(storageReq(row, target.id))
        if (resp.code === 200) result.updated += 1
        else {
          result.failed += 1
          log("error", t("backup.result_failed", { names: row.mount_path, msg: resp.message }))
        }
      }
      logResult("storages", result)
      return result
    }

    async function applyMetas(
      plan: { creates: Meta[]; updates: Meta[]; skipped: Meta[] },
    ): Promise<SectionResult> {
      const result = zeroResult()
      result.skipped = plan.skipped.length
      const currentMetas = (await listMetas()).data?.content ?? []
      const byPathMap = new Map(currentMetas.map((row) => [row.path, row]))

      for (const row of plan.creates) {
        const resp = await saveMeta({ ...row, id: 0 })
        if (resp.code === 200) result.created += 1
        else {
          result.failed += 1
          log("error", t("backup.result_failed", { names: row.path, msg: resp.message }))
        }
      }
      for (const row of plan.updates) {
        const target = byPathMap.get(row.path)
        if (!target) {
          result.failed += 1
          continue
        }
        const resp = await saveMeta({ ...row, id: target.id })
        if (resp.code === 200) result.updated += 1
        else {
          result.failed += 1
          log("error", t("backup.result_failed", { names: row.path, msg: resp.message }))
        }
      }
      logResult("metas", result)
      return result
    }

    function logResult(section: BackupSection, result: SectionResult) {
      if (result.created === 0 && result.updated === 0 && result.failed === 0) return
      log(
        result.failed > 0 ? "warn" : "success",
        t("backup.result_line", {
          name: t(SECTION_LABEL[section]),
          created: result.created,
          updated: result.updated,
          skipped: result.skipped,
          failed: result.failed,
        }),
      )
    }

    return () => (
      <div class="ol-manage-page">
        <div class="ol-manage-toolbar">
          <h2 class="ol-manage-h2">{t("manage.sidemenu.backup-restore")}</h2>
          <div class="ol-manage-actions">
            {logs.value.length > 0 ? (
              <ElButton size="small" text={true} onClick={() => (logs.value = [])}>
                {t("backup.clear_log")}
              </ElButton>
            ) : null}
          </div>
        </div>

        <ElAlert
          class="ol-br-alert"
          type="info"
          showIcon={false}
          closable={false}
          title={t("backup.intro")}
        />

        <div class="ol-br-grid">
          <ElCard shadow="never" class="ol-br-card">
            <div class="ol-br-card-head">
              <span class="ol-br-card-title">{t("backup.backup_title")}</span>
              <span class="ol-br-card-note">{t("backup.backup_note")}</span>
            </div>

            <div class="ol-br-field">
              <label class="ol-br-label">{t("backup.encrypt_password")}</label>
              <ElInput
                modelValue={password.value}
                onUpdate:modelValue={(v: string) => (password.value = v)}
                type="password"
                showPassword={true}
                clearable={true}
                placeholder={t("backup.encrypt_password_placeholder")}
              />
              <div class="ol-field-help">{t("backup.encrypt_hint")}</div>
            </div>

            <ElAlert
              class="ol-br-inline-alert"
              type="warning"
              showIcon={true}
              closable={false}
              title={t("backup.secret_warning")}
            />

            <ElButton type="primary" loading={backingUp.value} onClick={() => void doBackup()}>
              <ElIcon style={{ marginRight: "4px" }}>
                <Download />
              </ElIcon>
              {t("backup.backup")}
            </ElButton>
          </ElCard>

          <ElCard shadow="never" class="ol-br-card">
            <div class="ol-br-card-head">
              <span class="ol-br-card-title">{t("backup.restore_title")}</span>
              <span class="ol-br-card-note">{t("backup.restore_note")}</span>
            </div>

            <div
              class={["ol-br-drop", dragging.value ? "is-dragging" : ""]}
              onDragover={(e: DragEvent) => {
                e.preventDefault()
                dragging.value = true
              }}
              onDragleave={() => (dragging.value = false)}
              onDrop={(e: DragEvent) => {
                e.preventDefault()
                onDrop(e)
              }}
            >
              <div class="ol-br-drop-text">
                {restoreFile.value ? fileInfo.value : t("backup.drop_hint")}
              </div>
              <div class="ol-br-drop-actions">
                <ElButton size="small" onClick={() => fileInput.value?.click()}>
                  {t("backup.choose_file")}
                </ElButton>
                {restoreFile.value ? (
                  <ElButton size="small" text={true} onClick={() => (restoreFile.value = null)}>
                    {t("backup.clear_file")}
                  </ElButton>
                ) : null}
              </div>
              <input
                ref={fileInput}
                type="file"
                accept=".json,application/json"
                class="ol-br-file-input"
                onChange={onPickFile}
              />
            </div>

            <div class="ol-br-field">
              <label class="ol-br-label">{t("backup.restore_password")}</label>
              <ElInput
                modelValue={restorePassword.value}
                onUpdate:modelValue={(v: string) => (restorePassword.value = v)}
                type="password"
                showPassword={true}
                clearable={true}
                placeholder={t("backup.restore_password_placeholder")}
              />
            </div>

            <div class="ol-br-switch">
              <ElSwitch
                modelValue={override.value}
                onUpdate:modelValue={(v: string | number | boolean) => (override.value = Boolean(v))}
              />
              <div class="ol-br-switch-text">
                <span class="ol-br-label">{t("backup.override")}</span>
                <span class="ol-field-help">{t("backup.override_hint")}</span>
              </div>
            </div>

            <ElButton type="primary" loading={restoring.value} onClick={() => void doRestore()}>
              <ElIcon style={{ marginRight: "4px" }}>
                <Upload />
              </ElIcon>
              {t("backup.restore")}
            </ElButton>
          </ElCard>
        </div>

        {summary.value.length > 0 ? (
          <ElCard shadow="never" class="ol-br-card ol-br-summary">
            <div class="ol-br-card-head">
              <span class="ol-br-card-title">{t("backup.summary_title")}</span>
            </div>
            <div class="ol-br-summary-list">
              {summary.value.map(({ section, result }) => (
                <div class="ol-br-summary-row" key={section}>
                  <span class="ol-br-summary-name">{t(SECTION_LABEL[section])}</span>
                  <span class="ol-br-summary-cell">
                    {t("backup.col_created")} <b>{result.created}</b>
                  </span>
                  <span class="ol-br-summary-cell">
                    {t("backup.col_updated")} <b>{result.updated}</b>
                  </span>
                  <span class="ol-br-summary-cell">
                    {t("backup.col_skipped")} <b>{result.skipped}</b>
                  </span>
                  <span class={["ol-br-summary-cell", result.failed > 0 ? "is-bad" : ""]}>
                    {t("backup.col_failed")} <b>{result.failed}</b>
                  </span>
                </div>
              ))}
            </div>
          </ElCard>
        ) : null}

        <ElCard shadow="never" class="ol-br-card">
          <div class="ol-br-card-head">
            <span class="ol-br-card-title">{t("backup.log_title")}</span>
            <span class="ol-br-card-note">
              {logs.value.length > 0
                ? t("backup.log_count", { count: logs.value.length })
                : t("backup.log_empty")}
            </span>
          </div>
          <div class="ol-br-log" data-log-count={logs.value.length}>
            {logs.value.length === 0 ? (
              <div class="ol-br-log-placeholder">{t("backup.log_placeholder")}</div>
            ) : (
              logs.value.map((row, index) => (
                <div class={`ol-br-log-row is-${row.type}`} key={index}>
                  <span class="ol-br-log-icon">{LOG_ICON[row.type]}</span>
                  <span class="ol-br-log-msg">{row.msg}</span>
                </div>
              ))
            )}
          </div>
        </ElCard>
      </div>
    )
  },
})
