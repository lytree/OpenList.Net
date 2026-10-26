import { createHash } from "node:crypto"
import { describe, expect, it } from "vitest"
import { sha1, sha1Hex } from "./sha1"

/**
 * 用 Node 的 crypto 做**逐长度**交叉验证。
 *
 * 手写 SHA-1 最容易错的地方不是压缩函数，而是补齐：消息长度正好落在
 * 55/56/63/64 这些边界时，「补 0x80 之后还要不要多一块」会差一个块。
 * 0..130 覆盖了 56/64/120/128 四个拐点，比挑几个固定字符串靠谱。
 */
describe("sha1", () => {
  const pattern = new Uint8Array(200)
  for (let i = 0; i < pattern.length; i += 1) pattern[i] = (i * 37 + 11) & 0xff

  it("与 Node crypto 在 0..130 每个长度上一致", () => {
    for (let length = 0; length <= 130; length += 1) {
      const input = pattern.subarray(0, length)
      const expected = createHash("sha1").update(input).digest("hex")
      expect(sha1Hex(input), `length=${length}`).toBe(expected)
    }
  })

  it("标准向量", () => {
    const encode = (text: string) => new TextEncoder().encode(text)
    expect(sha1Hex(encode(""))).toBe("da39a3ee5e6b4b0d3255bfef95601890afd80709")
    expect(sha1Hex(encode("abc"))).toBe("a9993e364706816aba3e25717850c26c9cd0d89d")
    expect(sha1Hex(encode("The quick brown fox jumps over the lazy dog"))).toBe(
      "2fd4e1c67a2d28fced849ee1bb76e7391b93eb12",
    )
  })

  it("返回 20 字节摘要", () => {
    expect(sha1(new Uint8Array([1, 2, 3]))).toHaveLength(20)
  })

  it("大输入（1 MB）也正确", () => {
    const big = new Uint8Array(1024 * 1024)
    for (let i = 0; i < big.length; i += 1) big[i] = i & 0xff
    expect(sha1Hex(big)).toBe(createHash("sha1").update(big).digest("hex"))
  })
})
