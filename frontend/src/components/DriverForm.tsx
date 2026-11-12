import { defineComponent, type PropType } from "vue"
import {
  ElForm,
  ElFormItem,
  ElInput,
  ElInputNumber,
  ElOption,
  ElSelect,
  ElSwitch,
  ElTooltip,
  ElIcon,
} from "element-plus"
import { QuestionFilled } from "@element-plus/icons-vue"
import type { DriverInfoItem } from "~/api/types"
import { t } from "~/i18n"

/**
 * 驱动配置动态表单渲染器（M2 最大风险点）。
 *
 * 后端 `DriverInfoItem` 只有 6 种 `type` 字符串（`StorageService.MapTypeName`）：
 * `string` / `text` / `number` / `float` / `bool` / `select`。本组件按 `type`
 * 渲染对应 Element Plus 控件，不持有任何状态——值统一存在父级的 `model` 上，
 * 变更通过 `onChange(name, value)` 回传（避免直接改 props 触发 readonly 警告）。
 *
 * 标签 / 帮助 / 选项文案都走 i18n：字段标签取 `drivers.<driver>.<name>`，
 * 帮助取 `drivers.<driver>.<name>-tips`（两者都回退后端原始值）；`select` 的
 * `options` 是逗号分隔字符串，选项标签优先取 `drivers.<driver>.<name>s.<opt>`，
 * 再取 `storages.common.<name>s.<opt>`（如 webdav_policys / order_bys），
 * 取不到则回退原始值。
 */

/** 取翻译；缺键时 t() 会原样返回 key，因此与 key 相同即视为未命中。 */
function lookup(key: string, fallback: string): string {
  if (!key) return fallback
  const mapped = t(key)
  return mapped === key ? fallback : mapped
}

/** 词条沿用上游驱动名，与后端 DriverName 存在少量拼写差异。 */
export const DRIVER_ALIAS: Record<string, string> = {
  WebDAV: "WebDav",
  "115": "115 Cloud",
}

export default defineComponent({
  name: "DriverForm",
  props: {
    items: { type: Array as PropType<DriverInfoItem[]>, required: true },
    /** 驱动名（= 后端 driver 字段），用于在 drivers.<driver> 下查字段文案。 */
    driver: { type: String, default: "" },
    /** 值容器：key = item.name。只读（渲染时取值），写入走 onChange。 */
    model: { type: Object as PropType<Record<string, unknown>>, required: true },
    disabled: { type: Boolean, default: false },
    onChange: {
      type: Function as PropType<(name: string, value: unknown) => void>,
      required: true,
    },
  },
  setup(props) {
    const prefix = () => {
      if (!props.driver) return ""
      const name = DRIVER_ALIAS[props.driver] ?? props.driver
      return `drivers.${name}`
    }

    function fieldLabel(item: DriverInfoItem): string {
      return lookup(prefix() && `${prefix()}.${item.name}`, item.name)
    }

    function fieldHelp(item: DriverInfoItem): string {
      return lookup(prefix() && `${prefix()}.${item.name}-tips`, item.help ?? "")
    }

    function optionLabel(item: DriverInfoItem, opt: string): string {
      const driverLabel = lookup(prefix() && `${prefix()}.${item.name}s.${opt}`, "")
      if (driverLabel) return driverLabel
      return lookup(`storages.common.${item.name}s.${opt}`, opt)
    }

    function renderControl(item: DriverInfoItem) {
      const value = props.model[item.name]
      const set = (v: unknown) => props.onChange(item.name, v)

      switch (item.type) {
        case "bool":
          return (
            <ElSwitch
              modelValue={Boolean(value)}
              disabled={props.disabled}
              onUpdate:modelValue={(v: boolean | string | number) => set(Boolean(v))}
            />
          )
        case "number":
        case "float":
          return (
            <ElInputNumber
              modelValue={typeof value === "number" ? (value as number) : Number(value ?? 0)}
              disabled={props.disabled}
              onUpdate:modelValue={(v: number | undefined) => set(v ?? 0)}
            />
          )
        case "select": {
          const opts = (item.options || "")
            .split(",")
            .map((s) => s.trim())
            .filter(Boolean)
          return (
            <ElSelect
              modelValue={(value as string) ?? ""}
              disabled={props.disabled}
              onUpdate:modelValue={(v: string | number | boolean) => set(v as string)}
            >
              {opts.map((opt) => (
                <ElOption key={opt} label={optionLabel(item, opt)} value={opt} />
              ))}
            </ElSelect>
          )
        }
        case "text":
          return (
            <ElInput
              modelValue={(value as string) ?? ""}
              disabled={props.disabled}
              type="textarea"
              autosize={{ minRows: 2, maxRows: 6 }}
              onUpdate:modelValue={(v: string) => set(v)}
            />
          )
        default:
          return (
            <ElInput
              modelValue={(value as string) ?? ""}
              disabled={props.disabled}
              onUpdate:modelValue={(v: string) => set(v)}
            />
          )
      }
    }

    return () => (
      <ElForm labelWidth="220px" labelPosition="right">
        {props.items.map((item) => (
          <ElFormItem
            key={item.name}
            label={`${fieldLabel(item)}${item.required ? " *" : ""}`}
          >
            {{
              default: () => (
                <div class="ol-driver-field">
                  {renderControl(item)}
                  {fieldHelp(item) && <div class="ol-field-help">{fieldHelp(item)}</div>}
                </div>
              ),
            }}
          </ElFormItem>
        ))}
      </ElForm>
    )
  },
})
