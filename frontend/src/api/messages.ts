import { http } from "./client"
import type { PResp } from "./types"

/**
 * 站内信（管理员 → 用户 的持久化消息）。
 *
 * **命名是 camelCase，不是 snake_case** —— 这个域和媒体域不一样：媒体的 DTO 逐个写了
 * `[JsonPropertyName("library_id")]`，而 `Message` 没有任何显式映射，走的是
 * `Program.cs` 里的全局 `PropertyNamingPolicy = JsonNamingPolicy.CamelCase`。
 * 照抄媒体那边的写法会拿到一堆 `undefined`。
 *
 * 两个入口各管一段：
 * - 管理端 `/admin/message/*`（`AdminEndpoints.cs`）：按用户查、发、删。
 * - 用户端 `/me/message/*`（`MeEndpoints.cs`）：自己的列表、未读数、标为已读。
 *
 * 另有一组 `POST /admin/message/send` + `POST /admin/message/get` 是**内存广播**，
 * 对应 Go 版的调试回显页（旧前端 Messenger 每秒轮询并把结果显示出来）。
 * 它不持久化、用户侧也没有消费端，故本模块不封装 —— 见 `docs/...` §34.6。
 */
export interface Message {
  id: number
  /** 接收者用户 id。 */
  userId: number
  title: string
  content: string
  read: boolean
  createdAt: string
}

// ---- 管理端 --------------------------------------------------------------

/** `GET /admin/message/list?user_id=` — 指定用户的消息（含已读）。 */
export const adminListMessages = (userId: number): PResp<Message[]> =>
  http.get<Message[]>("/admin/message/list", { params: { user_id: userId } })

/** `POST /admin/message/create` — 给指定用户发一条；标题与正文都不能为空（后端 400）。 */
export const adminCreateMessage = (req: {
  userId: number
  title: string
  content: string
}): PResp<unknown> => http.post<unknown>("/admin/message/create", req)

/** `POST /admin/message/delete?id=` — 删除一条消息。 */
export const adminDeleteMessage = (id: number): PResp<unknown> =>
  http.post<unknown>("/admin/message/delete", undefined, { params: { id } })

// ---- 用户端 --------------------------------------------------------------

/** `GET /me/message/list` — 当前用户的消息（新→旧）。 */
export const myMessages = (): PResp<Message[]> => http.get<Message[]>("/me/message/list")

/** `GET /me/message/unread` — 未读条数，用于头部徽标。 */
export const myUnreadCount = (): PResp<number> => http.get<number>("/me/message/unread")

/** `POST /me/message/read` — 标为已读；**只能动自己的**，否则后端返回 404。 */
export const markMessageRead = (id: number): PResp<unknown> =>
  http.post<unknown>("/me/message/read", { id })
