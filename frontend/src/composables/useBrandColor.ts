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

/** 站点没配 `main_color` 时的兜底：antd 的 purple-6。 */
export const DEFAULT_BRAND = "#722ed1"

/**
 * antd 官方 purple 阶梯中与 EP `light-3/5/7/8/9` 语义对应的几档。
 * 线性混白只能近似：`#722ed1` 混 90% 白得到 `#f1eafb`，而 antd 的 `colorPrimaryBg`
 * 是 `#f9f0ff`（HSV 提亮，不是 RGB 混合）。默认色走这张表，自定义色才退回混色。
 *
 * 深色不是「把浅色阶混黑」：antd dark 的 hover 比主色更亮（purple-8 #854eca），
 * active 更深（purple-5 #51258f），只有 tinted 底色才往暗处走（purple-1 #1a1325）。
 */
interface AntPalette {
  primary: string
  dark2: string
  steps: Record<number, string>
}

const ANT_PURPLE_LIGHT: AntPalette = {
  primary: "#722ed1",
  dark2: "#531dab",
  steps: { 3: "#9254de", 5: "#b37feb", 7: "#d3adf7", 8: "#efdbff", 9: "#f9f0ff" },
}

const ANT_PURPLE_DARK: AntPalette = {
  primary: "#642ab5",
  dark2: "#51258f",
  steps: { 3: "#854eca", 5: "#ab7ae0", 7: "#301c4d", 8: "#1a1325", 9: "#1a1325" },
}

export function applyBrandColor(color: string, dark: boolean) {
  const root = document.documentElement.style
  const valid = /^#([0-9a-f]{3}|[0-9a-f]{6})$/i.test(color)
  const seed = valid ? color : DEFAULT_BRAND
  const palette =
    seed.toLowerCase() === DEFAULT_BRAND ? (dark ? ANT_PURPLE_DARK : ANT_PURPLE_LIGHT) : null
  // 自定义色没有官方阶梯，只能按主题混向容器底色
  const base = dark ? "#141414" : "#ffffff"
  const primary = palette ? palette.primary : seed

  root.setProperty("--ol-brand", primary)
  root.setProperty("--el-color-primary", primary)
  root.setProperty(
    "--el-color-primary-dark-2",
    palette ? palette.dark2 : mix(seed, "#000000", 0.2),
  )

  for (const step of LIGHT_STEPS) {
    root.setProperty(
      `--el-color-primary-light-${step}`,
      palette ? palette.steps[step] : mix(seed, base, step / 10),
    )
  }
}

/** 在 App 根部调用一次：品牌色 / 深浅色变化时自动重算变量。 */
export function useBrandColor(getColor: () => string) {
  const { isDark } = useTheme()
  watchEffect(() => {
    applyBrandColor(getColor(), isDark.value)
  })
}
