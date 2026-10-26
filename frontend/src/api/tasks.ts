import { http } from "./client"
import type { PResp, TaskInfo } from "./types"

/**
 * 任务 API（`/api/task/...`）。
 *
 * 路由形态与 admin 组不同：类型放在 URL 路径里（`/task/{type}/{done}`），
 * 且 `/api/task` 只需登录、不限管理员 —— 管理员用 `/api/admin/task` 别名走同一套实现
 * （见 `TaskEndpoints.MapTaskRoutes`）。这里统一用 `/task`，普通用户也能看自己的任务。
 *
 * `{type}` 必须用 snake_case 别名（后端 `TypeAliases` 显式映射），不能传 C# 枚举名。
 */

export const TASK_TYPES = [
  "copy",
  "move",
  "upload",
  "transfer",
  "download",
  "decompress",
  "decompress_upload",
  "offline_download",
  "offline_download_transfer",
  "aria2_down",
  "aria2_transfer",
  "qbit_down",
  "qbit_transfer",
  "transmission_down",
  "transmission_transfer",
] as const

export type TaskTypeKey = (typeof TASK_TYPES)[number]

/** `done` 只接受后端约定的两个字面量。 */
export type TaskListState = "done" | "undone"

/** `GET /task/{type}/{done}` → Resp<TaskInfo[]>（注意是裸数组，不是分页信封）。 */
export const listTasks = (type: TaskTypeKey, state: TaskListState): PResp<TaskInfo[]> =>
  http.get<TaskInfo[]>(`/task/${type}/${state}`)

/** 单条动作：cancel / delete / retry（`?tid=`）。 */
export type SingleTaskAction = "cancel" | "delete" | "retry"

export const taskAction = (
  type: TaskTypeKey,
  action: SingleTaskAction,
  tid: string,
): PResp<Record<string, never>> =>
  http.post<Record<string, never>>(`/task/${type}/${action}`, undefined, { params: { tid } })

/** 整类动作：clear_done / clear_succeeded / retry_failed。 */
export type BulkTaskAction = "clear_done" | "clear_succeeded" | "retry_failed"

export const taskBulkAction = (
  type: TaskTypeKey,
  action: BulkTaskAction,
): PResp<Record<string, never>> => http.post<Record<string, never>>(`/task/${type}/${action}`)

/** 批量动作：cancel_some / delete_some / retry_some（body 为 tid 数组）。 */
export type BatchTaskAction = "cancel_some" | "delete_some" | "retry_some"

export const taskBatchAction = (
  type: TaskTypeKey,
  action: BatchTaskAction,
  tids: string[],
): PResp<Record<string, string>> =>
  http.post<Record<string, string>>(`/task/${type}/${action}`, tids)
