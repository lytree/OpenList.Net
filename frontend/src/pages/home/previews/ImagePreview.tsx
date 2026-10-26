import { defineComponent, ref, type PropType } from "vue"
import { ElButton, ElButtonGroup, ElIcon } from "element-plus"
import { RefreshLeft, RefreshRight, ZoomIn, ZoomOut } from "@element-plus/icons-vue"
import { t } from "~/i18n"
import type { PreviewChildProps } from "./types"

/**
 * 图片预览。
 *
 * 不用 `el-image` 自带的 `preview-src-list` 灯箱：灯箱是「覆盖全屏的另一个图层」，
 * 会丢掉宿主工具栏（切换预览器/下载）。这里在同一块舞台里做缩放/旋转/平移，
 * 双击在「适应窗口」与 1:1 之间切换。
 */
export default defineComponent({
  name: "ImagePreview",
  props: {
    obj: { type: Object as PropType<PreviewChildProps["obj"]>, required: true },
    src: { type: String, required: true },
  },
  setup(props) {
    const scale = ref(1)
    const rotate = ref(0)
    const tx = ref(0)
    const ty = ref(0)
    const dragging = ref(false)
    const failed = ref(false)

    let startX = 0
    let startY = 0
    let originX = 0
    let originY = 0

    const clamp = (value: number) => Math.min(8, Math.max(0.1, value))

    function zoomBy(factor: number) {
      scale.value = clamp(Number((scale.value * factor).toFixed(3)))
    }

    function reset() {
      scale.value = 1
      rotate.value = 0
      tx.value = 0
      ty.value = 0
    }

    function onWheel(e: WheelEvent) {
      // 向上滚放大；按住 Ctrl 时浏览器会缩放整页，这里一并拦掉
      e.preventDefault()
      zoomBy(e.deltaY < 0 ? 1.12 : 1 / 1.12)
    }

    function onPointerDown(e: PointerEvent) {
      if (scale.value <= 1) return
      dragging.value = true
      startX = e.clientX
      startY = e.clientY
      originX = tx.value
      originY = ty.value
      ;(e.currentTarget as HTMLElement).setPointerCapture?.(e.pointerId)
    }

    function onPointerMove(e: PointerEvent) {
      if (!dragging.value) return
      tx.value = originX + (e.clientX - startX)
      ty.value = originY + (e.clientY - startY)
    }

    function onPointerUp(e: PointerEvent) {
      dragging.value = false
      ;(e.currentTarget as HTMLElement).releasePointerCapture?.(e.pointerId)
    }

    return () => (
      <div class="ol-image-preview">
        <div class="ol-preview-toolbar">
          <ElButtonGroup size="small">
            <ElButton onClick={() => zoomBy(1 / 1.2)}>
              <ElIcon>
                <ZoomOut />
              </ElIcon>
            </ElButton>
            <ElButton onClick={() => zoomBy(1.2)}>
              <ElIcon>
                <ZoomIn />
              </ElIcon>
            </ElButton>
            <ElButton onClick={() => (rotate.value -= 90)}>
              <ElIcon>
                <RefreshLeft />
              </ElIcon>
            </ElButton>
            <ElButton onClick={() => (rotate.value += 90)}>
              <ElIcon>
                <RefreshRight />
              </ElIcon>
            </ElButton>
            <ElButton onClick={reset}>{t("home.preview.reset_zoom")}</ElButton>
          </ElButtonGroup>
          <span class="ol-preview-hint">{Math.round(scale.value * 100)}%</span>
        </div>

        <div
          class={dragging.value ? "ol-image-stage is-dragging" : "ol-image-stage"}
          onWheel={onWheel}
          onPointerdown={onPointerDown}
          onPointermove={onPointerMove}
          onPointerup={onPointerUp}
          onPointercancel={onPointerUp}
          onDblclick={() => (scale.value === 1 ? (scale.value = 2) : reset())}
        >
          {failed.value ? (
            <div class="ol-preview-empty">{t("home.preview.failed_load_img")}</div>
          ) : (
            <img
              class="ol-image-canvas"
              src={props.src}
              alt={props.obj.name}
              draggable={false}
              style={{
                transform: `translate(${tx.value}px, ${ty.value}px) scale(${scale.value}) rotate(${rotate.value}deg)`,
                cursor: scale.value > 1 ? (dragging.value ? "grabbing" : "grab") : "default",
              }}
              onError={() => (failed.value = true)}
            />
          )}
        </div>
      </div>
    )
  },
})
