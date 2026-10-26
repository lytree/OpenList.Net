import { defineComponent, computed, reactive, ref, onMounted } from "vue"
import { onBeforeRouteLeave } from "vue-router"
import {
  ElAlert,
  ElButton,
  ElCard,
  ElIcon,
  ElInput,
  ElMessage,
  ElMessageBox,
  ElTabPane,
  ElTabs,
} from "element-plus"
import { Refresh, RefreshLeft, Select } from "@element-plus/icons-vue"
import { listSettings, resetSettingsGroup, resetToken, saveSettings } from "~/api/admin"
import { SettingGroups, type SettingItem } from "~/api/types"
import SettingForm from "~/components/SettingForm"
import { useSettingsStore } from "~/stores/settings"
import { t } from "~/i18n"

/**
 * 站点设置（M2 · 最大的一块）。
 *
 * 数据完全由后端驱动：`GET /admin/setting/list` 一次返回全部 120+ 项，前端只按
 * `group` 分组成选项卡，再按 `type` 渲染控件 —— **不要**在前端硬编码任何键名或控件，
 * 后端新增设置项时这里应当零改动。
 *
 * 几个行为约定：
 * - **只提交改动项**：`save` 是合并写，把整组发回去会把后端新增但前端未加载的项覆盖成旧值。
 * - `group=0`（Single）是驱动专用临时目录，语义上不属于站点设置，直接隐藏。
 * - 保存成功后强制刷新公开设置 store，否则顶栏站点名/logo 要等下次刷新才变。
 * - 离开页面前有未保存改动时提示（避免误关）。
 */
export default defineComponent({
  name: "SettingsPage",
  setup() {
    const settingsStore = useSettingsStore()
    const items = ref<SettingItem[]>([])
    const model = reactive<Record<string, string>>({})
    const baseline = reactive<Record<string, string>>({})
    const activeGroup = ref(1)
    const keyword = ref("")
    const loading = ref(false)
    const saving = ref(false)
    const error = ref("")

    /** 有设置项的分组（隐藏 group 0 = 驱动专用临时目录）。 */
    const groups = computed(() =>
      SettingGroups.map((g) => ({
        ...g,
        count: items.value.filter((i) => i.group === g.group).length,
      })).filter((g) => g.count > 0),
    )

    const groupItems = computed(() => {
      const kw = keyword.value.trim().toLowerCase()
      return items.value.filter((item) => {
        if (item.group !== activeGroup.value) return false
        if (!kw) return true
        const label = t(`settings.${item.key}`).toLowerCase()
        return item.key.toLowerCase().includes(kw) || label.includes(kw)
      })
    })

    const dirtyKeys = computed(() =>
      Object.keys(model).filter((key) => model[key] !== baseline[key]),
    )
    const dirtyCount = computed(() => dirtyKeys.value.length)

    function fill(list: SettingItem[]) {
      items.value = list
      for (const k of Object.keys(model)) delete model[k]
      for (const k of Object.keys(baseline)) delete baseline[k]
      for (const item of list) {
        model[item.key] = item.value ?? ""
        baseline[item.key] = item.value ?? ""
      }
    }

    async function load() {
      loading.value = true
      error.value = ""
      const resp = await listSettings()
      loading.value = false
      if (resp.code !== 200) {
        error.value = resp.message
        return
      }
      fill(resp.data ?? [])
      if (!groups.value.some((g) => g.group === activeGroup.value)) {
        activeGroup.value = groups.value[0]?.group ?? 1
      }
    }
    onMounted(load)

    function onChange(key: string, value: string) {
      model[key] = value
    }

    async function onSave() {
      const changed = items.value
        .filter((item) => dirtyKeys.value.includes(item.key))
        .map((item) => ({ ...item, value: model[item.key] ?? "" }))
      if (!changed.length) return
      saving.value = true
      const resp = await saveSettings(changed)
      saving.value = false
      if (resp.code !== 200) {
        ElMessage.error(resp.message)
        return
      }
      for (const item of changed) baseline[item.key] = item.value
      ElMessage.success(t("global.save_success"))
      // 站点名 / logo / 主题色等都在公开设置里，必须强制重拉
      await settingsStore.load(true)
    }

    async function onResetGroup() {
      const group = activeGroup.value
      const label = groups.value.find((g) => g.group === group)?.labelKey
      try {
        await ElMessageBox.confirm(
          t("settings.reset_group_confirm", { group: label ? t(label) : String(group) }),
          t("settings.reset_group"),
          { type: "warning" },
        )
      } catch {
        return
      }
      const resp = await resetSettingsGroup(group)
      if (resp.code !== 200) {
        ElMessage.error(resp.message)
        return
      }
      ElMessage.success(t("global.update_success"))
      await load()
      await settingsStore.load(true)
    }

    async function onResetToken() {
      try {
        await ElMessageBox.confirm(t("settings.reset_token_confirm"), t("settings.reset_token"), {
          type: "warning",
        })
      } catch {
        return
      }
      const resp = await resetToken()
      if (resp.code !== 200) {
        ElMessage.error(resp.message)
        return
      }
      ElMessage.success(t("settings.reset_token_success"))
    }

    function onDiscard() {
      for (const key of Object.keys(model)) model[key] = baseline[key] ?? ""
    }

    // 未保存改动时离开提示：设置在独立页，误点导航等于白改一轮
    onBeforeRouteLeave(() => {
      if (!dirtyCount.value) return true
      return window.confirm(t("global.unsaved_changes_confirm"))
    })

    return () => (
      <div class="ol-manage-page">
        <ElCard shadow="never">
          <div class="ol-manage-toolbar">
            <h2 class="ol-manage-h2">{t("manage.sidemenu.settings")}</h2>
            <div class="ol-manage-actions">
              <ElInput
                size="small"
                clearable={true}
                style={{ width: "200px" }}
                placeholder={t("settings.search")}
                modelValue={keyword.value}
                onUpdate:modelValue={(v: string) => (keyword.value = v)}
              />
              <ElButton size="small" loading={loading.value} onClick={load}>
                <ElIcon>
                  <Refresh />
                </ElIcon>
                {t("global.refresh")}
              </ElButton>
              <ElButton size="small" disabled={!dirtyCount.value} onClick={onDiscard}>
                <ElIcon>
                  <RefreshLeft />
                </ElIcon>
                {t("settings.discard")}
              </ElButton>
              <ElButton
                size="small"
                type="primary"
                loading={saving.value}
                disabled={!dirtyCount.value}
                onClick={onSave}
              >
                <ElIcon>
                  <Select />
                </ElIcon>
                {t("settings.save_changes")}
                {dirtyCount.value ? ` (${dirtyCount.value})` : ""}
              </ElButton>
            </div>
          </div>

          {error.value && (
            <ElAlert type="error" showIcon={true} title={error.value} closable={false} />
          )}

          <ElTabs
            modelValue={activeGroup.value}
            class="ol-settings-tabs"
            onUpdate:modelValue={(v: string | number) => (activeGroup.value = Number(v))}
          >
            {groups.value.map((g) => (
              <ElTabPane key={g.group} name={g.group}>
                {{
                  // 12 个分组 + 计数会撑爆标签栏（右侧出现滚动箭头），只留分组名
                  label: () => <span>{t(g.labelKey)}</span>,
                }}
              </ElTabPane>
            ))}
          </ElTabs>

          {groupItems.value.length ? (
            <SettingForm items={groupItems.value} model={model} onChange={onChange} />
          ) : (
            <ElAlert
              type="info"
              showIcon={true}
              closable={false}
              title={keyword.value ? t("settings.no_match") : t("settings.empty_group")}
            />
          )}

          <div class="ol-manage-form-actions ol-settings-footer">
            <ElButton size="small" onClick={onResetGroup}>
              {t("settings.reset_group")}
            </ElButton>
            <ElButton size="small" type="danger" plain={true} onClick={onResetToken}>
              {t("settings.reset_token")}
            </ElButton>
          </div>
        </ElCard>
      </div>
    )
  },
})
