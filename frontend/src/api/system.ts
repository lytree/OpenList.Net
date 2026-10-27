import { http } from "./client"
import type { PResp } from "./types"

/**
 * 系统信息 API（`GET /api/admin/system/info`，见 docs §38）。
 *
 * 字段是 **snake_case**：后端这一族端点（`/admin/index/progress`、`/admin/scan/progress`）
 * 都用下划线键，全局 camelCase 策略对已是小写开头的键是空操作，所以能原样出来。
 * 这里按**线上字段名**写类型，不照搬 C# 属性名。
 */

export interface SystemDatabase {
  /** `sqlite` 或 `postgres`（后端已归一，不会再出现 `postgresql`）。 */
  type: string
  /** SQLite 文件绝对路径；Postgres 时为空串。 */
  file: string
  host: string
  port: number
  name: string
}

export interface SystemPaths {
  /** SQLite 文件所在目录；Postgres 时为空串（它没有「数据目录」这个概念）。 */
  data_dir: string
  /** 前端产物目录的绝对路径。 */
  dist_dir: string
  /** `dist/index.html` 的写入时间（≈ 前端构建时间）；取不到为空串。 */
  dist_built_at: string
  temp_dir: string
  /** 文件日志路径；未启用日志时为空串。 */
  log_file: string
  log_enabled: boolean
}

export interface SystemFeatures {
  s3: boolean
  ftp: boolean
  sftp: boolean
  ldap: boolean
  mcp: boolean
  search_index: string
  /** 0 表示不限制。 */
  max_connections: number
}

export interface SystemInfo {
  version: string
  tag: string
  /** 完整 40 位提交 SHA；开发态未注入时为空串。 */
  commit: string
  /** `Debug` / `Release`。 */
  configuration: string
  /** 后端程序集的写入时间（≈ 后端构建时间）；单文件发布取不到时为空串。 */
  built_at: string
  runtime: string
  os: string
  architecture: string
  processor_count: number
  /** 进程启动时间；取不到时为空串（此时 uptime_seconds 为 -1）。 */
  started_at: string
  uptime_seconds: number
  server_time: string
  database: SystemDatabase
  paths: SystemPaths
  features: SystemFeatures
}

export const systemInfo = (): PResp<SystemInfo> =>
  http.get<SystemInfo>("/admin/system/info") as PResp<SystemInfo>
