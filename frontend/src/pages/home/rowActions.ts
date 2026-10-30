import type { Obj } from "~/api/types"
import { t } from "~/i18n"

/** 行内「更多」菜单的一项。 */
export interface RowAction {
  command: string
  label: string
  danger?: boolean
  divided?: boolean
}

/**
 * 单个条目的行内操作菜单。
 *
 * 桌面表格的最后一列与手机卡片行的「更多」共用这一个函数：菜单项只此一份，
 * 命令一律交给 `onRowCommand` 解释。抽成纯函数是为了能单测「哪类条目有哪些动作」
 * 这种最容易两边跑偏的规则。
 *
 * 规则与旧表格模板逐条对齐：
 * - 所有条目都能「打开」；
 * - 文件才有「下载 / 复制直链」（目录没有字节流）；
 * - 可写时才有改名 / 移动 / 复制 / 删除，删除单独分组并标红。
 */
export function rowActionsFor(obj: Obj, opts: { writable: boolean }): RowAction[] {
  const items: RowAction[] = [{ command: "open", label: t("ops.open") }]
  if (!obj.is_dir) {
    items.push(
      { command: "download", label: t("home.toolbar.download") },
      { command: "copy_link", label: t("home.toolbar.copy_link") },
    )
  }
  if (opts.writable) {
    items.push(
      { command: "rename", label: t("ops.rename"), divided: true },
      { command: "move", label: t("ops.move_to") },
      { command: "copy", label: t("ops.copy_to") },
      { command: "delete", label: t("ops.delete"), danger: true, divided: true },
    )
  }
  return items
}
