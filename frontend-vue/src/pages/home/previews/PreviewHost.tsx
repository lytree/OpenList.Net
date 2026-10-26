import { computed, defineComponent, h, onErrorCaptured, ref, watch, type PropType } from "vue"
import { useRoute, useRouter } from "vue-router"
import { ElAlert, ElButton, ElIcon, ElOption, ElSelect, ElTag } from "element-plus"
import { Download, FullScreen, Link, Refresh } from "@element-plus/icons-vue"
import { useSettingsStore } from "~/stores/settings"
import { humanSize, formatTime } from "~/utils/format"
import { downloadLink, rawLink } from "~/utils/link"
import { t } from "~/i18n"
import type { FsGetData } from "~/api/types"
import { parseIframePreviews, resolvePreviews } from "./registry"
import type { PreviewEntry, PreviewProps } from "./types"

/**
 * 预览器宿主。
 *
 * 职责只有四件：
 * 1. 按当前文件算出可用预览器（`registry.resolvePreviews`）；
 * 2. 选中项写进 URL `?preview=`，刷新/分享/前进后退都能回到同一个预览器；
 * 3. 渲染选中组件，并把子组件的渲染错误收敛成一条提示而不是白屏；
 * 4. 提供与预览器无关的通用动作（下载、新窗口打开、全屏）。
 *
 * 具体预览能力全部在各自组件里，宿主不认识任何文件类型。
 */
export default defineComponent({
  name: "PreviewHost",
  props: {
    obj: { type: Object as PropType<FsGetData>, required: true },
    /** 文件完整路径（`obj` 不含路径）。 */
    path: { type: String, required: true },
    provider: { type: String, default: "" },
  },
  setup(props) {
    const route = useRoute()
    const router = useRouter()
    const settings = useSettingsStore()

    const entries = computed<PreviewEntry[]>(() =>
      resolvePreviews(props.obj, {
        path: props.path,
        provider: props.provider,
        iframePreviews: parseIframePreviews(settings.get("iframe_previews")),
        downloadFirst: settings.getBool("preview_download_by_default"),
      }),
    )

    const selectedKey = computed(() => String(route.query.preview ?? ""))
    /**
     * 当前条目：URL 指定的预览器不存在于本文件时（例如切换文件后沿用了
     * 上一个文件的 `?preview=`），退回列表第一项而不是报错。
     */
    const current = computed<PreviewEntry | undefined>(() => {
      const list = entries.value
      return list.find((entry) => entry.key === selectedKey.value) ?? list[0]
    })

    function select(key: string) {
      if (key === current.value?.key) return
      void router.replace({
        path: route.path,
        query: { ...route.query, preview: key },
      })
    }

    // ---- 子组件异常收敛 -------------------------------------------------
    const error = ref("")
    onErrorCaptured((err) => {
      error.value = String((err as Error)?.message ?? err)
      return false
    })
    watch(
      () => current.value?.key,
      () => (error.value = ""),
    )

    // ---- 通用动作 -------------------------------------------------------
    const mediaUrl = computed(() => rawLink(props.path, props.obj.raw_url, props.obj.sign))
    const downloadUrl = computed(() => downloadLink(props.path, props.obj.sign))

    const stageRef = ref<HTMLElement | null>(null)
    const reloadTick = ref(0)
    function toggleFullscreen() {
      const el = stageRef.value
      if (!el) return
      if (document.fullscreenElement) void document.exitFullscreen()
      else void el.requestFullscreen?.()
    }

    function reload() {
      // query 不变时路由不会重新导航，因此用自增 tick 强制重建预览组件
      reloadTick.value += 1
      error.value = ""
    }

    const childProps = computed<PreviewProps & Record<string, unknown>>(() => ({
      ...(current.value?.props ?? {}),
      obj: props.obj,
      path: props.path,
      provider: props.provider,
      // 统一给子组件一个可靠的媒体源，避免每个组件各写一遍兜底逻辑
      src: mediaUrl.value,
      download_url: downloadUrl.value,
    }))

    return () => {
      const entry = current.value
      return (
        <div class="ol-preview">
          <div class="ol-preview-bar">
            <div class="ol-preview-meta">
              <span class="ol-preview-name" title={props.obj.name}>
                {props.obj.name}
              </span>
              <span class="ol-preview-sub">
                {humanSize(props.obj.size)} · {formatTime(props.obj.modified)}
              </span>
              {props.provider && (
                <ElTag size="small" type="info">
                  {props.provider}
                </ElTag>
              )}
            </div>

            <div class="ol-preview-actions">
              {entries.value.length > 1 && (
                <ElSelect
                  modelValue={entry?.key ?? ""}
                  size="small"
                  style={{ width: "180px" }}
                  onUpdate:modelValue={(value: string | number | boolean) => select(String(value))}
                >
                  {entries.value.map((item) => (
                    <ElOption key={item.key} label={item.name} value={item.key} />
                  ))}
                </ElSelect>
              )}
              <ElButton size="small" onClick={reload}>
                <ElIcon>
                  <Refresh />
                </ElIcon>
              </ElButton>
              <ElButton size="small" onClick={() => window.open(mediaUrl.value, "_blank", "noopener")}>
                <ElIcon>
                  <Link />
                </ElIcon>
              </ElButton>
              <ElButton
                size="small"
                onClick={() => {
                  // 用隐藏链接触发浏览器原生下载，避免 window.open 被拦截
                  const a = document.createElement("a")
                  a.href = downloadUrl.value
                  a.download = props.obj.name
                  a.rel = "noopener"
                  document.body.appendChild(a)
                  a.click()
                  a.remove()
                }}
              >
                <ElIcon style={{ marginRight: "4px" }}>
                  <Download />
                </ElIcon>
                {t("home.toolbar.download")}
              </ElButton>
              <ElButton size="small" onClick={toggleFullscreen}>
                <ElIcon>
                  <FullScreen />
                </ElIcon>
              </ElButton>
            </div>
          </div>

          <div class="ol-preview-stage" ref={stageRef}>
            {error.value ? (
              <ElAlert
                type="error"
                showIcon={true}
                title={t("home.preview.failed_to_render", { message: error.value })}
                closable={false}
              />
            ) : entry ? (
              h(entry.component, { key: `${entry.key}:${reloadTick.value}`, ...childProps.value })
            ) : null}
          </div>
        </div>
      )
    }
  },
})
