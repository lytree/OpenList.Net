import { defineComponent, reactive, ref, onMounted } from "vue"
import {
  ElAlert,
  ElButton,
  ElCard,
  ElDatePicker,
  ElDialog,
  ElForm,
  ElFormItem,
  ElIcon,
  ElInput,
  ElInputNumber,
  ElMessage,
  ElMessageBox,
  ElTable,
  ElTableColumn,
  ElTag,
} from "element-plus"
import { CopyDocument, Delete, Edit, Link, Plus, Refresh, VideoPause, VideoPlay } from "@element-plus/icons-vue"
import {
  createShare,
  deleteShare,
  disableShare,
  enableShare,
  getShare,
  listShares,
  updateShare,
} from "~/api/shares"
import type { ShareInfo, ShareReq } from "~/api/types"
import { formatTime } from "~/utils/format"
import { t } from "~/i18n"

/**
 * 分享管理（M2 · shares）。
 *
 * 后端 `/api/share` 是**用户级**接口：管理员可见全部、普通用户只看自己。三条必须遵守：
 * 1. 定位一律用**分享码**（`?id=`），不是数据库主键；改分享码要用 `new_id`。
 * 2. `UpdateAsync` 会无条件覆盖 `accessed`，编辑表单必须回传当前访问次数，否则计数清零。
 * 3. 创建要求 `files` 至少一项，第一项会成为分享根路径（`Sharing.Path`）。
 *
 * ⚠️ 能力缺口：后端目前只有 `/sd/{sid}[/path]` 的**直链流式下载**，没有「按分享码列目录」
 * 的公开接口，也没有公开的分享详情接口。因此本页只能给出单文件的直链，做不出可浏览的
 * 分享页 —— 目录分享的链接点开是 404，UI 里已明确标注。
 */
interface ShareForm {
  filesText: string
  code: string
  pwd: string
  expires: string
  maxAccessed: number
  remark: string
}

function emptyForm(): ShareForm {
  return { filesText: "", code: "", pwd: "", expires: "", maxAccessed: 0, remark: "" }
}

export default defineComponent({
  name: "SharesPage",
  setup() {
    const rows = ref<ShareInfo[]>([])
    const loading = ref(false)
    const error = ref("")

    const dialogVisible = ref(false)
    const editing = ref<ShareInfo | null>(null)
    const saving = ref(false)
    const form = reactive<ShareForm>(emptyForm())

    async function load() {
      loading.value = true
      error.value = ""
      const resp = await listShares()
      loading.value = false
      if (resp.code !== 200) {
        error.value = resp.message
        return
      }
      rows.value = resp.data.content
    }
    onMounted(load)

    function openCreate() {
      editing.value = null
      Object.assign(form, emptyForm())
      dialogVisible.value = true
    }

    async function openEdit(row: ShareInfo) {
      // 必须重新拉一次：`accessed` 会被每次访问递增，用列表快照回传会把计数写回旧值
      const resp = await getShare(row.id)
      const fresh = resp.code === 200 ? resp.data : row
      editing.value = fresh
      Object.assign(form, {
        filesText: (fresh.files ?? []).join("\n"),
        code: fresh.id,
        pwd: fresh.pwd ?? "",
        expires: fresh.expires ? fresh.expires.replace(/(\.\d+)?(Z|[+-]\d\d:\d\d)$/, "") : "",
        maxAccessed: fresh.max_accessed ?? 0,
        remark: fresh.remark ?? "",
      })
      dialogVisible.value = true
    }

    function parseFiles(): string[] {
      return form.filesText
        .split("\n")
        .map((s) => s.trim())
        .filter(Boolean)
    }

    async function onSave() {
      const files = parseFiles()
      if (!files.length) {
        ElMessage.warning(t("shares.files_placeholder"))
        return
      }
      const base = {
        files,
        pwd: form.pwd || null,
        expires: form.expires || null,
        max_accessed: form.maxAccessed ?? 0,
        disabled: editing.value?.disabled ?? false,
        remark: form.remark || null,
      }
      saving.value = true
      const resp = editing.value
        ? await updateShare(editing.value.id, {
            ...base,
            // 改名走 new_id；不填则保持原码
            new_id: form.code && form.code !== editing.value.id ? form.code : null,
            // 后端会无条件覆盖 accessed，必须回传原值
            accessed: editing.value.accessed,
          })
        : await createShare({
            ...base,
            id: form.code || null,
            accessed: 0,
          } as ShareReq)
      saving.value = false
      if (resp.code !== 200) {
        ElMessage.error(resp.message)
        return
      }
      ElMessage.success(t(editing.value ? "global.update_success" : "global.save_success"))
      dialogVisible.value = false
      await load()
    }

    async function onToggle(row: ShareInfo) {
      const resp = row.disabled ? await enableShare(row.id) : await disableShare(row.id)
      if (resp.code !== 200) {
        ElMessage.error(resp.message)
        return
      }
      await load()
    }

    async function onDelete(row: ShareInfo) {
      try {
        await ElMessageBox.confirm(
          t("global.delete_confirm", { name: row.id }),
          t("global.delete"),
          { type: "warning" },
        )
      } catch {
        return
      }
      const resp = await deleteShare(row.id)
      if (resp.code !== 200) {
        ElMessage.error(resp.message)
        return
      }
      ElMessage.success(t("global.delete_success"))
      await load()
    }

    async function copy(text: string, successKey: string) {
      try {
        await navigator.clipboard.writeText(text)
        ElMessage.success(t(successKey))
      } catch {
        ElMessage.error(t("global.clipboard_denied"))
      }
    }

    /** 分享页地址（公开、只读、受密码/过期/次数约束）。 */
    const shareLink = (code: string) => `${location.origin}/#/s/${code}`

    function sharePath(row: ShareInfo): string {
      const files = row.files ?? []
      if (!files.length) return "-"
      return files.length > 1 ? `${files[0]} (+${files.length - 1})` : files[0]
    }

    return () => (
      <div class="ol-manage-page">
        <ElCard shadow="never">
          <div class="ol-manage-toolbar">
            <h2 class="ol-manage-h2">{t("manage.sidemenu.shares")}</h2>
            <div class="ol-manage-actions">
              <ElButton size="small" loading={loading.value} onClick={load}>
                <ElIcon>
                  <Refresh />
                </ElIcon>
                {t("global.refresh")}
              </ElButton>
              <ElButton size="small" type="primary" onClick={openCreate}>
                <ElIcon>
                  <Plus />
                </ElIcon>
                {t("global.add")}
              </ElButton>
            </div>
          </div>

          <ElAlert
            type="info"
            showIcon={true}
            closable={false}
            class="ol-manage-tip"
            title={t("shares.link_hint")}
          />

          {error.value && (
            <ElAlert type="error" showIcon={true} title={error.value} closable={false} />
          )}

          <ElTable data={rows.value} size="small" style={{ width: "100%" }}>
            <ElTableColumn label={t("shares.id")} width={140}>
              {{
                default: ({ row }) => {
                  const r = row as unknown as ShareInfo
                  return (
                    <div class="ol-manage-inline">
                      <span class="ol-mono">{r.id}</span>
                      <span title={t("shares.copy_code")}>
                        <ElButton
                          size="small"
                          text={true}
                          onClick={() => copy(r.id, "global.copied")}
                        >
                          <ElIcon>
                            <CopyDocument />
                          </ElIcon>
                        </ElButton>
                      </span>
                    </div>
                  )
                },
              }}
            </ElTableColumn>
            <ElTableColumn label={t("shares.files")} minWidth={170} showOverflowTooltip={true}>
              {{
                default: ({ row }) => {
                  const r = row as unknown as ShareInfo
                  return <span class="ol-mono">{sharePath(r)}</span>
                },
              }}
            </ElTableColumn>
            <ElTableColumn label={t("shares.creator")} prop="creator" width={100} />
            <ElTableColumn label={t("shares.pwd")} width={76}>
              {{
                default: ({ row }) => {
                  const r = row as unknown as ShareInfo
                  return r.pwd ? (
                    <ElTag size="small" type="warning">
                      •••
                    </ElTag>
                  ) : (
                    <span class="ol-muted">-</span>
                  )
                },
              }}
            </ElTableColumn>
            <ElTableColumn label={t("shares.expires")} width={128}>
              {{
                default: ({ row }) => {
                  const r = row as unknown as ShareInfo
                  return r.expires ? (
                    <span class="ol-field-help">{formatTime(r.expires)}</span>
                  ) : (
                    <span class="ol-muted">{t("shares.never")}</span>
                  )
                },
              }}
            </ElTableColumn>
            <ElTableColumn label={t("shares.accessed")} width={88}>
              {{
                default: ({ row }) => {
                  const r = row as unknown as ShareInfo
                  const max = r.max_accessed > 0 ? r.max_accessed : "∞"
                  return (
                    <span>
                      {r.accessed} / {max}
                    </span>
                  )
                },
              }}
            </ElTableColumn>
            <ElTableColumn label={t("shares.status")} width={72}>
              {{
                default: ({ row }) => {
                  const r = row as unknown as ShareInfo
                  return (
                    <ElTag size="small" type={r.disabled ? "info" : "success"}>
                      {t(`shares.status_list.${r.disabled ? "disabled" : "work"}`)}
                    </ElTag>
                  )
                },
              }}
            </ElTableColumn>
            <ElTableColumn label={t("global.operations")} width={118} align="center">
              {{
                default: ({ row }) => {
                  const r = row as unknown as ShareInfo
                  return (
                    <div class="ol-manage-row-ops">
                      <span title={shareLink(r.id)}>
                        <ElButton
                          size="small"
                          text={true}
                          onClick={() => copy(shareLink(r.id), "shares.copy_msg")}
                        >
                          <ElIcon>
                            <Link />
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
                      <span title={r.disabled ? t("global.enable") : t("global.disable")}>
                        <ElButton size="small" text={true} onClick={() => onToggle(r)}>
                          <ElIcon>{r.disabled ? <VideoPlay /> : <VideoPause />}</ElIcon>
                        </ElButton>
                      </span>
                      <span title={t("global.delete")}>
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

        <ElDialog
          modelValue={dialogVisible.value}
          title={t(editing.value ? "global.edit" : "global.add")}
          width="620px"
          onUpdate:modelValue={(v: boolean) => (dialogVisible.value = v)}
        >
          {{
            default: () => (
              <ElForm labelWidth="180px">
                <ElFormItem label={t("shares.files")} required={true}>
                  {{
                    default: () => (
                      <ElInput
                        type="textarea"
                        rows={3}
                        modelValue={form.filesText}
                        placeholder={t("shares.files_placeholder")}
                        onUpdate:modelValue={(v: string) => (form.filesText = v)}
                      />
                    ),
                  }}
                </ElFormItem>
                <ElFormItem label={t("shares.id")}>
                  {{
                    default: () => (
                      <ElInput
                        modelValue={form.code}
                        placeholder={t("shares.id_placeholder")}
                        onUpdate:modelValue={(v: string) => (form.code = v)}
                      />
                    ),
                  }}
                </ElFormItem>
                <ElFormItem label={t("shares.password")}>
                  {{
                    default: () => (
                      <ElInput
                        type="password"
                        showPassword={true}
                        modelValue={form.pwd}
                        onUpdate:modelValue={(v: string) => (form.pwd = v)}
                      />
                    ),
                  }}
                </ElFormItem>
                <ElFormItem label={t("shares.expires")}>
                  {{
                    default: () => (
                      <ElDatePicker
                        type="datetime"
                        modelValue={form.expires || null}
                        valueFormat="YYYY-MM-DDTHH:mm:ss"
                        placeholder={t("shares.never")}
                        onUpdate:modelValue={(v: string | null) => (form.expires = v ?? "")}
                      />
                    ),
                  }}
                </ElFormItem>
                <ElFormItem label={t("shares.max_accessed")}>
                  {{
                    default: () => (
                      <div class="ol-field-col">
                        <ElInputNumber
                          modelValue={form.maxAccessed}
                          min={0}
                          onUpdate:modelValue={(v: number | undefined) =>
                            (form.maxAccessed = v ?? 0)
                          }
                        />
                        <span class="ol-field-help">{t("shares.max_accessed_help")}</span>
                      </div>
                    ),
                  }}
                </ElFormItem>
                <ElFormItem label={t("shares.remark")}>
                  {{
                    default: () => (
                      <ElInput
                        modelValue={form.remark}
                        onUpdate:modelValue={(v: string) => (form.remark = v)}
                      />
                    ),
                  }}
                </ElFormItem>
              </ElForm>
            ),
            footer: () => (
              <div class="ol-manage-form-actions">
                <ElButton onClick={() => (dialogVisible.value = false)}>{t("global.cancel")}</ElButton>
                <ElButton type="primary" loading={saving.value} onClick={onSave}>
                  {t("global.save")}
                </ElButton>
              </div>
            ),
          }}
        </ElDialog>
      </div>
    )
  },
})
