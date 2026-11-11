import { computed, defineComponent, ref, type PropType } from "vue"
import { useRoute, useRouter } from "vue-router"
import { ElButton, ElOption, ElSelect, ElSwitch, ElTag } from "element-plus"
import { pathDir, pathJoin } from "~/utils/path"
import { humanSize } from "~/utils/format"
import { useSettingsStore } from "~/stores/settings"
import { t } from "~/i18n"
import type { Obj } from "~/api/types"
import type { PreviewChildProps } from "./types"

const RATES = [0.5, 0.75, 1, 1.25, 1.5, 2]
const AUTO_NEXT_KEY = "preview_video_auto_next"

/** 播放列表条目：优先用宿主注入的完整列表，退回后端 `related` 的序列信息。 */
interface PlaylistEntry {
  name: string
  size?: number
}

/**
 * 视频预览。
 *
 * 媒体源直接用 `/p/{path}`（后端签名代理，支持 Range，拖动进度条不会整份重下）。
 * 不做 HLS/ASS/artplayer：先把「能播、能切集、能自动连播」做稳，
 * 字幕/多轨等交给后续 milestone。
 *
 * 播放列表：宿主会注入「同目录全部视频」，这是切上一集/下一集的主来源；
 * 后端 `related`（同目录同前缀兄弟）只在目录列表拿不到时兜底。
 */
export default defineComponent({
  name: "VideoPreview",
  props: {
    obj: { type: Object as PropType<PreviewChildProps["obj"]>, required: true },
    src: { type: String, required: true },
    path: { type: String, required: true },
    playlist: { type: Array as PropType<Obj[]>, default: () => [] },
    playlistPlay: { type: Function as PropType<(index: number) => void>, default: undefined },
  },
  setup(props) {
    const route = useRoute()
    const router = useRouter()
    const settings = useSettingsStore()

    // 视频自动播放由设置项 video_autoplay 控制（默认关）。
    const autoplay = settings.getBool("video_autoplay")

    const videoRef = ref<HTMLVideoElement | null>(null)
    const rate = ref(1)
    const autoNext = ref(localStorage.getItem(AUTO_NEXT_KEY) !== "false")
    const failed = ref(false)

    /** 播放列表 = 宿主给的「同目录视频」，拿不到时退回后端 related，按名字排序保证顺序稳定。 */
    const playlist = computed<PlaylistEntry[]>(() => {
      const host = props.playlist
      if (host && host.length > 0) {
        return host.map((item) => ({ name: item.name, size: item.size }))
      }
      const related = props.obj.related ?? []
      return [...new Set([props.obj.name, ...related.map((item) => item.name)])]
        .sort((a, b) => a.localeCompare(b, "zh-CN", { numeric: true }))
        .map((name) => ({
          name,
          size:
            name === props.obj.name
              ? props.obj.size
              : related.find((item) => item.name === name)?.size,
        }))
    })

    const currentIndex = computed(() =>
      playlist.value.findIndex((item) => item.name === props.obj.name),
    )

    function playAt(index: number) {
      const item = playlist.value[index]
      if (!item) return
      // 宿主注入的列表由宿主完成导航（路径拼接 + 保持预览器）
      if (props.playlistPlay) {
        props.playlistPlay(index)
        return
      }
      const target = pathJoin(pathDir(props.path), item.name)
      void router.replace({
        path: route.path,
        query: { ...route.query, path: target, preview: "video" },
      })
    }

    function playPrev() {
      const total = playlist.value.length
      if (total <= 1 || currentIndex.value < 0) return
      playAt((currentIndex.value - 1 + total) % total)
    }

    function playNext() {
      const total = playlist.value.length
      if (total <= 1 || currentIndex.value < 0) return
      playAt((currentIndex.value + 1) % total)
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
              autoplay={autoplay}
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
            <ElButton size="small" disabled={playlist.value.length <= 1} onClick={playPrev}>
              {t("home.preview.prev")}
            </ElButton>
            <ElButton size="small" disabled={playlist.value.length <= 1} onClick={playNext}>
              {t("home.preview.next")}
            </ElButton>
          </div>
        </div>

        {playlist.value.length > 1 && (
          <div class="ol-playlist">
            {playlist.value.map((item, index) => (
              <ElTag
                key={item.name}
                size="small"
                class="ol-playlist-item"
                onClick={() => playAt(index)}
              >
                <span title={`${item.name} · ${humanSize(item.size ?? 0)}`}>{item.name}</span>
              </ElTag>
            ))}
          </div>
        )}
      </div>
    )
  },
})
