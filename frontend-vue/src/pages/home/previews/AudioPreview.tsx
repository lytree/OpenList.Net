import { defineComponent, ref, type PropType } from "vue"
import { ElOption, ElSelect, ElSwitch } from "element-plus"
import { Headset } from "@element-plus/icons-vue"
import { humanSize } from "~/utils/format"
import { t } from "~/i18n"
import type { PreviewChildProps } from "./types"

const RATES = [0.5, 0.75, 1, 1.25, 1.5, 2]

/** 音频预览：单曲播放 + 倍速/循环。封面用后端 `thumb`，没有则退回图标。 */
export default defineComponent({
  name: "AudioPreview",
  props: {
    obj: { type: Object as PropType<PreviewChildProps["obj"]>, required: true },
    src: { type: String, required: true },
  },
  setup(props) {
    const audioRef = ref<HTMLAudioElement | null>(null)
    const rate = ref(1)
    const loop = ref(false)
    const failed = ref(false)

    function onRateChange(value: string | number | boolean) {
      rate.value = Number(value)
      if (audioRef.value) audioRef.value.playbackRate = rate.value
    }

    function onToggleLoop(value: string | number | boolean) {
      loop.value = Boolean(value)
      if (audioRef.value) audioRef.value.loop = loop.value
    }

    return () => (
      <div class="ol-audio-preview">
        <div class="ol-audio-cover">
          {props.obj.thumb ? (
            <img src={props.obj.thumb} alt="" class="ol-audio-cover-img" />
          ) : (
            <Headset class="ol-audio-cover-icon" />
          )}
        </div>
        <div class="ol-audio-title" title={props.obj.name}>
          {props.obj.name}
        </div>
        <div class="ol-preview-hint">{humanSize(props.obj.size)}</div>

        {failed.value ? (
          <div class="ol-preview-empty">{t("home.preview.play_failed")}</div>
        ) : (
          <audio
            ref={audioRef}
            class="ol-audio-el"
            src={props.src}
            controls={true}
            autoplay={true}
            onLoadedmetadata={() => {
              if (audioRef.value) audioRef.value.playbackRate = rate.value
            }}
            onError={() => (failed.value = true)}
          />
        )}

        <div class="ol-preview-toolbar">
          <span class="ol-preview-hint">{t("home.preview.playback_rate")}</span>
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
          <span class="ol-preview-hint">{t("home.preview.loop")}</span>
          <ElSwitch modelValue={loop.value} size="small" onUpdate:modelValue={onToggleLoop} />
        </div>
      </div>
    )
  },
})
