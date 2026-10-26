import { computed, defineComponent, onBeforeUnmount, onMounted, ref, type PropType } from "vue"
import { ElAlert, ElSkeleton } from "element-plus"
import { t } from "~/i18n"
import type { PreviewChildProps } from "./types"

/** pptx-preview 会把每页铺成绝对定位的 DOM，体积上限比 docx 更保守。 */
const SIZE_LIMIT = 30 * 1024 * 1024
/** 幻灯片按 16:9 渲染；宽度取一个在 1200px 容器内也清晰的值。 */
const SLIDE_WIDTH = 960
const SLIDE_HEIGHT = 540

/**
 * PowerPoint（`.pptx`）预览。
 *
 * `pptx-preview` 用 jszip 解包 OOXML 再逐页转成 HTML（`mode: "list"` 是全部平铺、
 * 滚动查看，比单页翻页更适合快速扫读）。`.ppt` 老格式同样不支持。
 */
export default defineComponent({
  name: "PptPreview",
  props: {
    obj: { type: Object as PropType<PreviewChildProps["obj"]>, required: true },
    src: { type: String, required: true },
  },
  setup(props) {
    const loading = ref(true)
    const error = ref("")
    const tooLarge = computed(() => props.obj.size > SIZE_LIMIT)
    const host = ref<HTMLElement | null>(null)
    /** 组件卸载时要显式 destroy，否则它会留下全局事件与图片 URL。 */
    let previewer: { destroy?: () => void } | null = null

    onMounted(async () => {
      if (tooLarge.value || !host.value) {
        loading.value = false
        return
      }
      try {
        const { init } = await import("pptx-preview")
        const resp = await fetch(props.src)
        if (!resp.ok) throw new Error(`${resp.status} ${resp.statusText}`)
        const buffer = await resp.arrayBuffer()
        previewer = init(host.value, {
          width: SLIDE_WIDTH,
          height: SLIDE_HEIGHT,
          mode: "list",
        })
        await (previewer as unknown as { preview: (data: ArrayBuffer) => Promise<unknown> }).preview(buffer)
      } catch (e) {
        error.value = String((e as Error)?.message ?? e)
      } finally {
        loading.value = false
      }
    })

    onBeforeUnmount(() => {
      previewer?.destroy?.()
      previewer = null
      if (host.value) host.value.innerHTML = ""
    })

    return () => (
      <div class="ol-office-preview">
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
        ) : (
          <div class="ol-office-scroll">
            {loading.value && (
              <div class="ol-office-body">
                <ElSkeleton rows={8} animated={true} />
                <div class="ol-preview-hint">{t("home.preview.office_loading")}</div>
              </div>
            )}
            <div ref={host} class="ol-pptx-host" />
          </div>
        )}
      </div>
    )
  },
})
