import { defineComponent, onBeforeUnmount, onMounted, ref, type PropType } from "vue"
import { ElAlert, ElSkeleton } from "element-plus"
import { t } from "~/i18n"
import "asciinema-player/dist/bundle/asciinema-player.css"
import type { PreviewChildProps } from "./types"

/**
 * asciinema 录制（`.cast`）回放。
 *
 * `create()` 自己去拉 `src`（播放器内部按需 Range 读取），所以这里不需要先 fetch。
 * 播放器实例必须 `dispose()`：它会注册定时器与 resize 监听。
 */
export default defineComponent({
  name: "AsciinemaPreview",
  props: {
    obj: { type: Object as PropType<PreviewChildProps["obj"]>, required: true },
    src: { type: String, required: true },
  },
  setup(props) {
    const loading = ref(true)
    const error = ref("")
    const host = ref<HTMLElement | null>(null)
    let player: { dispose?: () => void } | null = null

    onMounted(async () => {
      if (!host.value) return
      try {
        const { create } = await import("asciinema-player")
        player = create(props.src, host.value, {
          fit: "width",
          autoPlay: true,
          terminalFontSize: "small",
        })
        loading.value = false
      } catch (e) {
        error.value = String((e as Error)?.message ?? e)
        loading.value = false
      }
    })

    onBeforeUnmount(() => {
      player?.dispose?.()
      player = null
    })

    return () => (
      <div class="ol-asciinema-preview">
        {error.value ? (
          <div class="ol-office-body">
            <ElAlert
              type="error"
              showIcon={true}
              closable={false}
              title={t("home.preview.asciinema_failed", { message: error.value })}
            />
          </div>
        ) : (
          <>
            {loading.value && (
              <div class="ol-office-body">
                <ElSkeleton rows={5} animated={true} />
              </div>
            )}
            <div ref={host} class="ol-asciinema-host" />
          </>
        )}
      </div>
    )
  },
})
