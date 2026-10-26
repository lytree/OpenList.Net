import { describe, expect, it } from "vitest"
import { UserRole, type AdminUser, type ShareInfo, type StorageWithDriver } from "~/api/types"
import type { MediaScrapeStatus } from "~/api/media"
import {
  libraryHealth,
  libraryMatchRate,
  libraryState,
  libraryStateTagType,
  shareStats,
  storageStats,
  userStats,
} from "./dashboard"

/** 只填被测字段，其余用最小合法值补齐 —— 断言失败时能一眼看出是哪一项。 */
const user = (patch: Partial<AdminUser>): AdminUser =>
  ({
    id: 0,
    username: "u",
    password: "",
    base_path: "/",
    role: UserRole.GENERAL,
    permission: 0,
    sso_id: "",
    disabled: false,
    allow_ldap: false,
    ...patch,
  }) as AdminUser

const storage = (patch: Partial<StorageWithDriver>): StorageWithDriver =>
  ({
    id: 0,
    mount_path: "/",
    driver: "Local",
    driver_name: "Local",
    addition: "{}",
    order: 0,
    disabled: false,
    status: "work",
    web_proxy: false,
    enable_sign: false,
    modified: "",
    ...patch,
  }) as StorageWithDriver

const share = (patch: Partial<ShareInfo>): ShareInfo =>
  ({
    id: "s",
    files: [],
    max_accessed: 0,
    accessed: 0,
    disabled: false,
    creator: "admin",
    creator_role: 2,
    ...patch,
  }) as ShareInfo

const scrapeRow = (patch: Partial<MediaScrapeStatus> = {}): MediaScrapeStatus =>
  ({
    library_id: 1,
    library_name: "lib",
    pending: 0,
    unmatched: 0,
    matched: 0,
    running: false,
    batch_size: 0,
    batch_done: 0,
    failed: 0,
    error: null,
    ...patch,
  }) as MediaScrapeStatus

describe("userStats", () => {
  it("分别统计管理员与被停用账号", () => {
    const stats = userStats([
      user({ role: UserRole.ADMIN, is_built_in: true }),
      user({ role: UserRole.ADMIN, disabled: true }),
      user({ role: UserRole.GENERAL }),
      user({ role: UserRole.GUEST, disabled: true }),
    ])
    // 停用的管理员仍计入 admins —— 停用是「能不能登录」，不是「是不是管理员」
    expect(stats).toEqual({ total: 4, admins: 2, disabled: 2 })
  })

  it("空列表全是 0", () => {
    expect(userStats([])).toEqual({ total: 0, admins: 0, disabled: 0 })
  })
})

describe("storageStats", () => {
  it("分布只算启用的挂载，按数量降序、同数量按名字升序", () => {
    const stats = storageStats([
      storage({ driver: "Local", driver_name: "Local" }),
      storage({ driver: "S3", driver_name: "S3" }),
      storage({ driver: "S3", driver_name: "S3" }),
      storage({ driver: "Local", driver_name: "Local", disabled: true }),
      storage({ driver: "WebDAV", driver_name: "WebDAV" }),
    ])
    expect(stats.total).toBe(5)
    expect(stats.enabled).toBe(4)
    expect(stats.disabled).toBe(1)
    expect(stats.byDriver).toEqual([
      { name: "S3", count: 2 },
      { name: "Local", count: 1 },
      { name: "WebDAV", count: 1 },
    ])
  })

  it("driver_name 缺省时退回驱动 key", () => {
    const stats = storageStats([storage({ driver: "Aliyundrive", driver_name: "" })])
    expect(stats.byDriver).toEqual([{ name: "Aliyundrive", count: 1 }])
  })

  it("全部停用时分布为空（而不是列出 0 项）", () => {
    const stats = storageStats([storage({ disabled: true })])
    expect(stats).toEqual({ total: 1, enabled: 0, disabled: 1, byDriver: [] })
  })
})

describe("shareStats", () => {
  it("启用数只排除 disabled，访问次数含失效分享", () => {
    const stats = shareStats([
      share({ accessed: 3 }),
      share({ accessed: 0 }),
      share({ accessed: 7, disabled: true }),
    ])
    expect(stats).toEqual({ total: 3, enabled: 2, disabled: 1, accessed: 10 })
  })
})

describe("libraryHealth", () => {
  it("汇总三类计数并按三类之和算匹配率", () => {
    const health = libraryHealth([
      scrapeRow({ matched: 6, pending: 2 }),
      scrapeRow({ library_id: 2, unmatched: 2, running: true }),
    ])
    expect(health.libraries).toBe(2)
    expect(health.matched).toBe(6)
    expect(health.pending).toBe(2)
    expect(health.unmatched).toBe(2)
    expect(health.running).toBe(1)
    // 6 / 10
    expect(health.rate).toBe(60)
  })

  it("空库不进分母，整体为 0 而不是 NaN", () => {
    const health = libraryHealth([scrapeRow()])
    expect(health.rate).toBe(0)
    expect(libraryHealth([]).rate).toBe(0)
  })
})

describe("libraryMatchRate", () => {
  it("空库返回 0（不能渲染成 100）", () => {
    expect(libraryMatchRate(scrapeRow())).toBe(0)
  })

  it("四舍五入到整数百分比", () => {
    expect(libraryMatchRate(scrapeRow({ matched: 1, pending: 2 }))).toBe(33)
    expect(libraryMatchRate(scrapeRow({ matched: 2, pending: 1 }))).toBe(67)
  })
})

describe("libraryState", () => {
  it("运行中优先于一切", () => {
    expect(libraryState(scrapeRow({ running: true, pending: 5, failed: 1 }))).toBe("running")
  })

  it("失败记录（failed 或 error）优先于待刮削", () => {
    expect(libraryState(scrapeRow({ failed: 1, pending: 5 }))).toBe("failed")
    expect(libraryState(scrapeRow({ error: "invalid api key", pending: 5 }))).toBe("failed")
  })

  it("有待刮削 / 有未匹配 / 已干净 / 空库", () => {
    expect(libraryState(scrapeRow({ pending: 3 }))).toBe("pending")
    expect(libraryState(scrapeRow({ unmatched: 2 }))).toBe("unmatched")
    expect(libraryState(scrapeRow({ matched: 4 }))).toBe("clean")
    expect(libraryState(scrapeRow())).toBe("empty")
  })

  it("每种状态都有对应的 Tag 颜色", () => {
    expect(libraryStateTagType("running")).toBe("primary")
    expect(libraryStateTagType("failed")).toBe("danger")
    expect(libraryStateTagType("pending")).toBe("warning")
    expect(libraryStateTagType("unmatched")).toBe("info")
    expect(libraryStateTagType("clean")).toBe("success")
    expect(libraryStateTagType("empty")).toBe("info")
  })
})
