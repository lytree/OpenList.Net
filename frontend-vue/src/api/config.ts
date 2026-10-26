/**
 * 运行时配置：API 基址与 base_path。
 *
 * 后端（OpenList）会在下发的 index.html 里注入 `window.OPENLIST_CONFIG`，
 * 桌面端（Avalonia）也通过它指定本地 API 地址；开发态则回落到 VITE_API_URL。
 * 结构与旧前端保持一致，避免部署侧需要改注入脚本。
 */
declare global {
  interface Window {
    OPENLIST_CONFIG?: {
      api?: string
      base_path?: string
      main_color?: string
      [key: string]: unknown
    }
  }
}

export let basePath = ""

export function setBasePath(path: string) {
  basePath = path.startsWith("/") ? path : `/${path}`
  if (basePath.endsWith("/")) basePath = basePath.slice(0, -1)
}

function resolveApi(): string {
  let api = (import.meta.env.VITE_API_URL as string | undefined) ?? "/"
  const injected = window.OPENLIST_CONFIG?.api
  if (injected) api = injected
  // "/" 表示「与页面同源」，这时代码由后端直接托管。
  if (api === "/") api = location.origin + basePath
  return api.endsWith("/") ? api.slice(0, -1) : api
}

if (window.OPENLIST_CONFIG?.base_path) {
  setBasePath(window.OPENLIST_CONFIG.base_path)
}

export const apiBase = resolveApi()

/** 拼接可对外访问的资源地址（缩略图、直链等）。 */
export function apiUrl(path: string): string {
  if (!path) return ""
  if (/^https?:\/\//i.test(path)) return path
  return `${apiBase}${path.startsWith("/") ? "" : "/"}${path}`
}
