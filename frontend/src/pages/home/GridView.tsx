import { computed, defineComponent, ref, type PropType } from "vue"
import { ElCheckbox, ElIcon } from "element-plus"
import { ObjType, type Obj } from "~/api/types"
import { humanSize } from "~/utils/format"
import { downloadLink } from "~/utils/link"
import { pathJoin } from "~/utils/path"
import { kindColor, kindIcon } from "~/pages/home/kind"

/**
 * 网格 / 图墙视图。
 *
 * 交互约定（与列表保持一致，避免两套肌肉记忆）：
 * - **单击卡片 = 打开**（目录进入、文件进预览页），与列表行点名称一致；
 * - **勾选框 = 选中**，仅在 hover 或已选中时显示，点它不会触发打开；
 * - 右键 = 与列表同一套菜单（父组件传 `onContextmenu`）。
 *
 * `image` 模式只渲染目录与图片（旧前端的「图墙」），并按 `show_folder_in_image_view`
 * 的默认值把目录排在最前 —— 目录恒在前本来就是 `useFileList` 的排序规则，
 * 这里不再重复处理。
 *
 * 缩略图来源：优先用后端下发的 `obj.thumb`（`/p?type=thumb`，见
 * `docs/thumbnail-service-plan.md`）。后端默认**关闭**缩略图服务（`thumb_enable=false`），
 * 此时 `thumb` 为空、退回 `/d{path}` 原图直链 —— 行为与升级前完全一致。
 * 打开后卡片只拉 320px 的小图，不再为了一个 320 的格子下载 8MB 的 JPEG；
 * 同时按 1x/2x 下发 `srcset`，高 DPI 屏不糊。
 */
export default defineComponent({
  name: "FileGridView",
  props: {
    objs: { type: Array as PropType<Obj[]>, default: () => [] },
    mode: { type: String as PropType<"grid" | "image">, default: "grid" },
    /** 已选中的文件名列表（父组件的唯一真源）。 */
    selected: { type: Array as PropType<string[]>, default: () => [] },
    path: { type: String, default: "/" },
    selectable: { type: Boolean, default: true },
  },
  emits: {
    open: (_obj: Obj) => true,
    toggle: (_obj: Obj, _checked: boolean) => true,
    contextmenu: (_obj: Obj, _ev: MouseEvent) => true,
  },
  setup(props, { emit }) {
    const visible = computed(() =>
      props.mode === "image"
        ? props.objs.filter((obj) => obj.is_dir || obj.type === ObjType.IMAGE)
        : props.objs,
    )

    // 两级降级：缩略图失败 → 原图；原图也失败 → 图标。
    // 之所以不直接退图标：开启缩略图但服务端没装 ffmpeg 时，卡片会全变成图标，
    // 那比"多下一个原图"糟糕得多。
    const thumbFailed = ref(new Set<string>())
    const imageFailed = ref(new Set<string>())

    function thumbSrc(obj: Obj): string {
      if (obj.is_dir) return ""
      if (obj.thumb && !thumbFailed.value.has(obj.name)) return obj.thumb
      if (obj.type === ObjType.IMAGE && !imageFailed.value.has(obj.name)) {
        return downloadLink(pathJoin(props.path, obj.name), obj.sign)
      }
      return ""
    }

    /**
     * 由 1x 的 thumb URL 派生 2x 档位（`srcset`）。
     *
     * 走 URL API 改 `w`/`h` 而不是字符串拼接：`thumb` 可能是绝对地址（反代部署），
     * 也可能带 `&sign=`，拼字符串容易把 query 弄坏。签名只覆盖路径，改尺寸不影响校验。
     * 2x 上限 1024（后端夹取范围），否则高 DPI 屏会请求超出解码上限的尺寸。
     */
    function thumbSrcSet(thumb: string): string | undefined {
      try {
        const url = new URL(thumb, window.location.origin)
        const edge = Number(url.searchParams.get("w")) || 320
        const hi = Math.min(1024, edge * 2)
        if (hi <= edge) return undefined
        const hiUrl = new URL(url)
        hiUrl.searchParams.set("w", String(hi))
        hiUrl.searchParams.set("h", String(hi))
        return `${url.toString()} 1x, ${hiUrl.toString()} 2x`
      } catch {
        return undefined
      }
    }

    function fallbackIcon(obj: Obj) {
      return kindIcon(obj)
    }

    /** 记录一条加载失败的图：先淘汰缩略图，再淘汰原图，第三次才交给图标。 */
    function onImageError(obj: Obj) {
      if (obj.thumb && !thumbFailed.value.has(obj.name)) {
        thumbFailed.value = new Set([...thumbFailed.value, obj.name])
        return
      }
      imageFailed.value = new Set([...imageFailed.value, obj.name])
    }

    return () => {
      if (visible.value.length === 0) {
        return <div class="ol-grid-empty">此目录没有可显示的图片</div>
      }
      return (
        <div class={props.mode === "image" ? "ol-grid ol-grid-image" : "ol-grid"}>
          {visible.value.map((obj) => {
            const checked = props.selected.includes(obj.name)
            const src = thumbSrc(obj)
            const srcset = obj.thumb && src === obj.thumb ? thumbSrcSet(obj.thumb) : undefined
            const Icon = fallbackIcon(obj)
            return (
              <div
                key={obj.name}
                class={{ "ol-grid-card": true, "is-selected": checked }}
                title={obj.name}
                onClick={() => emit("open", obj)}
                onContextmenu={(ev: MouseEvent) => {
                  ev.preventDefault()
                  ev.stopPropagation()
                  emit("contextmenu", obj, ev)
                }}
              >
                <div class="ol-grid-thumb">
                  {src ? (
                    <img
                      src={src}
                      srcset={srcset}
                      alt={obj.name}
                      loading="lazy"
                      decoding="async"
                      onError={() => onImageError(obj)}
                    />
                  ) : (
                    <ElIcon size={props.mode === "image" ? 44 : 34} color={kindColor(obj)}>
                      <Icon />
                    </ElIcon>
                  )}
                  {props.selectable && (
                    <span class="ol-grid-check" onClick={(ev: MouseEvent) => ev.stopPropagation()}>
                      <ElCheckbox
                        modelValue={checked}
                        onChange={(v: string | number | boolean) => emit("toggle", obj, Boolean(v))}
                      />
                    </span>
                  )}
                  {!obj.is_dir && props.mode === "grid" && (
                    <span class="ol-grid-badge">{humanSize(obj.size)}</span>
                  )}
                </div>
                <div class="ol-grid-name">{obj.name}</div>
              </div>
            )
          })}
        </div>
      )
    }
  },
})
