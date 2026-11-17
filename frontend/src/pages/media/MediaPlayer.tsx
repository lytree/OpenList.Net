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
    /**
     * 节流后的进度回调（父组件决定写回频率）。
     *
     * 第二个参数是片长：比例类度量（最远/离开位置）要拿它当分母，而
     * `files.duration_seconds` 是扫描期探测的，远程挂载常常探不出来 —— 播放器自己知道的是最准的。
     */
    progress: (_seconds: number, _duration: number) => true,
    /** 元数据加载完，片长已知。父组件据此尽早把片长交给会话跟踪器。 */
    duration: (_seconds: number) => true,
    /** 用户拖了一次进度条。父组件只计数，不单独发请求。 */
    seek: () => true,
    ended: () => true,
    close: () => true,
  },
  setup(props, { emit }) {
    const videoRef = ref<HTMLVideoElement | null>(null)
    const rate = ref(1)
    const failed = ref(false)
    /** startAt 只消费一次：续播 seek 之后用户再拖回开头不该再被拽走。 */
    const startAtConsumed = ref(false)

    /** `video.duration` 在元数据到位前是 NaN，统一在这里挡掉。 */
    function knownDuration(video: HTMLVideoElement): number {
      return Number.isFinite(video.duration) && video.duration > 0 ? video.duration : 0
    }

    function onLoadedMetadata() {
      const video = videoRef.value
      if (!video) return
      const duration = knownDuration(video)
      if (duration > 0) emit("duration", duration)
      if (startAtConsumed.value) return
      if (props.startAt > 5 && duration > 0 && props.startAt < duration - 10) {
        video.currentTime = props.startAt
      }
      startAtConsumed.value = true
    }

    function onTimeUpdate() {
      const video = videoRef.value
      if (!video || !video.currentTime) return
      emit("progress", video.currentTime, knownDuration(video))
    }

    /** 拖动：换源（`emptied`）与元数据回填引起的 seek 不算用户拖动，这里不区分 —— 见调用方口径。 */
    function onSeeking() {
      emit("seek")
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
      if (video && video.currentTime > 0) emit("progress", video.currentTime, knownDuration(video))
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
            onSeeking={onSeeking}
            onEnded={onEnded}
            onError={onError}
          />
        )}
      </div>
    )
  },
})
