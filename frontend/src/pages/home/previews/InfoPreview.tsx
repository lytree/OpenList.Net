import { computed, defineComponent, type PropType } from "vue"
import { ElButton, ElDescriptions, ElDescriptionsItem, ElMessage, ElSpace, ElTag } from "element-plus"
import { ObjType } from "~/api/types"
import { humanSize, formatTime } from "~/utils/format"
import { t } from "~/i18n"
import type { PreviewChildProps } from "./types"

const KIND_KEY: Record<number, string> = {
  [ObjType.FOLDER]: "folder",
  [ObjType.VIDEO]: "video",
  [ObjType.AUDIO]: "audio",
  [ObjType.TEXT]: "text",
  [ObjType.IMAGE]: "image",
}

async function copy(text: string) {
  try {
    await navigator.clipboard.writeText(text)
    ElMessage.success(t("global.copied"))
  } catch {
    ElMessage.error(t("global.clipboard_denied"))
  }
}

/** 文件信息：原始详情页（M1 的 Descriptions）搬到这里，作为最稳的兜底视图。 */
export default defineComponent({
  name: "InfoPreview",
  props: {
    obj: { type: Object as PropType<PreviewChildProps["obj"]>, required: true },
    path: { type: String, required: true },
    src: { type: String, required: true },
    download_url: { type: String, required: true },
    provider: { type: String, default: "" },
  },
  setup(props) {
    const kind = computed(() => t(`home.preview.kind.${KIND_KEY[props.obj.type] ?? "unknown"}`))

    return () => (
      <div class="ol-info-preview">
        <ElDescriptions column={1} border={true} size="small">
          <ElDescriptionsItem label={t("home.obj.name")}>{props.obj.name}</ElDescriptionsItem>
          <ElDescriptionsItem label={t("home.preview.kind_label")}>
            <ElTag size="small">{kind.value}</ElTag>
          </ElDescriptionsItem>
          <ElDescriptionsItem label={t("home.obj.size")}>{humanSize(props.obj.size)}</ElDescriptionsItem>
          <ElDescriptionsItem label={t("home.obj.modified")}>
            {formatTime(props.obj.modified)}
          </ElDescriptionsItem>
          {props.obj.created && (
            <ElDescriptionsItem label={t("home.preview.created")}>
              {formatTime(props.obj.created)}
            </ElDescriptionsItem>
          )}
          <ElDescriptionsItem label={t("home.preview.path")}>
            <span class="ol-mono">{props.path}</span>
          </ElDescriptionsItem>
          {props.provider && (
            <ElDescriptionsItem label={t("home.preview.provider")}>{props.provider}</ElDescriptionsItem>
          )}
          <ElDescriptionsItem label={t("home.preview.raw_link")}>
            <ElSpace size={6} wrap={true}>
              <span class="ol-mono ol-ellipsis">{props.src}</span>
              <ElButton size="small" text={true} type="primary" onClick={() => void copy(props.src)}>
                {t("global.copy")}
              </ElButton>
            </ElSpace>
          </ElDescriptionsItem>
          <ElDescriptionsItem label={t("home.preview.download_link")}>
            <ElSpace size={6} wrap={true}>
              <span class="ol-mono ol-ellipsis">{props.download_url}</span>
              <ElButton
                size="small"
                text={true}
                type="primary"
                onClick={() => void copy(props.download_url)}
              >
                {t("global.copy")}
              </ElButton>
            </ElSpace>
          </ElDescriptionsItem>
        </ElDescriptions>
      </div>
    )
  },
})
