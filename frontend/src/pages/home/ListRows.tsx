import { defineComponent, type PropType } from "vue"
import { ElCheckbox, ElDropdown, ElDropdownItem, ElDropdownMenu, ElIcon } from "element-plus"
import { MoreFilled } from "@element-plus/icons-vue"
import type { Obj } from "~/api/types"
import { formatTime, humanSize } from "~/utils/format"
import { kindColor, kindIcon } from "~/pages/home/kind"
import type { RowAction } from "~/pages/home/rowActions"

/**
 * 手机端的文件列表（卡片行）。
 *
 * 为什么不用表格：列表模式的四列（名称/大小/时间/操作）在 390px 宽下必然横向溢出，
 * 表格内部滚动在触屏上既难发现也难操作。手机上改成「图标 + 名称 + 一行副信息 + 更多」，
 * 信息密度接近，全部可见，且每行高度撑到 48px 以上（触控目标下限）。
 *
 * 交互与表格严格对齐，保证切换视图不改肌肉记忆：
 * - 点行 = 打开（目录进入、文件进预览）；
 * - 左侧勾选框 = 选中，点它不会触发行点击；只有可写时才出现（与表格的 selection 列同条件）；
 * - 「更多」= 表格最后一列的下拉菜单，菜单项由父组件 `actions` 提供（唯一来源）；
 * - 长按/右键 = 与表格同一套菜单（父组件传 `contextmenu`）。
 */
export default defineComponent({
  name: "FileListRows",
  props: {
    objs: { type: Array as PropType<Obj[]>, default: () => [] },
    /** 已选中的文件名（父组件的 `ops.selected` 是唯一真源）。 */
    selected: { type: Array as PropType<string[]>, default: () => [] },
    selectable: { type: Boolean, default: true },
    /** 行内菜单项。父组件按对象类型与权限算好后传入。 */
    actions: { type: Function as PropType<(obj: Obj) => RowAction[]>, required: true },
  },
  emits: {
    open: (_obj: Obj) => true,
    toggle: (_obj: Obj, _checked: boolean) => true,
    contextmenu: (_obj: Obj, _ev: MouseEvent) => true,
    action: (_command: string, _obj: Obj) => true,
  },
  setup(props, { emit }) {
    return () => (
      <div class="ol-rowlist">
        {props.objs.map((obj) => {
          const checked = props.selected.includes(obj.name)
          const Icon = kindIcon(obj)
          return (
            <div
              key={obj.name}
              class={{ "ol-row": true, "is-selected": checked }}
              onClick={() => emit("open", obj)}
              onContextmenu={(ev: MouseEvent) => {
                ev.preventDefault()
                ev.stopPropagation()
                emit("contextmenu", obj, ev)
              }}
            >
              {props.selectable && (
                <span class="ol-row-check" onClick={(ev: MouseEvent) => ev.stopPropagation()}>
                  <ElCheckbox
                    modelValue={checked}
                    onChange={(v: string | number | boolean) => emit("toggle", obj, Boolean(v))}
                  />
                </span>
              )}
              <ElIcon class="ol-row-icon" size={20} color={kindColor(obj)}>
                <Icon />
              </ElIcon>
              <div class="ol-row-main">
                <div class="ol-row-name">{obj.name}</div>
                <div class="ol-row-meta">
                  <span>{obj.is_dir ? "文件夹" : humanSize(obj.size)}</span>
                  <span class="ol-row-dot">·</span>
                  <span>{formatTime(obj.modified)}</span>
                </div>
              </div>
              <ElDropdown
                trigger="click"
                onCommand={(command: string) => emit("action", command, obj)}
                v-slots={{
                  default: () => (
                    <span
                      class="ol-row-more"
                      onClick={(ev: MouseEvent) => ev.stopPropagation()}
                    >
                      <ElIcon size={16}>
                        <MoreFilled />
                      </ElIcon>
                    </span>
                  ),
                  dropdown: () => (
                    <ElDropdownMenu>
                      {props.actions(obj).map((item) => (
                        <ElDropdownItem
                          key={item.command}
                          command={item.command}
                          divided={item.divided}
                          class={item.danger ? "is-danger" : undefined}
                        >
                          {item.label}
                        </ElDropdownItem>
                      ))}
                    </ElDropdownMenu>
                  ),
                }}
              />
            </div>
          )
        })}
      </div>
    )
  },
})
