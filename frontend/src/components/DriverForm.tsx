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
 * 标签 / 帮助 / 选项文案都走 i18n，查找顺序是**驱动优先、通用兜底**：
 *
 * | 位置 | 标签 | 帮助 |
 * |---|---|---|
 * | 先 | `drivers.<driver>.<name>` | `drivers.<driver>.<name>-tips` |
 * | 再 | `storages.common.<name>` | `storages.common.<name>-tips` |
 * | 最后 | 后端给的原始值（通常是英文字段名） | 后端 `help` |
 *
 * 那个「再」是必须的：通用配置段（挂载路径 / 缓存 / 代理 / WebDAV 策略 / 排序 …）由
 * 后端 `StorageService.BuildCommonItems` 统一拼出来，**不属于任何一个驱动**，所以
 * `drivers.<driver>.*` 里根本没有它们 —— 只查驱动那一层，界面上就是一串
 * `cache_expiration`、`disable_proxy_sign` 这样的原始字段名（中文词条其实早就写在
 * `storages.common` 里，只是没人去找）。
 *
 * `select` 的 `options` 是逗号分隔字符串，选项标签同样是驱动优先、`storages.common.<name>s.<opt>`
 * 兜底（如 webdav_policys / order_bys），取不到则回退原始值。
 */

/** 取翻译；缺键时 t() 会原样返回 key，因此与 key 相同即视为未命中。 */
function lookup(key: string, fallback: string): string {
  if (!key) return fallback
  const mapped = t(key)
  return mapped === key ? fallback : mapped
}

/**
 * 取翻译并**区分「没词条」与「词条是空串」**。
 *
 * 空串是刻意保留的信号：`username` / `password` 这类字段后端的 help 只有 "Username"，
 * 中文里再显示一遍「用户名」纯属噪音，于是词条写成 `""` 表示「这一行不要帮助」。
 * 用 `||` 链会把空串当成未命中再退回英文 help，所以这里必须显式判 found。
 */
function translate(key: string): { found: boolean; text: string } {
  if (!key) return { found: false, text: "" }
  const mapped = t(key)
  return mapped === key ? { found: false, text: "" } : { found: true, text: mapped }
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
      const driver = translate(prefix() && `${prefix()}.${item.name}`)
      if (driver.found) return driver.text
      const common = translate(`storages.common.${item.name}`)
      return common.found ? common.text : item.name
    }

    function fieldHelp(item: DriverInfoItem): string {
      const driver = translate(prefix() && `${prefix()}.${item.name}-tips`)
      if (driver.found) return driver.text
      const common = translate(`storages.common.${item.name}-tips`)
      return common.found ? common.text : (item.help ?? "")
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
