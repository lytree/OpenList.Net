import type { SystemFeatures } from "~/api/system"

/**
 * 「关于」页的展示口径（纯函数，见 docs §38）。
 *
 * 放在这里而不是组件里，是为了让每条口径都能单测 —— 这些函数都在**换算**后端给的原始值
 * （秒、sha、时间戳），算错了页面上看起来仍然是「一串数字」，没人会发现。
 */

/** 提交 SHA 的短形式（默认 7 位，与 `git log --oneline` 一致）。 */
export function shortCommit(commit: string | undefined | null, length = 7): string {
  const raw = (commit ?? "").trim()
  if (!raw) return ""
  // 短于 length 时原样返回：某些构建只注入 7 位，截断只会得到更短的怪串
  return raw.length <= length ? raw : raw.slice(0, length)
}

/**
 * 运行时长：`2d 3h` / `1h 5m` / `45s` / `0s`。
 *
 * 只保留**两个**量级：`1d 0h 3m 12s` 这种四位数字在卡片里既读不快也放不下，
 * 而「跑了几天」的精度到小时就足够了。负数（后端拿不到进程启动时间时给 -1）
 * 统一显示 `-`，不要渲染成 `-1s` 那种像 bug 的东西。
 */
export function formatUptime(seconds: number | undefined | null): string {
  if (seconds == null || !Number.isFinite(seconds) || seconds < 0) return "-"

  const total = Math.floor(seconds)
  if (total < 60) return `${total}s`

  const days = Math.floor(total / 86400)
  const hours = Math.floor((total % 86400) / 3600)
  const minutes = Math.floor((total % 3600) / 60)

  if (days > 0) return `${days}d ${hours}h`
  if (hours > 0) return `${hours}h ${minutes}m`
  return `${minutes}m ${total % 60}s`
}

/**
 * 服务器时间与本机时钟的偏差（秒，正数表示服务器快）。
 *
 * 返回 `null` 表示后端没给可解析的时间 —— 此时页面显示 `-`，
 * 绝不能把 `NaN` 当成「没有偏差」显示成 `0s`。
 */
export function clockSkewSeconds(
  serverTime: string | undefined | null,
  clientNow: number = Date.now(),
): number | null {
  if (!serverTime) return null
  const server = new Date(serverTime).getTime()
  if (Number.isNaN(server)) return null
  return Math.round((server - clientNow) / 1000)
}

/** 偏差的可读形式：`+3s` / `-45s` / `0s`；`null` 时为 `-`。 */
export function formatSkew(seconds: number | null | undefined): string {
  if (seconds == null || !Number.isFinite(seconds)) return "-"
  if (seconds === 0) return "0s"
  return `${seconds > 0 ? "+" : ""}${seconds}s`
}

/** 能力标签的固定顺序与展示名（关闭的也列出来 —— 「什么没开」同样是信息）。 */
const CAPABILITIES: { key: keyof SystemFeatures; label: string }[] = [
  { key: "s3", label: "S3" },
  { key: "ftp", label: "FTP" },
  { key: "sftp", label: "SFTP" },
  { key: "ldap", label: "LDAP" },
  { key: "mcp", label: "MCP" },
]

/**
 * 把 `features` 摊平成标签列表。
 *
 * 只认 `CAPABILITIES` 里列出的布尔项：`search_index` 是字符串、
 * `max_connections` 是数字，它们各自单独成行（标签里塞 `database` 会长短不齐）。
 */
export function capabilityTags(features: SystemFeatures | undefined | null): {
  key: string
  label: string
  enabled: boolean
}[] {
  if (!features) return []
  return CAPABILITIES.map(({ key, label }) => ({
    key: String(key),
    label,
    enabled: Boolean(features[key]),
  }))
}

/** `0` 表示不限制，直接显示 0 会被误读成「一个连接都不许」。 */
export function formatMaxConnections(value: number | undefined | null): string {
  if (value == null || !Number.isFinite(value)) return "-"
  return value <= 0 ? "∞" : String(value)
}
