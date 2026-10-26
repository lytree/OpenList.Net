import type { Obj } from "~/api/types"

/**
 * 列表侧纯函数：关键字过滤 + 分页切片。
 *
 * 为什么放在 `utils/` 而不是 `composables/useFileList.ts`：
 * 这两个逻辑是「给定输入必然得到确定输出」的，和 Vue 的响应式无关；抽出来后
 * 可以直接单测边界（越界页、0 条、pageSize=0），不必造一个路由环境。
 */

/** 每页条数候选。与旧前端 `default_page_size` 的取值域对齐。 */
export const PAGE_SIZES = [30, 50, 100, 200] as const

/**
 * 文件名过滤（大小写不敏感的子串匹配）。
 *
 * 只匹配 `name`：旧前端的目录内搜索也是这个语义。匹配路径会让人误以为
 * 「结果还在别的目录」，而列表始终是当前目录的视图。
 *
 * `keyword` 放宽到 null/undefined：`ElInput` 的类型是 `string | number | null`，
 * clearable 清空时可能写回 null，而模板里的 `v-model` 不会替我们兜底。
 * 这里防御一次，比在上层每个调用点写 `?? ""` 更省事。
 */
export function filterObjs(objs: Obj[], keyword: string | null | undefined): Obj[] {
  const kw = (keyword ?? "").trim().toLowerCase()
  if (!kw) return objs
  return objs.filter((obj) => (obj.name ?? "").toLowerCase().includes(kw))
}

export interface PageResult<T> {
  items: T[]
  /** 过滤后的总条数（不是分页后的）。 */
  total: number
  /** 总页数，至少 1。 */
  pages: number
  /** 实际生效的页码（越界时被夹到 [1, pages]）。 */
  page: number
}

/**
 * 客户端分页。
 *
 * 刻意不用后端 `per_page` 分页：后端不支持 `sort` 参数，排序只能在前端做，
 * 服务端分页会让「翻页后的顺序」与「当前排序列」互相打架（每页各自排）。
 * 一次取全量再切片，排序/过滤/分页三者永远一致；目录很大时的传输成本由
 * `default_page_size=0`（不分页）这条逃生路径兜住。
 */
export function paginate<T>(items: T[], page: number, pageSize: number): PageResult<T> {
  const total = items.length
  if (pageSize <= 0) return { items, total, pages: 1, page: 1 }
  const pages = Math.max(1, Math.ceil(total / pageSize))
  const current = Math.min(Math.max(1, Math.trunc(page) || 1), pages)
  const start = (current - 1) * pageSize
  return { items: items.slice(start, start + pageSize), total, pages, page: current }
}

/**
 * 页码序列（含省略号），给自绘分页条用。
 *
 * 规则：首页、末页恒在，当前页左右各 `span` 个；中间断开处插 `"…"`。
 * 页数少（<= span*2+3）时全部列出，不出现省略号。
 */
export function pageList(page: number, pages: number, span = 1): (number | "…")[] {
  if (pages <= 1) return [1]
  const current = Math.min(Math.max(1, Math.trunc(page) || 1), pages)
  const window = span * 2 + 1
  if (pages <= window + 4) {
    return Array.from({ length: pages }, (_, i) => i + 1)
  }
  const items: (number | "…")[] = [1]
  const start = Math.max(2, current - span)
  const end = Math.min(pages - 1, current + span)
  if (start > 2) items.push("…")
  for (let i = start; i <= end; i++) items.push(i)
  if (end < pages - 1) items.push("…")
  items.push(pages)
  return items
}
