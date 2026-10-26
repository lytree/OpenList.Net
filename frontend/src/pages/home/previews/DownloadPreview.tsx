import { defineComponent, type PropType } from "vue"
import { ElButton, ElIcon } from "element-plus"
import { Document, Download } from "@element-plus/icons-vue"
import { humanSize } from "~/utils/format"
import { t } from "~/i18n"
import type { PreviewChildProps } from "./types"

/**
 * 下载兜底页。
 *
 * 每个文件都一定有这个预览器（排在最后）：既覆盖「类型不支持」的情况，
 * 也覆盖「用户就是想下载」的情况。
 */
export default defineComponent({
  name: "DownloadPreview",
  props: {
    obj: { type: Object as PropType<PreviewChildProps["obj"]>, required: true },
    download_url: { type: String, required: true },
  },
  setup(props) {
    function download() {
      const a = document.createElement("a")
      a.href = props.download_url
      a.download = props.obj.name
      a.rel = "noopener"
      document.body.appendChild(a)
      a.click()
      a.remove()
    }

    return () => (
      <div class="ol-download-preview">
        <ElIcon size={64} class="ol-download-icon">
          <Document />
        </ElIcon>
        <div class="ol-download-name" title={props.obj.name}>
          {props.obj.name}
        </div>
        <div class="ol-preview-hint">{humanSize(props.obj.size)}</div>
        <div class="ol-download-actions">
          <ElButton type="primary" onClick={download}>
            <ElIcon style={{ marginRight: "4px" }}>
              <Download />
            </ElIcon>
            {t("home.preview.download")}
          </ElButton>
          <ElButton onClick={() => window.open(props.download_url, "_blank", "noopener")}>
            {t("home.preview.open_in_new_window")}
          </ElButton>
        </div>
        <p class="ol-preview-hint">{t("home.preview.no_inline_preview")}</p>
      </div>
    )
  },
})
