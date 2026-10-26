import { describe, expect, it } from "vitest"
import { parseIni, parseUrlShortcut } from "./ini"

describe("parseIni", () => {
  it("按段归组，段名/键名转小写", () => {
    const ini = parseIni("[InternetShortcut]\nURL=https://a.example/\nIconIndex=0")
    expect(ini.internetshortcut.url).toBe("https://a.example/")
    expect(ini.internetshortcut.iconindex).toBe("0")
  })

  it("忽略注释、空行与 BOM", () => {
    const ini = parseIni("\uFEFF; comment\r\n# another\r\n\r\n[Sec]\r\nKey = value ")
    expect(ini.sec.key).toBe("value")
  })

  it("值里允许出现等号", () => {
    expect(parseIni("[s]\nurl=https://x/?a=1&b=2").s.url).toBe("https://x/?a=1&b=2")
  })

  it("无段的键不丢，归到空段名", () => {
    expect(parseIni("url=https://bare.example/")[""].url).toBe("https://bare.example/")
  })
})

describe("parseUrlShortcut", () => {
  it("取 InternetShortcut.URL，大小写不敏感", () => {
    expect(parseUrlShortcut("[INTERNETSHORTCUT]\nUrl = https://a.example/ ")).toBe("https://a.example/")
  })

  it("兼容 [Default] 段", () => {
    expect(parseUrlShortcut("[Default]\nURL=https://b.example/")).toBe("https://b.example/")
  })

  it("缺少 URL 时返回空串", () => {
    expect(parseUrlShortcut("[InternetShortcut]\nIconFile=x.ico")).toBe("")
    expect(parseUrlShortcut("")).toBe("")
  })
})
