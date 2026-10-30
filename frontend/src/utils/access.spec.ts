import { describe, expect, it } from "vitest"
import { GuestSwitch, guestBlocked } from "./access"

/**
 * 访客准入判定的四条边界。
 *
 * 这些用例存在的理由：守卫判错的方向只有两种，而且都很隐蔽 ——
 * 判宽了 = 页面能进但接口 401（用户看到一片报错）；判严了 = 明明开了「允许游客」
 * 却还是被弹去登录页。两个门面的默认值又相反，光靠人肉看代码很难保证没写反。
 */
const bools = (map: Record<string, string>) => (key: string) => map[key] === "true"

describe("guestBlocked", () => {
  it("媒体：开关默认开 → 游客放行", () => {
    const meta = { guestSwitch: GuestSwitch.media }
    expect(guestBlocked(meta, bools({ media_guest_access: "true" }))).toBe(false)
  })

  it("媒体：开关关掉 → 游客必须登录", () => {
    const meta = { guestSwitch: GuestSwitch.media }
    expect(guestBlocked(meta, bools({ media_guest_access: "false" }))).toBe(true)
  })

  it("首页：默认（开关 false）→ 必须登录", () => {
    const meta = { requiresAuth: true, guestSwitch: GuestSwitch.home }
    expect(guestBlocked(meta, bools({ home_guest_access: "false" }))).toBe(true)
  })

  it("首页：开关打开 → 游客可只读浏览", () => {
    const meta = { requiresAuth: true, guestSwitch: GuestSwitch.home }
    expect(guestBlocked(meta, bools({ home_guest_access: "true" }))).toBe(false)
  })

  it("只写 requiresAuth 的页面与开关无关（播放列表 / 口味 / 管理台）", () => {
    const meta = { requiresAuth: true }
    // 就算两个开关都开着，这些「按用户」的页面也要求登录
    expect(guestBlocked(meta, bools({ media_guest_access: "true", home_guest_access: "true" }))).toBe(true)
  })

  it("公开页面（登录页 / 分享页）不受影响", () => {
    expect(guestBlocked({}, bools({}))).toBe(false)
  })

  it("设置键缺失时按「关」处理：宁可要登录，也不要把私有目录当公开", () => {
    const meta = { requiresAuth: true, guestSwitch: GuestSwitch.home }
    expect(guestBlocked(meta, bools({}))).toBe(true)
  })

  it("两个开关键名与后端设置表一致（改名会同时打穿前后端）", () => {
    expect(GuestSwitch.media).toBe("media_guest_access")
    expect(GuestSwitch.home).toBe("home_guest_access")
  })
})
