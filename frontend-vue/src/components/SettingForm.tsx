import { defineComponent } from "vue"
import { ElInput, ElInputNumber, ElOption, ElSelect, ElSwitch, ElTag } from "element-plus"
import { SettingFlag, type SettingItem } from "~/api/types"
import { t } from "~/i18n"

/**
 * 设置项渲染器（M2 · settings）。
 *
 * 与 `DriverForm` 同构，但数据源是后端 `SettingItem`：值一律是**字符串**，
 * 控件只负责「字符串 ⇄ 控件值」的转换，不要在组件里把 number 存成 number，
 * 否则保存时后端会把 "30" 写成 "30.0" 之类不一致的形态。
 *
 * 三个必须遵守的约定：
 * 1. `flag=READONLY(2)` / `DEPRECATED(3)` 只读；`PRIVATE(1)` 只表示不暴露给游客，
 *    不影响管理员编辑，不要一起禁掉。
 * 2. `options` 是逗号分隔字符串（例如 `zh-CN,zh-TW,en-US`），不是数组。
 * 3. 文案优先取 `settings.<key>`，帮助取 `settings.<key>-tips`；两者都没有时
 *    回落到后端 `help` 字段，最后才显示原始 key（漏翻时立刻能看出来）。
 */
export interface SettingFormProps {
  items: SettingItem[]
  model: Record<string, string>
  onChange: (key: string, value: string) => void
  disabled?: boolean
}

function labelOf(key: string): string {
  const path = `settings.${key}`
  const text = t(path)
  return text === path ? key : text
}

function helpOf(item: SettingItem): string {
  const path = `settings.${item.key}-tips`
  const text = t(path)
  if (text !== path) return text
  return item.help ?? ""
}

const FLAG_LABEL: Record<number, string> = {
  [SettingFlag.READONLY]: "settings.flag.readonly",
  [SettingFlag.DEPRECATED]: "settings.flag.deprecated",
}

export default defineComponent({
  name: "SettingForm",
  props: {
    items: { type: Array as () => SettingItem[], required: true },
    model: { type: Object as () => Record<string, string>, required: true },
    onChange: { type: Function as unknown as () => (key: string, value: string) => void, required: true },
    disabled: { type: Boolean, default: false },
  },
  setup(props) {
    function control(item: SettingItem) {
      const key = item.key
      const value = props.model[key] ?? ""
      const locked =
        props.disabled ||
        item.flag === SettingFlag.READONLY ||
        item.flag === SettingFlag.DEPRECATED
      const emit = (next: string) => props.onChange(key, next)

      switch (item.type) {
        case "bool":
          return (
            <ElSwitch
              modelValue={value === "true"}
              disabled={locked}
              onUpdate:modelValue={(v: string | number | boolean) => emit(v ? "true" : "false")}
            />
          )
        case "number":
        case "float":
          return (
            <ElInputNumber
              modelValue={value === "" ? undefined : Number(value)}
              disabled={locked}
              step={item.type === "float" ? 0.1 : 1}
              onUpdate:modelValue={(v: number | undefined) => emit(v == null ? "" : String(v))}
            />
          )
        case "select":
          return (
            <ElSelect
              modelValue={value}
              disabled={locked}
              style={{ minWidth: "260px" }}
              onUpdate:modelValue={(v: string | number | boolean | undefined) =>
                emit(v == null ? "" : String(v))
              }
            >
              {(item.options ?? "")
                .split(",")
                .map((o) => o.trim())
                .filter(Boolean)
                .map((o) => (
                  <ElOption key={o} label={o} value={o} />
                ))}
            </ElSelect>
          )
        case "text":
          return (
            <ElInput
              type="textarea"
              rows={4}
              modelValue={value}
              disabled={locked}
              onUpdate:modelValue={(v: string) => emit(v)}
            />
          )
        case "multipath":
          return (
            <ElInput
              type="textarea"
              rows={4}
              modelValue={value}
              disabled={locked}
              placeholder={t("settings.multipath_placeholder")}
              onUpdate:modelValue={(v: string) => emit(v)}
            />
          )
        default:
          return (
            <ElInput
              modelValue={value}
              disabled={locked}
              onUpdate:modelValue={(v: string) => emit(v)}
            />
          )
      }
    }

    return () => (
      <div class="ol-setting-list">
        {props.items.map((item) => {
          const help = helpOf(item)
          const flagLabel = FLAG_LABEL[item.flag]
          return (
            <div class="ol-setting-row" key={item.key} data-key={item.key}>
              <div class="ol-setting-label">
                <span>{labelOf(item.key)}</span>
                {flagLabel && (
                  <ElTag size="small" type={item.flag === SettingFlag.DEPRECATED ? "info" : "warning"}>
                    {t(flagLabel)}
                  </ElTag>
                )}
                <span class="ol-mono ol-setting-key">{item.key}</span>
              </div>
              <div class="ol-setting-control">
                {control(item)}
                {help && <div class="ol-field-help">{help}</div>}
              </div>
            </div>
          )
        })}
      </div>
    )
  },
})
