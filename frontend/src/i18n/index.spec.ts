import { beforeEach, describe, expect, it } from "vitest"
import { availableLanguages, locale, setLocale, t } from "./index"

/**
 * i18n 冒烟：锁住三条最容易回归的语义 —— 取值、插值、回退。
 * 这些行为一旦错，整站文案会静默变成 key，很难在人工点检时发现。
 */
describe("i18n", () => {
  beforeEach(() => {
    localStorage.clear()
    setLocale("en")
  })

  it("按点路径取值", () => {
    expect(t("login.login")).toBe("Login")
    expect(t("global.nav_home")).toBe("Home")
  })

  it("支持双花括号插值（现有字典的主流写法）", () => {
    expect(t("home.toolbar.archive.extract_header", { path: "/a/b.zip" })).toBe(
      "Extract /a/b.zip to...",
    )
    expect(t("global.delete_confirm", { name: "x.txt" })).toBe(
      "Are you sure you want to delete [x.txt]?",
    )
  })

  it("缺少参数时保留占位符原样，不产生二次解析", () => {
    expect(t("home.toolbar.archive.extract_header")).toBe("Extract {{path}} to...")
  })

  it("缺键时回退为 key 本身（显式暴露漏翻）", () => {
    expect(t("not.exists.anywhere")).toBe("not.exists.anywhere")
  })

  it("切换语言会持久化，并只暴露真实存在的语言", () => {
    setLocale("en")
    expect(locale.value).toBe("en")
    expect(localStorage.getItem("lang")).toBe("en")
    expect(availableLanguages.map((l) => l.code)).toContain("en")
    expect(availableLanguages.find((l) => l.code === "en")?.name).toBe("English")
  })

  it("切换到不存在的语言不生效（防止 localStorage 脏值导致全站裸 key）", () => {
    setLocale("en")
    setLocale("zz-ZZ")
    expect(locale.value).toBe("en")
  })
})
