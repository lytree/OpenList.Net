import { Document, FolderOpened, Headset, Picture, VideoCamera } from "@element-plus/icons-vue"
import { objKind } from "~/composables/useFileList"
import type { Obj } from "~/api/types"

/**
 * 文件条目的「种类 → 图标 / 颜色」映射。
 *
 * 列表（`index.tsx`）、网格（`GridView.tsx`）、手机卡片行（`ListRows.tsx`）三个视图都要画
 * 同一枚图标，此前各自写了一份，颜色还不完全一致（目录/文本两处有偏差）。这里收成唯一来源，
 * 新增视图只 import 不复制。
 *
 * 颜色刻意不走主题变量：它表达的是**文件类型**（目录橙、视频蓝、音频绿…），
 * 深浅色下要一致，跟随主题反而会让「视频蓝」在暗色下变淡、失去区分度。
 */

export type FileKind = ReturnType<typeof objKind>

/**
 * 图标组件类型。element-plus 的图标是 SFC 产物，用其中一个的具体类型做代表，
 * 这样返回值仍然可以当 JSX 标签用（标成 `Component` 会失去可调用签名）。
 */
export type FileKindIcon = typeof Document

export const KIND_ICON: Record<FileKind, FileKindIcon> = {
  folder: FolderOpened,
  video: VideoCamera,
  audio: Headset,
  image: Picture,
  text: Document,
  other: Document,
}

export const KIND_COLOR: Record<FileKind, string> = {
  folder: "#e6a23c",
  video: "#409eff",
  audio: "#67c23a",
  image: "#909399",
  // 文本用中性灰而不是更深的 #606266：深色主题下 #606266 几乎看不见，而图标颜色不跟主题走。
  text: "#909399",
  other: "#909399",
}

/** 条目的种类（目录 / 视频 / 音频 / 图片 / 文本 / 其他）。 */
export function kindOf(obj: Obj): FileKind {
  return objKind(obj)
}

/** 条目对应的图标组件。 */
export function kindIcon(obj: Obj): FileKindIcon {
  return KIND_ICON[kindOf(obj)]
}

/** 条目对应的图标颜色。 */
export function kindColor(obj: Obj): string {
  return KIND_COLOR[kindOf(obj)]
}
