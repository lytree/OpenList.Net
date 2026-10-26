import { computed, defineComponent, nextTick, ref, watch, type PropType } from "vue"
import { marked } from "marked"
import DOMPurify from "dompurify"
import { ElAlert, ElOption, ElSelect, ElSkeleton } from "element-plus"
import { t } from "~/i18n"
import { TEXT_ENCODINGS, useTextContent, type TextEncoding } from "./useTextContent"
import type { PreviewChildProps } from "./types"

// 单换行不强制 <br>（GitHub 行为），表格/删除线等 GFM 扩展默认开启。
marked.setOptions({ gfm: true, breaks: false, async: false })

/**
 * Markdown 预览。
 *
 * 渲染管线是「marked → DOMPurify → v-html」：文件内容是不可信输入，
 * 必须消毒后再进 DOM（旧前端也走 DOMPurify）。消毒后把外链统一改成
 * `target=_blank rel=noopener`，避免在 SPA 里被导航走。
 */
export default defineComponent({
  name: "MarkdownPreview",
  props: {
    obj: { type: Object as PropType<PreviewChildProps["obj"]>, required: true },
    src: { type: String, required: true },
  },
  setup(props) {
    const src = computed(() => props.src)
    const size = computed(() => props.obj.size)
    const { text, loading, error, truncated, tooLarge, encoding } = useTextContent(src, size)

    const rootRef = ref<HTMLElement | null>(null)

    const html = computed(() => {
      if (!text.value) return ""
      const raw = marked.parse(text.value) as string
      return DOMPurify.sanitize(raw, { USE_PROFILES: { html: true } })
    })

    watch(html, async () => {
      await nextTick()
      rootRef.value?.querySelectorAll("a[href]").forEach((anchor) => {
        anchor.setAttribute("target", "_blank")
        anchor.setAttribute("rel", "noopener noreferrer")
      })
    })

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
          <span class="ol-preview-hint">
            {truncated.value ? t("home.preview.truncated") : `${text.value.length} chars`}
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
            <div ref={rootRef} class="ol-markdown" v-html={html.value} />
          )}
        </div>
      </div>
    )
  },
})
