import { computed, defineComponent, type PropType } from "vue"
import { ElCheckbox, ElIcon } from "element-plus"
import {
  Document,
  FolderOpened,
  Headset,
  Picture,
  VideoCamera,
} from "@element-plus/icons-vue"
import { ObjType, type Obj } from "~/api/types"
import { humanSize } from "~/utils/format"
import { downloadLink } from "~/utils/link"
import { pathJoin } from "~/utils/path"

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
 * 缩略图来源：后端当前不下发 `thumb`（`Obj.thumb` 恒为空），所以图片直接用
 * `/d{path}` 直链。`loading="lazy"` 让浏览器只解码视口内的图，
 * 是大目录下最省事也最有效的优化；真正的缩略图服务（`/p?type=thumb`）留在 M4。
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

    function thumbSrc(obj: Obj): string {
      if (obj.is_dir) return ""
      if (obj.thumb) return obj.thumb
      if (obj.type === ObjType.IMAGE) {
        return downloadLink(pathJoin(props.path, obj.name), obj.sign)
      }
      return ""
    }

    function fallbackIcon(obj: Obj) {
      if (obj.is_dir) return FolderOpened
      switch (obj.type) {
        case ObjType.IMAGE:
          return Picture
        case ObjType.VIDEO:
          return VideoCamera
        case ObjType.AUDIO:
          return Headset
        default:
          return Document
      }
    }

    function fallbackColor(obj: Obj): string {
      if (obj.is_dir) return "#e6a23c"
      switch (obj.type) {
        case ObjType.IMAGE:
          return "#909399"
        case ObjType.VIDEO:
          return "#409eff"
        case ObjType.AUDIO:
          return "#67c23a"
        default:
          return "#909399"
      }
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
                    <img src={src} alt={obj.name} loading="lazy" decoding="async" />
                  ) : (
                    <ElIcon size={props.mode === "image" ? 44 : 34} color={fallbackColor(obj)}>
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
