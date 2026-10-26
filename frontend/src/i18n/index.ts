import { computed, ref } from "vue"

/**
 * 轻量 i18n。
 *
 * 为什么不用 vue-i18n：
 * 1. 现有 `src/lang/**` 字典直接沿用 SolidJS 前端（键路径、`{param}` 占位符语义完全一致），
 *    换成 vue-i18n 需要重写全部文案格式，且其消息语法会把 `|` 当复数分隔符——
 *    `global.json` 里恰好有一条含 `|` 的文案，会产生静默错译。
 * 2. 这里只做两件事：按点路径取值 + `{param}` 插值，行为可用单测锁定，无编译器开销。
 *
 * 与旧实现保持一致的三条语义：
 * - 缺键回退到英文（en），再缺则返回 key 本身（UI 上立刻暴露漏翻）。
 * - 语言切换是响应式的：t() 在渲染中读取 locale，切换即重渲染。
 * - 语言偏好持久化在 localStorage["lang"]。
 */

type Dict = { [key: string]: string | Dict }

/** Vite 在构建期把每个字典文件静态收集为 { 路径: 默认导出 }。 */
const modules = import.meta.glob<{ default: Dict }>("../lang/*/*.json", {
  eager: true,
})

/** locale -> namespace -> 字典 的嵌套结构，例如 dicts.en.login.login_to */
const dicts: Record<string, Record<string, Dict>> = {}

for (const [path, mod] of Object.entries(modules)) {
  // ../lang/en/login.json -> ["en", "login"]
  const parts = path.replace(/^.*\/lang\//, "").replace(/\.json$/, "").split("/")
  const [locale, ns] = parts
  if (!locale || !ns || ns === "index") continue
  ;(dicts[locale] ??= {})[ns] = mod.default ?? (mod as unknown as Dict)
}

export const availableLanguages = Object.keys(dicts)
  .map((code) => ({
    code,
    // 语言名写在 lang/<code>/index.json 的 lang 字段，字典本身没有该命名空间，
    // 因此单独读取原始模块。
    name: readLanguageName(code),
  }))
  .sort((a, b) => a.code.localeCompare(b.code))

function readLanguageName(code: string): string {
  const key = `../lang/${code}/index.json`
  const mod = modules[key] as unknown as { default?: { lang?: string } } | undefined
  return mod?.default?.lang ?? code
}

const FALLBACK_LOCALE = "en"

function detectLocale(): string {
  const saved = localStorage.getItem("lang")
  if (saved && dicts[saved]) return saved

  const browser = navigator.language?.toLowerCase() ?? ""
  const exact = availableLanguages.find((l) => l.code.toLowerCase() === browser)
  if (exact) return exact.code
  const prefix = availableLanguages.find(
    (l) => l.code.toLowerCase().split("-")[0] === browser.split("-")[0],
  )
  return prefix?.code ?? FALLBACK_LOCALE
}

export const locale = ref<string>(detectLocale())

export function setLocale(next: string) {
  if (!dicts[next]) return
  locale.value = next
  localStorage.setItem("lang", next)
  document.documentElement.lang = next
}

document.documentElement.lang = locale.value

/** 按点路径在字典中取值，遇到对象/缺失都返回 undefined。 */
function lookup(localeCode: string, key: string): string | undefined {
  const [ns, ...rest] = key.split(".")
  const root = dicts[localeCode]?.[ns]
  if (!root) return undefined

  let node: string | Dict | undefined = root
  for (const seg of rest) {
    if (typeof node !== "object" || node === null) return undefined
    node = node[seg]
  }
  return typeof node === "string" ? node : undefined
}

/**
 * 占位符语法：既有字典用的是 `{{param}}`（@solid-primitives/i18n 风格），
 * 也存在少量单花括号 `{param}`。两种都要支持，且必须一次扫描完成 ——
 * 分两次 replace 的话，第一个缺失参数保留原样后会被第二遍再解析一次，导致输出错乱。
 */
const PARAM_RE = /\{\{(\w+)\}\}|\{(\w+)\}/g

function interpolate(template: string, params?: Record<string, unknown>): string {
  if (!params) return template
  return template.replace(PARAM_RE, (raw, double: string, single: string) => {
    const name = double ?? single
    return name in params ? String(params[name]) : raw
  })
}

export type TranslateParams = Record<string, unknown>

/**
 * 翻译函数。模板里可以直接用（响应式），也可以在脚本里调用（取当前语言快照）。
 */
export function t(key: string, params?: TranslateParams): string {
  // 读一次 locale 建立依赖，切换语言时模板自动更新。
  const current = locale.value
  const text = lookup(current, key) ?? lookup(FALLBACK_LOCALE, key)
  if (text === undefined) return key
  return interpolate(text, params)
}

/** 组件的 setup 里用 useT() 拿到同一份响应式翻译函数（便于注入/测试替身）。 */
export function useT() {
  return (key: string, params?: TranslateParams) => t(key, params)
}

export const currentLanguage = computed(() =>
  availableLanguages.find((l) => l.code === locale.value),
)
