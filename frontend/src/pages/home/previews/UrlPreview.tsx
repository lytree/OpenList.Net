import { computed, defineComponent, type PropType } from "vue"
import { ElAlert, ElButton, ElIcon, ElSkeleton } from "element-plus"
import { CopyDocument, Link } from "@element-plus/icons-vue"
import { ElMessage } from "element-plus"
import { t } from "~/i18n"
import { parseUrlShortcut } from "~/utils/ini"
import { useTextContent } from "./useTextContent"
import type { PreviewChildProps } from "./types"

/**
 * `.url`（Windows Internet Shortcut）预览。
 *
 * 这种文件本身没有内容可看，唯一有意义的信息是里面写的地址：
 * 解析出来展示 + 一键复制 / 新窗口打开。解析失败时把原文交给用户判断
 * （常见原因是文件根本不是 `.url`，只是被改了扩展名）。
 */
export default defineComponent({
  name: "UrlPreview",
  props: {
    obj: { type: Object as PropType<PreviewChildProps["obj"]>, required: true },
    src: { type: String, required: true },
  },
  setup(props) {
    const { text, loading, error } = useTextContent(computed(() => props.src), computed(() => props.obj.size))
    const url = computed(() => (text.value ? parseUrlShortcut(text.value) : ""))

    async function copy() {
      if (!url.value) return
      try {
        await navigator.clipboard.writeText(url.value)
        ElMessage.success(t("home.toolbar.copy_link"))
      } catch (e) {
        ElMessage.error(String((e as Error)?.message ?? e))
      }
    }

    return () => (
      <div class="ol-url-preview">
        {loading.value ? (
          <ElSkeleton rows={3} animated={true} />
        ) : error.value ? (
          <ElAlert type="error" showIcon={true} closable={false} title={error.value} />
        ) : url.value ? (
          <>
            <div class="ol-preview-toolbar">
              <span class="ol-preview-hint">{t("home.preview.url_target")}</span>
              <a class="ol-url-target" href={url.value} target="_blank" rel="noopener noreferrer">
                {url.value}
              </a>
              <div class="ol-preview-toolbar-right">
                <ElButton size="small" onClick={copy}>
                  <ElIcon style={{ marginRight: "4px" }}>
                    <CopyDocument />
                  </ElIcon>
                  {t("home.toolbar.copy_link")}
                </ElButton>
                <ElButton
                  size="small"
                  type="primary"
                  onClick={() => window.open(url.value, "_blank", "noopener")}
                >
                  <ElIcon style={{ marginRight: "4px" }}>
                    <Link />
                  </ElIcon>
                  {t("home.preview.open_in_new_window")}
                </ElButton>
              </div>
            </div>
            <div class="ol-preview-empty">{t("home.preview.url_hint")}</div>
          </>
        ) : (
          <>
            <ElAlert
              type="warning"
              showIcon={true}
              closable={false}
              title={t("home.preview.url_invalid")}
            />
            <div class="ol-text-scroll">
              <pre class="ol-plain-text is-wrap">{text.value}</pre>
            </div>
          </>
        )}
      </div>
    )
  },
})
