import { computed, defineComponent, type PropType } from "vue"
import { ElAlert, ElButton, ElIcon } from "element-plus"
import { Link } from "@element-plus/icons-vue"
import { t } from "~/i18n"
import { convertUrl } from "./registry"
import type { PreviewChildProps } from "./types"

/**
 * 设置驱动的 iframe 预览。
 *
 * 管理员在「站点设置 → iframe_previews」里配置形如
 * `{ "docx,xlsx": { "Office 在线预览": "https://view.officeapps.live.com/op/view.aspx?src=$eurl" } }`。
 * URL 模板变量与旧前端 `convertURL` 完全一致（见 `registry.convertUrl`）。
 */
export default defineComponent({
  name: "IframePreview",
  props: {
    obj: { type: Object as PropType<PreviewChildProps["obj"]>, required: true },
    src: { type: String, required: true },
    download_url: { type: String, required: true },
    scheme: { type: String, default: "" },
  },
  setup(props) {
    const url = computed(() =>
      props.scheme
        ? convertUrl(props.scheme, {
            raw_url: props.src,
            d_url: props.download_url,
            name: props.obj.name,
            ts: true,
          })
        : "",
    )

    return () => (
      <div class="ol-html-preview">
        {url.value ? (
          <iframe class="ol-frame-el" src={url.value} title={props.obj.name} />
        ) : (
          <ElAlert
            type="warning"
            showIcon={true}
            closable={false}
            title={t("home.preview.iframe_missing_scheme")}
          />
        )}
        {url.value && (
          <div class="ol-preview-toolbar">
            <span class="ol-preview-hint ol-ellipsis">{url.value}</span>
            <ElButton
              size="small"
              text={true}
              type="primary"
              onClick={() => window.open(url.value, "_blank", "noopener")}
            >
              <ElIcon style={{ marginRight: "4px" }}>
                <Link />
              </ElIcon>
              {t("home.preview.open_in_new_window")}
            </ElButton>
          </div>
        )}
      </div>
    )
  },
})
