import { onBeforeUnmount, onMounted } from "vue"

export interface ShortcutHandlers {
  /** Ctrl/Cmd+A：全选（由调用方负责判定当前视图是否可选）。 */
  onSelectAll?: () => void
  /** Delete：删除当前选择。 */
  onDelete?: () => void
  /** F2：重命名（单项选择时）。 */
  onRename?: () => void
  /** Ctrl/Cmd+C：复制到剪贴板（前端内存态）。 */
  onCopy?: () => void
  /** Ctrl/Cmd+X：剪切（标记移动）。 */
  onCut?: () => void
  /** Ctrl/Cmd+V：粘贴到当前目录。 */
  onPaste?: () => void
  /** Escape：清空选择/收起面板。 */
  onEscape?: () => void
}

function isTypingTarget(target: EventTarget | null): boolean {
  const el = target as HTMLElement | null
  if (!el) return false
  return (
    el.tagName === "INPUT" ||
    el.tagName === "TEXTAREA" ||
    el.tagName === "SELECT" ||
    el.isContentEditable === true
  )
}

/** MessageBox / Dialog 打开时快捷键让位（否则 Delete 会在确认框底下继续触发）。 */
function hasBlockingOverlay(): boolean {
  return (
    document.querySelector(".el-overlay-message-box") !== null ||
    document.querySelector(".el-dialog") !== null
  )
}

/**
 * 文件页键盘快捷键（M1，对齐旧前端 key-event store + operations 语义）：
 * Ctrl+A 全选、Delete 删除、F2 重命名、Ctrl+C/X/V 复制/剪切/粘贴、Escape 取消。
 *
 * 只在「非输入态、无弹窗」时生效；Ctrl+C/X/V 不 preventDefault（保留系统剪贴板语义）。
 */
export function useShortcuts(handlers: ShortcutHandlers) {
  function onKeydown(e: KeyboardEvent) {
    if (isTypingTarget(e.target)) return
    if (hasBlockingOverlay()) return
    const mod = e.ctrlKey || e.metaKey

    if (mod && (e.key === "a" || e.key === "A")) {
      e.preventDefault()
      handlers.onSelectAll?.()
      return
    }
    if (mod && (e.key === "c" || e.key === "C")) {
      handlers.onCopy?.()
      return
    }
    if (mod && (e.key === "x" || e.key === "X")) {
      handlers.onCut?.()
      return
    }
    if (mod && (e.key === "v" || e.key === "V")) {
      handlers.onPaste?.()
      return
    }
    if (e.key === "Delete") {
      handlers.onDelete?.()
      return
    }
    if (e.key === "F2") {
      e.preventDefault()
      handlers.onRename?.()
      return
    }
    if (e.key === "Escape") {
      handlers.onEscape?.()
    }
  }

  onMounted(() => document.addEventListener("keydown", onKeydown))
  onBeforeUnmount(() => document.removeEventListener("keydown", onKeydown))
}
