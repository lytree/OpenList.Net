/**
 * 访客准入判定（纯函数，便于单测）。
 *
 * 站点有两个**独立门面**，各自一个开关（设置 → 访问控制）：
 * - 媒体 `media_guest_access`：默认 **开**，媒体是公开门面（落地页）；
 * - 首页 `home_guest_access`：默认 **关**，文件浏览需要登录，管理员可显式放开。
 *
 * 三条语义：
 * 1. 有 `guestSwitch` 的路由：**开关说了算** —— 关掉就必须登录（媒体关掉后走这条路；
 *    首页因为同时写了 `requiresAuth`，默认也走这里，开关一开就放行游客）。
 * 2. 没有 `guestSwitch` 但有 `requiresAuth`：与开关无关，永远要登录（播放列表、口味画像、管理台）。
 * 3. 两者都没有：公开页面（登录页、分享页）。
 *
 * 判定必须与后端 `IGuestAccessService` 完全一致，否则会出现「页面能进、接口 401」
 * 或者「接口能进、页面被弹走」的割裂。
 */

/** 路由 meta 里与访客策略相关的字段。 */
export interface GuestGateMeta {
  /** 需要登录（默认允许游客浏览，写操作由按钮级权限控制）。 */
  requiresAuth?: boolean
  /** 公开设置里的开关键名；未登录能不能进这一页由它决定。 */
  guestSwitch?: string
}

/**
 * 游客是否被挡在门外（true = 需要先登录）。
 *
 * @param meta   目标路由的 meta。
 * @param getBool 读取公开设置布尔值（`"true"` / `"1"` 为真）。
 */
export function guestBlocked(meta: GuestGateMeta, getBool: (key: string) => boolean): boolean {
  if (meta.guestSwitch) return !getBool(meta.guestSwitch)
  return Boolean(meta.requiresAuth)
}

/** 站点两个门面的访客开关键名（与后端设置表同名，别在这里造新名字）。 */
export const GuestSwitch = {
  media: "media_guest_access",
  home: "home_guest_access",
} as const
