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

/**
 * 字典完整性：zh 必须覆盖 en 的每一个键。
 *
 * 缺键不会报错，只会静默回退英文 —— 单看界面根本看不出「这里漏翻了」，
 * 所以只能靠键集合比对。`index.json` 只存语言名（`lang` 字段），i18n 不把它当命名空间。
 */
describe("zh 字典覆盖度", () => {
  const enModules = import.meta.glob<{ default: Record<string, unknown> }>("../lang/en/*.json", {
    eager: true,
  })
  const zhModules = import.meta.glob<{ default: Record<string, unknown> }>("../lang/zh/*.json", {
    eager: true,
  })

  /** 把嵌套字典摊平成 `a.b.c` 形式的键列表（只收叶子）。 */
  function flatten(node: Record<string, unknown>, prefix = ""): string[] {
    return Object.entries(node).flatMap(([key, value]) => {
      const path = prefix ? `${prefix}.${key}` : key
      if (value && typeof value === "object") return flatten(value as Record<string, unknown>, path)
      return [path]
    })
  }

  const namespaceOf = (path: string) => path.replace(/^.*\/lang\/(en|zh)\//, "").replace(/\.json$/, "")

  it("zh 的每个命名空间都存在，且键集合与 en 完全一致", () => {
    const enNamespaces = Object.keys(enModules)
      .map(namespaceOf)
      .filter((ns) => ns !== "index")
    const zhNamespaces = Object.keys(zhModules)
      .map(namespaceOf)
      .filter((ns) => ns !== "index")

    expect(zhNamespaces.sort()).toEqual(enNamespaces.sort())

    for (const ns of enNamespaces) {
      const enKeys = flatten(enModules[`../lang/en/${ns}.json`]!.default)
      const zhKeys = flatten(zhModules[`../lang/zh/${ns}.json`]!.default)
      expect({ ns, missing: enKeys.filter((key) => !zhKeys.includes(key)) }).toEqual({
        ns,
        missing: [],
      })
      expect({ ns, extra: zhKeys.filter((key) => !enKeys.includes(key)) }).toEqual({
        ns,
        extra: [],
      })
    }
  })
})
