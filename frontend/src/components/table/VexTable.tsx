import { computed, defineComponent, ref, watch } from "vue"
import type { PropType, SlotsType } from "vue"
import { VxeTable } from "vxe-table"

/** vxe 表格实例上用到的勾选相关方法。 */
interface VexTableInstance {
  toggleAllCheckboxRow: () => Promise<unknown>
  clearCheckboxRow: () => Promise<unknown>
  setCheckboxRow: (rows: unknown[], checked: boolean) => Promise<unknown>
  getCheckboxRecords: () => unknown[]
}

/**
 * 表格：对外保持 Element Plus `ElTable` 的属性 / 事件 / 方法语义，
 * 内部由 vxe-table 渲染（虚拟滚动，大数据量更稳）。
 *
 * 语义差异都在这里抹平：
 * - `selection-change`：由 vxe 的 `checkbox-change` / `checkbox-all` 合成一次事件；
 * - `row-contextmenu`：vxe 只在启用 `menu-config` 时派发 `cell-menu`，
 *   这里传一个空 options 的 config —— 事件照发，但不弹 vxe 自带的右键菜单；
 * - `toggleAllSelection` / `clearSelection` / `toggleRowSelection`：映射到 vxe 的
 *   `toggleAllCheckboxRow` / `clearCheckboxRow` / `setCheckboxRow`。
 */
export const VexTable = defineComponent({
  name: "VexTable",
  props: {
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    data: { type: Array as PropType<any[]>, default: () => [] },
    rowKey: { type: String, default: undefined },
    size: { type: String as PropType<"large" | "default" | "small">, default: "default" },
    border: { type: Boolean, default: false },
    stripe: { type: Boolean, default: false },
    emptyText: { type: String, default: undefined },
    height: { type: [Number, String], default: undefined },
    maxHeight: { type: [Number, String], default: undefined },
    loading: { type: Boolean, default: false },
    /** 全局默认：超长文本省略 + tooltip，避免窄屏把行撑开 */
    showOverflow: { type: [Boolean, String], default: "tooltip" },
    highlightCurrentRow: { type: Boolean, default: false },
  },
  emits: {
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    "selection-change": (_rows: any[]) => true,
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    "row-contextmenu": (_row: any, _column: any, _event: MouseEvent) => true,
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    "row-click": (_row: any, _column: any, _event: MouseEvent) => true,
    "sort-change": (_payload: unknown) => true,
  },
  slots: Object as SlotsType<{ default?: () => unknown }>,
  setup(props, { emit, expose, slots }) {
    const tableRef = ref<VexTableInstance | null>(null)

    /** vxe 只有开启 menu-config 才派发右键事件；空 options 表示「不弹自带菜单」。 */
    const menuConfig = { body: { options: [] } }

    const vxeProps = computed(
      () =>
        ({
          data: props.data,
          size: props.size === "small" ? "mini" : props.size === "large" ? "medium" : "small",
          border: props.border,
          stripe: props.stripe,
          round: false,
          emptyText: props.emptyText ?? "暂无数据",
          height: props.height,
          maxHeight: props.maxHeight,
          loading: props.loading,
          showOverflow: props.showOverflow,
          highlightCurrentRow: props.highlightCurrentRow,
          rowConfig: { keyField: props.rowKey, isHover: true },
          menuConfig,
          // eslint-disable-next-line @typescript-eslint/no-explicit-any
        }) as any,
    )

    function selectedRows(): unknown[] {
      return tableRef.value?.getCheckboxRecords?.() ?? []
    }

    function emitSelection() {
      emit("selection-change", selectedRows())
    }

    /**
     * ElTable 的 toggleRowSelection 常被 forEach 逐行调用（恢复跨视图勾选），
     * 这里合并到同一微任务里一次性提交，避免 N 次重绘。
     */
    let pendingOn: unknown[] = []
    let pendingOff: unknown[] = []
    let flushScheduled = false

    function flushSelection() {
      flushScheduled = false
      const on = pendingOn
      const off = pendingOff
      pendingOn = []
      pendingOff = []
      const table = tableRef.value
      if (!table) return
      // vxe 的 setCheckboxRow 是「静默」的：不派发 checkbox-change，
      // 所以这里手动补一次 selection-change，保持 ElTable 的语义。
      const jobs: Promise<unknown>[] = []
      if (on.length) jobs.push(table.setCheckboxRow(on, true))
      if (off.length) jobs.push(table.setCheckboxRow(off, false))
      if (jobs.length) void Promise.all(jobs).then(emitSelection)
    }

    function queueRowSelection(row: unknown, checked: boolean) {
      if (checked) pendingOn.push(row)
      else pendingOff.push(row)
      if (flushScheduled) return
      flushScheduled = true
      void Promise.resolve().then(flushSelection)
    }

    // 数据整体换掉（切目录 / 翻页）时清空旧勾选：vxe 的勾选状态按 rowid 记忆，
    // 不清会出现「新目录里同名文件被误勾上」。ElTable 也是同样的行为。
    watch(
      () => props.data,
      () => {
        const table = tableRef.value
        if (!table) return
        if (!selectedRows().length) return
        void table.clearCheckboxRow()
        emitSelection()
      },
    )

    expose({
      // 这三个方法在 vxe 里都是「静默」的，统一补发 selection-change
      toggleAllSelection: () => tableRef.value?.toggleAllCheckboxRow().then(emitSelection),
      clearSelection: () => tableRef.value?.clearCheckboxRow().then(emitSelection),
      toggleRowSelection: (row: unknown, checked = true) => queueRowSelection(row, checked),
      getSelectionRows: () => selectedRows(),
    })

    return () => (
      <VxeTable
        ref={tableRef}
        {...vxeProps.value}
        onCheckboxChange={(params: { records?: unknown[] }) =>
          emit("selection-change", params?.records ?? selectedRows())
        }
        onCheckboxAll={(params: { records?: unknown[] }) =>
          emit("selection-change", params?.records ?? selectedRows())
        }
        onCellMenu={(params: { row: unknown; column: unknown; $event: Event }) =>
          emit("row-contextmenu", params.row, params.column, params.$event as MouseEvent)
        }
        onCellClick={(params: { row: unknown; column: unknown; $event: Event }) =>
          emit("row-click", params.row, params.column, params.$event as MouseEvent)
        }
        onSortChange={(params: unknown) => emit("sort-change", params)}
        v-slots={slots}
      />
    )
  },
})
