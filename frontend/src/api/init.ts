import { http } from "./client"
import type { PResp } from "./types"

/**
 * 系统初始化（安装向导）API。
 *
 * 后端约定（见 `OpenList.Server/Endpoints/PublicEndpoints.cs`）：
 * - `GET|POST /public/init_status` → `{ initialized: boolean }`，判据是「是否已存在管理员账号」。
 * - `POST /public/init/setup` → 创建内置管理员（可顺带写 `site_title`）；已初始化时返回 code 400。
 *
 * 两个接口都匿名可访问，且在未初始化时 `/public/settings` 依然是可用的（默认设置已种子化），
 * 所以向导页不需要任何登录态。
 */

export type InitStatus = {
  initialized: boolean
}

export type InitSetupPayload = {
  username: string
  password: string
  site_title?: string
}

export const fetchInitStatus = (): PResp<InitStatus> => http.get<InitStatus>("/public/init_status")

export const initSetup = (payload: InitSetupPayload): PResp<undefined> =>
  http.post<undefined>("/public/init/setup", payload)
