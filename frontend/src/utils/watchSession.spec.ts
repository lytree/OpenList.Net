import { describe, expect, it, vi } from "vitest"
import type { WatchReportPayload } from "~/api/media"
import { createWatchTracker } from "./watchSession"

/**
 * 会话边界是这一层的全部价值：token 一换就是新会话，所以「换文件算新的一段」
 * 不是靠猜，而是结构上必然。这些用例把四个边界钉住。
 */
function setup() {
  const sent: WatchReportPayload[] = []
  const send = vi.fn(async (payload: WatchReportPayload) => {
    sent.push(payload)
    return { code: 200 }
  })
  let seq = 0
  const tracker = createWatchTracker(send, () => `t${++seq}`)
  return { sent, send, tracker }
}

describe("watchSession tracker", () => {
  it("起播上传一次非结束上报，并带上新 token", () => {
    const { sent, tracker } = setup()
    tracker.start(7, 70, 120)

    expect(sent).toHaveLength(1)
    expect(sent[0]).toMatchObject({ token: "t1", item_id: 7, file_id: 70, position_seconds: 120 })
    expect(sent[0].closing).toBe(false)
    expect(tracker.active).toBe(true)
  })

  it("心跳复用同一个 token", () => {
    const { sent, tracker } = setup()
    tracker.start(7, 70, 0)
    tracker.beat(10)
    tracker.beat(20)

    expect(sent.map((p) => p.token)).toEqual(["t1", "t1", "t1"])
    expect(sent.map((p) => p.position_seconds)).toEqual([0, 10, 20])
  })

  it("结束时带 closing，之后心跳不再上报", () => {
    const { sent, tracker } = setup()
    tracker.start(7, 70, 0)
    tracker.end(600, true)
    tracker.beat(610)

    expect(sent).toHaveLength(2)
    expect(sent[1]).toMatchObject({ token: "t1", position_seconds: 600, closing: true, completed: true })
    expect(tracker.active).toBe(false)
    expect(sent).toHaveLength(2)
  })

  it("换文件 = 先收尾上一段，再开新 token", () => {
    const { sent, tracker } = setup()
    tracker.start(7, 70, 0)
    tracker.beat(300)
    tracker.start(7, 71, 0)

    expect(sent.map((p) => [p.token, p.file_id, p.closing])).toEqual([
      ["t1", 70, false],
      ["t1", 70, false],
      ["t1", 70, true], // 上一段收尾
      ["t2", 71, false],
    ])
    // 收尾位置必须是**上一段**的最后位置，不是新文件的 0
    expect(sent[2].position_seconds).toBe(300)
    expect(sent[3].position_seconds).toBe(0)
  })

  it("播了 0 秒就关，不留下空会话", () => {
    const { sent, tracker } = setup()
    tracker.start(7, 70, 0)
    tracker.end(0, false)

    expect(sent).toHaveLength(1) // 只有起播那一条
    expect(sent[0].closing).toBe(false)
    expect(tracker.active).toBe(false)
  })

  it("位置为负时归零（播放器异常值不该写进库）", () => {
    const { sent, tracker } = setup()
    tracker.start(7, 70, -5)

    expect(sent[0].position_seconds).toBe(0)
  })

  it("上报失败不影响播放（不抛出）", () => {
    const send = vi.fn(async () => {
      throw new Error("network")
    })
    const tracker = createWatchTracker(send, () => "tx")

    expect(() => tracker.start(7, 70, 0)).not.toThrow()
    expect(() => tracker.beat(10)).not.toThrow()
  })

  it("片长随心跳送出，供服务端算最远/离开比例", () => {
    const { sent, tracker } = setup()
    tracker.start(7, 70, 0)
    tracker.beat(10, 5400)

    expect(sent[0].duration_seconds).toBeUndefined() // 起播时元数据还没到
    expect(sent[1].duration_seconds).toBe(5400)
  })

  it("setDuration 后，下一次上报就带上片长（不必等心跳参数）", () => {
    const { sent, tracker } = setup()
    tracker.start(7, 70, 120)
    tracker.setDuration(5400.4)
    tracker.beat(130)

    expect(sent[1].duration_seconds).toBe(5400) // 取整，别把小数写进库
  })

  it("片长只在拿到正数时覆盖（NaN / 0 不能抹掉已知值）", () => {
    const { sent, tracker } = setup()
    tracker.start(7, 70, 0)
    tracker.setDuration(5400)
    tracker.beat(10, Number.NaN)
    tracker.beat(20, 0)

    expect(sent[1].duration_seconds).toBe(5400)
    expect(sent[2].duration_seconds).toBe(5400)
  })

  it("拖动次数按增量上报，并在送出后清零", () => {
    const { sent, tracker } = setup()
    tracker.start(7, 70, 0)
    tracker.seek()
    tracker.seek()
    tracker.beat(10)

    expect(sent[0].seeks).toBeUndefined() // 起播那一发还没有拖动
    expect(sent[1].seeks).toBe(2)

    tracker.beat(20)
    expect(sent[2].seeks).toBeUndefined() // 上一发的 2 次已经送走了
  })

  it("换文件时，上一段的拖动跟着收尾那一发走，新段从零开始", () => {
    const { sent, tracker } = setup()
    tracker.start(7, 70, 0)
    // 先走一段：位置为 0 就换文件的话，收尾那一发本来就会被"不留空会话"的规则跳过
    tracker.beat(300)
    tracker.seek()
    tracker.start(7, 71, 0)

    // 0: 起播 t1 / 1: 心跳 / 2: t1 收尾（带走 1 次拖动）/ 3: t2 起播（干净）
    expect(sent[2]).toMatchObject({ token: "t1", closing: true, seeks: 1 })
    expect(sent[3]).toMatchObject({ token: "t2", closing: false })
    expect(sent[3].seeks).toBeUndefined()
  })

  it("没有活动会话时的拖动不计数", () => {
    const { sent, tracker } = setup()
    tracker.seek()
    tracker.start(7, 70, 0)
    tracker.beat(10)

    expect(sent[0].seeks).toBeUndefined()
    expect(sent[1].seeks).toBeUndefined()
  })
})
