import { defineAsyncComponent } from "vue"
import { ObjType } from "~/api/types"
import { t } from "~/i18n"
import { ARCHIVE_EXTS } from "~/utils/archive"
import { ext } from "~/utils/path"
import type { PreviewContext, PreviewDef, PreviewEntry } from "./types"

/**
 * 预览器注册表。
 *
 * 分派顺序（对齐旧前端 `previews/index.ts`）：
 * 1. 命中内置定义（type / 扩展名 / 驱动 / 自定义 match）；
 * 2. 追加设置里的 iframe 预览（管理员配的外部预览站点）；
 * 3. 末尾永远有「下载」兜底；
 * 4. 一个都没命中且文件较小时，额外挂一个「按文本打开」——只作为用户手选项，
 *    不自动选中，避免把二进制文件渲染成乱码。
 */

/** 大于该体积的文本不自动预览（但仍可从下拉里手动选，预览器内部做截断）。 */
export const AUTO_TEXT_PREVIEW_LIMIT = 1 * 1024 * 1024

/** 「按文本打开」兜底识别：后端未分类成 TEXT、但内容多半可读的扩展名。 */
const TEXTUAL_EXTS = [
  "txt", "log", "json", "jsonc", "json5", "xml", "yml", "yaml", "toml", "ini", "conf", "cfg",
  "env", "properties", "csv", "tsv", "srt", "vtt", "ass", "ssa", "lrc", "nfo", "diff", "patch",
  "sql", "sh", "bash", "zsh", "fish", "ps1", "bat", "cmd",
  "py", "js", "mjs", "cjs", "ts", "tsx", "jsx", "vue", "svelte", "go", "rs", "java", "kt", "kts",
  "c", "h", "cc", "cpp", "hpp", "cs", "fs", "php", "rb", "swift", "scala", "lua", "pl", "r",
  "css", "scss", "sass", "less", "styl", "graphql", "gql", "proto", "dockerfile", "makefile",
  "gitignore", "gitattributes", "editorconfig", "license", "readme", "lock",
]

const defs: PreviewDef[] = [
  {
    key: "archive",
    exts: ARCHIVE_EXTS,
    order: 15,
    component: defineAsyncComponent(() => import("./ArchivePreview")),
  },
  {
    key: "torrent",
    // 后端把 .torrent 归为 UNKNOWN，只能靠扩展名命中；顺序排在 archive 之后、
    // 文本/下载兜底之前，否则会被当未知文本打开
    exts: ["torrent"],
    order: 16,
    component: defineAsyncComponent(() => import("./TorrentPreview")),
  },
  {
    key: "markdown",
    exts: ["md", "markdown", "mdx"],
    order: 10,
    component: defineAsyncComponent(() => import("./MarkdownPreview")),
  },
  {
    key: "html",
    exts: ["html", "htm", "xhtml"],
    order: 10,
    component: defineAsyncComponent(() => import("./HtmlPreview")),
  },
  {
    key: "image",
    type: ObjType.IMAGE,
    // svg 归图片而不是 HTML：`<img>` 渲染不会执行里面的脚本，比 srcdoc 更安全
    exts: ["avif", "avifs", "bmp", "gif", "ico", "jfif", "jpeg", "jpg", "png", "svg", "webp"],
    order: 20,
    component: defineAsyncComponent(() => import("./ImagePreview")),
  },
  {
    key: "video",
    type: ObjType.VIDEO,
    exts: ["mp4", "mkv", "webm", "mov", "m4v", "avi", "flv", "ts", "m2ts", "wmv"],
    order: 20,
    component: defineAsyncComponent(() => import("./VideoPreview")),
  },
  {
    key: "audio",
    type: ObjType.AUDIO,
    exts: ["mp3", "flac", "wav", "aac", "m4a", "ogg", "opus", "wma", "ape", "alac"],
    order: 20,
    component: defineAsyncComponent(() => import("./AudioPreview")),
  },
  {
    key: "pdf",
    exts: ["pdf"],
    order: 20,
    component: defineAsyncComponent(() => import("./PdfPreview")),
  },
  {
    key: "url_open",
    exts: ["url"],
    order: 20,
    component: defineAsyncComponent(() => import("./UrlPreview")),
  },
  {
    key: "plist",
    exts: ["plist"],
    order: 20,
    component: defineAsyncComponent(() => import("./PlistPreview")),
  },
  {
    key: "ipa",
    exts: ["ipa"],
    order: 20,
    component: defineAsyncComponent(() => import("./IpaPreview")),
  },
  {
    key: "doc",
    // 只接 OOXML（.docx）。.doc 是 OLE 容器，浏览器里没有可靠解析器。
    exts: ["docx", "docm", "dotx"],
    order: 20,
    component: defineAsyncComponent(() => import("./DocPreview")),
  },
  {
    key: "xls",
    exts: ["xls", "xlsx", "xlsm", "xlsb"],
    order: 20,
    component: defineAsyncComponent(() => import("./XlsPreview")),
  },
  {
    key: "ppt",
    exts: ["pptx", "pptm", "ppsx"],
    order: 20,
    component: defineAsyncComponent(() => import("./PptPreview")),
  },
  {
    key: "asciinema",
    exts: ["cast"],
    order: 20,
    component: defineAsyncComponent(() => import("./AsciinemaPreview")),
  },
  {
    key: "text",
    // 后端把可识别的文本统一标成 TEXT；扩展名白名单再兜一层，
    // 覆盖后端按「未知」处理但其实是纯文本的情况（.conf/.env/.lock …）。
    type: ObjType.TEXT,
    exts: TEXTUAL_EXTS,
    order: 30,
    component: defineAsyncComponent(() => import("./TextPreview")),
  },
  {
    key: "text_editor",
    // 排在只读预览之后：默认仍是「看一眼」，想改的人再从下拉切过来
    type: ObjType.TEXT,
    exts: TEXTUAL_EXTS,
    order: 40,
    component: defineAsyncComponent(() => import("./TextEditorPreview")),
  },
]

const DOWNLOAD_DEF: PreviewDef = {
  key: "download",
  order: 1000,
  component: defineAsyncComponent(() => import("./DownloadPreview")),
}

const INFO_DEF: PreviewDef = {
  key: "info",
  order: 200,
  component: defineAsyncComponent(() => import("./InfoPreview")),
}

/** iframe 预览共用一个异步组件：每个条目各建一个会反复触发 chunk 加载。 */
const IFRAME_COMPONENT = defineAsyncComponent(() => import("./IframePreview"))

/** 设置 `iframe_previews` 的解析结果：扩展名/正则 → { 显示名: URL 模板 }。 */
export type IframePreviewMap = Record<string, Record<string, string>>

export function parseIframePreviews(raw: string | undefined | null): IframePreviewMap {
  if (!raw) return {}
  try {
    const parsed = JSON.parse(raw) as unknown
    if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) return {}
    const result: IframePreviewMap = {}
    for (const [key, value] of Object.entries(parsed as Record<string, unknown>)) {
      if (!value || typeof value !== "object" || Array.isArray(value)) continue
      const schemes: Record<string, string> = {}
      for (const [name, scheme] of Object.entries(value as Record<string, unknown>)) {
        if (typeof scheme === "string" && scheme) schemes[name] = scheme
      }
      if (Object.keys(schemes).length > 0) result[key] = schemes
    }
    return result
  } catch {
    // 设置写错时静默降级：预览列表里不出现 iframe 项，比整页报错好。
    return {}
  }
}

/** 设置键匹配：`ppt,pptx` 按扩展名，`/^pdf$/` 按正则（对齐旧前端）。 */
function iframeKeyMatches(key: string, name: string): boolean {
  const extension = ext(name)
  if (key.startsWith("/")) {
    const end = key.lastIndexOf("/")
    if (end <= 0) return false
    try {
      return new RegExp(key.slice(1, end), key.slice(end + 1)).test(extension)
    } catch {
      return false
    }
  }
  return key
    .split(",")
    .map((part) => part.trim().toLowerCase())
    .filter(Boolean)
    .includes(extension)
}

/**
 * URL 模板变量替换，与旧前端 `convertURL` 同语义：
 * `$url`/`$eurl`/`$burl`（预览代理地址，e=encodeURIComponent，b=base64）、
 * `$durl`/`$edurl`/`$bdurl`（下载直链）、`$name`。
 */
export function convertUrl(
  scheme: string,
  args: { raw_url: string; d_url: string; name: string; ts?: boolean },
): string {
  let dUrl = args.d_url
  if (args.ts) {
    try {
      const url = new URL(dUrl, location.origin)
      url.searchParams.set("openlist_ts", String(Date.now()))
      dUrl = url.toString()
    } catch {
      // 相对路径在部分环境下 new URL 会失败，保持原样即可
    }
  }
  const wrap = (value: string, flags: string | undefined) => {
    let out = value
    for (const flag of flags ?? "") {
      if (flag === "e") out = encodeURIComponent(out)
      else if (flag === "b") out = btoa(out)
    }
    return out
  }
  return scheme
    .replace("$name", args.name)
    .replace(/\$[eb_]*url/g, (matched) => wrap(args.raw_url, matched.match(/[eb]/g)?.join("")))
    .replace(/\$[eb_]*durl/g, (matched) => wrap(dUrl, matched.match(/[eb]/g)?.join("")))
}

function matches(ctx: PreviewContext, def: PreviewDef): boolean {
  if (def.provider && !def.provider.test(ctx.provider)) return false
  if (def.match && def.match(ctx)) return true
  if (def.type !== undefined && def.type === ctx.type) return true
  if (def.exts && def.exts.includes(ext(ctx.name))) return true
  return false
}

/**
 * 按扩展名猜 `ObjType`。
 *
 * 归档内条目只有名字和大小（后端不解压就分类不了），而预览器分派依赖 `ObjType`，
 * 所以从注册表自己的 `type` 声明反推 —— 不另维护一张表，避免两处漂移。
 */
export function guessObjType(name: string): ObjType {
  const extension = ext(name)
  for (const def of defs) {
    if (def.type !== undefined && def.exts?.includes(extension)) return def.type
  }
  return ObjType.UNKNOWN
}

export interface ResolveOptions {
  provider?: string
  path?: string
  /** 设置里的 `iframe_previews`（已解析）。 */
  iframePreviews?: IframePreviewMap
  /** 设置 `preview_download_by_default` 为真时，「下载」排到最前。 */
  downloadFirst?: boolean
  /** 额外条目（如归档内预览时注入的上一级入口），由调用方决定顺序。 */
  extra?: PreviewEntry[]
}

export function resolvePreviews(
  obj: { name: string; type: ObjType; size: number },
  opts: ResolveOptions = {},
): PreviewEntry[] {
  const ctx: PreviewContext = {
    name: obj.name,
    type: obj.type,
    size: obj.size,
    provider: opts.provider ?? "",
    path: opts.path ?? "",
  }

  const entries: PreviewEntry[] = []
  for (const def of defs) {
    if (!matches(ctx, def)) continue
    if (def.available && !def.available(ctx)) continue
    entries.push({
      key: def.key,
      name: t(`home.preview.names.${def.key}`),
      component: def.component,
      order: def.order ?? 100,
    })
  }

  // 设置驱动的 iframe 预览：外部站点按 URL 模板接管某类文件。
  const iframeMap = opts.iframePreviews ?? {}
  for (const [key, schemes] of Object.entries(iframeMap)) {
    if (!iframeKeyMatches(key, obj.name)) continue
    for (const [label, scheme] of Object.entries(schemes)) {
      entries.push({
        key: `iframe:${key}:${label}`,
        name: label,
        component: IFRAME_COMPONENT,
        order: 90,
        props: { scheme },
      })
    }
  }

  // 先判断「有没有真正的预览能力」，它决定下载与信息页的先后：
  // 有预览时下载是兜底（排最后），没预览时下载才是用户最可能的意图（排最前）。
  const hasRealPreview = entries.some((entry) => entry.order < 200)

  const infoOrder = hasRealPreview ? 200 : 300
  entries.push({
    key: INFO_DEF.key,
    name: t("home.preview.names.info"),
    component: INFO_DEF.component,
    order: infoOrder,
  })

  // 未知类型 + 小文件：给一个「按文本打开」的手选项，排在下载之后。
  if (!hasRealPreview && obj.size <= AUTO_TEXT_PREVIEW_LIMIT) {
    const textDef = defs.find((def) => def.key === "text")!
    entries.push({
      key: "text",
      name: t("home.preview.names.text"),
      component: textDef.component,
      order: 1001,
    })
  }

  const downloadOrder = opts.downloadFirst ? -1 : hasRealPreview ? (DOWNLOAD_DEF.order ?? 1000) : 250
  entries.push({
    key: DOWNLOAD_DEF.key,
    name: t("home.preview.names.download"),
    component: DOWNLOAD_DEF.component,
    order: downloadOrder,
  })

  if (opts.extra?.length) entries.push(...opts.extra)

  // 稳定排序：order 相同保持注册表顺序（Array#sort 在 ES2019 起保证稳定）
  return entries.sort((a, b) => a.order - b.order)
}
