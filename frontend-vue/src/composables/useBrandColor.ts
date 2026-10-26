import { watchEffect } from "vue"
import { useTheme } from "./useTheme"

/**
 * 品牌色注入。
 *
 * 站点设置里的 `main_color`（或部署注入的 `OPENLIST_CONFIG.main_color`）是唯一真源，
 * 这里把它换算成 Element Plus 需要的一整套 `--el-color-primary*` 变量。
 * 不这么做的话，只有「主色」变了，hover/边框/浅底色仍是 EP 默认蓝，观感割裂。
 */
function parseHex(hex: string): [number, number, number] {
  const raw = hex.replace("#", "").trim()
  const full =
    raw.length === 3
      ? raw
          .split("")
          .map((c) => c + c)
          .join("")
      : raw
  const int = Number.parseInt(full.slice(0, 6), 16)
  return [(int >> 16) & 255, (int >> 8) & 255, int & 255]
}

function toHex([r, g, b]: [number, number, number]): string {
  return `#${[r, g, b].map((v) => Math.round(v).toString(16).padStart(2, "0")).join("")}`
}

/** 按权重把 from 混向 to（weight=0 取 from，1 取 to）。 */
function mix(from: string, to: string, weight: number): string {
  const a = parseHex(from)
  const b = parseHex(to)
  return toHex([
    a[0] + (b[0] - a[0]) * weight,
    a[1] + (b[1] - a[1]) * weight,
    a[2] + (b[2] - a[2]) * weight,
  ])
}

const LIGHT_STEPS = [3, 5, 7, 8, 9] as const

export function applyBrandColor(color: string, dark: boolean) {
  const root = document.documentElement.style
  const valid = /^#([0-9a-f]{3}|[0-9a-f]{6})$/i.test(color)
  const primary = valid ? color : "#1890ff"

  root.setProperty("--ol-brand", primary)
  root.setProperty("--el-color-primary", primary)
  root.setProperty("--el-color-primary-dark-2", mix(primary, "#000000", 0.2))

  // 深色模式下 EP 的浅色阶是「向背景色靠拢」，混白会刺眼，改混深底。
  const base = dark ? "#141414" : "#ffffff"
  for (const step of LIGHT_STEPS) {
    root.setProperty(`--el-color-primary-light-${step}`, mix(primary, base, step / 10))
  }
}

/** 在 App 根部调用一次：品牌色 / 深浅色变化时自动重算变量。 */
export function useBrandColor(getColor: () => string) {
  const { isDark } = useTheme()
  watchEffect(() => {
    applyBrandColor(getColor(), isDark.value)
  })
}
