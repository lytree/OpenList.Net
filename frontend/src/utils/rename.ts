/**
 * 批量重命名的规则计算（纯函数，不碰 DOM / 不发请求）。
 *
 * 三种模式与上游一致：正则替换、序号命名、查找替换。差别在于这里**先把整批
 * 结果算出来再校验**（非法名、撞名），而不是让用户点确定后才由后端报错 ——
 * 一次 500 之后用户已经不知道该改哪一条了。
 */

export type RenameMode = "regex" | "sequence" | "replace"

export interface RenameSource {
  name: string
  is_dir: boolean
  created?: string
  modified?: string
}

export interface RenameItem {
  src_name: string
  new_name: string
  /** 规则没命中，保持原名（不会提交）。 */
  unchanged: boolean
  /** 该条目的新名不合法时的原因。 */
  error?: string
}

export interface RenamePlan {
  /** 全部条目（含未变化的），用于预览。 */
  items: RenameItem[]
  /** 真正会提交的部分。 */
  changed: RenameItem[]
  /** 阻断提交的问题（非法名 / 撞名），每条都是一句可直接展示的话。 */
  problems: string[]
}

/** 文件名里不允许出现的字符（与 `UPath.IsValidName` 的意图一致）。 */
const FORBIDDEN = /[\\/:*?"<>|]/
const MAX_LENGTH = 255

/** 校验单个文件名；合法返回空串，否则返回原因。 */
export function validateName(name: string): string {
  const value = (name ?? "").trim()
  if (!value) return "文件名不能为空"
  if (value === "." || value === "..") return "文件名不能是 . 或 .."
  if (FORBIDDEN.test(value)) return `文件名不能包含 \\ / : * ? " < > |`
  if (value.length > MAX_LENGTH) return `文件名超过 ${MAX_LENGTH} 个字符`
  return ""
}

/** 把日期展开成 `{created_year}` 这类占位符的值（对齐上游的模板变量）。 */
export function expandTimePlaceholders(template: string, source: RenameSource): string {
  const created = source.created ? new Date(source.created) : null
  const modified = source.modified ? new Date(source.modified) : null
  const pad = (value: number, length = 2) => String(value).padStart(length, "0")
  const map: Record<string, string> = {}
  if (created && !Number.isNaN(created.getTime())) {
    map.created_year = pad(created.getFullYear(), 4)
    map.created_month = pad(created.getMonth() + 1)
    map.created_date = pad(created.getDate())
    map.created_hour = pad(created.getHours())
    map.created_minute = pad(created.getMinutes())
    map.created_second = pad(created.getSeconds())
  }
  if (modified && !Number.isNaN(modified.getTime())) {
    map.modified_year = pad(modified.getFullYear(), 4)
    map.modified_month = pad(modified.getMonth() + 1)
    map.modified_date = pad(modified.getDate())
    map.modified_hour = pad(modified.getHours())
    map.modified_minute = pad(modified.getMinutes())
    map.modified_second = pad(modified.getSeconds())
  }
  return template.replace(/\{(created|modified)_(year|month|date|hour|minute|second)\}/g, (whole, kind, part) => {
    return map[`${kind}_${part}`] ?? whole
  })
}

/** 拆分「主名 + 扩展名」；目录不做扩展名处理（`v1.2` 的目录不该被当成 .2）。 */
function splitExt(name: string, isDir: boolean): { base: string; suffix: string } {
  if (isDir) return { base: name, suffix: "" }
  const index = name.lastIndexOf(".")
  // 开头就是点的文件（.gitignore）整体算主名，切出去会得到空主名
  if (index <= 0) return { base: name, suffix: "" }
  return { base: name.slice(0, index), suffix: name.slice(index) }
}

/** 正则替换：`$1` 反向引用与时间占位符都支持。 */
export function planRegexRename(
  sources: RenameSource[],
  pattern: string,
  replacement: string,
): RenamePlan {
  let probe: RegExp
  try {
    probe = new RegExp(pattern)
  } catch (e) {
    return {
      ...noopPlan(sources),
      problems: [`正则表达式不合法：${String((e as Error)?.message ?? e)}`],
    }
  }
  return finalize(sources, (source) => {
    if (!probe.test(source.name)) return source.name
    // 另建一个带 g 的实例：probe 的 lastIndex 会被 test 记住
    const applied = source.name.replace(new RegExp(pattern, "g"), replacement)
    return expandTimePlaceholders(applied, source)
  })
}

/** 模板末尾是不是已经带扩展名（`.mp4` 这种 1–5 位字母数字）。 */
function looksLikeExtension(value: string): boolean {
  const index = value.lastIndexOf(".")
  if (index <= 0) return false
  const suffix = value.slice(index + 1)
  return suffix.length >= 1 && suffix.length <= 5 && /^[A-Za-z0-9]+$/.test(suffix)
}

/**
 * 序号命名：`template` 里的 `{number}` 会被序号替换（没有就追加在主名后），
 * `{ext}` 会被替换成原扩展名（不含点）。不写 `{ext}` 时自动保留原扩展名；
 * 模板末尾自己写了扩展名（`photo.jpg`）则不再重复拼接。
 */
export function planSequentialRename(
  sources: RenameSource[],
  template: string,
  start: number,
  padding: number,
): RenamePlan {
  let counter = Number.isFinite(start) && start > 0 ? Math.trunc(start) : 1
  return finalize(sources, (source) => {
    const { suffix } = splitExt(source.name, source.is_dir)
    const number = padding > 0 ? String(counter).padStart(padding, "0") : String(counter)
    counter += 1
    let body = template.includes("{number}")
      ? template.replace(/\{number\}/g, number)
      : `${template}${number}`
    body = body.replace(/\{ext\}/g, suffix.replace(/^\./, ""))
    if (looksLikeExtension(body)) return body
    return `${body}${suffix}`
  })
}

/** 查找替换：字面量、区分大小写、替换全部出现位置。 */
export function planFindReplace(
  sources: RenameSource[],
  find: string,
  replacement: string,
): RenamePlan {
  if (!find) return finalize(sources, (source) => source.name)
  return finalize(sources, (source) =>
    source.name.includes(find) ? source.name.split(find).join(replacement) : source.name,
  )
}

/** 全部保持原名的计划（用于模式切换/参数为空时的占位）。 */
function noopPlan(sources: RenameSource[]): RenamePlan {
  const items: RenameItem[] = sources.map((source) => ({
    src_name: source.name,
    new_name: source.name,
    unchanged: true,
  }))
  return { items, changed: [], problems: [] }
}

/** 统一的收尾：算出变化集、逐条校验、检出撞名。 */function finalize(sources: RenameSource[], compute: (source: RenameSource) => string): RenamePlan {
  const items: RenameItem[] = sources.map((source) => {
    let newName = source.name
    try {
      newName = compute(source)
    } catch {
      // 单条计算失败（例如极端输入）不该炸掉整个预览
      newName = source.name
    }
    newName = newName.trim()
    return {
      src_name: source.name,
      new_name: newName,
      unchanged: newName === source.name,
      error: newName === source.name ? undefined : validateName(newName) || undefined,
    }
  })

  const problems: string[] = []
  const invalid = items.filter((item) => item.error)
  if (invalid.length > 0) {
    problems.push(`${invalid.length} 项的新文件名不合法：${invalid.slice(0, 3).map((i) => i.new_name || "(空)").join("、")}`)
  }

  // 撞名：把「最终名字」全部登记一遍（含未变化的），重复即为冲突。
  // 这也覆盖了 a→b 而 b 本来就在选中的情况 —— 后端是逐条改名，交换必然失败。
  const seen = new Map<string, string>()
  const duplicates = new Set<string>()
  for (const item of items) {
    const key = item.new_name.toLowerCase()
    const owner = seen.get(key)
    if (owner !== undefined && owner !== item.src_name) duplicates.add(item.new_name)
    seen.set(key, item.src_name)
  }
  if (duplicates.size > 0) {
    problems.push(`${duplicates.size} 个新文件名重复：${[...duplicates].slice(0, 3).join("、")}`)
  }

  return {
    items,
    changed: items.filter((item) => !item.unchanged && !item.error),
    problems,
  }
}
