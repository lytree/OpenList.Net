import { defineComponent, ref, type PropType } from "vue"
import { ElAlert, ElButton, ElDescriptions, ElDescriptionsItem, ElIcon } from "element-plus"
import { Iphone } from "@element-plus/icons-vue"
import { t } from "~/i18n"
import type { PreviewChildProps } from "./types"

/**
 * `.ipa` 预览：iOS 在线安装。
 *
 * 两条路径，覆盖两类设备：
 * 1. **Safari OTA**：后端 `/i/{linkName}` 现生成 Apple plist manifest
 *    （linkName = base64(urlEncoded(rawUrl) + "/" + urlEncoded(fileName))），
 *    再交给 `itms-services://` 触发安装；
 * 2. **TrollStore**：`apple-magnifier://install?url=<直链>`，免签名安装。
 *
 * 这里必须用**绝对** `src`（`raw_url`）：plist 内嵌的就是这个地址，
 * 相对路径 iOS 无法回源。
 */
export default defineComponent({
  name: "IpaPreview",
  props: {
    obj: { type: Object as PropType<PreviewChildProps["obj"]>, required: true },
    src: { type: String, required: true },
  },
  setup(props) {
    const installing = ref(false)
    const trInstalling = ref(false)

    /** 与后端 `MiscEndpoints.BuildPlist` 的约定一致：base64(编码后的 url + "/" + 编码后的文件名)。 */
    function manifestUrl(): string {
      const raw = `${encodeURIComponent(props.src)}/${encodeURIComponent(props.obj.name)}`
      return `${location.origin}/i/${btoa(raw)}.plist`
    }

    return () => (
      <div class="ol-install-preview">
        <ElAlert
          type="info"
          showIcon={true}
          closable={false}
          title={t("home.preview.install_ios_only")}
        />
        <ElDescriptions column={1} size="small" border={true}>
          <ElDescriptionsItem label={t("home.preview.kind_label")}>ipa</ElDescriptionsItem>
          <ElDescriptionsItem label={t("home.preview.path")}>{props.obj.name}</ElDescriptionsItem>
        </ElDescriptions>
        <div class="ol-install-actions">
          <ElButton
            type="primary"
            onClick={() => {
              installing.value = true
              window.location.href = `itms-services://?action=download-manifest&url=${encodeURIComponent(manifestUrl())}`
            }}
          >
            <ElIcon style={{ marginRight: "4px" }}>
              <Iphone />
            </ElIcon>
            {t(`home.preview.${installing.value ? "installing" : "install"}`)}
          </ElButton>
          <ElButton
            onClick={() => {
              trInstalling.value = true
              window.location.href = `apple-magnifier://install?url=${encodeURIComponent(props.src)}`
            }}
          >
            {t(`home.preview.${trInstalling.value ? "tr-installing" : "tr-install"}`)}
          </ElButton>
        </div>
      </div>
    )
  },
})
