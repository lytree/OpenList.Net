import { computed, defineComponent, ref, type PropType } from "vue"
import { ElAlert, ElOption, ElSelect, ElSkeleton, ElSwitch } from "element-plus"
import { t } from "~/i18n"
import { TEXT_ENCODINGS, useTextContent, type TextEncoding } from "./useTextContent"
import type { PreviewChildProps } from "./types"

/**
 * 纯文本预览。
 *
 * 等宽显示 + 可选自动折行；编码可切（中文 GBK 文件在 UTF-8 下会整屏乱码，
 * 这是旧前端最常被抱怨的问题之一）。大文件只读开头 1 MB，并明确提示已截断。
 */
export default defineComponent({
  name: "TextPreview",
  props: {
    obj: { type: Object as PropType<PreviewChildProps["obj"]>, required: true },
    src: { type: String, required: true },
  },
  setup(props) {
    const src = computed(() => props.src)
    const size = computed(() => props.obj.size)
    const { text, loading, error, truncated, tooLarge, encoding } = useTextContent(src, size)

    const wrap = ref(localStorage.getItem("preview_text_wrap") === "true")
    const lines = computed(() => (text.value ? text.value.split("\n").length : 0))

    function onWrapChange(value: string | number | boolean) {
      wrap.value = Boolean(value)
      localStorage.setItem("preview_text_wrap", String(wrap.value))
    }

    function onEncodingChange(value: string | number | boolean) {
      encoding.value = String(value) as TextEncoding
    }

    return () => (
      <div class="ol-text-preview">
        <div class="ol-preview-toolbar">
          <ElSelect
            modelValue={encoding.value}
            size="small"
            style={{ width: "150px" }}
            onUpdate:modelValue={onEncodingChange}
          >
            {TEXT_ENCODINGS.map((item) => (
              <ElOption key={item.value} label={item.label} value={item.value} />
            ))}
          </ElSelect>
          <span class="ol-preview-hint">{t("home.preview.word_wrap")}</span>
          <ElSwitch modelValue={wrap.value} size="small" onUpdate:modelValue={onWrapChange} />
          <span class="ol-preview-hint">
            {truncated.value
              ? t("home.preview.truncated")
              : t("home.preview.lines", { count: lines.value })}
          </span>
        </div>

        <div class="ol-text-scroll">
          {tooLarge.value ? (
            <ElAlert type="info" showIcon={true} closable={false} title={t("home.preview.too_large")} />
          ) : error.value ? (
            <ElAlert type="error" showIcon={true} closable={false} title={error.value} />
          ) : loading.value ? (
            <ElSkeleton rows={12} animated={true} />
          ) : (
            <pre class={wrap.value ? "ol-plain-text is-wrap" : "ol-plain-text"}>
              {text.value}
            </pre>
          )}
        </div>
      </div>
    )
  },
})
