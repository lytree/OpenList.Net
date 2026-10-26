import { defineComponent, onUnmounted, ref, watch, type PropType } from "vue"
import { ElButton, ElIcon, ElOption, ElSelect, ElTag } from "element-plus"
import { Close } from "@element-plus/icons-vue"
import { t } from "~/i18n"

/**
 * 媒体详情页的内嵌播放器。
 *
 * **为什么不复用首页的 `VideoPreview`**：它把「同目录兄弟视频做剧集列表」绑在
 * 路由 query 上（`?path=&preview=video`），媒体详情页没有这套路由语义；
 * 而媒体页自己就掌握条目的全部文件清单，切集 = 点下一行，不需要路由跳转。
 *
 * 进度上报由父组件（ItemDetail）负责：播放器只负责「现在第几秒」，
 * 节流、写回哪个条目、要不要标记看完，都是媒体域的事。
 */

const RATES = [0.5, 0.75, 1, 1.25, 1.5, 2]

export default defineComponent({
  name: "MediaPlayer",
  props: {
    src: { type: String, required: true },
    /** 起播位置（秒），来自条目的 `progress_seconds`。 */
    startAt: { type: Number, default: 0 },
    poster: { type: String, default: "" },
    title: { type: String, default: "" },
  },
  emits: {
    /** 节流后的进度回调（父组件决定写回频率）。 */
    progress: (_seconds: number) => true,
    ended: () => true,
    close: () => true,
  },
  setup(props, { emit }) {
    const videoRef = ref<HTMLVideoElement | null>(null)
    const rate = ref(1)
    const failed = ref(false)
    /** startAt 只消费一次：续播 seek 之后用户再拖回开头不该再被拽走。 */
    const startAtConsumed = ref(false)

    function onLoadedMetadata() {
      const video = videoRef.value
      if (!video || startAtConsumed.value) return
      if (props.startAt > 5 && Number.isFinite(video.duration) && props.startAt < video.duration - 10) {
        video.currentTime = props.startAt
      }
      startAtConsumed.value = true
    }

    function onTimeUpdate() {
      const video = videoRef.value
      if (!video || !video.currentTime) return
      emit("progress", video.currentTime)
    }

    function onEnded() {
      emit("ended")
    }

    watch(
      () => props.src,
      () => {
        // 换源（切集）重置：新文件从 0 开始（续播位置由父组件传新的 startAt）
        startAtConsumed.value = false
        failed.value = false
      },
    )

    function onError() {
      failed.value = true
    }

    onUnmounted(() => {
      // 关页面/关播放器前把最后位置交出去，别丢最近几秒
      const video = videoRef.value
      if (video && video.currentTime > 0) emit("progress", video.currentTime)
    })

    return () => (
      <div class="ol-mediaplayer">
        <div class="ol-mediaplayer-bar">
          <span class="ol-mediaplayer-title">{props.title}</span>
          <ElTag size="small" type="info">
            {t("media.resume_hint")}
          </ElTag>
          <ElSelect
            modelValue={rate.value}
            size="small"
            style={{ width: "90px" }}
            onUpdate:modelValue={(value: string) => {
              rate.value = Number(value)
              if (videoRef.value) videoRef.value.playbackRate = rate.value
            }}
          >
            {RATES.map((value) => (
              <ElOption key={value} label={`${value}x`} value={value} />
            ))}
          </ElSelect>
          <ElButton size="small" onClick={() => emit("close")}>
            <ElIcon style={{ marginRight: "4px" }}><Close /></ElIcon>
            {t("media.stop")}
          </ElButton>
        </div>
        {failed.value ? (
          <div class="ol-mediaplayer-failed">{t("media.play_failed")}</div>
        ) : (
          <video
            ref={videoRef}
            class="ol-mediaplayer-video"
            src={props.src}
            poster={props.poster || undefined}
            controls={true}
            preload="metadata"
            onLoadedmetadata={onLoadedMetadata}
            onTimeupdate={onTimeUpdate}
            onEnded={onEnded}
            onError={onError}
          />
        )}
      </div>
    )
  },
})
