import { computed, defineComponent } from "vue"
import { ElButton, ElIcon, ElOption, ElSelect } from "element-plus"
import { ArrowLeft, ArrowRight } from "@element-plus/icons-vue"
import { pageList, PAGE_SIZES } from "~/utils/list"

/**
 * 分页条（自绘）。
 *
 * **为什么不用 `ElPagination`**：它在当前 Vue/EP 组合下渲染成空注释节点
 *（`<div class="ol-pager"><!----></div>`，无报错、无警告），排查成本远高于自己写一个。
 * 自绘还有一个附带好处：语义完全按我们的口径表达（「共 N 项」是过滤后的数量）。
 *
 * 交互保持最朴素：首页/末页恒在、当前页 ±1、上一页/下一页按钮在边界禁用。
 */
export default defineComponent({
  name: "FilePager",
  props: {
    page: { type: Number, required: true },
    pageSize: { type: Number, required: true },
    total: { type: Number, required: true },
    pages: { type: Number, required: true },
  },
  emits: {
    "update:page": (_page: number) => true,
    "update:pageSize": (_size: number) => true,
  },
  setup(props, { emit }) {
    const items = computed(() => pageList(props.page, props.pages))

    function goto(p: number | "…") {
      if (p === "…") return
      if (p === props.page) return
      emit("update:page", p)
    }

    return () => (
      <div class="ol-pager">
        <span class="ol-pager-total">共 {props.total} 项</span>
        <div class="ol-pager-actions">
          <ElSelect
            modelValue={props.pageSize}
            size="small"
            style={{ width: "104px" }}
            onUpdate:modelValue={(v: number) => emit("update:pageSize", Number(v))}
          >
            {PAGE_SIZES.map((size) => (
              <ElOption key={size} label={`${size} 条/页`} value={size} />
            ))}
          </ElSelect>
          <ElButton
            size="small"
            text={true}
            disabled={props.page <= 1}
            onClick={() => goto(props.page - 1)}
          >
            <ElIcon>
              <ArrowLeft />
            </ElIcon>
          </ElButton>
          {items.value.map((item, index) =>
            item === "…" ? (
              <span key={`gap-${index}`} class="ol-pager-gap">
                …
              </span>
            ) : (
              <ElButton
                key={item}
                size="small"
                text={item !== props.page}
                type={item === props.page ? "primary" : "default"}
                class={item === props.page ? "ol-pager-current" : ""}
                onClick={() => goto(item)}
              >
                {item}
              </ElButton>
            ),
          )}
          <ElButton
            size="small"
            text={true}
            disabled={props.page >= props.pages}
            onClick={() => goto(props.page + 1)}
          >
            <ElIcon>
              <ArrowRight />
            </ElIcon>
          </ElButton>
        </div>
      </div>
    )
  },
})
