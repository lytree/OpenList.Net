import { computed, defineComponent } from "vue"
import type { PropType, SlotsType } from "vue"
import { VxeColumn } from "vxe-table"

/** vxe 单元格插槽参数（只声明业务用到的字段，其余透传）。 */
interface VexCellSlotParams {
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  row: any
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  column: any
  rowIndex: number
  $rowIndex: number
  [key: string]: unknown
}

/**
 * 列组件：保留 Element Plus `ElTableColumn` 的属性语义（label/prop/minWidth/
 * showOverflowTooltip/type="selection"），内部翻译成 vxe-table 的列属性。
 *
 * 之所以包一层而不是直接写 `VxeColumn`，是为了让业务页面不用关心 vxe 的
 * `title/field/showOverflow/type=checkbox` 这些命名差异，替换点只落在组件名上。
 */
export const VexColumn = defineComponent({
  name: "VexColumn",
  props: {
    /** 表头文案（ElTable 的 label） */
    label: { type: String, default: undefined },
    /** 取值字段（ElTable 的 prop） */
    prop: { type: String, default: undefined },
    width: { type: [Number, String], default: undefined },
    minWidth: { type: [Number, String], default: undefined },
    maxWidth: { type: [Number, String], default: undefined },
    align: { type: String as PropType<"left" | "center" | "right">, default: undefined },
    headerAlign: { type: String as PropType<"left" | "center" | "right">, default: undefined },
    fixed: { type: [String, Boolean], default: undefined },
    /** selection / index / expand（ElTable 语义） */
    type: { type: String, default: undefined },
    sortable: { type: [Boolean, String], default: undefined },
    /** 单元格超长省略并显示 tooltip（ElTable 语义） */
    showOverflowTooltip: { type: [Boolean, Object], default: undefined },
    className: { type: String, default: undefined },
  },
  slots: Object as SlotsType<{
    default?: (params: VexCellSlotParams) => unknown
    header?: (params: VexCellSlotParams) => unknown
  }>,
  setup(props, { slots }) {
    // 集中翻译成 vxe 的列属性；这里刻意放宽类型，避免被 vxe 的泛型列定义束缚
    const vxeProps = computed(
      () =>
        ({
          type: toVxeType(props.type),
          field: props.prop,
          title: props.label,
          width: props.width,
          minWidth: props.minWidth,
          maxWidth: props.maxWidth,
          align: props.align,
          headerAlign: props.headerAlign,
          fixed: props.fixed,
          sortable: props.sortable,
          className: props.className,
          showOverflow: props.showOverflowTooltip ? "tooltip" : undefined,
          // eslint-disable-next-line @typescript-eslint/no-explicit-any
        }) as any,
    )

    return () => <VxeColumn {...vxeProps.value} v-slots={slots} />
  },
})

/** ElTable 的特殊列类型 → vxe 的列类型。 */
function toVxeType(type?: string) {
  if (!type) return undefined
  if (type === "selection") return "checkbox"
  if (type === "index") return "seq"
  return type
}
