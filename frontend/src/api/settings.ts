import { http } from "./client"

/**
 * 站点公开设置（`GET /api/public/settings`）。
 *
 * 后端返回 `Record<string, string>`：`site_title`、`logo`、`main_color`、
 * `ldap_login_enabled`、`webauthn_login_enabled` 等都在这里。
 * 值统一是字符串，"布尔"要按 "true"/"1" 判断（沿用旧前端 `getSettingBool`）。
 */
export type PublicSettings = Record<string, string>

export const fetchPublicSettings = () => http.get<PublicSettings>("/public/settings")

export const getSettingBool = (settings: PublicSettings, key: string) => {
  const value = settings[key]
  return value === "true" || value === "1"
}
