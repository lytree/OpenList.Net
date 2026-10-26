import { computed, defineComponent, ref, type PropType } from "vue"
import { useRoute, useRouter } from "vue-router"
import { ElButton, ElOption, ElSelect, ElSwitch, ElTag } from "element-plus"
import { pathDir, pathJoin } from "~/utils/path"
import { humanSize } from "~/utils/format"
import { t } from "~/i18n"
import type { PreviewChildProps } from "./types"

const RATES = [0.5, 0.75, 1, 1.25, 1.5, 2]
const AUTO_NEXT_KEY = "preview_video_auto_next"

/**
 * 视频预览。
 *
 * 媒体源直接用 `/p/{path}`（后端签名代理，支持 Range，拖动进度条不会整份重下）。
 * 不做 HLS/ASS/artplayer：后端下发的 `related` 只覆盖「同目录同前缀的兄弟视频」，
 * 先把「能播、能切集、能自动连播」做稳，字幕/多轨等交给后续 milestone。
 */
export default defineComponent({
  name: "VideoPreview",
  props: {
    obj: { type: Object as PropType<PreviewChildProps["obj"]>, required: true },
    src: { type: String, required: true },
    path: { type: String, required: true },
  },
  setup(props) {
    const route = useRoute()
    const router = useRouter()

    const videoRef = ref<HTMLVideoElement | null>(null)
    const rate = ref(1)
    const autoNext = ref(localStorage.getItem(AUTO_NEXT_KEY) !== "false")
    const failed = ref(false)

    /** 播放列表 = 当前文件 + 后端给的同系列兄弟，按名字排序保证顺序稳定。 */
    const playlist = computed(() => {
      const names = [props.obj.name, ...(props.obj.related ?? []).map((item) => item.name)]
      return [...new Set(names)].sort((a, b) => a.localeCompare(b, "zh-CN", { numeric: true }))
    })

    const currentIndex = computed(() => playlist.value.indexOf(props.obj.name))

    function playAt(index: number) {
      const name = playlist.value[index]
      if (!name) return
      const target = pathJoin(pathDir(props.path), name)
      void router.replace({
        path: route.path,
        query: { ...route.query, path: target, preview: "video" },
      })
    }

    function playNext() {
      if (playlist.value.length <= 1) return
      playAt((currentIndex.value + 1) % playlist.value.length)
    }

    function onEnded() {
      if (autoNext.value) playNext()
    }

    function onRateChange(value: string | number | boolean) {
      rate.value = Number(value)
      if (videoRef.value) videoRef.value.playbackRate = rate.value
    }

    function onToggleAutoNext(value: string | number | boolean) {
      autoNext.value = Boolean(value)
      localStorage.setItem(AUTO_NEXT_KEY, String(autoNext.value))
    }

    function onLoadedMetadata() {
      if (videoRef.value) videoRef.value.playbackRate = rate.value
    }

    return () => (
      <div class="ol-video-preview">
        <div class="ol-media-stage">
          {failed.value ? (
            <div class="ol-preview-empty">{t("home.preview.play_failed")}</div>
          ) : (
            <video
              ref={videoRef}
              class="ol-media-el"
              src={props.src}
              controls={true}
              autoplay={true}
              playsinline={true}
              onEnded={onEnded}
              onLoadedmetadata={onLoadedMetadata}
              onError={() => (failed.value = true)}
            />
          )}
        </div>

        <div class="ol-preview-toolbar ol-preview-toolbar-wrap">
          <span class="ol-preview-hint">
            {currentIndex.value + 1} / {playlist.value.length}
          </span>
          <div class="ol-preview-toolbar-right">
            <span class="ol-preview-hint">{t("home.preview.auto_next")}</span>
            <ElSwitch
              modelValue={autoNext.value}
              size="small"
              onUpdate:modelValue={onToggleAutoNext}
            />
            <ElSelect
              modelValue={rate.value}
              size="small"
              style={{ width: "92px" }}
              onUpdate:modelValue={onRateChange}
            >
              {RATES.map((value) => (
                <ElOption key={value} label={`${value}x`} value={value} />
              ))}
            </ElSelect>
            <ElButton size="small" disabled={playlist.value.length <= 1} onClick={playNext}>
              {t("home.preview.next")}
            </ElButton>
          </div>
        </div>

        {playlist.value.length > 1 && (
          <div class="ol-playlist">
            {props.obj.related?.map((item) => (
              <ElTag
                key={item.name}
                size="small"
                class="ol-playlist-item"
                onClick={() => {
                  const index = playlist.value.indexOf(item.name)
                  if (index >= 0) playAt(index)
                }}
              >
                <span title={`${item.name} · ${humanSize(item.size)}`}>{item.name}</span>
              </ElTag>
            ))}
          </div>
        )}
      </div>
    )
  },
})
