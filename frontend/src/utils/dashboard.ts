/**
 * 管理台仪表盘的纯函数层（不碰 DOM / 网络，便于单测）。
 *
 * 仪表盘**没有后端聚合端点**（上游同样没有：各页各取一份）。这里的做法是并发拉
 * 7 个已存在的端点，在浏览器里做计数与分组 —— 零新增接口、零后端改动。代价是
 * 「大表整表拉回本地数」，所以每个函数都只产出计数，不把原始列表留在组件状态里。
 *
 * 计数口径都写在函数注释里，别按直觉改成另一种（例如「挂载按驱动分组」只算启用的，
 * 停用的挂载不产生流量，混进去会让分布图与真实负载不符）。
 */
import { UserRole, type AdminUser, type ShareInfo, type StorageWithDriver } from "~/api/types"
import type { MediaScrapeStatus } from "~/api/media"
import { percentOf } from "./mediaCatalog"

export interface UserStats {
  total: number
  /** 角色为 ADMIN 的账号数（含被停用的：停用的管理员仍是管理员，另行用 disabled 表达）。 */
  admins: number
  disabled: number
}

export function userStats(users: readonly AdminUser[]): UserStats {
  let admins = 0
  let disabled = 0
  for (const user of users) {
    if (user.role === UserRole.ADMIN) admins += 1
    if (user.disabled) disabled += 1
  }
  return { total: users.length, admins, disabled }
}

export interface DriverBucket {
  /** 驱动展示名（后端 `driver_name`，拿不到时退回驱动 key）。 */
  name: string
  count: number
}

export interface StorageStats {
  total: number
  enabled: number
  disabled: number
  /** 只统计**启用**的挂载，按驱动分组，数量降序（同数量按名字升序，保证渲染稳定）。 */
  byDriver: DriverBucket[]
}

export function storageStats(storages: readonly StorageWithDriver[]): StorageStats {
  const buckets = new Map<string, number>()
  let disabled = 0
  for (const storage of storages) {
    if (storage.disabled) {
      disabled += 1
      continue
    }
    const name = storage.driver_name || storage.driver
    buckets.set(name, (buckets.get(name) ?? 0) + 1)
  }
  const byDriver = [...buckets.entries()]
    .map(([name, count]) => ({ name, count }))
    .sort((a, b) => b.count - a.count || a.name.localeCompare(b.name))
  return {
    total: storages.length,
    enabled: storages.length - disabled,
    disabled,
    byDriver,
  }
}

export interface ShareStats {
  total: number
  enabled: number
  disabled: number
  /** 累计访问次数（后端 `accessed`，含已失效的分享）。 */
  accessed: number
}

export function shareStats(shares: readonly ShareInfo[]): ShareStats {
  let enabled = 0
  let accessed = 0
  for (const share of shares) {
    if (!share.disabled) enabled += 1
    accessed += share.accessed ?? 0
  }
  return {
    total: shares.length,
    enabled,
    disabled: shares.length - enabled,
    accessed,
  }
}

export interface LibraryHealth {
  libraries: number
  matched: number
  pending: number
  unmatched: number
  /** 正在跑批量刮削的库数。 */
  running: number
  /** 全局匹配率（0-100）：分母是「已匹配 + 待刮削 + 未匹配」，空库不计入。 */
  rate: number
}

export function libraryHealth(rows: readonly MediaScrapeStatus[]): LibraryHealth {
  let matched = 0
  let pending = 0
  let unmatched = 0
  let running = 0
  for (const row of rows) {
    matched += row.matched
    pending += row.pending
    unmatched += row.unmatched
    if (row.running) running += 1
  }
  return {
    libraries: rows.length,
    matched,
    pending,
    unmatched,
    running,
    rate: percentOf(matched, matched + pending + unmatched),
  }
}

/** 单库匹配率：空库返回 0（不是 100），否则「没有任何条目」会被渲染成「全部完成」。 */
export function libraryMatchRate(row: MediaScrapeStatus): number {
  return percentOf(row.matched, row.matched + row.pending + row.unmatched)
}

export type LibraryState = "running" | "failed" | "pending" | "unmatched" | "clean" | "empty"

/**
 * 单库状态。优先级有意如此：
 * - `running` 最高：正在跑的时候下面的计数每轮都在变，先说「在跑」；
 * - `failed` 次之：失败记录会保留到下一轮开始，比「还有待刮削」更需要被看见；
 * - 其余按「还有多少活要干」排序，`clean` 只在三类计数全为 0 且确实有条目时成立。
 */
export function libraryState(row: MediaScrapeStatus): LibraryState {
  if (row.running) return "running"
  if (row.failed > 0 || (row.error ?? "").length > 0) return "failed"
  if (row.pending > 0) return "pending"
  if (row.unmatched > 0) return "unmatched"
  return row.matched > 0 ? "clean" : "empty"
}

/** 状态 → Element Plus Tag 的 type（颜色语义）。 */
export function libraryStateTagType(
  state: LibraryState,
): "primary" | "success" | "warning" | "info" | "danger" {
  switch (state) {
    case "running":
      return "primary"
    case "failed":
      return "danger"
    case "pending":
      return "warning"
    case "unmatched":
      return "info"
    case "clean":
      return "success"
    default:
      return "info"
  }
}
