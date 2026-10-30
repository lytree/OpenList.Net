import { defineComponent, computed, reactive, ref, onMounted } from "vue"
import {
  ElAlert,
  ElButton,
  ElCard,
  ElCheckbox,
  ElCheckboxGroup,
  ElDialog,
  ElDropdown,
  ElDropdownItem,
  ElDropdownMenu,
  ElForm,
  ElFormItem,
  ElIcon,
  ElInput,
  ElMessage,
  ElMessageBox,
  ElOption,
  ElSelect,
  ElSwitch,
  ElTag,
} from "element-plus"
import { VexTable, VexColumn } from "~/components/table"
import { Edit, Key, MoreFilled, Plus, Refresh } from "@element-plus/icons-vue"
import {
  cancelUser2Fa,
  createUser,
  deleteUser,
  listUsers,
  setUserPassword,
  updateUser,
} from "~/api/admin"
import { PermissionBits, UserRole, type AdminUser, type Permission } from "~/api/types"
import { t } from "~/i18n"

/**
 * 用户管理（M2）。
 *
 * 后端真源：`AdminEndpoints` 的 `/admin/user/*` + `IUserService` 的
 * `CreateUserReq` / `UpdateUserReq`。三条必须遵守的后端约束：
 * 1. `update` 是**部分更新** —— 没传的字段保持原值，但传 `null` 会被写成 null，
 *    所以这里只把「用户实际改过」的字段塞进请求体。
 * 2. 内置管理员（`is_built_in`）不可删除 / 改名 / 降权，前端直接禁用对应控件，
 *    而不是让后端报错。
 * 3. `permission` 是位掩码，位号以 `PermissionBits`（真源 `User.cs`）为准；
 *    不要再按旧前端的数组下标算。
 */
const PERM_KEYS = Object.keys(PermissionBits) as Permission[]

const ROLE_OPTIONS = [
  { value: UserRole.GENERAL, labelKey: "users.roles.general" },
  { value: UserRole.GUEST, labelKey: "users.roles.guest" },
  { value: UserRole.ADMIN, labelKey: "users.roles.admin" },
]

function bitsToKeys(mask: number): Permission[] {
  return PERM_KEYS.filter((key) => ((mask >> PermissionBits[key]) & 1) === 1)
}

function keysToBits(keys: Permission[]): number {
  return keys.reduce((acc, key) => acc | (1 << PermissionBits[key]), 0)
}

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
      role: UserRole.GENERAL as number,
      basePath: "",
      disabled: false,
      allowLdap: false,
      perms: [] as Permission[],
    })

    const pwdVisible = ref(false)
    const pwdTarget = ref<AdminUser | null>(null)
    const pwd = reactive({ password: "", confirm: "" })

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
    onMounted(load)

    function openCreate() {
      editing.value = null
      form.username = ""
      form.password = ""
      form.role = UserRole.GENERAL
      form.basePath = ""
      form.disabled = false
      form.allowLdap = false
      form.perms = bitsToKeys(0)
      dialogVisible.value = true
    }

    function openEdit(row: AdminUser) {
      editing.value = row
      form.username = row.username
      form.password = ""
      form.role = row.role
      form.basePath = row.base_path ?? ""
      form.disabled = row.disabled
      form.allowLdap = row.allow_ldap
      form.perms = bitsToKeys(row.permission)
      dialogVisible.value = true
    }

    async function onSave() {
      if (!form.username.trim()) {
        ElMessage.warning(t("global.empty_input"))
        return
      }
      if (!editing.value && !form.password) {
        ElMessage.warning(t("global.empty_input"))
        return
      }
      saving.value = true
      const permission = keysToBits(form.perms)
      const resp = editing.value
        ? await updateUser({
            id: editing.value.id,
            // 内置管理员不允许改名/降权，连字段都不发，避免后端返回 403 让用户困惑
            username: editing.value.is_built_in ? undefined : form.username,
            role: editing.value.is_built_in ? undefined : (form.role as UserRole),
            password: form.password || undefined,
            permission,
            base_path: form.basePath || null,
            disabled: form.disabled,
            allow_ldap: form.allowLdap,
          })
        : await createUser({
            username: form.username,
            password: form.password,
            role: form.role as UserRole,
            permission,
            base_path: form.basePath || null,
            disabled: form.disabled,
            allow_ldap: form.allowLdap,
          })
      saving.value = false
      if (resp.code !== 200) {
        ElMessage.error(resp.message)
        return
      }
      ElMessage.success(t(editing.value ? "global.update_success" : "global.save_success"))
      dialogVisible.value = false
      await load()
    }

    function openPassword(row: AdminUser) {
      pwdTarget.value = row
      pwd.password = ""
      pwd.confirm = ""
      pwdVisible.value = true
    }

    async function onSavePassword() {
      if (!pwd.password) {
        ElMessage.warning(t("global.empty_input"))
        return
      }
      if (pwd.password !== pwd.confirm) {
        ElMessage.warning(t("users.confirm_password_not_same"))
        return
      }
      const target = pwdTarget.value
      if (!target) return
      const resp = await setUserPassword(target.id, pwd.password)
      if (resp.code !== 200) {
        ElMessage.error(resp.message)
        return
      }
      ElMessage.success(t("global.update_success"))
      pwdVisible.value = false
    }

    async function onCancel2Fa(row: AdminUser) {
      const resp = await cancelUser2Fa(row.id)
      if (resp.code !== 200) {
        ElMessage.error(resp.message)
        return
      }
      ElMessage.success(t("users.cancel_2fa_success"))
      await load()
    }

    async function onDelete(row: AdminUser) {
      try {
        await ElMessageBox.confirm(
          t("global.delete_confirm", { name: row.username }),
          t("global.delete"),
          { type: "warning" },
        )
      } catch {
        return
      }
      const resp = await deleteUser(row.id)
      if (resp.code !== 200) {
        ElMessage.error(resp.message)
        return
      }
      ElMessage.success(t("global.delete_success"))
      await load()
    }

    const builtinEditing = computed(() => Boolean(editing.value?.is_built_in))

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
              <ElButton size="small" type="primary" onClick={openCreate}>
                <ElIcon>
                  <Plus />
                </ElIcon>
                {t("global.add")}
              </ElButton>
            </div>
          </div>

          {error.value && (
            <ElAlert type="error" showIcon={true} title={error.value} closable={false} />
          )}

          <VexTable data={rows.value} size="small" style={{ width: "100%" }}>
            <VexColumn prop="id" label="ID" width={56} />
            <VexColumn label={t("users.username")} minWidth={130}>
              {{
                default: ({ row }) => {
                  const r = row as unknown as AdminUser
                  return (
                    <span class="ol-manage-inline">
                      <span>{r.username}</span>
                      {r.is_built_in && (
                        <ElTag size="small" type="warning">
                          {t("users.builtin")}
                        </ElTag>
                      )}
                    </span>
                  )
                },
              }}
            </VexColumn>
            <VexColumn label={t("users.role")} width={96}>
              {{
                default: ({ row }) => {
                  const r = row as unknown as AdminUser
                  const opt = ROLE_OPTIONS.find((o) => o.value === r.role)
                  return (
                    <ElTag size="small" type={r.role === UserRole.ADMIN ? "danger" : "info"}>
                      {opt ? t(opt.labelKey) : String(r.role)}
                    </ElTag>
                  )
                },
              }}
            </VexColumn>
            <VexColumn label={t("users.base_path")} minWidth={110}>
              {{
                default: ({ row }) => {
                  const r = row as unknown as AdminUser
                  return <span class="ol-mono">{r.base_path || "/"}</span>
                },
              }}
            </VexColumn>
            <VexColumn label={t("users.permission")} width={120}>
              {{
                default: ({ row }) => {
                  const r = row as unknown as AdminUser
                  const count = bitsToKeys(r.permission).length
                  return (
                    <span>
                      {count > 0 ? count : t("users.none")} / {PERM_KEYS.length}
                    </span>
                  )
                },
              }}
            </VexColumn>
            <VexColumn label={t("users.disabled")} width={92}>
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
            <VexColumn label="2FA" width={66}>
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
            <VexColumn label={t("global.operations")} width={230} align="center">
              {{
                default: ({ row }) => {
                  const r = row as unknown as AdminUser
                  return (
                    <div class="ol-manage-row-ops">
                      <ElButton size="small" text={true} type="primary" onClick={() => openEdit(r)}>
                        <ElIcon>
                          <Edit />
                        </ElIcon>
                        {t("global.edit")}
                      </ElButton>
                      <ElButton size="small" text={true} onClick={() => openPassword(r)}>
                        <ElIcon>
                          <Key />
                        </ElIcon>
                        {t("users.change_password")}
                      </ElButton>
                      {/* 低频且危险的操作收进下拉：4 个按钮并排会被表格宽度裁掉 */}
                      <ElDropdown
                        trigger="click"
                        onCommand={(cmd: string) => {
                          if (cmd === "cancel2fa") void onCancel2Fa(r)
                          else if (cmd === "delete") void onDelete(r)
                        }}
                      >
                        {{
                          default: () => (
                            <ElButton size="small" text={true}>
                              <ElIcon>
                                <MoreFilled />
                              </ElIcon>
                            </ElButton>
                          ),
                          dropdown: () => (
                            <ElDropdownMenu>
                              <ElDropdownItem command="cancel2fa" disabled={!r.otp}>
                                {t("users.cancel_2fa")}
                              </ElDropdownItem>
                              <ElDropdownItem command="delete" disabled={r.is_built_in} divided={true}>
                                {t("global.delete")}
                              </ElDropdownItem>
                            </ElDropdownMenu>
                          ),
                        }}
                      </ElDropdown>
                    </div>
                  )
                },
              }}
            </VexColumn>
          </VexTable>
        </ElCard>

        <ElDialog
          modelValue={dialogVisible.value}
          title={t(editing.value ? "global.edit" : "global.add")}
          width="640px"
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
                        disabled={builtinEditing.value}
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
                        placeholder={
                          editing.value ? t("users.change_password-tips") : undefined
                        }
                        onUpdate:modelValue={(v: string) => (form.password = v)}
                      />
                    ),
                  }}
                </ElFormItem>
                <ElFormItem label={t("users.role")}>
                  {{
                    default: () => (
                      <ElSelect
                        modelValue={form.role}
                        disabled={builtinEditing.value}
                        onUpdate:modelValue={(v: string | number | boolean) =>
                          (form.role = Number(v))
                        }
                      >
                        {ROLE_OPTIONS.map((o) => (
                          <ElOption key={o.value} label={t(o.labelKey)} value={o.value} />
                        ))}
                      </ElSelect>
                    ),
                  }}
                </ElFormItem>
                <ElFormItem label={t("users.base_path")}>
                  {{
                    default: () => (
                      <ElInput
                        modelValue={form.basePath}
                        placeholder="/"
                        onUpdate:modelValue={(v: string) => (form.basePath = v)}
                      />
                    ),
                  }}
                </ElFormItem>
                <ElFormItem label={t("users.permission")}>
                  {{
                    default: () => (
                      <ElCheckboxGroup
                        modelValue={form.perms}
                        onUpdate:modelValue={(v: unknown) =>
                          (form.perms = (v as Permission[]) ?? [])
                        }
                      >
                        <div class="ol-perm-grid">
                          {PERM_KEYS.map((key) => (
                            <ElCheckbox key={key} value={key} label={t(`users.permissions.${key}`)} />
                          ))}
                        </div>
                      </ElCheckboxGroup>
                    ),
                  }}
                </ElFormItem>
                <ElFormItem label={t("users.disabled")}>
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

        <ElDialog
          modelValue={pwdVisible.value}
          title={t("users.change_password")}
          width="460px"
          onUpdate:modelValue={(v: boolean) => (pwdVisible.value = v)}
        >
          {{
            default: () => (
              <ElForm labelWidth="140px">
                <ElFormItem label={t("users.username")}>
                  {{
                    default: () => <span>{pwdTarget.value?.username}</span>,
                  }}
                </ElFormItem>
                <ElFormItem label={t("users.password")} required={true}>
                  {{
                    default: () => (
                      <ElInput
                        type="password"
                        showPassword={true}
                        modelValue={pwd.password}
                        onUpdate:modelValue={(v: string) => (pwd.password = v)}
                      />
                    ),
                  }}
                </ElFormItem>
                <ElFormItem label={t("users.confirm_password")} required={true}>
                  {{
                    default: () => (
                      <ElInput
                        type="password"
                        showPassword={true}
                        modelValue={pwd.confirm}
                        onUpdate:modelValue={(v: string) => (pwd.confirm = v)}
                      />
                    ),
                  }}
                </ElFormItem>
              </ElForm>
            ),
            footer: () => (
              <div class="ol-manage-form-actions">
                <ElButton onClick={() => (pwdVisible.value = false)}>{t("global.cancel")}</ElButton>
                <ElButton type="primary" onClick={onSavePassword}>
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
