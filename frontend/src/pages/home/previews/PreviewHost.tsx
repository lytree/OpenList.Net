import { computed, defineComponent, h, onErrorCaptured, ref, watch, type PropType } from "vue"
import { useRoute, useRouter } from "vue-router"
import { ElAlert, ElButton, ElIcon, ElOption, ElSelect, ElTag } from "element-plus"
import { Download, FullScreen, Link, Refresh } from "@element-plus/icons-vue"
import { useSettingsStore } from "~/stores/settings"
import { humanSize, formatTime } from "~/utils/format"
import { downloadLink, rawLink } from "~/utils/link"
import { pathDir, pathJoin } from "~/utils/path"
import { fsList } from "~/api/fs"
import { ObjType, type FsGetData, type Obj } from "~/api/types"
import { t } from "~/i18n"
import {
  guessObjType,
  parseExternalPreviews,
  parseExtList,
  parseIframePreviews,
  resolvePreviews,
  type TypeExtMap,
} from "./registry"
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

    /** 四类可配置扩展名（`audio_types` 等）：配了就整表替换内置表。 */
    const typeExts = computed<TypeExtMap>(() => ({
      audio: parseExtList(settings.get("audio_types")),
      video: parseExtList(settings.get("video_types")),
      image: parseExtList(settings.get("image_types")),
      text: parseExtList(settings.get("text_types")),
    }))

    const entries = computed<PreviewEntry[]>(() =>
      resolvePreviews(props.obj, {
        path: props.path,
        provider: props.provider,
        iframePreviews: parseIframePreviews(settings.get("iframe_previews")),
        externalPreviews: parseExternalPreviews(settings.get("external_previews")),
        typeExts: typeExts.value,
        downloadFirst: settings.getBool("preview_download_by_default"),
        archiveDefault: settings.getBool("preview_archives_by_default"),
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

    // ---- 同目录播放列表（图片 / 视频的「上一张 / 下一张」） ------------------
    /** 当前预览器是否属于可连播的媒体类型；其余预览器不拉目录列表。 */
    const mediaKind = computed<"image" | "video" | null>(() => {
      const key = current.value?.key
      if (key === "image") return "image"
      if (key === "video") return "video"
      return null
    })

    /** 父目录条目：视频的 `related` 只覆盖同前缀兄弟，切集要的是整个目录。 */
    const siblingObjs = ref<Obj[]>([])
    let siblingSeq = 0

    watch(
      [() => pathDir(props.path), mediaKind],
      async ([dir, kind]) => {
        const seq = ++siblingSeq
        siblingObjs.value = []
        if (!kind) return
        const resp = await fsList(dir)
        // 换目录/换预览器后旧响应不再写回，避免列表串台
        if (seq !== siblingSeq || resp.code !== 200) return
        siblingObjs.value = resp.data.content ?? []
      },
      { immediate: true },
    )

    /** 判定条目是否属于当前媒体类型：后端类型与可配置扩展名任一命中即可。 */
    function isMediaKind(obj: Obj, kind: "image" | "video"): boolean {
      if (obj.is_dir) return false
      const type = kind === "image" ? ObjType.IMAGE : ObjType.VIDEO
      return obj.type === type || guessObjType(obj.name, typeExts.value) === type
    }

    /** 播放列表：同目录同类型文件 + 当前文件（列表还没回来时至少保证自身在内）。 */
    const playlist = computed<Obj[]>(() => {
      const kind = mediaKind.value
      if (!kind) return []
      const items = siblingObjs.value.filter((item) => isMediaKind(item, kind))
      if (!items.some((item) => item.name === props.obj.name)) {
        if (!isMediaKind(props.obj, kind)) return []
        items.push(props.obj)
      }
      return items.sort((a, b) => a.name.localeCompare(b.name, "zh-CN", { numeric: true }))
    })

    /** 播放列表导航：改 URL 的 `path`，与视频播放器原来的切集方式一致。 */
    function playlistPlay(index: number) {
      const item = playlist.value[index]
      if (!item) return
      void router.replace({
        path: route.path,
        query: {
          ...route.query,
          path: pathJoin(pathDir(props.path), item.name),
          // 保持当前预览器；目标文件不支持它时，宿主会自动退回列表第一项
          preview: current.value?.key ?? "video",
        },
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

    const childProps = computed<PreviewProps & Record<string, unknown>>(() => {
      const base = {
        ...(current.value?.props ?? {}),
        obj: props.obj,
        path: props.path,
        provider: props.provider,
        // 统一给子组件一个可靠的媒体源，避免每个组件各写一遍兜底逻辑
        src: mediaUrl.value,
        download_url: downloadUrl.value,
        // 归档预览据此判断条目类型，与主列表保持同一套可配置扩展名
        typeExts: typeExts.value,
      }
      // 播放列表只注入给认这两个 prop 的图片/视频预览器，
      // 否则会作为普通 attrs 落到其它预览器根节点上
      if (!mediaKind.value) return base
      return { ...base, playlist: playlist.value, playlistPlay }
    })

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
