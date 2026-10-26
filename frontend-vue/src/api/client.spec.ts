import { AxiosError, type AxiosAdapter, type AxiosResponse } from "axios"
import { afterEach, describe, expect, it, vi } from "vitest"
import { ApiError, type Envelope, instance, setToken, unwrap } from "./client"
import { UserMethods, PermissionBits } from "./types"

/**
 * HTTP 信封的冒烟用例。
 *
 * 重点不是「能不能请求」，而是**错误归一**：非 2xx 必须变成 `Resp` 信封，
 * 且只在服务端真给了 data 时才带 data 键 —— 分片上传靠这个区分传输层失败。
 */
const originalAdapter = instance.defaults.adapter

function respondWith(body: unknown, status = 200): AxiosAdapter {
  return async (config) => {
    const response: AxiosResponse = {
      data: body,
      status,
      statusText: String(status),
      headers: {},
      config: config as never,
    }
    if (status >= 200 && status < 300) return response
    throw new AxiosError(
      `Request failed with status code ${status}`,
      "ERR_BAD_RESPONSE",
      config as never,
      null,
      response,
    )
  }
}


/** 拦截器已把响应换成信封，类型系统仍认为是 AxiosResponse，这里统一收口。 */
const call = async (url: string): Promise<Envelope> =>
  (await instance.get(url)) as unknown as Envelope

afterEach(() => {
  instance.defaults.adapter = originalAdapter
  setToken(null)
})

describe("unwrap", () => {
  it("code=200 时返回 data", () => {
    expect(unwrap({ code: 200, message: "success", data: { a: 1 } })).toEqual({ a: 1 })
  })

  it("code≠200 时抛 ApiError 并带上 code/message", () => {
    expect(() => unwrap({ code: 500, message: "boom", data: undefined })).toThrowError(ApiError)
    try {
      unwrap({ code: 500, message: "boom" })
    } catch (error) {
      const apiError = error as ApiError
      expect(apiError.code).toBe(500)
      expect(apiError.message).toBe("boom")
    }
  })
})

describe("响应归一", () => {
  it("2xx 直接透出信封", async () => {
    instance.defaults.adapter = respondWith({ code: 200, message: "success", data: { ok: true } })
    const resp = await call("/ping")
    expect(resp).toMatchObject({ code: 200, data: { ok: true } })
  })

  it("非 2xx 转成信封，message 取服务端文案", async () => {
    instance.defaults.adapter = respondWith({ code: 500, message: "storage not found" }, 500)
    const resp = await call("/boom")
    expect(resp.code).toBe(500)
    expect(resp.message).toBe("storage not found")
    // 服务端没给 data，就**不能**出现 data 键
    expect("data" in resp).toBe(false)
  })

  it("服务端给了 data 时透传", async () => {
    instance.defaults.adapter = respondWith({ message: "init failed", data: { reason: "no storage" } }, 500)
    const resp = await call("/boom")
    expect(resp.data).toEqual({ reason: "no storage" })
  })

  it("401 触发全局未授权回调", async () => {
    const handler = vi.fn()
    const { onUnauthorized } = await import("./client")
    onUnauthorized(handler)
    instance.defaults.adapter = respondWith({ code: 401, message: "unauthorized" }, 401)

    const resp = await call("/private")
    expect(resp.code).toBe(401)
    expect(handler).toHaveBeenCalledOnce()

    onUnauthorized(null)
  })

  it("设置了 token 时自动带上 Authorization 头", async () => {
    setToken("token-abc")
    let seen = ""
    instance.defaults.adapter = async (config) => {
      seen = String(config.headers.Authorization)
      return {
        data: { code: 200, message: "success", data: null },
        status: 200,
        statusText: "OK",
        headers: {},
        config,
      }
    }
    await call("/ping")
    expect(seen).toBe("token-abc")
  })
})

describe("权限位", () => {
  it("按位判断，不依赖数组下标", () => {
    // 后端 admin 的默认权限掩码
    const admin = { role: 2, permission: 0x71ff } as never
    expect(UserMethods.can(admin, "rename")).toBe(true)
    expect(UserMethods.can(admin, "delete")).toBe(true)
    // 0x71FF 的 bit14 为 1 → 媒体库管理
    expect(UserMethods.can(admin, "manage_media")).toBe(true)
    expect(PermissionBits.manage_media).toBe(14)
  })

  it("普通用户的越权判断为 false", () => {
    const general = { role: 0, permission: 0b0000_0000 } as never
    expect(UserMethods.can(general, "write_content")).toBe(false)
    expect(UserMethods.can(null, "write_content")).toBe(false)
  })
})
