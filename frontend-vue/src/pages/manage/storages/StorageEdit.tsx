import { defineComponent, reactive, ref, computed, watch } from "vue"
import { useRoute, useRouter } from "vue-router"
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
  ElOption,
  ElSelect,
  ElSwitch,
} from "element-plus"
import { ArrowLeft } from "@element-plus/icons-vue"
import {
  createStorage,
  getDriverInfo,
  getStorage,
  listDrivers,
  updateStorage,
} from "~/api/admin"
import type {
  DriverInfo,
  DriverInfoItem,
  DriverMap,
  StorageWithDriver,
} from "~/api/types"
import DriverForm from "~/components/DriverForm"
import { t } from "~/i18n"

/**
 * 存储新增 / 编辑（M2 核心）。
 *
 * 关键分工（以 `StorageService` 后端为真源）：
 * - common 列表里命中 `TOP_LEVEL` 的字段（mount_path/order/remark/web_proxy/
 *   webdav_policy/enable_sign/extract_folder/order_by/order_direction）走
 *   `CreateStorageReq` 顶层；其余 common 项 + 全部 additional 项塞进 `addition`
 *   JSON（驱动挂载时反序列化为 ConfigItems）。`disabled` 是独立开关、不在 common。
 * - 编辑时从 `storage.addition` 反序列化回填 additional + 非顶层 common 项。
 */
const TOP_LEVEL = new Set([
  "mount_path",
  "order",
  "remark",
  "web_proxy",
  "webdav_policy",
  "enable_sign",
  "extract_folder",
  "order_by",
  "order_direction",
])
/** 这几个顶层字段在页面上单独渲染（更醒目的布局 + 必填标记），不再进 DriverForm。 */
const EXPLICIT = new Set(["mount_path", "order", "remark"])

function defaultValue(item: DriverInfoItem): unknown {
  const d = item.default
  switch (item.type) {
    case "bool":
      return d === "true"
    case "number":
    case "float":
      return d ? Number(d) : 0
    default:
      return d ?? ""
  }
}

export default defineComponent({
  name: "StorageEditPage",
  setup() {
    const route = useRoute()
    const router = useRouter()
    const editId = computed<number | null>(() => {
      const id = (route.params as Record<string, string>).id
      return id ? Number(id) : null
    })

    const drivers = ref<DriverMap>({})
    const driver = ref<string>("")
    const info = ref<DriverInfo | null>(null)
    const formModel = reactive<Record<string, unknown>>({})
    const disabled = ref(false)
    const loading = ref(false)
    const saving = ref(false)
    const loadError = ref("")

    function applyDefaults(d: DriverInfo) {
      for (const k of Object.keys(formModel)) delete formModel[k]
      for (const item of d.common) formModel[item.name] = defaultValue(item)
      for (const item of d.additional) formModel[item.name] = defaultValue(item)
    }

    async function ensureInfo(name: string): Promise<DriverInfo | null> {
      if (drivers.value[name]) return drivers.value[name]
      const resp = await getDriverInfo(name)
      if (resp.code !== 200) {
        loadError.value = resp.message
        return null
      }
      drivers.value[name] = resp.data
      return resp.data
    }

    watch(driver, async (name) => {
      if (!name) return
      const d = await ensureInfo(name)
      if (d) {
        info.value = d
        applyDefaults(d)
      }
    })

    async function init() {
      loading.value = true
      loadError.value = ""
      try {
        const resp = await listDrivers()
        if (resp.code !== 200) {
          loadError.value = resp.message
          return
        }
        drivers.value = resp.data

        if (editId.value != null) {
          const s = await getStorage(editId.value)
          if (s.code !== 200) {
            loadError.value = s.message
            return
          }
          const st: StorageWithDriver = s.data
          const d = await ensureInfo(st.driver)
          if (!d) return
          driver.value = st.driver
          disabled.value = st.disabled
          info.value = d
          applyDefaults(d)
          let addition: Record<string, unknown> = {}
          try {
            addition = JSON.parse(st.addition || "{}")
          } catch {
            /* 旧数据非法 JSON 时忽略，按默认值呈现 */
          }
          for (const item of [...d.common, ...d.additional]) {
            if (item.name in addition) formModel[item.name] = addition[item.name]
          }
        } else if (Object.keys(resp.data).length) {
          driver.value = Object.keys(resp.data)[0]
        }
      } finally {
        loading.value = false
      }
    }

    function buildAddition(d: DriverInfo): string {
      const obj: Record<string, unknown> = {}
      for (const item of d.common) {
        if (!TOP_LEVEL.has(item.name)) obj[item.name] = formModel[item.name]
      }
      for (const item of d.additional) obj[item.name] = formModel[item.name]
      return JSON.stringify(obj)
    }

    async function onSave() {
      if (!info.value || !driver.value) return
      const req = {
        id: editId.value ?? undefined,
        mount_path: String(formModel["mount_path"] ?? ""),
        driver: driver.value,
        order: Number(formModel["order"] ?? 0),
        remark: (formModel["remark"] as string) || null,
        disabled: disabled.value,
        web_proxy: Boolean(formModel["web_proxy"]),
        webdav_policy: (formModel["webdav_policy"] as string) || null,
        enable_sign: Boolean(formModel["enable_sign"]),
        extract_folder: (formModel["extract_folder"] as string) || null,
        order_by: (formModel["order_by"] as string) || null,
        order_direction: (formModel["order_direction"] as string) || null,
        addition: buildAddition(info.value),
      }
      saving.value = true
      const resp = editId.value != null ? await updateStorage(req) : await createStorage(req)
      saving.value = false
      if (resp.code !== 200) {
        ElMessage.error(resp.message)
        return
      }
      ElMessage.success(t(editId.value != null ? "global.update_success" : "global.save_success"))
      await router.push("/@manage/storages")
    }

    const driverOptions = computed(() => Object.keys(drivers.value).sort())
    const driverLabel = (name: string) => {
      const mapped = t(`drivers.drivers.${name}`)
      return mapped !== `drivers.drivers.${name}` ? mapped : name
    }
    const commonForForm = computed(() =>
      info.value ? info.value.common.filter((i) => !EXPLICIT.has(i.name)) : [],
    )

    init()

    return () => {
      if (loading.value) {
        return <ElAlert type="info" showIcon={true} title="Loading…" />
      }
      if (loadError.value) {
        return <ElAlert type="error" showIcon={true} title={loadError.value} closable={false} />
      }
      return (
        <div class="ol-manage-page">
          <ElCard shadow="never">
            <div class="ol-manage-toolbar">
              <ElButton text={true} onClick={() => router.push("/@manage/storages")}>
                <ElIcon>
                  <ArrowLeft />
                </ElIcon>
                {t("global.back")}
              </ElButton>
              <h2 class="ol-manage-h2">{t(editId.value != null ? "global.edit" : "global.add")}</h2>
            </div>

            {info.value?.config.alert && (
              <ElAlert
                type="warning"
                showIcon={true}
                title={info.value.config.alert.split("|")[0]}
                closable={false}
              />
            )}

            <ElForm labelWidth="220px" labelPosition="right">
              <ElFormItem label={t("storages.common.driver")}>
                {{
                  default: () => (
                    <ElSelect
                      modelValue={driver.value}
                      disabled={editId.value != null}
                      onUpdate:modelValue={(v: string | number | boolean) => (driver.value = v as string)}
                    >
                      {driverOptions.value.map((name) => (
                        <ElOption key={name} label={driverLabel(name)} value={name} />
                      ))}
                    </ElSelect>
                  ),
                }}
              </ElFormItem>
              <ElFormItem label={t("storages.common.mount_path")} required={true}>
                {{
                  default: () => (
                    <ElInput
                      modelValue={String(formModel["mount_path"] ?? "")}
                      onUpdate:modelValue={(v: string) => (formModel["mount_path"] = v)}
                    />
                  ),
                }}
              </ElFormItem>
              <ElFormItem label={t("storages.common.order")}>
                {{
                  default: () => (
                    <ElInputNumber
                      modelValue={Number(formModel["order"] ?? 0)}
                      onUpdate:modelValue={(v: number | undefined) => (formModel["order"] = v ?? 0)}
                    />
                  ),
                }}
              </ElFormItem>
              <ElFormItem label={t("storages.common.remark")}>
                {{
                  default: () => (
                    <ElInput
                      modelValue={(formModel["remark"] as string) ?? ""}
                      onUpdate:modelValue={(v: string) => (formModel["remark"] = v)}
                    />
                  ),
                }}
              </ElFormItem>
              <ElFormItem label="Disabled (storage off)">
                {{
                  default: () => (
                    <ElSwitch
                      modelValue={disabled.value}
                      onUpdate:modelValue={(v: boolean | string | number) => (disabled.value = Boolean(v))}
                    />
                  ),
                }}
              </ElFormItem>
            </ElForm>

            {info.value && (
              <>
                <h3 class="ol-manage-section">{t("storages.common.mount_path")} · Common</h3>
                <DriverForm
                  items={commonForForm.value}
                  model={formModel}
                  onChange={(name: string, value: unknown) => (formModel[name] = value)}
                />
                <h3 class="ol-manage-section">Additional</h3>
                <DriverForm
                  items={info.value.additional}
                  model={formModel}
                  onChange={(name: string, value: unknown) => (formModel[name] = value)}
                />
              </>
            )}

            <div class="ol-manage-form-actions">
              <ElButton onClick={() => router.push("/@manage/storages")}>
                {t("global.cancel")}
              </ElButton>
              <ElButton type="primary" loading={saving.value} onClick={onSave}>
                {t(editId.value != null ? "global.update" : "global.save")}
              </ElButton>
            </div>
          </ElCard>
        </div>
      )
    }
  },
})
