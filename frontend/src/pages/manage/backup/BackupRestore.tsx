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
  createUser,
  listMetas,
  listSettings,
  listStorages,
  listUsers,
  saveMeta,
  saveSettings,
  updateStorage,
  updateUser,
} from "~/api/admin"
import { createShare, listShares, updateShare } from "~/api/shares"
import { UserRole, type AdminUser, type Meta, type SettingItem, type ShareInfo, type StorageWithDriver } from "~/api/types"
import {
  backupFileName,
  countBackup,
  openBackup,
  randomPassword,
  reconcile,
  remapUserIds,
  restorableSettings,
  sealBackup,
  selfLockoutRisk,
  serializeBackup,
  stripUserSecrets,
  userIdToUsername,
  usernameToId,
  type BackupPayload,
  type BackupSection,
  type OpenFailure,
} from "~/utils/backup"
import { useSettingsStore } from "~/stores/settings"
import { useUserStore } from "~/stores/user"
import { t } from "~/i18n"

/**
 * 备份 / 恢复 —— `/@manage/backup-restore`（管理台最后一个就绪模块）。
 *
 * **没有后端端点**：和上游一致，备份 = 把五个既有管理端点拉下来拼成一个 JSON，
 * 恢复 = 解出 JSON 后逐条走既有 CRUD。好处是零新增接口、两套前端的文件互通；
 * 代价是恢复只能「新增 + 更新」，**永远不删**（删是另一个语义，得单独确认）。
 *
 * 三处和上游不同、但必须不同（理由都写进 §37）：
 *
 * 1. **用户凭据不随备份迁移**。本仓库 `/admin/user/list` 返回 `UserPublic`，没有密码哈希；
 *    旧前端的文件里带着 Go 的 `password` 哈希，恢复时一律丢弃 —— 拿哈希当明文再散列一次
 *    只会造出一批登不进去的账号。新建账号用随机口令 + 默认停用，由管理员重置后再启用。
 * 2. **设置项永远合并写入**，不受「覆盖」开关影响：设置是 upsert，不存在「跳过」；
 *    真按开关处理，在新实例上恢复会出现「几乎什么都没恢复」的假象。开关只作用于
 *    用户 / 挂载 / 元数据 / 分享这四类「对象」。
 * 3. **自锁保护**：文件里的「当前登录管理员」若被降权或停用，整份恢复直接拒绝。
 *    上游没这道检查，恢复完下一次请求就 401，只能走 CLI 救场。
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
  users: "manage.sidemenu.users",
  storages: "manage.sidemenu.storages",
  metas: "manage.sidemenu.metas",
  shares: "manage.sidemenu.shares",
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
    const userStore = useUserStore()

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

    /** 拉当前五个域；任一失败返回 null（备份不能缺块）。 */
    async function gather(): Promise<BackupPayload | null> {
      const [settings, users, storages, metas, shares] = await Promise.all([
        listSettings(),
        listUsers(),
        listStorages(),
        listMetas(),
        listShares(),
      ])
      const failed: string[] = []
      if (settings.code !== 200) failed.push(t("backup.settings"))
      if (users.code !== 200) failed.push(t("manage.sidemenu.users"))
      if (storages.code !== 200) failed.push(t("manage.sidemenu.storages"))
      if (metas.code !== 200) failed.push(t("manage.sidemenu.metas"))
      if (shares.code !== 200) failed.push(t("manage.sidemenu.shares"))
      if (failed.length > 0) {
        log("error", t("backup.gather_failed", { names: failed.join(" / ") }))
        return null
      }
      return {
        settings: settings.data ?? [],
        users: stripUserSecrets(users.data?.content ?? []),
        storages: storages.data?.content ?? [],
        metas: metas.data?.content ?? [],
        shares: shares.data?.content ?? [],
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

        // 计划阶段：先算完四个域的对账，再做安全校验 —— 发现要自锁时可以一个字节都不写
        const userPlan = reconcile(payload.users, current.users, (row) => row.username, override.value)
        const storagePlan = reconcile(payload.storages, current.storages, (row) => row.mount_path, override.value)
        const metaPlan = reconcile(payload.metas, current.metas, (row) => row.path, override.value)
        const sharePlan = reconcile(payload.shares, current.shares, (row) => row.id, override.value)

        const me = userStore.me
        const selfRows = userPlan.updates
          .filter((row) => row.username === me?.username)
          .map((row) => ({ ...row, id: me?.id ?? 0 }))
        const risk = selfLockoutRisk(me?.id ?? 0, selfRows)
        if (risk) {
          log("error", t("backup.self_lockout", { name: risk }))
          return
        }

        // 1) 设置：合并写，不删；不受覆盖开关影响（见文件头注释）
        summary.value.push({ section: "settings", result: await applySettings(restorableSettings(payload.settings)) })
        // 2) 用户
        summary.value.push({ section: "users", result: await applyUsers(userPlan, current.users) })
        // 3) 挂载
        summary.value.push({ section: "storages", result: await applyStorages(storagePlan, current.storages) })
        // 4) 元数据（read/write 用户 id 要按用户名重映射 —— 跨实例 id 必然对不上）
        summary.value.push({ section: "metas", result: await applyMetas(metaPlan, payload.users, current.users) })
        // 5) 分享
        summary.value.push({ section: "shares", result: await applyShares(sharePlan) })

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

    async function applyUsers(
      plan: { creates: AdminUser[]; updates: AdminUser[]; skipped: AdminUser[] },
      current: AdminUser[],
    ): Promise<SectionResult> {
      const result = zeroResult()
      result.skipped = plan.skipped.length
      const builtIn = new Set(current.filter((row) => row.is_built_in).map((row) => row.username))
      const byName = new Map(current.map((row) => [row.username, row]))

      for (const row of plan.creates) {
        if (builtIn.has(row.username)) {
          // 备份里的内置管理员在本实例初始化时一定已经存在，不重复建
          result.skipped += 1
          continue
        }
        const resp = await createUser({
          username: row.username,
          // 口令不随备份迁移：随机值 + 默认停用，由管理员重置后再启用
          password: randomPassword(),
          role: row.role ?? UserRole.GENERAL,
          permission: row.permission ?? 0,
          base_path: row.base_path ?? null,
          disabled: true,
          allow_ldap: row.allow_ldap,
        })
        if (resp.code === 200) {
          result.created += 1
          log("warn", t("backup.user_created_disabled", { name: row.username }))
        } else {
          result.failed += 1
          log("error", t("backup.result_failed", { names: row.username, msg: resp.message }))
        }
      }

      for (const row of plan.updates) {
        const target = byName.get(row.username)
        if (!target) {
          result.failed += 1
          continue
        }
        const resp = await updateUser({
          id: target.id,
          // 只覆盖权限相关字段：用户名与密码不动。
          // `sso_id` 不恢复：它是实例侧的 SSO 绑定（对端身份系统给的），
          // 跨实例抄过来只会把账号绑到一个不存在的身份上。
          role: row.role,
          permission: row.permission,
          base_path: row.base_path ?? null,
          disabled: row.disabled,
          allow_ldap: row.allow_ldap,
        })
        if (resp.code === 200) result.updated += 1
        else {
          result.failed += 1
          log("error", t("backup.result_failed", { names: row.username, msg: resp.message }))
        }
      }
      logResult("users", result)
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
      fileUsers: AdminUser[],
      currentUsers: AdminUser[],
    ): Promise<SectionResult> {
      const result = zeroResult()
      result.skipped = plan.skipped.length
      const currentMetas = (await listMetas()).data?.content ?? []
      const byPathMap = new Map(currentMetas.map((row) => [row.path, row]))
      const oldIdToName = userIdToUsername(fileUsers)
      const nameToNewId = usernameToId(currentUsers)

      const mapIds = (row: Meta): Meta => ({
        ...row,
        read_users: remapUserIds(row.read_users, oldIdToName, nameToNewId),
        write_users: remapUserIds(row.write_users, oldIdToName, nameToNewId),
      })

      for (const row of plan.creates) {
        const resp = await saveMeta({ ...mapIds(row), id: 0 })
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
        const resp = await saveMeta({ ...mapIds(row), id: target.id })
        if (resp.code === 200) result.updated += 1
        else {
          result.failed += 1
          log("error", t("backup.result_failed", { names: row.path, msg: resp.message }))
        }
      }
      logResult("metas", result)
      return result
    }

    async function applyShares(plan: {
      creates: ShareInfo[]
      updates: ShareInfo[]
      skipped: ShareInfo[]
    }): Promise<SectionResult> {
      const result = zeroResult()
      result.skipped = plan.skipped.length

      /** 更新时 `accessed` 必须回传原值，否则后端会把访问计数清零（见 api/shares.ts 注释）。 */
      const toReq = (row: ShareInfo) => ({
        id: row.id,
        files: row.files ?? [],
        expires: row.expires ?? null,
        pwd: row.pwd ?? null,
        max_accessed: row.max_accessed ?? 0,
        disabled: row.disabled ?? false,
        remark: row.remark ?? null,
        readme: row.readme ?? null,
        header: row.header ?? null,
        order_by: row.order_by ?? null,
        order_direction: row.order_direction ?? null,
        extract_folder: row.extract_folder ?? null,
        accessed: row.accessed ?? 0,
      })

      for (const row of plan.creates) {
        const resp = await createShare(toReq(row))
        if (resp.code === 200) result.created += 1
        else {
          result.failed += 1
          log("error", t("backup.result_failed", { names: row.id, msg: resp.message }))
        }
      }
      for (const row of plan.updates) {
        const resp = await updateShare(row.id, toReq(row))
        if (resp.code === 200) result.updated += 1
        else {
          result.failed += 1
          log("error", t("backup.result_failed", { names: row.id, msg: resp.message }))
        }
      }
      logResult("shares", result)
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
