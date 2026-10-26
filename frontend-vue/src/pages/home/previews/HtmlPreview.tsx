import { computed, defineComponent, type PropType } from "vue"
import { ElAlert, ElSkeleton } from "element-plus"
import { t } from "~/i18n"
import { useTextContent } from "./useTextContent"
import type { PreviewChildProps } from "./types"

/**
 * HTML 预览。
 *
 * 用 `srcdoc` + `sandbox` 隔离运行：HTML 文件是用户上传的任意内容，
 * 不能和主应用同源执行（否则可直接读 localStorage 里的 token）。
 * `allow-scripts` 让页面里的 JS 能跑，`allow-same-origin` **故意不给**。
 *
 * 另外注入 `<base>` 指向文件所在目录的 `/p` 路径，让同目录的 css/图片
 * 等相对引用能正确加载（后端 `/p/{path}` 对任意文件生效）。
 */
export default defineComponent({
  name: "HtmlPreview",
  props: {
    obj: { type: Object as PropType<PreviewChildProps["obj"]>, required: true },
    src: { type: String, required: true },
  },
  setup(props) {
    const src = computed(() => props.src)
    const size = computed(() => props.obj.size)
    const { text, loading, error, tooLarge } = useTextContent(src, size)

    const doc = computed(() => {
      if (!text.value) return ""
      const base = props.src.slice(0, props.src.lastIndexOf("/") + 1)
      const baseTag = /<base[\s>]/i.test(text.value) ? "" : `<base href="${base}">`
      if (/<head[\s>]/i.test(text.value)) {
        return text.value.replace(/<head([^>]*)>/i, `<head$1>${baseTag}`)
      }
      return `<!doctype html><html><head>${baseTag}</head><body>${text.value}</body></html>`
    })

    return () => (
      <div class="ol-html-preview">
        {tooLarge.value ? (
          <ElAlert type="info" showIcon={true} closable={false} title={t("home.preview.too_large")} />
        ) : error.value ? (
          <ElAlert type="error" showIcon={true} closable={false} title={error.value} />
        ) : loading.value ? (
          <ElSkeleton rows={10} animated={true} />
        ) : (
          <iframe
            class="ol-frame-el"
            title={props.obj.name}
            srcdoc={doc.value}
            sandbox="allow-scripts allow-forms allow-popups allow-modals"
          />
        )}
      </div>
    )
  },
})
