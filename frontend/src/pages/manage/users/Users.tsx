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
  ElSwitch,
  ElTag,
} from "element-plus"
import { VexTable, VexColumn } from "~/components/table"
import { Edit, Refresh } from "@element-plus/icons-vue"
import { listUsers, updateUser } from "~/api/admin"
import type { AdminUser, UpdateUserReq } from "~/api/types"
import { t } from "~/i18n"
import { formatTime } from "~/utils/format"

/**
 * 用户管理（管理台）。
 *
 * 后端真源：`AdminEndpoints` 的 `/admin/user/list` + `/admin/user/update`，
 * 以及 `IUserService.ListAsync` / `UpdateAsync`。
 *
 * **这个页面刻意只剩「编辑」一个写操作**：
 * - `添加` / `删除`：产品定位是个人资源管理，库里只有一个内置管理员，
 *   新建第二个账号会牵动「谁能看到什么」的整套语义，后端也不提供这两个端点；
 * - `禁用`：同理，先把入口收起来（编辑弹窗里的禁用开关也不渲染）。
 *
 * 三条后端约束必须遵守：
 * 1. `update` 是**部分更新** —— 没传的字段保持原值，所以这里只把「实际改过」的字段
 *    塞进请求体，密码留空表示不改密码。
 * 2. 列表接口返回的是 `UserPublic`，没有密码哈希 —— 页面也拿不到，不要试图展示。
 * 3. 时间字段是 camelCase（`createdAt` / `updatedAt`）：全局序列化策略是 CamelCase，
 *    只有标了 `[JsonPropertyName]` 的 `otp` / `allow_ldap` / `sso_id` 保持 snake_case。
 */

/**
 * 暂时隐藏的入口。放开某个开关之前先把对应后端端点补回来 ——
 * 「添加」需要 `POST /admin/user/create`，「禁用」需要把 `disabled` 下发到 update。
 */
const SHOW_CREATE = false
const SHOW_DISABLE = false

export default defineComponent({
  name: "UsersPage",
  setup() {
    const rows = ref<AdminUser[]>([])
    const loading = ref(false)
    const error = ref("")

    const dialogVisible = ref(false)
    const editing = ref<AdminUser | null>(null)
    const saving = ref(false)
    const form = reactive({
      username: "",
      password: "",
      allowLdap: false,
      disabled: false,
    })

    async function load() {
      loading.value = true
      error.value = ""
      const resp = await listUsers()
      loading.value = false
      if (resp.code !== 200) {
        error.value = resp.message
        return
      }
      rows.value = resp.data.content
    }

    onMounted(() => {
      void load()
    })

    function openEdit(row: AdminUser) {
      editing.value = row
      form.username = row.username
      form.password = ""
      form.allowLdap = row.allow_ldap
      form.disabled = row.disabled
      dialogVisible.value = true
    }

    async function onSave() {
      const target = editing.value
      if (!target) return
      if (!form.username.trim()) {
        ElMessage.warning(t("global.empty_input"))
        return
      }

      // 只提交改过的字段：部分更新语义下，把没改的字段一起发过去等于凭空覆盖。
      const payload: UpdateUserReq = { id: target.id }
      if (form.username.trim() !== target.username) payload.username = form.username.trim()
      if (form.password) payload.password = form.password
      if (form.allowLdap !== target.allow_ldap) payload.allow_ldap = form.allowLdap
      if (SHOW_DISABLE && form.disabled !== target.disabled) payload.disabled = form.disabled

      if (Object.keys(payload).length === 1) {
        // 一个字都没改：不必惊动后端
        dialogVisible.value = false
        return
      }

      saving.value = true
      const resp = await updateUser(payload)
      saving.value = false
      if (resp.code !== 200) {
        ElMessage.error(resp.message)
        return
      }
      ElMessage.success(t("global.update_success"))
      dialogVisible.value = false
      await load()
    }

    return () => (
      <div class="ol-manage-page">
        <ElCard shadow="never">
          <div class="ol-manage-toolbar">
            <h2 class="ol-manage-h2">{t("manage.sidemenu.users")}</h2>
            <div class="ol-manage-actions">
              <ElButton size="small" loading={loading.value} onClick={load}>
                <ElIcon>
                  <Refresh />
                </ElIcon>
                {t("global.refresh")}
              </ElButton>
              {SHOW_CREATE && (
                <ElButton size="small" type="primary">
                  {t("global.add")}
                </ElButton>
              )}
            </div>
          </div>

          <ElAlert
            type="info"
            showIcon={true}
            closable={false}
            title={t("users.tips")}
            class="ol-manage-tip"
          />

          {error.value && (
            <ElAlert type="error" showIcon={true} title={error.value} closable={false} />
          )}

          <VexTable data={rows.value} size="small" style={{ width: "100%" }}>
            <VexColumn prop="id" label="ID" width={64} />
            <VexColumn label={t("users.username")} minWidth={150}>
              {{
                default: ({ row }) => {
                  const r = row as unknown as AdminUser
                  return <span>{r.username}</span>
                },
              }}
            </VexColumn>
            <VexColumn label={t("users.status")} width={100}>
              {{
                default: ({ row }) => {
                  const r = row as unknown as AdminUser
                  return (
                    <ElTag size="small" type={r.disabled ? "info" : "success"}>
                      {t(r.disabled ? "global.disable" : "users.available")}
                    </ElTag>
                  )
                },
              }}
            </VexColumn>
            <VexColumn label="2FA" width={80}>
              {{
                default: ({ row }) => {
                  const r = row as unknown as AdminUser
                  return (
                    <ElTag size="small" type={r.otp ? "success" : "info"}>
                      {r.otp ? "ON" : "OFF"}
                    </ElTag>
                  )
                },
              }}
            </VexColumn>
            <VexColumn label={t("users.allow_ldap")} width={150}>
              {{
                default: ({ row }) => {
                  const r = row as unknown as AdminUser
                  return (
                    <ElTag size="small" type={r.allow_ldap ? "success" : "info"}>
                      {r.allow_ldap ? "ON" : "OFF"}
                    </ElTag>
                  )
                },
              }}
            </VexColumn>
            <VexColumn label={t("users.created_at")} width={170}>
              {{
                default: ({ row }) => {
                  const r = row as unknown as AdminUser
                  return <span class="ol-mono">{formatTime(r.createdAt)}</span>
                },
              }}
            </VexColumn>
            <VexColumn label={t("global.operations")} width={120} align="center">
              {{
                default: ({ row }) => {
                  const r = row as unknown as AdminUser
                  return (
                    <div class="ol-manage-row-ops">
                      {/* 只有「编辑」：添加 / 禁用 / 删除等入口暂时收起（见文件头注释）。 */}
                      <ElButton size="small" text={true} type="primary" onClick={() => openEdit(r)}>
                        <ElIcon>
                          <Edit />
                        </ElIcon>
                        {t("global.edit")}
                      </ElButton>
                    </div>
                  )
                },
              }}
            </VexColumn>
          </VexTable>
        </ElCard>

        <ElDialog
          modelValue={dialogVisible.value}
          title={t("global.edit")}
          width="560px"
          onUpdate:modelValue={(v: boolean) => (dialogVisible.value = v)}
        >
          {{
            default: () => (
              <ElForm labelWidth="150px">
                <ElFormItem label={t("users.username")} required={true}>
                  {{
                    default: () => (
                      <ElInput
                        modelValue={form.username}
                        onUpdate:modelValue={(v: string) => (form.username = v)}
                      />
                    ),
                  }}
                </ElFormItem>
                <ElFormItem label={t("users.password")}>
                  {{
                    default: () => (
                      <ElInput
                        type="password"
                        showPassword={true}
                        modelValue={form.password}
                        placeholder={t("users.password_tips")}
                        onUpdate:modelValue={(v: string) => (form.password = v)}
                      />
                    ),
                  }}
                </ElFormItem>
                <ElFormItem label={t("users.allow_ldap")}>
                  {{
                    default: () => (
                      <ElSwitch
                        modelValue={form.allowLdap}
                        onUpdate:modelValue={(v: string | number | boolean) =>
                          (form.allowLdap = Boolean(v))
                        }
                      />
                    ),
                  }}
                </ElFormItem>
                {SHOW_DISABLE && (
                  <ElFormItem label={t("global.disable")}>
                    {{
                      default: () => (
                        <ElSwitch
                          modelValue={form.disabled}
                          onUpdate:modelValue={(v: string | number | boolean) =>
                            (form.disabled = Boolean(v))
                          }
                        />
                      ),
                    }}
                  </ElFormItem>
                )}
              </ElForm>
            ),
            footer: () => (
              <div class="ol-manage-form-actions">
                <ElButton onClick={() => (dialogVisible.value = false)}>
                  {t("global.cancel")}
                </ElButton>
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
