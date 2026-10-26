/**
 * 媒体库管理台的纯函数层（不碰 DOM / 网络，便于单测）。
 *
 * 扫描状态取值来自后端 `MediaScanProgress.State` / `MediaScanTask.State`：
 * idle / running / completed / failed / cancelled（认不出的按 idle 处理，
 * 与后端 `ApplySort` 认不出排序字段按 title 处理的口径一致）。
 */
import type { MediaScanLog, MediaScanTask, MediaScrapeStatus } from "~/api/media"

export type ScanState = "idle" | "running" | "completed" | "failed" | "cancelled"

const KNOWN_STATES = new Set<string>(["idle", "running", "completed", "failed", "cancelled"])

/** 归一化扫描状态；未知值兜底为 idle。 */
export function normalizeScanState(state: string | null | undefined): ScanState {
  return state && KNOWN_STATES.has(state) ? (state as ScanState) : "idle"
}

/** 扫描状态 → Element Plus Tag 的 type（颜色语义，供行内徽标用）。 */
export function scanTagType(
  state: string | null | undefined,
): "primary" | "success" | "danger" | "info" | "warning" {
  switch (normalizeScanState(state)) {
    case "running":
      return "warning"
    case "completed":
      return "success"
    case "failed":
      return "danger"
    default:
      // idle / cancelled：无活动的中性色
      return "info"
  }
}

/** 媒体库类型 → i18n 键尾（media.json 的 type_movie / type_tv / type_mixed）。 */
export function libraryTypeKey(type: number): string {
  switch (type) {
    case 0:
      return "movie"
    case 1:
      return "tv"
    default:
      return "mixed"
  }
}

/** 扫描计数 → "+a ~u -r" 紧凑摘要（三条都为 0 时显示 "-"）。 */
export function formatScanCounts(c: { added: number; updated: number; removed: number }): string {
  const parts = [
    c.added > 0 ? `+${c.added}` : "",
    c.updated > 0 ? `~${c.updated}` : "",
    c.removed > 0 ? `-${c.removed}` : "",
  ].filter(Boolean)
  return parts.length ? parts.join(" ") : "-"
}

/**
 * 扫描历史行 → 一句话摘要（日志对话框的副标题列）。
 * 失败行优先显示错误（截断到 80 字符，避免把表格撑爆）。
 */
export function scanLogSummary(log: Pick<MediaScanLog, "error" | "added" | "updated" | "removed">): string {
  if (log.error) {
    const msg = log.error.length > 80 ? `${log.error.slice(0, 80)}…` : log.error
    return msg
  }
  return formatScanCounts(log)
}

/**
 * 从跨库任务列表里取指定库的任务项；没有记录（从未扫过）返回 null。
 * 管理台行内徽标用它代替逐行请求 `/scan`（一次 `/tasks` 覆盖全部库）。
 */
export function taskOfLibrary(tasks: MediaScanTask[], libraryId: number): MediaScanTask | null {
  return tasks.find((t) => t.library_id === libraryId) ?? null
}

/** 从刮削状态列表里取指定库的状态；没有记录返回 null（非管理员或库不可见）。 */
export function scrapeOfLibrary(
  statuses: MediaScrapeStatus[],
  libraryId: number,
): MediaScrapeStatus | null {
  return statuses.find((s) => s.library_id === libraryId) ?? null
}

/**
 * 本轮批量刮削的完成百分比。
 *
 * `batch_size` 是「本轮计划处理数」，不是「库里总数」—— 后端把单轮上限定在 2000，
 * 拿总数当分母的话，一个大库永远显示 3%。两者都为 0（未运行）时返回 0，
 * 让进度条退回不显示的分支。
 */
export function scrapeProgress(status: MediaScrapeStatus | null): number {
  if (!status || !status.running || status.batch_size <= 0) return 0
  const done = Math.min(Math.max(status.batch_done, 0), status.batch_size)
  return Math.round((done / status.batch_size) * 100)
}
