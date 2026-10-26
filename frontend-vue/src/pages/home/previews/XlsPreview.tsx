import { computed, defineComponent, onMounted, ref, type PropType } from "vue"
import { ElAlert, ElOption, ElSelect, ElSkeleton } from "element-plus"
import { t } from "~/i18n"
import type { PreviewChildProps } from "./types"

/** 单表最多渲染的行/列：再多就只是把浏览器拖死，用户也不会在预览里逐格看。 */
const MAX_ROWS = 500
const MAX_COLS = 40
/** 超过该体积直接放弃解析（xlsx 解析要整份读进内存）。 */
const SIZE_LIMIT = 30 * 1024 * 1024

interface Sheet {
  name: string
  rows: string[][]
  truncatedRows: boolean
  truncatedCols: boolean
}

/**
 * Excel（`.xls` / `.xlsx`）预览。
 *
 * 用 SheetJS 解析后自己画表格：它的 `sheet_to_json(header:1)` 给出二维数组，
 * 渲染成 `<table>` 比引一套表格组件轻得多，也便于按行列截断。
 * 只读不写 —— 编辑表格是另一个量级的功能（要保留样式/公式/合并单元格）。
 */
export default defineComponent({
  name: "XlsPreview",
  props: {
    obj: { type: Object as PropType<PreviewChildProps["obj"]>, required: true },
    src: { type: String, required: true },
  },
  setup(props) {
    const loading = ref(true)
    const error = ref("")
    const sheets = ref<Sheet[]>([])
    const active = ref(0)
    const tooLarge = computed(() => props.obj.size > SIZE_LIMIT)
    const current = computed(() => sheets.value[active.value])

    onMounted(async () => {
      if (tooLarge.value) {
        loading.value = false
        return
      }
      try {
        const XLSX = await import("xlsx")
        const resp = await fetch(props.src)
        if (!resp.ok) throw new Error(`${resp.status} ${resp.statusText}`)
        const buffer = await resp.arrayBuffer()
        const book = XLSX.read(buffer, { type: "array" })
        sheets.value = book.SheetNames.map((name) => {
          const matrix = XLSX.utils.sheet_to_json<string[]>(book.Sheets[name], {
            header: 1,
            raw: false,
            defval: "",
            blankrows: false,
          })
          const truncatedRows = matrix.length > MAX_ROWS
          const truncatedCols = matrix.some((row) => row.length > MAX_COLS)
          return {
            name,
            rows: matrix
              .slice(0, MAX_ROWS)
              .map((row) => row.slice(0, MAX_COLS).map((cell) => String(cell ?? ""))),
            truncatedRows,
            truncatedCols,
          }
        })
      } catch (e) {
        error.value = String((e as Error)?.message ?? e)
      } finally {
        loading.value = false
      }
    })

    return () => (
      <div class="ol-office-preview">
        <div class="ol-preview-toolbar">
          {sheets.value.length > 1 && (
            <ElSelect
              modelValue={active.value}
              size="small"
              style={{ width: "200px" }}
              onUpdate:modelValue={(value: string | number | boolean) => (active.value = Number(value))}
            >
              {sheets.value.map((sheet, index) => (
                <ElOption key={sheet.name} label={sheet.name} value={index} />
              ))}
            </ElSelect>
          )}
          {current.value && (
            <span class="ol-preview-hint">
              {current.value.rows.length} × {current.value.rows[0]?.length ?? 0}
              {current.value.truncatedRows || current.value.truncatedCols
                ? `（已截断到 ${MAX_ROWS} 行 / ${MAX_COLS} 列）`
                : ""}
            </span>
          )}
        </div>

        {tooLarge.value ? (
          <div class="ol-office-body">
            <ElAlert type="info" showIcon={true} closable={false} title={t("home.preview.office_too_large")} />
          </div>
        ) : error.value ? (
          <div class="ol-office-body">
            <ElAlert
              type="error"
              showIcon={true}
              closable={false}
              title={t("home.preview.office_failed", { message: error.value })}
            />
          </div>
        ) : loading.value ? (
          <div class="ol-office-body">
            <ElSkeleton rows={8} animated={true} />
          </div>
        ) : (
          <div class="ol-office-scroll">
            <table class="ol-sheet-table">
              <tbody>
                {current.value?.rows.map((row, rowIndex) => (
                  <tr key={rowIndex}>
                    <th class="ol-sheet-gutter">{rowIndex + 1}</th>
                    {row.map((cell, cellIndex) => (
                      <td key={cellIndex}>{cell}</td>
                    ))}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    )
  },
})
