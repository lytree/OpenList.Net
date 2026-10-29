/**
 * 媒体域纯函数工具（无 DOM / 无网络 / 无 i18n 依赖）。
 *
 * 之所以单独成文件：这些规则在「播放列表重排」「统计条形图宽度」「人物头像占位」
 * 三处都要用，而且都有边界条件（越界索引、total=0、空名字）。放进页面里就只能靠
 * 手工点击验证，抽出来才能被单测钉住（见 mediaCatalog.spec.ts）。
 */

/** 把 `from` 位置的元素移动到 `to`，越界索引自动收敛到端点；返回新数组。 */
export function moveInArray<T>(list: readonly T[], from: number, to: number): T[] {
  const next = [...list]
  if (next.length === 0) return next
  const last = next.length - 1
  const src = Math.min(Math.max(from, 0), last)
  const dst = Math.min(Math.max(to, 0), last)
  if (src === dst) return next
  const [moved] = next.splice(src, 1)
  if (moved === undefined) return next
  next.splice(dst, 0, moved)
  return next
}

/**
 * 按 id 上移/下移一格，返回新的 id 顺序。
 *
 * 找不到 id、或 `delta` 会把它推出边界时，返回**原顺序的副本**（不是原引用）：
 * 调用方可以无脑把返回值交给 reorder 接口，不必先判断「有没有真的移动」。
 */
export function moveIdBy(ids: readonly number[], id: number, delta: number): number[] {
  const from = ids.indexOf(id)
  if (from < 0) return [...ids]
  const to = from + delta
  if (to < 0 || to >= ids.length) return [...ids]
  return moveInArray(ids, from, to)
}

/** 百分比（0-100 整数）；`total <= 0` 时返回 0，避免统计页出现 NaN 宽度。 */
export function percentOf(count: number, total: number): number {
  if (!Number.isFinite(count) || !Number.isFinite(total) || total <= 0) return 0
  return Math.round((count / total) * 100)
}

/** 秒 → 小时数（保留一位小数）。用于「累计观看时长」。 */
export function hoursOf(seconds: number): number {
  if (!Number.isFinite(seconds) || seconds <= 0) return 0
  return Math.round((seconds / 3600) * 10) / 10
}

/** 秒 → 分钟数（四舍五入，向上取整到 1 分钟：59 秒也是「1 分钟」而不是 0）。 */
export function minutesOf(seconds: number): number {
  if (!Number.isFinite(seconds) || seconds <= 0) return 0
  return Math.max(1, Math.round(seconds / 60))
}

/**
 * 头像占位用的首字：
 * - 中日韩等非拉丁字符取 1 个字（「张国荣」→「张」）；
 * - 拉丁名取首个单词首字母并大写（「brad pitt」→「B」）；
 * - 空/空白返回 `?`。
 */
export function nameInitial(name: string | null | undefined): string {
  const trimmed = (name ?? "").trim()
  if (!trimmed) return "?"
  const first = Array.from(trimmed)[0] ?? "?"
  // 拉丁字母与数字走大写；其它（CJK、emoji）原样保留
  return /[a-z0-9]/i.test(first) ? first.toUpperCase() : first
}

/**
 * 播放队列里的一个位置：第 `item` 个条目（播放列表顺序）的第 `file` 个文件。
 *
 * 队列是**两层**的（条目 → 文件），因为一个条目可能是分片/DVD 多文件；
 * 「下一集」在层的边界上语义不同（同条目内是下一片，跨条目是下一条），
 * 所以位置必须带层级，不能只用一个自增序号。
 */
export interface QueuePos {
  item: number
  file: number
}

/**
 * 队列里的下一个位置；已到队尾时返回 `null`。
 *
 * `fileCount` 是**当前条目**的文件数（跨条目前无法预知，只能按需拉取），
 * `itemCount` 是播放列表条目数。两层都到头才是真正的结束 ——
 * 只判断 `item + 1 < itemCount` 会在「当前条目还没播完」时提前跳下一条。
 */
export function nextQueuePos(
  pos: QueuePos,
  fileCount: number,
  itemCount: number,
): QueuePos | null {
  if (pos.file + 1 < fileCount) return { item: pos.item, file: pos.file + 1 }
  if (pos.item + 1 < itemCount) return { item: pos.item + 1, file: 0 }
  return null
}

/**
 * 队列里的上一个位置；已在队首时返回 `null`。
 *
 * 跨条目往前只落到**上一项的第 0 个文件**：上一个条目有几个文件此时并不知道
 * （要再发一次请求），而「往前一集从头播」比「先查再跳」更符合直觉。
 */
export function prevQueuePos(pos: QueuePos, itemCount: number): QueuePos | null {
  if (pos.file > 0) return { item: pos.item, file: pos.file - 1 }
  if (pos.item > 0 && pos.item - 1 < itemCount) return { item: pos.item - 1, file: 0 }
  return null
}

/**
 * 取权重最高的前 `n` 个桶。
 *
 * 后端已按 count 降序返回，这里仍重新排序 + 截断：统计页偶尔要把多个来源
 * （stats.by_genre 与 taste.genres）合并后再取 Top N，不能假设入参有序。
 */
export function topBuckets<T extends { count: number }>(buckets: readonly T[], n: number): T[] {
  if (n <= 0) return []
  return [...buckets].sort((a, b) => b.count - a.count).slice(0, n)
}
