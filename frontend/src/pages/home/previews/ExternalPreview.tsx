import { computed, defineComponent, onMounted, ref, type PropType } from "vue"
import { ElAlert, ElButton, ElIcon } from "element-plus"
import { Link } from "@element-plus/icons-vue"
import { t } from "~/i18n"
import { convertUrl } from "./registry"
import type { PreviewChildProps } from "./types"

/**
 * 外置预览（设置 `external_previews`）。
 *
 * 与 iframe 预览的区别是「打开方式」：iframe 把外部站点嵌进预览舞台，
 * 外置预览则把地址交给**浏览器之外**的程序 —— `vlc://$eurl`、`potplayer://$url`
 * 这类自定义协议会唤起本机播放器，所以这里不渲染内容，只负责触发打开。
 *
 * 自动打开可能被弹窗拦截（不是所有浏览器都把「点下拉选一项」算作用户手势），
 * 被拦时页面留在原地并给出一个按钮，用户再点一次即可，不会白屏。
 */
export default defineComponent({
  name: "ExternalPreview",
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

    const blocked = ref(false)

    function open() {
      if (!url.value) return
      const win = window.open(url.value, "_blank", "noopener")
      blocked.value = !win
    }

    onMounted(open)

    return () => (
      <div class="ol-external-preview">
        {url.value ? (
          <>
            <div class="ol-preview-hint">{t("home.preview.external_hint")}</div>
            <div class="ol-preview-hint ol-ellipsis">{url.value}</div>
            <ElButton type="primary" onClick={open}>
              <ElIcon style={{ marginRight: "4px" }}>
                <Link />
              </ElIcon>
              {t("home.preview.open_in_new_window")}
            </ElButton>
            {blocked.value && (
              <ElAlert
                type="warning"
                showIcon={true}
                closable={false}
                title={t("home.preview.external_blocked")}
              />
            )}
          </>
        ) : (
          <ElAlert
            type="warning"
            showIcon={true}
            closable={false}
            title={t("home.preview.iframe_missing_scheme")}
          />
        )}
      </div>
    )
  },
})
