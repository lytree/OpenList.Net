import axios from "axios"
import { apiBase } from "./config"
import type { FsGetData, FsListData, Resp } from "./types"

/**
 * 公开分享访问（`/s/:code` 页面专用）。
 *
 * 为什么单独建一个 axios 实例：
 * 1. **不能带登录 token**。后端判定分享码的前提是「请求未认证」；如果管理员带着自己的
 *    JWT 打开分享链接，`ShareAccess` 会跳过分享解析，用户看到的就是整站根目录而不是分享内容。
 * 2. 分享码放在 `Authorization` 头里（与后端 `ShareAccess` 约定一致），密码放在请求体的
 *    `password` 字段（复用 `FsListReq/FsGetReq` 的既有字段）。
 *
 * 分享码访问只开放三个读端点（list / get / dirs），写操作一律 403 —— 分享访客是
 * `Role=Guest` 且权限位为 0。
 */
const shareClient = axios.create({
  baseURL: `${apiBase}/api`,
  headers: { "Content-Type": "application/json;charset=utf-8" },
})

function envelope(error: unknown): Resp<never> {
  const err = error as { response?: { data?: Resp<never>; status?: number }; message?: string }
  const body = err.response?.data
  if (body && typeof body === "object" && "code" in body) return body
  return {
    code: err.response?.status ?? -1,
    message: err.message ?? "network error",
    data: null as never,
  }
}

/** `POST /fs/get`（分享码鉴权）。路径相对分享根：`/` 即分享根本身。 */
export async function shareFsGet(
  code: string,
  path: string,
  pwd?: string,
): Promise<Resp<FsGetData>> {
  try {
    const { data } = await shareClient.post<Resp<FsGetData>>(
      "/fs/get",
      { path, password: pwd ?? "" },
      { headers: { Authorization: code } },
    )
    return data
  } catch (e) {
    return envelope(e)
  }
}

/** `POST /fs/list`（分享码鉴权）。 */
export async function shareFsList(
  code: string,
  path: string,
  pwd?: string,
): Promise<Resp<FsListData>> {
  try {
    const { data } = await shareClient.post<Resp<FsListData>>(
      "/fs/list",
      { path, password: pwd ?? "", page: 1, per_page: 0 },
      { headers: { Authorization: code } },
    )
    return data
  } catch (e) {
    return envelope(e)
  }
}

/**
 * `POST /fs/archive/list`（分享码鉴权）。
 *
 * 归档预览要列条目，而游客没有 JWT；`/fs/archive/list` 对匿名请求走 `ShareAccess`，
 * 认 `Authorization: <分享码>`，所以这里只需要把分享码塞进头里，不用另开端点。
 */
export async function shareFsArchiveList(
  code: string,
  path: string,
  archivePass?: string,
): Promise<Resp<{ content: { name: string; size: number; is_dir: boolean; modified: string }[]; total: number }>> {
  try {
    const { data } = await shareClient.post<
      Resp<{ content: { name: string; size: number; is_dir: boolean; modified: string }[]; total: number }>
    >(
      "/fs/archive/list",
      { path, password: "", archive_pass: archivePass ?? "" },
      { headers: { Authorization: code } },
    )
    return data
  } catch (e) {
    return envelope(e)
  }
}

/**
 * 分享内文件的下载直链（`/sd/{code}/{相对路径}`）。
 * 注意：`/sd` 走的是分享根 + 相对路径，**不是**完整挂载路径。
 */
export function shareDownloadLink(code: string, relPath: string, pwd?: string): string {
  const rel = relPath
    .split("/")
    .filter(Boolean)
    .map(encodeURIComponent)
    .join("/")
  const base = `${apiBase}/sd/${encodeURIComponent(code)}${rel ? `/${rel}` : ""}`
  return pwd ? `${base}?pwd=${encodeURIComponent(pwd)}` : base
}
