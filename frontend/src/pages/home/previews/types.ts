import type { Component } from "vue"
import type { FsGetData, ObjType, Resp } from "~/api/types"

/**
 * 预览器契约。
 *
 * 与旧 SolidJS 前端的 `Preview` 结构对齐（key/type/exts/provider/component），
 * 差异只有两点：
 * 1. **排序用显式 `order`**，旧版用「prior 布尔 + 两个数组」的隐式顺序，难读；
 * 2. **匹配上下文带 `size`**，因为「大文本 / 未知小文件」的分支需要文件大小。
 */

/** 所有预览器组件统一接收的 props。 */
export interface PreviewProps {
  /** `fs/get` 的完整响应：name/size/modified/type/raw_url/related… */
  obj: FsGetData
  /** 文件完整路径。`obj` 本身不含路径，/d 直链兜底时需要。 */
  path: string
  /** 当前挂载的驱动名（如 `Aliyundrive`），用于 provider 级匹配。 */
  provider: string
}

export interface PreviewContext {
  name: string
  type: ObjType
  size: number
  provider: string
  path: string
}

/** 四类「扩展名由设置决定」的类型，对应 `audio_types` / `video_types` / `image_types` / `text_types`。 */
export type TypeExtKey = "audio" | "video" | "image" | "text"

export interface PreviewDef {
  /** 稳定标识，同时是 URL `?preview=` 的取值与 i18n `home.preview.names.*` 的后缀。 */
  key: string
  component: Component
  /** 命中的 ObjType（后端已按扩展名分类，优先用它而不是自己维护扩展名表）。 */
  type?: ObjType
  /** 扩展名（不含点，小写）。用于后端分类不到的补充类型（pdf/md/html…）。 */
  exts?: string[]
  /**
   * 扩展名改由站点设置提供：配置了对应键时**整表替换** `exts`。
   *
   * 只给四类媒体/文本用 —— 它们在上游就是可配置的（管理员可能想用自己的
   * `.m4b`/`.avif` 列表），而 pdf/md/docx 这些是「预览器能力」而不是「类型定义」，
   * 扩展名写死才不会因为设置漏项导致预览消失。
   */
  extSettingsKey?: TypeExtKey
  /** 驱动名正则，命中才出现（如阿里云盘专用播放器）。 */
  provider?: RegExp
  /** 自定义匹配，优先级最高；返回 true 即命中。 */
  match?: (ctx: PreviewContext) => boolean
  /** 越小越靠前，默认 100。 */
  order?: number
  /** 运行时可用性（如设置开关、权限）。返回 false 则不出现在列表里。 */
  available?: (ctx: PreviewContext) => boolean
}

export interface PreviewEntry {
  key: string
  /** 已翻译的显示名。 */
  name: string
  component: Component
  order: number
  /** 额外透传给组件的 props（如 iframe 预览的 URL 模板）。 */
  props?: Record<string, unknown>
}

/** 归档内条目（`/fs/archive/list` 的 `content` 元素）。 */
export interface ArchiveListEntry {
  name: string
  size: number
  is_dir: boolean
  modified: string
}

/**
 * 归档数据源。
 *
 * 默认由 `ArchivePreview` 直接调登录态接口；分享页把它换成「带分享码」的实现，
 * 否则游客请求 `/fs/archive/list` 只会 401。
 */
export type ArchiveListFn = (
  path: string,
  archivePass: string,
) => Promise<Resp<{ content: ArchiveListEntry[]; total: number }>>

/**
 * 宿主动态注入给预览器组件的 props。
 *
 * `src` / `download_url` 由宿主统一算好（`raw_url` 优先、`/d` 兜底），
 * 子组件不再各自实现一遍地址兜底，避免出现「某个预览器能播、另一个 404」。
 */
export interface PreviewChildProps extends PreviewProps {
  src: string
  download_url: string
  /** 仅 iframe 预览使用：外部预览站的 URL 模板。 */
  scheme?: string
  /** 仅归档预览使用：数据源（分享页注入带分享码的版本）。 */
  listArchive?: ArchiveListFn
  /**
   * 只读上下文（分享页）：隐藏解压按钮，归档内条目也不做内联预览
   * （`/ae` 需要登录态签名，游客拿不到）。
   */
  readOnly?: boolean
}
