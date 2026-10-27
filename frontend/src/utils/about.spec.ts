import { describe, expect, it } from "vitest"
import {
  capabilityTags,
  clockSkewSeconds,
  formatMaxConnections,
  formatSkew,
  formatUptime,
  shortCommit,
} from "./about"
import type { SystemFeatures } from "~/api/system"

const FEATURES: SystemFeatures = {
  s3: true,
  ftp: false,
  sftp: false,
  ldap: false,
  mcp: true,
  search_index: "database",
  max_connections: 0,
}

describe("shortCommit", () => {
  it("截到 7 位", () => {
    expect(shortCommit("07d789e2b9a81acd8d369a1aaad52afbf713168e")).toBe("07d789e")
  })

  it("本来就短时原样返回", () => {
    expect(shortCommit("ae21a4f")).toBe("ae21a4f")
  })

  it("空值返回空串（页面据此隐藏该行，而不是显示 undefined）", () => {
    expect(shortCommit("")).toBe("")
    expect(shortCommit(null)).toBe("")
    expect(shortCommit(undefined)).toBe("")
    expect(shortCommit("   ")).toBe("")
  })
})

describe("formatUptime", () => {
  it("不足一分钟只给秒", () => {
    expect(formatUptime(0)).toBe("0s")
    expect(formatUptime(59)).toBe("59s")
  })

  it("分钟级带秒，小时级带分", () => {
    expect(formatUptime(90)).toBe("1m 30s")
    expect(formatUptime(3600)).toBe("1h 0m")
    expect(formatUptime(3661)).toBe("1h 1m")
  })

  it("超过一天只留天与小时", () => {
    expect(formatUptime(86400)).toBe("1d 0h")
    expect(formatUptime(90061)).toBe("1d 1h")
  })

  it("负数与非法值显示 -（后端拿不到启动时间时给 -1）", () => {
    expect(formatUptime(-1)).toBe("-")
    expect(formatUptime(Number.NaN)).toBe("-")
    expect(formatUptime(null)).toBe("-")
    expect(formatUptime(undefined)).toBe("-")
  })
})

describe("clockSkewSeconds", () => {
  const now = Date.parse("2026-10-27T12:00:00.000Z")

  it("服务器快 3 秒", () => {
    expect(clockSkewSeconds("2026-10-27T12:00:03.000Z", now)).toBe(3)
  })

  it("服务器慢 45 秒给负数", () => {
    expect(clockSkewSeconds("2026-10-27T11:59:15.000Z", now)).toBe(-45)
  })

  it("空值或不可解析返回 null，而不是 0", () => {
    expect(clockSkewSeconds("", now)).toBeNull()
    expect(clockSkewSeconds("not-a-date", now)).toBeNull()
    expect(clockSkewSeconds(null, now)).toBeNull()
  })

  it("formatSkew 带符号", () => {
    expect(formatSkew(3)).toBe("+3s")
    expect(formatSkew(-45)).toBe("-45s")
    expect(formatSkew(0)).toBe("0s")
    expect(formatSkew(null)).toBe("-")
  })
})

describe("capabilityTags", () => {
  it("固定顺序、关闭项也列出", () => {
    const tags = capabilityTags(FEATURES)
    expect(tags.map((t) => t.label)).toEqual(["S3", "FTP", "SFTP", "LDAP", "MCP"])
    expect(tags.filter((t) => t.enabled).map((t) => t.label)).toEqual(["S3", "MCP"])
  })

  it("没有 features 时返回空数组（不崩、不渲染半张标签墙）", () => {
    expect(capabilityTags(null)).toEqual([])
    expect(capabilityTags(undefined)).toEqual([])
  })
})

describe("formatMaxConnections", () => {
  it("0 表示不限制，显示为 ∞", () => {
    expect(formatMaxConnections(0)).toBe("∞")
    expect(formatMaxConnections(-1)).toBe("∞")
  })

  it("正数原样显示", () => {
    expect(formatMaxConnections(256)).toBe("256")
  })

  it("缺值显示 -", () => {
    expect(formatMaxConnections(null)).toBe("-")
    expect(formatMaxConnections(undefined)).toBe("-")
  })
})
