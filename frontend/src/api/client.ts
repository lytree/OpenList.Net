import axios, { type AxiosError, type AxiosRequestConfig } from "axios"
import { apiBase } from "./config"
import type { Resp } from "./types"

/**
 * HTTP 客户端。
 *
 * 关键约定（与旧 SolidJS 前端一致，避免后端/上传逻辑出现行为差异）：
 * 1. **不抛异常**：非 2xx 也被归一成 `Resp` 信封返回，调用方只需看 `code`。
 *    唯一例外是网络层失败，此时 `code` 为 `undefined`。
 * 2. 非 2xx 时 `message` 优先取服务端信封里的文案（后端错误也是 `{code,message,data}`），
 *    避免用户只看到 axios 的 "Request failed with status code 500"。
 * 3. **仅当服务端确实返回非空 data 时才附加 data 键**：分片/多段上传靠
 *    「错误对象里到底有没有 data 键」区分「传输层失败」与「服务端信封」，
 *    无条件补 `data: null` 会让 CDN/网关的 HTML 错误页被误判成信封。
 */

const TOKEN_KEY = "token"

let token: string = localStorage.getItem(TOKEN_KEY) ?? ""

export const getToken = () => token

export function setToken(next?: string | null) {
  token = next ?? ""
  if (token) localStorage.setItem(TOKEN_KEY, token)
  else localStorage.removeItem(TOKEN_KEY)
}

export const instance = axios.create({
  baseURL: `${apiBase}/api`,
  headers: { "Content-Type": "application/json;charset=utf-8" },
  withCredentials: false,
})

instance.interceptors.request.use((config) => {
  if (token) {
    config.headers.set("Authorization", token)
  }
  return config
})

export type Envelope = {
  code: number | undefined
  message: string
  data?: unknown
}

type UnauthorizedHandler = () => void
let unauthorizedHandler: UnauthorizedHandler | null = null

/** 由路由/用户 store 注册：收到 401 时跳登录页。 */
export function onUnauthorized(handler: UnauthorizedHandler | null) {
  unauthorizedHandler = handler
}

instance.interceptors.response.use(
  // 拦截器把 AxiosResponse 换成业务信封；类型层面用双重断言收口，
  // 对外的 `http.*` 再统一转成 Promise<Resp<T>>，调用方看不到这层转换。
  (response) => {
    const body = response.data as Envelope
    // 少数后端错误也会用 200 + code:401 返回，两条路径都要触发未授权处理
    if (body?.code === 401) unauthorizedHandler?.()
    return body as unknown as typeof response
  },
  (error: AxiosError) => {
    const body = error.response?.data as
      | { message?: string; data?: unknown }
      | undefined
    const result: Envelope = {
      code: axios.isCancel(error) ? -1 : error.response?.status,
      message: body?.message || error.message,
    }
    if (body && typeof body === "object" && body.data != null) {
      result.data = body.data
    }
    if (result.code === 401) unauthorizedHandler?.()
    return result as unknown as Promise<never>
  },
)

export class ApiError extends Error {
  constructor(
    readonly code: number,
    message: string,
    readonly data?: unknown,
  ) {
    super(message)
    this.name = "ApiError"
  }
}

/** 把信封拆成数据；`code !== 200` 时抛 `ApiError`，供 async/await 场景使用。 */
export function unwrap<T>(resp: Resp<T> | Envelope): T {
  const code = resp.code
  if (code !== 200) {
    throw new ApiError(code ?? -1, resp.message ?? "Unknown error", resp.data)
  }
  return resp.data as T
}

function asResp<T>(promise: Promise<unknown>): Promise<Resp<T>> {
  return promise as Promise<Resp<T>>
}

export const http = {
  get: <T = unknown>(url: string, config?: AxiosRequestConfig) =>
    asResp<T>(instance.get(url, config)),
  post: <T = unknown>(url: string, data?: unknown, config?: AxiosRequestConfig) =>
    asResp<T>(instance.post(url, data, config)),
  put: <T = unknown>(url: string, data?: unknown, config?: AxiosRequestConfig) =>
    asResp<T>(instance.put(url, data, config)),
  delete: <T = unknown>(url: string, config?: AxiosRequestConfig) =>
    asResp<T>(instance.delete(url, config)),
}

/** 便捷方法：直接返回 data，失败抛 `ApiError`。 */
export const fetchData = async <T>(
  method: "get" | "post" | "put" | "delete",
  url: string,
  data?: unknown,
  config?: AxiosRequestConfig,
): Promise<T> => {
  const resp =
    method === "get"
      ? await http.get<T>(url, config)
      : method === "delete"
        ? await http.delete<T>(url, config)
        : method === "post"
          ? await http.post<T>(url, data, config)
          : await http.put<T>(url, data, config)
  return unwrap<T>(resp)
}
