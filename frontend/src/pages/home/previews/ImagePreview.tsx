import { computed, defineComponent, ref, watch, type PropType } from "vue"
import { ElButton, ElButtonGroup, ElIcon } from "element-plus"
import { RefreshLeft, RefreshRight, ZoomIn, ZoomOut } from "@element-plus/icons-vue"
import { t } from "~/i18n"
import type { Obj } from "~/api/types"
import type { PreviewChildProps } from "./types"

/**
 * 图片预览。
 *
 * 不用 `el-image` 自带的 `preview-src-list` 灯箱：灯箱是「覆盖全屏的另一个图层」，
 * 会丢掉宿主工具栏（切换预览器/下载）。这里在同一块舞台里做缩放/旋转/平移，
 * 双击在「适应窗口」与 1:1 之间切换。
 *
 * 「上一张 / 下一张」的列表由宿主注入（同目录图片），导航本身也由宿主改 URL 完成，
 * 因此本组件不需要知道路径拼接与预览器保持逻辑。
 */
export default defineComponent({
  name: "ImagePreview",
  props: {
    obj: { type: Object as PropType<PreviewChildProps["obj"]>, required: true },
    src: { type: String, required: true },
    playlist: { type: Array as PropType<Obj[]>, default: () => [] },
    playlistPlay: { type: Function as PropType<(index: number) => void>, default: undefined },
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

    // 切到同目录的下一张时组件实例不变（只有 props 变化），视图状态必须归零，
    // 否则会带着上一张的缩放/旋转看新图。
    watch(
      () => props.obj.name,
      () => {
        reset()
        failed.value = false
      },
    )

    // 播放列表里找不到当前文件时（目录列表尚未就绪）隐藏切图按钮
    const currentIndex = computed(() =>
      props.playlist.findIndex((item) => item.name === props.obj.name),
    )

    function playAt(index: number) {
      props.playlistPlay?.(index)
    }

    function playPrev() {
      const total = props.playlist.length
      if (total <= 1 || currentIndex.value < 0) return
      playAt((currentIndex.value - 1 + total) % total)
    }

    function playNext() {
      const total = props.playlist.length
      if (total <= 1 || currentIndex.value < 0) return
      playAt((currentIndex.value + 1) % total)
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
          {currentIndex.value >= 0 && props.playlist.length > 1 && (
            <ElButtonGroup size="small">
              <ElButton onClick={playPrev}>{t("home.preview.prev")}</ElButton>
              <ElButton onClick={playNext}>{t("home.preview.next")}</ElButton>
            </ElButtonGroup>
          )}
          {currentIndex.value >= 0 && props.playlist.length > 1 && (
            <span class="ol-preview-hint">
              {currentIndex.value + 1} / {props.playlist.length}
            </span>
          )}
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
