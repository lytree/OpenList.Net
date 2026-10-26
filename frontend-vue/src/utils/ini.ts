/**
 * `.url`（Windows Internet Shortcut）解析。
 *
 * 文件就是一个小 INI：
 * ```
 * [InternetShortcut]
 * URL=https://example.com/
 * ```
 * 规范细节（大小写、注释、BOM、CRLF）各家写法不一，这里按宽松规则解析：
 * - 段名与键名大小写不敏感；
 * - `;` 与 `#` 开头为注释；
 * - 值两侧空白裁掉，允许值里出现 `=`（只按第一个 `=` 切分）。
 */

/** 解析 INI 文本为「段名 → 键值」映射，段名与键名均转小写。 */
export function parseIni(text: string): Record<string, Record<string, string>> {
  const result: Record<string, Record<string, string>> = {}
  let section = ""
  for (const rawLine of text.replace(/^\uFEFF/, "").split(/\r?\n/)) {
    const line = rawLine.trim()
    if (!line || line.startsWith(";") || line.startsWith("#")) continue
    if (line.startsWith("[") && line.endsWith("]")) {
      section = line.slice(1, -1).trim().toLowerCase()
      result[section] ??= {}
      continue
    }
    const eq = line.indexOf("=")
    if (eq <= 0) continue
    const key = line.slice(0, eq).trim().toLowerCase()
    const value = line.slice(eq + 1).trim()
    // 落在任何段之外的键（不规范的 .url）归到空段名下，仍可被读到
    result[section] ??= {}
    result[section][key] = value
  }
  return result
}

/**
 * 从 `.url` 文件内容里取目标地址；取不到返回空串。
 *
 * 只认 `InternetShortcut.URL`，但允许键名大小写不一致；
 * 少数导出工具会写 `[DEFAULT]` 段，一并兜住。
 */
export function parseUrlShortcut(text: string): string {
  const ini = parseIni(text)
  const candidates = [ini.internetshortcut, ini.default, ini[""]]
  for (const section of candidates) {
    const value = section?.url
    if (value) return value
  }
  return ""
}
