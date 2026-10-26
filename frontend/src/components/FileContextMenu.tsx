import { defineComponent, nextTick, onBeforeUnmount, ref, watch, type PropType, Teleport } from "vue"

/** 右键菜单条目；hidden 项直接不渲染（对齐 solid-contextmenu 的 hidden 语义）。 */
export interface ContextMenuItem {
  key: string
  label: string
  danger?: boolean
  divided?: boolean
  disabled?: boolean
  handler?: () => void
}

/**
 * 轻量右键菜单（M1）。
 *
 * 旧前端用 solid-contextmenu（SolidJS 专属，不可移植），这里自实现：
 * Teleport 到 body 的 fixed 定位面板 + 三种关闭路径（外点/Esc/选项点击）。
 * 越界翻转：靠近右/下边缘时向内收，避免菜单被视口裁掉。
 */
export default defineComponent({
  name: "FileContextMenu",
  props: {
    visible: { type: Boolean, required: true },
    x: { type: Number, required: true },
    y: { type: Number, required: true },
    items: { type: Array as PropType<ContextMenuItem[]>, required: true },
  },
  emits: ["update:visible"],
  setup(props, { emit }) {
    const menuRef = ref<HTMLElement | null>(null)
    /** 翻转后的实际渲染坐标。 */
    const pos = ref({ x: props.x, y: props.y })

    watch(
      () => [props.visible, props.x, props.y] as const,
      async ([visible]) => {
        if (!visible) return
        pos.value = { x: props.x, y: props.y }
        await nextTick()
        const el = menuRef.value
        if (!el) return
        const { innerWidth, innerHeight } = window
        const { offsetWidth, offsetHeight } = el
        if (pos.value.x + offsetWidth > innerWidth - 8) pos.value.x = Math.max(8, innerWidth - offsetWidth - 8)
        if (pos.value.y + offsetHeight > innerHeight - 8) pos.value.y = Math.max(8, innerHeight - offsetHeight - 8)
      },
      { immediate: true },
    )

    function close() {
      emit("update:visible", false)
    }

    function onDocMousedown(e: MouseEvent) {
      if (!props.visible) return
      if (menuRef.value && !menuRef.value.contains(e.target as Node)) close()
    }
    function onDocKeydown(e: KeyboardEvent) {
      if (props.visible && e.key === "Escape") close()
    }
    document.addEventListener("mousedown", onDocMousedown, true)
    document.addEventListener("keydown", onDocKeydown)
    onBeforeUnmount(() => {
      document.removeEventListener("mousedown", onDocMousedown, true)
      document.removeEventListener("keydown", onDocKeydown)
    })

    return () => (
      <Teleport to="body">
        {props.visible && (
          <div
            ref={menuRef}
            class="ol-ctx-menu"
            style={{ left: `${pos.value.x}px`, top: `${pos.value.y}px` }}
            onContextmenu={(e: MouseEvent) => e.preventDefault()}
          >
            {props.items.map((item) => (
              <div
                key={item.key}
                class={{
                  "ol-ctx-item": true,
                  "ol-ctx-danger": !!item.danger,
                  "ol-ctx-divided": !!item.divided,
                  "ol-ctx-disabled": !!item.disabled,
                }}
                onClick={() => {
                  if (item.disabled) return
                  close()
                  item.handler?.()
                }}
              >
                {item.label}
              </div>
            ))}
          </div>
        )}
      </Teleport>
    )
  },
})
