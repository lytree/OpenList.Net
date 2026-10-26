import { useStorage } from "@vueuse/core"

/**
 * 文件浏览器的**本地视图偏好**（每台设备各自记忆，不写服务端）。
 *
 * 与旧前端 `store/local_settings.ts` 的对应关系：
 * - `global_default_layout` → `ol-view-mode`（list / grid / image）
 * - `show_sidebar` → `ol-tree-visible`
 *
 * 这些是「纯浏览器偏好」，进 `local_settings` 那种服务端设置没有收益：
 * 服务器不该知道用户在某台机器上喜欢网格还是列表。
 */

export type ViewMode = "list" | "grid" | "image"

export const VIEW_MODES: { value: ViewMode; label: string }[] = [
  { value: "list", label: "列表" },
  { value: "grid", label: "网格" },
  { value: "image", label: "图墙" },
]

export function useViewLayout() {
  const mode = useStorage<ViewMode>("ol-view-mode", "list")
  const treeVisible = useStorage<boolean>("ol-tree-visible", true)
  return { mode, treeVisible }
}
