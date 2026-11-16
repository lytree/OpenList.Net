/**
 * 访客准入判定（纯函数，便于单测）。
 *
 * 站点有两个**独立门面**，各自一个开关（设置 → 访问控制）：
 * - 媒体 `media_guest_access`：默认 **开**，媒体是公开门面（默认落地页）；
 * - 文件 `home_guest_access`：默认 **关**，文件浏览需要登录，管理员可显式放开。
 *
 * 三条语义：
 * 1. 只写 `guestSwitch` 的路由（`/media`、`/files` 两棵树的**根**）：**开关说了算**。
 *    文件树因此默认要登录（开关是 false），放开后游客可只读浏览。
 * 2. 写了 `requiresAuth` 的路由：**与开关无关，永远要登录** —— 想看 / 历史 /
 *    播放列表 / 口味 / 任务中心 / 管理台都是「按用户」的数据，公开没有意义。
 *    这一条必须优先于 `guestSwitch`：`meta` 是**父子合并**的，`/media` 根上的
 *    `media_guest_access` 会继承到下面每一页，如果开关先判，`media_guest_access=true`
 *    就会把「播放列表要登录」一起放开。
 * 3. 两者都没有：公开页面（登录页、安装页、分享页）。
 *
 * 判定必须与后端 `IGuestAccessService` 完全一致，否则会出现「页面能进、接口 401」
 * 或者「接口能进、页面被弹走」的割裂。
 */

/** 路由 meta 里与访客策略相关的字段。 */
export interface GuestGateMeta {
  /** 与开关无关的硬门槛：这一页必须登录。 */
  requiresAuth?: boolean
  /** 公开设置里的开关键名；未登录能不能进这一页由它决定。 */
  guestSwitch?: string
}

/**
 * 游客是否被挡在门外（true = 需要先登录）。
 *
 * @param meta   目标路由的 meta（已由 vue-router 合并父子 meta）。
 * @param getBool 读取公开设置布尔值（`"true"` / `"1"` 为真）。
 */
export function guestBlocked(meta: GuestGateMeta, getBool: (key: string) => boolean): boolean {
  if (meta.requiresAuth) return true
  if (meta.guestSwitch) return !getBool(meta.guestSwitch)
  return false
}

/** 站点两个门面的访客开关键名（与后端设置表同名，别在这里造新名字）。 */
export const GuestSwitch = {
  media: "media_guest_access",
  home: "home_guest_access",
} as const
