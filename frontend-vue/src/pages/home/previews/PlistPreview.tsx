import { defineComponent, ref, type PropType } from "vue"
import { ElAlert, ElButton, ElDescriptions, ElDescriptionsItem, ElIcon } from "element-plus"
import { Iphone } from "@element-plus/icons-vue"
import { t } from "~/i18n"
import type { PreviewChildProps } from "./types"

/**
 * `.plist` 预览：iOS OTA 安装入口。
 *
 * `itms-services://` 只能由 iOS 的 Safari 处理，其它平台点了没反应 ——
 * 所以这里同时给出安装按钮与一条说明，而不是假装能装。
 * 地址用宿主准备好的 `src`（`raw_url`，已是绝对地址）。
 */
export default defineComponent({
  name: "PlistPreview",
  props: {
    obj: { type: Object as PropType<PreviewChildProps["obj"]>, required: true },
    src: { type: String, required: true },
  },
  setup(props) {
    const installing = ref(false)
    const installUrl = () => `itms-services://?action=download-manifest&url=${encodeURIComponent(props.src)}`

    return () => (
      <div class="ol-install-preview">
        <ElAlert
          type="info"
          showIcon={true}
          closable={false}
          title={t("home.preview.install_ios_only")}
        />
        <ElDescriptions column={1} size="small" border={true}>
          <ElDescriptionsItem label={t("home.preview.kind_label")}>plist</ElDescriptionsItem>
          <ElDescriptionsItem label={t("home.preview.path")}>{props.obj.name}</ElDescriptionsItem>
        </ElDescriptions>
        <div>
          <ElButton
            type="primary"
            onClick={() => {
              installing.value = true
              window.location.href = installUrl()
            }}
          >
            <ElIcon style={{ marginRight: "4px" }}>
              <Iphone />
            </ElIcon>
            {t(`home.preview.${installing.value ? "installing" : "install"}`)}
          </ElButton>
        </div>
      </div>
    )
  },
})
