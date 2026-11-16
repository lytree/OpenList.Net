/** 与后端 (`OpenList.Server`) 的响应信封一致。 */
export interface Resp<T = unknown> {
  code: number
  message: string
  data: T
}

export type PResp<T> = Promise<Resp<T>>

export type EmptyResp = Resp<Record<string, never>>
export type PEmptyResp = Promise<EmptyResp>

export enum ObjType {
  UNKNOWN,
  FOLDER,
  VIDEO,
  AUDIO,
  TEXT,
  IMAGE,
}

export interface Obj {
  name: string
  size: number
  is_dir: boolean
  created: string
  modified: string
  sign?: string
  thumb: string
  type: ObjType
  /** 挂载点容量等详情，仅存储根目录返回。 */
  mount_details?: MountDetails
  /** 前端本地状态：列表选中。 */
  selected?: boolean
}

export interface MountDetails {
  total_space?: number
  used_space?: number
  free_space?: number
}

export type FsListData = {
  content: Obj[]
  total: number
  readme: string
  header: string
  write: boolean
  write_content_bypass: boolean
  provider: string
  direct_upload_tools?: string[]
}

export type FsListResp = Resp<FsListData>

export type FsGetData = Obj & {
  raw_url: string
  readme: string
  header: string
  provider: string
  related: Obj[]
}

export type FsGetResp = Resp<FsGetData>

export type RenameObj = {
  src_name: string
  new_name: string
}

/**
 * 单用户产品：登录即内置管理员，匿名访客的 `id` 为 0。
 * 角色 / 权限位 / BasePath 已从后端移除，这里也不再保留对应字段。
 */
export interface User {
  id: number
  username: string
  password: string
  sso_id: string
  disabled: boolean
  allow_ldap: boolean
}

export type Me = User & { otp: boolean }

// ---------------------------------------------------------------------------
// Admin / Storage / Driver（M2 管理台）
// 字段名以 `OpenList.Infrastructure/Services/StorageService.cs` 为真源：
// DriverInfoItem 序列化为 { name, type, default, options, required, help }；
// DriverInfoConfig 序列化为 snake_case。后端 Create/Update 只读取
// CreateStorageReq 顶层字段 + 原样存储 addition JSON（驱动专属配置）。
// ---------------------------------------------------------------------------

export type DriverItemType = "string" | "text" | "number" | "float" | "bool" | "select"

export interface DriverInfoItem {
  name: string
  type: DriverItemType
  default: string
  /** 逗号分隔的候选值（仅 select 类型使用）。 */
  options: string
  required: boolean
  help: string
}

export interface DriverInfoConfig {
  name: string
  local_sort: boolean
  only_local: boolean
  only_proxy: boolean
  no_cache: boolean
  no_upload: boolean
  need_ms: boolean
  default_root: string
  alert?: string | null
}

export interface DriverInfo {
  common: DriverInfoItem[]
  additional: DriverInfoItem[]
  config: DriverInfoConfig
}

/** `/admin/driver/list` 返回：驱动名 → 驱动信息。 */
export type DriverMap = Record<string, DriverInfo>

export interface StorageWithDriver {
  id: number
  mount_path: string
  driver: string
  driver_name: string
  /** 驱动专属配置的 JSON 字符串。 */
  addition: string
  order: number
  disabled: boolean
  remark?: string | null
  status: string
  order_by?: string | null
  order_direction?: string | null
  extract_folder?: string | null
  web_proxy: boolean
  webdav_policy?: string | null
  enable_sign: boolean
  /** ISO 字符串。 */
  modified: string
}

export interface CreateStorageReq {
  id?: number
  mount_path: string
  driver: string
  /** 驱动专属配置，JSON 字符串（原样存储，由驱动在挂载时反序列化为 ConfigItems）。 */
  addition: string
  order: number
  disabled?: boolean
  remark?: string | null
  order_by?: string | null
  order_direction?: string | null
  extract_folder?: string | null
  web_proxy: boolean
  webdav_policy?: string | null
  enable_sign: boolean
}

export interface PageResp<T> {
  content: T[]
  total: number
}

// ---------------------------------------------------------------------------
// 元数据 / 索引 / 任务（M2 第二批）
// 字段名真源：后端全局 `PropertyNamingPolicy = CamelCase`（Program.cs:83），
// 仅带 `[JsonPropertyName]` 的字段用 snake_case（如 allow_ldap、p_sub）。
// ---------------------------------------------------------------------------

/**
 * 路径元数据（`/admin/meta/list` 的行）。
 * 每个 `xxx_sub` 表示对应规则是否对子目录生效。
 */
export interface Meta {
  id: number
  path: string
  password?: string | null
  /** 密码是否对子目录生效。 */
  p_sub: boolean
  hide?: string | null
  h_sub: boolean
  readme?: string | null
  r_sub: boolean
  header?: string | null
  header_sub: boolean
}

/** `/admin/index/progress`（后端把 IndexProgress 压成 4 个字段）。 */
export interface IndexProgress {
  obj_count: number
  is_done: boolean
  last_done_time?: string
  error?: string
}

export interface IndexBuildReq {
  paths: string[]
  max_depth: number
}

/** `/admin/scan/progress`。 */
export interface ScanProgress {
  obj_count: number
  is_done: boolean
}

export interface ScanStartReq {
  path?: string
  /** 扫描速率（文件/秒，0 = 不限）；后端当前忽略，仅为兼容保留。 */
  limit?: number
}

/**
 * 任务（`/api/task/{type}/{done}` 的行）。
 * `state` 是数值枚举（0 Pending … 9 PreparingRetry），`status` 是后端给的小写文案。
 */
export interface TaskInfo {
  id: string
  type: number
  name: string
  state: number
  status: string
  progress: number
  total_bytes: number
  start_time?: string | null
  end_time?: string | null
  error?: string | null
  creator: string
}

// ---------------------------------------------------------------------------
// 站点设置（M2 · settings）
// 字段名真源：`OpenList.Core/Models/SettingItem.cs`。`type` 与 `flag` 都是后端
// 自定义 JSON 转换器输出的小写字符串 / 数字（见 SettingValueTypeConverter）。
// ---------------------------------------------------------------------------

export type SettingValueType =
  | "string"
  | "number"
  | "bool"
  | "select"
  | "text"
  | "float"
  | "multipath"

/** 可见性标志（`SettingFlag`）：0 公开 / 1 私有 / 2 只读 / 3 已废弃。 */
export const SettingFlag = {
  PUBLIC: 0,
  PRIVATE: 1,
  READONLY: 2,
  DEPRECATED: 3,
} as const

export interface SettingItem {
  key: string
  /** 一律以字符串存储；bool 为 "true"/"false"，number/float 为数字字面量。 */
  value: string
  help: string | null
  type: SettingValueType
  flag: number
  /** select 的候选值，逗号分隔。 */
  options: string | null
  group: number
}

/**
 * 分组编号 → 文案键。
 * 真源 `SettingGroup`：0=Single（驱动专用临时目录，不在设置页展示）、
 * 1=Site、2=Style、3=Preview、4=Global、5=Aria2、6=Index、7=SSO、8=LDAP、
 * 9=S3、10=FTP、11=Traffic、12=Media、13=Access、14=Thumbnail。
 */
export const SettingGroups: { group: number; labelKey: string }[] = [
  { group: 1, labelKey: "settings.groups.site" },
  { group: 2, labelKey: "settings.groups.style" },
  { group: 3, labelKey: "settings.groups.preview" },
  { group: 4, labelKey: "settings.groups.global" },
  { group: 5, labelKey: "settings.groups.aria2" },
  { group: 6, labelKey: "settings.groups.index" },
  { group: 7, labelKey: "settings.groups.sso" },
  { group: 8, labelKey: "settings.groups.ldap" },
  { group: 9, labelKey: "settings.groups.s3" },
  { group: 10, labelKey: "settings.groups.ftp" },
  { group: 11, labelKey: "settings.groups.traffic" },
  { group: 12, labelKey: "settings.groups.media" },
  // 访问控制：媒体 / 首页各自的访客开关（设置项 media_guest_access / home_guest_access）。
  // 放在最后而不是塞进「站点」，是因为它描述的是「两个门面各自的准入」，不是站点外观。
  { group: 13, labelKey: "settings.groups.access" },
  // 缩略图服务：/p?type=thumb（总开关、边长、质量、缓存、ffmpeg 路径）。
  // 单独一组而不是塞进「预览」：它管的是「列表用什么图」，与「点开怎么预览」是两件事。
  { group: 14, labelKey: "settings.groups.thumbnails" },
]
