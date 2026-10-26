/**
 * 展示层格式化。
 *
 * 体积：1024 进制、两位小数（与旧前端观感一致，别改成 1000 进制，
 * 否则同一个文件在两套前端里显示的大小会不同）。
 */
const UNITS = ["B", "KB", "MB", "GB", "TB", "PB"] as const

export function humanSize(bytes: number | undefined | null, empty = "-"): string {
  if (bytes == null) return empty
  if (bytes < 0) return empty
  if (bytes === 0) return "0 B"

  let value = bytes
  let unit = 0
  while (value >= 1024 && unit < UNITS.length - 1) {
    value /= 1024
    unit += 1
  }
  return `${value.toFixed(unit === 0 ? 0 : 2)} ${UNITS[unit]}`
}

/** ISO 时间 → 本地 `YYYY-MM-DD HH:mm`；无效时间返回原串（后端可能给空串）。 */
export function formatTime(input: string | undefined | null, empty = "-"): string {
  if (!input) return empty
  const date = new Date(input)
  if (Number.isNaN(date.getTime())) return input
  const pad = (n: number) => String(n).padStart(2, "0")
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(
    date.getHours(),
  )}:${pad(date.getMinutes())}`
}
