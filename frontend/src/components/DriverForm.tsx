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
 * `select` 的 `options` 是逗号分隔字符串；选项标签优先取
 * `storages.common.<name>s.<opt>` 翻译（如 webdav_policys / order_bys），
 * 取不到则回退原始值。
 */

function optionLabel(item: DriverInfoItem, opt: string): string {
  const mapped = t(`storages.common.${item.name}s.${opt}`)
  if (mapped !== `storages.common.${item.name}s.${opt}`) return mapped
  return opt
}

export default defineComponent({
  name: "DriverForm",
  props: {
    items: { type: Array as PropType<DriverInfoItem[]>, required: true },
    /** 值容器：key = item.name。只读（渲染时取值），写入走 onChange。 */
    model: { type: Object as PropType<Record<string, unknown>>, required: true },
    disabled: { type: Boolean, default: false },
    onChange: {
      type: Function as PropType<(name: string, value: unknown) => void>,
      required: true,
    },
  },
  setup(props) {
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
            label={`${item.name}${item.required ? " *" : ""}`}
          >
            {{
              default: () => (
                <div class="ol-driver-field">
                  {renderControl(item)}
                  {item.help && <div class="ol-field-help">{item.help}</div>}
                </div>
              ),
            }}
          </ElFormItem>
        ))}
      </ElForm>
    )
  },
})
