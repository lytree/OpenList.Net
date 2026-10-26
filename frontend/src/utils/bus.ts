/**
 * 极简事件总线（跨组件、单进程、同步派发）。
 *
 * 为什么需要它：预览器嵌在 `PreviewHost` 里，而「离线下载」对话框挂在页面级。
 * 把回调一层层从页面传到预览器，中间要穿过 `PreviewHost` → 注册表 → 各预览组件，
 * 每个预览器的 props 都得跟着加一个和它无关的回调。这里只在**真正需要跨层**的场景用，
 * 其余状态继续走 props（可读性优先）。
 *
 * 约定：事件名用 `动词-名词`，载荷类型写在 `AppEvents` 里；订阅返回取消函数，
 * 组件 `onUnmounted` 里调用，避免热更新后重复触发。
 */

export interface AppEvents {
  /**
   * 请求打开「离线下载」对话框。
   *
   * `urls` 会预填到链接框（种子预览传磁力链接）；`dir` 缺省时用当前目录。
   */
  "offline-download": { urls: string[] }
}

type Handler<K extends keyof AppEvents> = (payload: AppEvents[K]) => void

const handlers = new Map<keyof AppEvents, Set<Handler<never>>>()

/** 订阅事件，返回取消订阅函数。 */
export function onEvent<K extends keyof AppEvents>(name: K, handler: Handler<K>): () => void {
  let set = handlers.get(name)
  if (!set) {
    set = new Set()
    handlers.set(name, set)
  }
  set.add(handler as Handler<never>)
  return () => {
    set?.delete(handler as Handler<never>)
  }
}

/** 派发事件。没有订阅者时静默丢弃（不影响调用方逻辑）。 */
export function emitEvent<K extends keyof AppEvents>(name: K, payload: AppEvents[K]): void {
  const set = handlers.get(name)
  if (!set) return
  // 复制一份再遍历：处理函数里可能取消订阅（例如打开对话框后立即销毁来源组件）
  for (const handler of [...set]) (handler as Handler<K>)(payload)
}
