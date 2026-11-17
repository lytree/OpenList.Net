import type { WatchReportPayload } from "~/api/media"

/**
 * 播放会话跟踪器 —— 把「一次连续观看」翻译成一串上报。
 *
 * 会话边界由**显式信号**决定，不靠时间间隔猜：
 *
 * | 事件 | 动作 |
 * |---|---|
 * | 起播 / 换文件 / 连播下一条 | `start()`：先把上一段收尾，再拿新 token 开一段 |
 * | 每 10 秒节流写进度 | `beat()`：更新位置，服务端据此累加"真的看了多久" |
 * | 元数据加载完 | `setDuration()`：把片长交出去，比例类度量靠它当分母 |
 * | 用户拖进度条 | `seek()`：只累加计数，搭下一次上报的车，不单独发请求 |
 * | 播完（整条目） | `end(pos, true)` |
 * | 关播放器 / 离开页面 | `end(pos, false)` |
 *
 * **token 的生命周期就是会话的生命周期**：换文件必然换 token，所以"一条会话只对应
 * 一个文件"是结构上的保证，不依赖服务端猜。崩溃/断电没有信号，由服务端按空闲时长兜底。
 *
 * **拖动次数传增量不传累计**：上报是即发即忘的，丢一发就会让"累计值"倒退，
 * 而增量丢一发只是少记一次，不会把已经写进库的数算错。
 *
 * 上报是即发即忘的：进度写回已经是后台行为，观看记录失败也不该弹错 —— 大不了这段历史少了。
 * 唯一的例外是 `start()` 之后立刻 `end()` 的极端情况（播了 0 秒就关），服务端会留下一条
 * 0 秒会话；它不影响统计，但会在历史里露头，所以 `end()` 在位置为 0 且未播完时**不发请求**。
 */

/** 生成会话标识：优先 `crypto.randomUUID`，非安全上下文（局域网 http）退回时间戳+随机串。 */
export function newWatchToken(): string {
  const cryptoApi = globalThis.crypto as Crypto | undefined
  if (cryptoApi?.randomUUID) return cryptoApi.randomUUID()
  return `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`
}

export interface WatchTracker {
  /** 起播（或换文件）：结束上一段，并开一段新的。 */
  start: (itemId: number, fileId: number | null | undefined, positionSeconds: number) => void
  /** 心跳：更新位置（可顺带更新片长）。没有活动会话时是空操作。 */
  beat: (positionSeconds: number, durationSeconds?: number) => void
  /** 只更新片长；元数据一加载完就调，不必等下一个心跳。 */
  setDuration: (durationSeconds: number) => void
  /** 记一次拖动。只累加，随下一次上报送出。 */
  seek: () => void
  /** 结束当前会话。`completed` 表示整个条目播完了。 */
  end: (positionSeconds: number, completed?: boolean) => void
  /** 是否有活动会话（测试与排障用）。 */
  readonly active: boolean
}

export type WatchSend = (payload: WatchReportPayload) => Promise<unknown>

export function createWatchTracker(send: WatchSend, makeToken: () => string = newWatchToken): WatchTracker {
  let token = ""
  let itemId = 0
  let fileId: number | null = null
  /** 本段最后一次上报的位置。**换文件时必须用它收尾** —— 新文件的起点是 0，拿它收尾会把上一段记成"在 0 秒结束"。 */
  let lastPosition = 0
  /** 本次播放的片长（秒），0 表示还不知道。转码流的时长只以播放器为准，所以由客户端给。 */
  let duration = 0
  /** 本段尚未上报的拖动次数。 */
  let pendingSeeks = 0

  /** 片长只在拿到正数时覆盖：`video.duration` 在元数据到位前是 NaN，别用它抹掉已知值。 */
  function takeDuration(value?: number) {
    if (typeof value === "number" && Number.isFinite(value) && value > 0) {
      duration = Math.round(value)
    }
  }

  function report(position: number, closing: boolean, completed = false) {
    if (!token || !itemId) return
    const safe = Math.max(0, Math.floor(position))
    lastPosition = safe
    const payload: WatchReportPayload = {
      token,
      item_id: itemId,
      file_id: fileId,
      position_seconds: safe,
      closing,
      completed,
    }
    // 片长未知就不送这个字段：服务端会沿用上一次的值，而不是把比例类度量清掉。
    if (duration > 0) payload.duration_seconds = duration
    // 增量读一次就清零 —— 这一发带走了这几秒里的全部拖动。
    if (pendingSeeks > 0) payload.seeks = pendingSeeks
    pendingSeeks = 0
    // 即发即忘：失败只意味着这段历史缺失，不该阻塞播放
    void send(payload).catch(() => {})
  }

  function clear() {
    token = ""
    itemId = 0
    fileId = null
    lastPosition = 0
    duration = 0
    pendingSeeks = 0
  }

  function finish(position: number, completed: boolean) {
    if (!token) return
    // 0 秒且没播完 = 点了播放马上关掉，不值得在历史里留一行
    if (!completed && position <= 0) {
      clear()
      return
    }
    report(position, true, completed)
    clear()
  }

  return {
    start(nextItemId, nextFileId, positionSeconds) {
      // 上一段先收尾：用它**自己的**最后位置和自己的片长，而不是新文件的起点
      if (token) finish(lastPosition, false)
      itemId = nextItemId
      fileId = nextFileId ?? null
      token = makeToken()
      // 新文件是新的一段：片长与拖动计数都从零开始
      duration = 0
      pendingSeeks = 0
      report(positionSeconds, false)
    },
    beat(positionSeconds, durationSeconds) {
      takeDuration(durationSeconds)
      report(positionSeconds, false)
    },
    setDuration(durationSeconds) {
      takeDuration(durationSeconds)
    },
    seek() {
      // 没有会话时不计数：否则一堆没人认领的拖动会带进下一段。
      if (token) pendingSeeks += 1
    },
    end(positionSeconds, completed = false) {
      finish(positionSeconds, completed)
    },
    get active() {
      return Boolean(token)
    },
  }
}
