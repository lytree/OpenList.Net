import { createHash } from "node:crypto"
import { describe, expect, it } from "vitest"
import { hashPassword } from "./auth"

/**
 * 密码哈希必须与后端 `UserService.PasswordHashSuffix` 完全一致，
 * 否则「登录失败」会被误判成用户密码错 —— 这类 bug 排查成本极高，
 * 因此这里用 Node 的 crypto 做一次独立实现的交叉校验。
 */
describe("hashPassword", () => {
  it("与后端约定一致：sha256(密码 + '-https://github.com/alist-org/alist')", () => {
    const expected = createHash("sha256")
      .update("admin-https://github.com/alist-org/alist")
      .digest("hex")
    expect(hashPassword("admin")).toBe(expected)
    expect(hashPassword("admin")).toHaveLength(64)
  })

  it("不同密码产生不同哈希", () => {
    expect(hashPassword("a")).not.toBe(hashPassword("b"))
  })
})
