import { http } from "./client"
import type { PageResp, PResp, ShareInfo, ShareReq } from "./types"

/**
 * 分享 API（`/api/share`）。
 *
 * 与 admin 组不同：分享是**用户级**能力，路由只需登录；管理员调用时可见/可管全部，
 * 普通用户只看得到自己的。定位一律用**分享码**（查询参数 `id`），不是数据库主键。
 *
 * 返回值是 `ShareInfo`（含 `creator`），列表被后端包成 `{ content, total }`。
 */

/** `GET /share/list` → Resp<{ content, total }>。 */
export const listShares = (): PResp<PageResp<ShareInfo>> =>
  http.get<PageResp<ShareInfo>>("/share/list")

/** `GET /share/get?id={code}` → Resp<ShareInfo>。 */
export const getShare = (code: string): PResp<ShareInfo> =>
  http.get<ShareInfo>("/share/get", { params: { id: code } })

/** `POST /share/create` → Resp<ShareInfo>。创建时必须至少有一个路径。 */
export const createShare = (req: ShareReq): PResp<ShareInfo> =>
  http.post<ShareInfo>("/share/create", req)

/** `POST /share/update?id={code}` → Resp<ShareInfo>。 */
export const updateShare = (code: string, req: ShareReq): PResp<ShareInfo> =>
  http.post<ShareInfo>("/share/update", req, { params: { id: code } })

/** `POST /share/delete?id={code}`。 */
export const deleteShare = (code: string): PResp<Record<string, never>> =>
  http.post<Record<string, never>>("/share/delete", undefined, { params: { id: code } })

/** `POST /share/enable?id={code}`。 */
export const enableShare = (code: string): PResp<Record<string, never>> =>
  http.post<Record<string, never>>("/share/enable", undefined, { params: { id: code } })

/** `POST /share/disable?id={code}`。 */
export const disableShare = (code: string): PResp<Record<string, never>> =>
  http.post<Record<string, never>>("/share/disable", undefined, { params: { id: code } })
