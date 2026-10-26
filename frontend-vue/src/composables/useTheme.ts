import { computed, ref, watchEffect } from "vue"

/**
 * 主题（浅色 / 深色 / 跟随系统）。
 *
 * 机制：Element Plus 的深色模式由 `<html class="dark">` 触发（样式来自
 * `element-plus/theme-chalk/dark/css-vars.css`），因此这里只负责维护这个 class，
 * 所有 EP 组件和我们自己的 --ol-* token 会自动跟随，不需要逐个组件改色。
 */
export type ThemeMode = "light" | "dark" | "auto"

const STORAGE_KEY = "theme-mode"

const media = window.matchMedia("(prefers-color-scheme: dark)")

const mode = ref<ThemeMode>(readMode())
const systemDark = ref(media.matches)

media.addEventListener?.("change", (e) => {
  systemDark.value = e.matches
})

function readMode(): ThemeMode {
  const saved = localStorage.getItem(STORAGE_KEY)
  return saved === "light" || saved === "dark" || saved === "auto" ? saved : "auto"
}

const isDark = computed(() =>
  mode.value === "auto" ? systemDark.value : mode.value === "dark",
)

watchEffect(() => {
  const root = document.documentElement
  root.classList.toggle("dark", isDark.value)
  root.style.colorScheme = isDark.value ? "dark" : "light"
})

function setMode(next: ThemeMode) {
  mode.value = next
  localStorage.setItem(STORAGE_KEY, next)
}

function toggle() {
  setMode(isDark.value ? "light" : "dark")
}

export function useTheme() {
  return { mode, isDark, setMode, toggle }
}
