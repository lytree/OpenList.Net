import { computed, defineComponent, onBeforeUnmount, onMounted, ref, type PropType } from "vue"
import { ElAlert, ElIcon, ElSkeleton, ElButton } from "element-plus"
import { ZoomIn, ZoomOut } from "@element-plus/icons-vue"
import { t } from "~/i18n"
import { humanSize } from "~/utils/format"
import type { PreviewChildProps } from "./types"

/** 超过这个体积不在浏览器里排版：docx-preview 会把整个文档铺成 DOM，太大会卡死标签页。 */
const SIZE_LIMIT = 30 * 1024 * 1024

/**
 * Word（`.docx`）预览。
 *
 * `docx-preview` 直接产出分页 DOM，样式与 Word 接近；样式表挂到组件容器上
 * （`styleContainer` 传同一元素），避免它的全局 `@page` 规则影响整页。
 *
 * `.doc`（OLE 老格式）不支持 —— 那是完全不同的二进制容器，
 * 前端没有可靠且体积可接受的解析器，交给「下载」处理。
 */
export default defineComponent({
  name: "DocPreview",
  props: {
    obj: { type: Object as PropType<PreviewChildProps["obj"]>, required: true },
    src: { type: String, required: true },
  },
  setup(props) {
    const loading = ref(true)
    const error = ref("")
    const tooLarge = computed(() => props.obj.size > SIZE_LIMIT)
    const scale = ref(1)
    const container = ref<HTMLElement | null>(null)

    async function render() {
      const host = container.value
      if (!host) return
      loading.value = true
      error.value = ""
      try {
        // 动态导入：docx-preview 只在真的打开 docx 时才加载（约 200 KB gzip）
        const { renderAsync } = await import("docx-preview")
        const resp = await fetch(props.src)
        if (!resp.ok) throw new Error(`${resp.status} ${resp.statusText}`)
        const blob = await resp.blob()
        host.innerHTML = ""
        await renderAsync(blob, host, host, {
          className: "ol-docx-doc",
          inWrapper: true,
          breakPages: true,
          ignoreWidth: false,
          renderHeaders: true,
          renderFooters: true,
        })
      } catch (e) {
        error.value = String((e as Error)?.message ?? e)
      } finally {
        loading.value = false
      }
    }

    onMounted(() => {
      if (!tooLarge.value) void render()
      else loading.value = false
    })
    onBeforeUnmount(() => {
      if (container.value) container.value.innerHTML = ""
    })

    const zoom = (factor: number) => {
      scale.value = Math.min(2, Math.max(0.5, Number((scale.value * factor).toFixed(2))))
    }

    return () => (
      <div class="ol-office-preview">
        <div class="ol-preview-toolbar">
          <ElButton size="small" onClick={() => zoom(1 / 1.15)}>
            <ElIcon>
              <ZoomOut />
            </ElIcon>
          </ElButton>
          <span class="ol-preview-hint">{Math.round(scale.value * 100)}%</span>
          <ElButton size="small" onClick={() => zoom(1.15)}>
            <ElIcon>
              <ZoomIn />
            </ElIcon>
          </ElButton>
          <span class="ol-preview-hint">{humanSize(props.obj.size)}</span>
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
        ) : (
          <div class="ol-office-scroll">
            {loading.value && (
              <div class="ol-office-body">
                <ElSkeleton rows={8} animated={true} />
                <div class="ol-preview-hint">{t("home.preview.office_loading")}</div>
              </div>
            )}
            <div
              ref={container}
              class="ol-office-doc"
              style={{ transform: `scale(${scale.value})`, transformOrigin: "top left" }}
            />
          </div>
        )}
      </div>
    )
  },
})
