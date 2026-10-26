import { defineComponent, reactive, ref, onMounted } from "vue"
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
import { Delete, Edit, Plus, Refresh } from "@element-plus/icons-vue"
import { deleteMeta, listMetas, listUsers, saveMeta } from "~/api/admin"
import type { AdminUser, Meta } from "~/api/types"
import { t } from "~/i18n"

/**
 * 路径元数据（M2）。
 *
 * 一个 Meta = 一条路径上的四类规则：密码、读写白名单、隐藏、readme/header，
 * 每类各带一个 `*_sub`（是否对子目录生效）。
 *
 * 后端 `POST /admin/meta/save` 按 `Id` 分派 create/update，且 update 是整体覆盖，
 * 所以这里每次提交都带全字段（含各 sub 开关），避免把未显示的规则清成默认值。
 */

interface MetaForm {
  id: number
  path: string
  password: string
  pSub: boolean
  readUsers: number[]
  readUsersSub: boolean
  writeUsers: number[]
  writeUsersSub: boolean
  write: boolean
  wSub: boolean
  hide: string
  hSub: boolean
  readme: string
  rSub: boolean
  header: string
  headerSub: boolean
}

function emptyForm(): MetaForm {
  return {
    id: 0,
    path: "/",
    password: "",
    pSub: false,
    readUsers: [],
    readUsersSub: false,
    writeUsers: [],
    writeUsersSub: false,
    write: false,
    wSub: false,
    hide: "",
    hSub: false,
    readme: "",
    rSub: false,
    header: "",
    headerSub: false,
  }
}

function toForm(meta: Meta): MetaForm {
  return {
    id: meta.id,
    path: meta.path,
    password: meta.password ?? "",
    pSub: meta.p_sub,
    readUsers: meta.read_users ?? [],
    readUsersSub: meta.read_users_sub,
    writeUsers: meta.write_users ?? [],
    writeUsersSub: meta.write_users_sub,
    write: meta.write,
    wSub: meta.w_sub,
    hide: meta.hide ?? "",
    hSub: meta.h_sub,
    readme: meta.readme ?? "",
    rSub: meta.r_sub,
    header: meta.header ?? "",
    headerSub: meta.header_sub,
  }
}

export default defineComponent({
  name: "MetasPage",
  setup() {
    const rows = ref<Meta[]>([])
    const users = ref<AdminUser[]>([])
    const loading = ref(false)
    const error = ref("")

    const dialogVisible = ref(false)
    const saving = ref(false)
    const form = reactive<MetaForm>(emptyForm())

    async function load() {
      loading.value = true
      error.value = ""
      const resp = await listMetas()
      loading.value = false
      if (resp.code !== 200) {
        error.value = resp.message
        return
      }
      rows.value = resp.data.content
    }

    async function loadUsers() {
      const resp = await listUsers()
      if (resp.code === 200) users.value = resp.data.content
    }

    onMounted(() => {
      void load()
      void loadUsers()
    })

    function openCreate() {
      Object.assign(form, emptyForm())
      dialogVisible.value = true
    }

    function openEdit(row: Meta) {
      Object.assign(form, toForm(row))
      dialogVisible.value = true
    }

    async function onSave() {
      if (!form.path.trim()) {
        ElMessage.warning(t("global.empty_input"))
        return
      }
      const payload: Meta = {
        id: form.id,
        path: form.path,
        password: form.password || null,
        p_sub: form.pSub,
        read_users: form.readUsers,
        read_users_sub: form.readUsersSub,
        write_users: form.writeUsers,
        write_users_sub: form.writeUsersSub,
        write: form.write,
        w_sub: form.wSub,
        // hide 必须给字符串：旧库该列是 INTEGER NOT NULL，null 会被后端拒绝（500）
        hide: form.hide,
        h_sub: form.hSub,
        readme: form.readme || null,
        r_sub: form.rSub,
        header: form.header || null,
        header_sub: form.headerSub,
      }
      saving.value = true
      const resp = await saveMeta(payload)
      saving.value = false
      if (resp.code !== 200) {
        ElMessage.error(resp.message)
        return
      }
      ElMessage.success(t(form.id ? "global.update_success" : "global.save_success"))
      dialogVisible.value = false
      await load()
    }

    async function onDelete(row: Meta) {
      try {
        await ElMessageBox.confirm(
          t("global.delete_confirm", { name: row.path }),
          t("global.delete"),
          { type: "warning" },
        )
      } catch {
        return
      }
      const resp = await deleteMeta(row.id)
      if (resp.code !== 200) {
        ElMessage.error(resp.message)
        return
      }
      ElMessage.success(t("global.delete_success"))
      await load()
    }

    /** 一行「开关 + 说明」的通用渲染，避免 7 个 sub 开关重复抄写。 */
    function SubSwitch(props: {
      modelValue: boolean
      onUpdate: (v: boolean) => void
      label: string
    }) {
      return (
        <div class="ol-sub-switch">
          <ElSwitch
            modelValue={props.modelValue}
            onUpdate:modelValue={(v: string | number | boolean) => props.onUpdate(Boolean(v))}
          />
          <span class="ol-field-help">{props.label}</span>
        </div>
      )
    }

    return () => (
      <div class="ol-manage-page">
        <ElCard shadow="never">
          <div class="ol-manage-toolbar">
            <h2 class="ol-manage-h2">{t("manage.sidemenu.metas")}</h2>
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
            title={t("metas.tips")}
            class="ol-manage-tip"
          />

          {error.value && (
            <ElAlert type="error" showIcon={true} title={error.value} closable={false} />
          )}

          <ElTable data={rows.value} size="small" style={{ width: "100%" }}>
            <ElTableColumn label={t("metas.path")} prop="path" minWidth={180}>
              {{
                default: ({ row }) => {
                  const r = row as unknown as Meta
                  return <span class="ol-mono">{r.path}</span>
                },
              }}
            </ElTableColumn>
            <ElTableColumn label={t("metas.password")} width={110}>
              {{
                default: ({ row }) => {
                  const r = row as unknown as Meta
                  return r.password ? (
                    <ElTag size="small" type="warning">
                      {r.p_sub ? t("metas.sub_mark") : "•••"}
                    </ElTag>
                  ) : (
                    <span class="ol-muted">-</span>
                  )
                },
              }}
            </ElTableColumn>
            <ElTableColumn label={t("metas.write")} width={110}>
              {{
                default: ({ row }) => {
                  const r = row as unknown as Meta
                  return r.write ? (
                    <ElTag size="small" type="danger">
                      {r.w_sub ? t("metas.sub_mark") : "ON"}
                    </ElTag>
                  ) : (
                    <span class="ol-muted">-</span>
                  )
                },
              }}
            </ElTableColumn>
            <ElTableColumn label={t("metas.write_users")} width={120}>
              {{
                default: ({ row }) => {
                  const r = row as unknown as Meta
                  const n = (r.write_users ?? []).length
                  return n ? (
                    <span>{n}</span>
                  ) : (
                    <span class="ol-muted">{t("metas.all_permitted_users")}</span>
                  )
                },
              }}
            </ElTableColumn>
            <ElTableColumn label={t("metas.read_users")} width={120}>
              {{
                default: ({ row }) => {
                  const r = row as unknown as Meta
                  const n = (r.read_users ?? []).length
                  return n ? (
                    <span>{n}</span>
                  ) : (
                    <span class="ol-muted">{t("metas.all_permitted_users")}</span>
                  )
                },
              }}
            </ElTableColumn>
            <ElTableColumn label={t("metas.hide")} minWidth={140} showOverflowTooltip={true}>
              {{
                default: ({ row }) => {
                  const r = row as unknown as Meta
                  return r.hide ? (
                    <span class="ol-mono">{r.hide.replace(/\n/g, " · ")}</span>
                  ) : (
                    <span class="ol-muted">-</span>
                  )
                },
              }}
            </ElTableColumn>
            <ElTableColumn label={t("metas.readme")} width={100}>
              {{
                default: ({ row }) => {
                  const r = row as unknown as Meta
                  return r.readme ? (
                    <ElTag size="small" type="info">
                      {r.r_sub ? t("metas.sub_mark") : "ON"}
                    </ElTag>
                  ) : (
                    <span class="ol-muted">-</span>
                  )
                },
              }}
            </ElTableColumn>
            <ElTableColumn label={t("metas.header")} width={100}>
              {{
                default: ({ row }) => {
                  const r = row as unknown as Meta
                  return r.header ? (
                    <ElTag size="small" type="info">
                      {r.header_sub ? t("metas.sub_mark") : "ON"}
                    </ElTag>
                  ) : (
                    <span class="ol-muted">-</span>
                  )
                },
              }}
            </ElTableColumn>
            <ElTableColumn label={t("global.operations")} width={170} align="center">
              {{
                default: ({ row }) => {
                  const r = row as unknown as Meta
                  return (
                    <div class="ol-manage-row-ops">
                      <ElButton size="small" text={true} type="primary" onClick={() => openEdit(r)}>
                        <ElIcon>
                          <Edit />
                        </ElIcon>
                        {t("global.edit")}
                      </ElButton>
                      <ElButton size="small" text={true} type="danger" onClick={() => onDelete(r)}>
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

        <ElDialog
          modelValue={dialogVisible.value}
          title={t(form.id ? "global.edit" : "global.add")}
          width="720px"
          top="6vh"
          onUpdate:modelValue={(v: boolean) => (dialogVisible.value = v)}
        >
          {{
            default: () => (
              <ElForm labelWidth="170px">
                <ElFormItem label={t("metas.path")} required={true}>
                  {{
                    default: () => (
                      <ElInput
                        modelValue={form.path}
                        placeholder="/foo"
                        onUpdate:modelValue={(v: string) => (form.path = v)}
                      />
                    ),
                  }}
                </ElFormItem>

                <h4 class="ol-manage-section">{t("metas.section_password")}</h4>
                <ElFormItem label={t("metas.password")}>
                  {{
                    default: () => (
                      <div class="ol-field-col">
                        <ElInput
                          type="password"
                          showPassword={true}
                          modelValue={form.password}
                          onUpdate:modelValue={(v: string) => (form.password = v)}
                        />
                        <SubSwitch
                          modelValue={form.pSub}
                          onUpdate={(v) => (form.pSub = v)}
                          label={t("metas.apply_sub")}
                        />
                      </div>
                    ),
                  }}
                </ElFormItem>

                <h4 class="ol-manage-section">{t("metas.section_permission")}</h4>
                <ElFormItem label={t("metas.write")}>
                  {{
                    default: () => (
                      <div class="ol-field-col">
                        <ElSwitch
                          modelValue={form.write}
                          onUpdate:modelValue={(v: string | number | boolean) =>
                            (form.write = Boolean(v))
                          }
                        />
                        <span class="ol-field-help">{t("metas.write_help")}</span>
                        <SubSwitch
                          modelValue={form.wSub}
                          onUpdate={(v) => (form.wSub = v)}
                          label={t("metas.apply_sub")}
                        />
                      </div>
                    ),
                  }}
                </ElFormItem>
                <ElFormItem label={t("metas.read_users")}>
                  {{
                    default: () => (
                      <div class="ol-field-col">
                        <ElSelect
                          multiple={true}
                          modelValue={form.readUsers}
                          placeholder={t("metas.all_permitted_users")}
                          onUpdate:modelValue={(v: unknown) =>
                            (form.readUsers = (v as number[]) ?? [])
                          }
                        >
                          {users.value.map((u) => (
                            <ElOption key={u.id} label={u.username} value={u.id} />
                          ))}
                        </ElSelect>
                        <span class="ol-field-help">{t("metas.read_users_help")}</span>
                        <SubSwitch
                          modelValue={form.readUsersSub}
                          onUpdate={(v) => (form.readUsersSub = v)}
                          label={t("metas.apply_sub")}
                        />
                      </div>
                    ),
                  }}
                </ElFormItem>
                <ElFormItem label={t("metas.write_users")}>
                  {{
                    default: () => (
                      <div class="ol-field-col">
                        <ElSelect
                          multiple={true}
                          modelValue={form.writeUsers}
                          placeholder={t("metas.all_permitted_users")}
                          onUpdate:modelValue={(v: unknown) =>
                            (form.writeUsers = (v as number[]) ?? [])
                          }
                        >
                          {users.value.map((u) => (
                            <ElOption key={u.id} label={u.username} value={u.id} />
                          ))}
                        </ElSelect>
                        <span class="ol-field-help">{t("metas.write_users_help")}</span>
                        <SubSwitch
                          modelValue={form.writeUsersSub}
                          onUpdate={(v) => (form.writeUsersSub = v)}
                          label={t("metas.apply_sub")}
                        />
                      </div>
                    ),
                  }}
                </ElFormItem>

                <h4 class="ol-manage-section">{t("metas.section_view")}</h4>
                <ElFormItem label={t("metas.hide")}>
                  {{
                    default: () => (
                      <div class="ol-field-col">
                        <ElInput
                          type="textarea"
                          rows={3}
                          modelValue={form.hide}
                          placeholder={t("metas.hide_help")}
                          onUpdate:modelValue={(v: string) => (form.hide = v)}
                        />
                        <SubSwitch
                          modelValue={form.hSub}
                          onUpdate={(v) => (form.hSub = v)}
                          label={t("metas.apply_sub")}
                        />
                      </div>
                    ),
                  }}
                </ElFormItem>
                <ElFormItem label={t("metas.readme")}>
                  {{
                    default: () => (
                      <div class="ol-field-col">
                        <ElInput
                          modelValue={form.readme}
                          placeholder={t("metas.readme_help")}
                          onUpdate:modelValue={(v: string) => (form.readme = v)}
                        />
                        <SubSwitch
                          modelValue={form.rSub}
                          onUpdate={(v) => (form.rSub = v)}
                          label={t("metas.apply_sub")}
                        />
                      </div>
                    ),
                  }}
                </ElFormItem>
                <ElFormItem label={t("metas.header")}>
                  {{
                    default: () => (
                      <div class="ol-field-col">
                        <ElInput
                          modelValue={form.header}
                          placeholder={t("metas.header_help")}
                          onUpdate:modelValue={(v: string) => (form.header = v)}
                        />
                        <SubSwitch
                          modelValue={form.headerSub}
                          onUpdate={(v) => (form.headerSub = v)}
                          label={t("metas.apply_sub")}
                        />
                      </div>
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
