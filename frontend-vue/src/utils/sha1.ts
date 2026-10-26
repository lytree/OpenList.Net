/**
 * SHA-1（纯 TypeScript，约 60 行）。
 *
 * 为什么不直接用 `crypto.subtle.digest("SHA-1")`：Web Crypto 只在**安全上下文**
 * 可用，而自建网盘最常见的访问方式恰恰是 `http://192.168.x.x:5244`（非 localhost、
 * 无证书）。`crypto.subtle` 在那里是 `undefined`，一调用就抛。
 * 用纯 JS 实现则环境无关，且不需要为几十字节哈希引入一个加密库依赖。
 *
 * 安全性说明：这里只用于 BT 的 info_hash（协议规定就是 SHA-1），
 * 不用于任何口令散列或签名场景。
 */

const K = new Uint32Array([0x5a827999, 0x6ed9eba1, 0x8f1bbcdc, 0xca62c1d6])

/** 计算 SHA-1，返回 20 字节摘要。 */
export function sha1(input: Uint8Array): Uint8Array {
  const bitLength = input.length * 8
  // 消息 + 0x80 + 若干 0x00 + 8 字节长度，总长补齐到 64 的倍数
  const zeroPadding = (56 - ((input.length + 1) % 64) + 64) % 64
  const total = input.length + 1 + zeroPadding + 8
  const buffer = new Uint8Array(total)
  buffer.set(input)
  buffer[input.length] = 0x80

  // UTF-8 长度字段：64 位大端。用两个 32 位写，避免 JS 的 2^53 精度问题。
  const high = Math.floor(bitLength / 0x100000000)
  const low = bitLength >>> 0
  const view = new DataView(buffer.buffer)
  view.setUint32(total - 8, high, false)
  view.setUint32(total - 4, low, false)

  let h0 = 0x67452301
  let h1 = 0xefcdab89
  let h2 = 0x98badcfe
  let h3 = 0x10325476
  let h4 = 0xc3d2e1f0

  const w = new Uint32Array(80)

  for (let offset = 0; offset < total; offset += 64) {
    for (let i = 0; i < 16; i += 1) w[i] = view.getUint32(offset + i * 4, false)
    for (let i = 16; i < 80; i += 1) {
      const x = w[i - 3] ^ w[i - 8] ^ w[i - 14] ^ w[i - 16]
      w[i] = ((x << 1) | (x >>> 31)) >>> 0
    }

    let a = h0
    let b = h1
    let c = h2
    let d = h3
    let e = h4

    for (let i = 0; i < 80; i += 1) {
      let f: number
      let k: number
      if (i < 20) {
        f = (b & c) | (~b & d)
        k = K[0]
      } else if (i < 40) {
        f = b ^ c ^ d
        k = K[1]
      } else if (i < 60) {
        f = (b & c) | (b & d) | (c & d)
        k = K[2]
      } else {
        f = b ^ c ^ d
        k = K[3]
      }
      const temp = (((a << 5) | (a >>> 27)) + f + e + k + w[i]) >>> 0
      e = d
      d = c
      c = ((b << 30) | (b >>> 2)) >>> 0
      b = a
      a = temp
    }

    h0 = (h0 + a) >>> 0
    h1 = (h1 + b) >>> 0
    h2 = (h2 + c) >>> 0
    h3 = (h3 + d) >>> 0
    h4 = (h4 + e) >>> 0
  }

  const out = new Uint8Array(20)
  const outView = new DataView(out.buffer)
  outView.setUint32(0, h0, false)
  outView.setUint32(4, h1, false)
  outView.setUint32(8, h2, false)
  outView.setUint32(12, h3, false)
  outView.setUint32(16, h4, false)
  return out
}

/** 计算 SHA-1 并转成小写十六进制（BT 的 info_hash 展示格式）。 */
export function sha1Hex(input: Uint8Array): string {
  const digest = sha1(input)
  let out = ""
  for (const byte of digest) out += byte.toString(16).padStart(2, "0")
  return out
}
