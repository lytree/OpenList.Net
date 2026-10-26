/**
 * 路径工具。
 *
 * 与旧前端 `utils/path.ts` 保持同名同义 —— 这些函数被文件操作、分享签名、
 * 下载直链反复使用，语义漂移会直接导致「能进目录但打不开文件」这类难查问题。
 * 与旧实现的唯一差异：不再依赖全局 `base_path`（新前端用 `api/config.ts` 的
 * `basePath` + `apiBase`，这里只做纯字符串运算，便于单测）。
 */

/** 规范化：保证以 `/` 开头、不以 `/` 结尾；`noRootSlash` 时根目录返回空串。 */
export function standardizePath(path: string, noRootSlash = false): string {
  let result = path
  if (result.endsWith("/")) result = result.slice(0, -1)
  if (!result.startsWith("/")) result = `/${result}`
  if (noRootSlash && result === "/") return ""
  return result
}

/** 拼接并合并多余斜杠。 */
export function pathJoin(...paths: string[]): string {
  return paths.join("/").replace(/\/{2,}/g, "/")
}

/** 取最后一段（文件名）。 */
export function pathBase(path: string): string {
  return path.split("/").pop() ?? ""
}

/** 取父目录；根目录的父目录仍是 `/`。 */
export function pathDir(path: string): string {
  const dir = path.split("/").slice(0, -1).join("/")
  return dir || "/"
}

/**
 * 扩展名（不含点，小写）。无扩展名返回空串。
 * 注意：`.gitignore` 这类点开头文件按旧前端语义**算作**扩展名 `gitignore` ——
 * 预览器按扩展名分流时两边行为必须一致。
 */
export function ext(path: string): string {
  const base = pathBase(path)
  const index = base.lastIndexOf(".")
  if (index < 0) return ""
  return base.slice(index + 1).toLowerCase()
}

/** 去掉扩展名的主文件名。 */
export function baseName(fullName: string): string {
  const index = fullName.lastIndexOf(".")
  return index <= 0 ? fullName : fullName.slice(0, index)
}

/**
 * 逐段编码路径。`all=false` 时只转义会破坏 URL 结构的字符（保留中文原样，
 * 便于后端按原文匹配）；`all=true` 时整体 `encodeURIComponent`。
 */
export function encodePath(path: string, all = false): string {
  return path
    .split("/")
    .map((segment) =>
      all
        ? encodeURIComponent(segment)
        : segment
            .replace(/%/g, "%25")
            .replace(/\?/g, "%3F")
            .replace(/#/g, "%23")
            .replace(/ /g, "%20"),
    )
    .join("/")
}

/** 面包屑：把 `/a/b/c` 拆成 [{name:"a",path:"/a"}, …]，根目录用 `root` 标签。 */
export function pathSegments(path: string, rootLabel = "/"): { name: string; path: string }[] {
  const segments = standardizePath(path).split("/").filter(Boolean)
  const result = [{ name: rootLabel, path: "/" }]
  let current = ""
  for (const segment of segments) {
    current += `/${segment}`
    result.push({ name: segment, path: current })
  }
  return result
}
