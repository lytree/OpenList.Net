import { describe, expect, it } from "vitest"
import { GuestSwitch, guestBlocked } from "./access"

/**
 * 访客准入判定的边界。
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

  it("文件：默认（开关 false）→ 必须登录", () => {
    const meta = { guestSwitch: GuestSwitch.home }
    expect(guestBlocked(meta, bools({ home_guest_access: "false" }))).toBe(true)
  })

  it("文件：开关打开 → 游客可只读浏览", () => {
    const meta = { guestSwitch: GuestSwitch.home }
    expect(guestBlocked(meta, bools({ home_guest_access: "true" }))).toBe(false)
  })

  it("只写 requiresAuth 的页面与开关无关（想看 / 历史 / 播放列表 / 口味 / 任务中心）", () => {
    const meta = { requiresAuth: true }
    // 就算两个开关都开着，这些「按用户」的页面也要求登录
    expect(guestBlocked(meta, bools({ media_guest_access: "true", home_guest_access: "true" }))).toBe(
      true,
    )
  })

  it("父路由继承下来的 guestSwitch 不能把 requiresAuth 放开", () => {
    // `/media` 根上有 media_guest_access，子页面的 meta 会把它一并继承下来。
    // 如果开关先判，游客就能进「想看 / 播放列表」——这正是拆分两棵路由树时踩过的坑。
    const meta = { requiresAuth: true, guestSwitch: GuestSwitch.media }
    expect(guestBlocked(meta, bools({ media_guest_access: "true" }))).toBe(true)
  })

  it("公开页面（登录页 / 安装页）不受影响", () => {
    expect(guestBlocked({}, bools({}))).toBe(false)
  })

  it("设置键缺失时按「关」处理：宁可要登录，也不要把私有目录当公开", () => {
    const meta = { guestSwitch: GuestSwitch.home }
    expect(guestBlocked(meta, bools({}))).toBe(true)
  })

  it("两个开关键名与后端设置表一致（改名会同时打穿前后端）", () => {
    expect(GuestSwitch.media).toBe("media_guest_access")
    expect(GuestSwitch.home).toBe("home_guest_access")
  })
})
