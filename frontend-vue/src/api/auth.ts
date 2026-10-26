import { sha256 } from "js-sha256"
import { http, instance, setToken } from "./client"
import type { Me, PResp, Resp } from "./types"

/**
 * 认证 API。
 *
 * 后端约定（见 `OpenList.Server/Endpoints/AuthEndpoints.cs`）：
 * - `POST /api/auth/login` 明文密码；`/login/hash` 收「sha256(密码 + 固定后缀)」。
 * - 密码哈希固定后缀为 `UserService.PasswordHashSuffix`
 *   （`-https://github.com/alist-org/alist`），改动此值会让所有历史密码失效。
 * - 成功返回 `{ token }`，token 直接放进 `Authorization` 头（不带 Bearer 前缀）。
 */
const PASSWORD_HASH_SUFFIX = "-https://github.com/alist-org/alist"

export function hashPassword(password: string): string {
  return sha256(`${password}${PASSWORD_HASH_SUFFIX}`)
}

export type LoginPayload = {
  username: string
  password: string
  otp_code?: string
}

export type TokenData = { token: string }

export const login = (payload: LoginPayload): PResp<TokenData> =>
  http.post<TokenData>("/auth/login", payload)

export const loginWithHash = (
  payload: Omit<LoginPayload, "password"> & { password: string },
): PResp<TokenData> => http.post<TokenData>("/auth/login/hash", payload)

export const loginWithLdap = (payload: LoginPayload): PResp<TokenData> =>
  http.post<TokenData>("/auth/login/ldap", payload)

export const logout = () => http.get("/auth/logout")

/** 当前登录用户（未登录时后端返回 401 信封）。 */
export const fetchMe = (): PResp<Me> => http.get<Me>("/me")

export function persistToken(data: TokenData) {
  setToken(data.token)
  return data.token
}

/* ------------------------------------------------------------------ *
 * WebAuthn（通行密钥）登录
 * ------------------------------------------------------------------ */

export type WebAuthnTemp = {
  session: string
  options: { publicKey: PublicKeyCredentialRequestOptionsJSON }
}

export const webauthnSupported = () =>
  typeof window !== "undefined" && typeof window.PublicKeyCredential === "function"

export const webauthnConditionalAvailable = async (): Promise<boolean> => {
  if (!webauthnSupported()) return false
  const PKC = window.PublicKeyCredential as typeof PublicKeyCredential & {
    isConditionalMediationAvailable?: () => Promise<boolean>
  }
  return (await PKC.isConditionalMediationAvailable?.()) ?? false
}

export const webauthnBeginLogin = (
  username: string,
  signal?: AbortSignal,
): PResp<WebAuthnTemp> =>
  http.get<WebAuthnTemp>(
    `/authn/webauthn_begin_login?username=${encodeURIComponent(username)}`,
    { signal },
  )

/**
 * 完成登录。注意 `session` 走请求头而不是查询串，且 body 是凭据的 JSON 快照，
 * 必须自己 `JSON.stringify`（axios 对字符串 body 不会再序列化）。
 */
export const webauthnFinishLogin = (
  session: string,
  credential: PublicKeyCredential,
  username: string,
  signal?: AbortSignal,
): PResp<TokenData> =>
  instance.post(
    `/authn/webauthn_finish_login?username=${encodeURIComponent(username)}`,
    JSON.stringify(credential.toJSON()),
    { headers: { session }, signal },
  ) as Promise<Resp<TokenData>>

/** 完整的一次 WebAuthn 断言流程（浏览器弹窗 → 换 token）。 */
export async function webauthnAssert(
  username: string,
  opts: { conditional?: boolean; signal?: AbortSignal } = {},
): Promise<TokenData> {
  const begin = await webauthnBeginLogin(username, opts.signal)
  if (begin.code !== 200) throw new Error(begin.message)

  const requestOptions: CredentialRequestOptions = {
    publicKey: PublicKeyCredential.parseRequestOptionsFromJSON(
      begin.data.options.publicKey,
    ),
    signal: opts.signal,
  }
  if (opts.conditional) requestOptions.mediation = "conditional"

  const credential = (await navigator.credentials.get(
    requestOptions,
  )) as PublicKeyCredential | null
  if (!credential) throw new Error("WebAuthn credential unavailable")

  const finish = await webauthnFinishLogin(
    begin.data.session,
    credential,
    username,
    opts.signal,
  )
  if (finish.code !== 200) throw new Error(finish.message)
  return finish.data
}
