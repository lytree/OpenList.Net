import { describe, expect, it } from "vitest"
import { encodeFsPath, thumbLink } from "./link"

describe("encodeFsPath", () => {
  it("逐段编码并保留 / 分隔符", () => {
    expect(encodeFsPath("/a b/c#d")).toBe("/a%20b/c%23d")
    expect(encodeFsPath("/")).toBe("/")
  })
})

describe("thumbLink", () => {
  it("拼出 /p/{path}?type=thumb&w=&h=", () => {
    const url = thumbLink("/movies/英雄 本色.mkv", null, 320)
    // 路径按段编码（中文也要编码，后端按原路径匹配）
    expect(url).toBe(
      `${location.origin}/p/movies/%E8%8B%B1%E9%9B%84%20%E6%9C%AC%E8%89%B2.mkv?type=thumb&w=320&h=320`,
    )
  })

  it("有签名时带上 sign，没有时不带空的 sign 参数", () => {
    const signed = thumbLink("/a.mp4", "abc+/=", 640)
    expect(signed).toContain("w=640&h=640")
    expect(signed).toContain("&sign=abc%2B%2F%3D")

    const anonymous = thumbLink("/a.mp4")
    expect(anonymous).not.toContain("sign=")
    expect(anonymous).toContain("w=320&h=320")
  })
})
