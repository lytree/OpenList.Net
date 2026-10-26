import { describe, expect, it } from "vitest"
import { ObjType } from "~/api/types"
import { convertUrl, guessObjType, parseIframePreviews, resolvePreviews } from "./registry"
import type { PreviewEntry } from "./types"

const file = (name: string, type: ObjType, size = 1024) => ({ name, type, size })
const keys = (entries: PreviewEntry[]) => entries.map((entry) => entry.key)

describe("resolvePreviews", () => {
  it("markdown 优先于纯文本，下载恒在最后", () => {
    const result = resolvePreviews(file("readme.md", ObjType.TEXT))
    expect(keys(result)[0]).toBe("markdown")
    expect(keys(result)).toContain("text")
    expect(keys(result)[0]).not.toBe("text")
    expect(keys(result)[result.length - 1]).toBe("download")
  })

  it("按后端 ObjType 命中图片/视频/音频", () => {
    expect(keys(resolvePreviews(file("a.jpg", ObjType.IMAGE)))[0]).toBe("image")
    expect(keys(resolvePreviews(file("a.mp4", ObjType.VIDEO)))[0]).toBe("video")
    expect(keys(resolvePreviews(file("a.flac", ObjType.AUDIO)))[0]).toBe("audio")
  })

  it("扩展名能补后端分类：pdf 即使被标成 UNKNOWN 也能预览", () => {
    expect(keys(resolvePreviews(file("a.pdf", ObjType.UNKNOWN)))[0]).toBe("pdf")
  })

  it("未知二进制大文件：下载排最前，信息页在其后", () => {
    const result = keys(resolvePreviews(file("a.bin", ObjType.UNKNOWN, 5 * 1024 * 1024)))
    expect(result).toEqual(["download", "info"])
  })

  it("未知小文件额外提供一个「按文本打开」，且排在下载之后", () => {
    const result = keys(resolvePreviews(file("a.bin", ObjType.UNKNOWN, 1024)))
    expect(result).toEqual(["download", "info", "text"])
  })

  it("downloadFirst 把下载提到最前（设置 preview_download_by_default）", () => {
    const result = keys(resolvePreviews(file("a.md", ObjType.TEXT), { downloadFirst: true }))
    expect(result[0]).toBe("download")
  })

  it("设置里的 iframe 预览按扩展名注入，排在原生预览之后", () => {
    // docx 现在有原生渲染器：默认走它（文件不出服务器），
    // 管理员配的在线预览作为下拉里的另一个选项，而不是抢默认位。
    const iframePreviews = parseIframePreviews(
      JSON.stringify({ docx: { "Office 在线预览": "https://view.example/?src=$eurl" } }),
    )
    const result = resolvePreviews(file("a.docx", ObjType.UNKNOWN), { iframePreviews })
    expect(result[0].key).toBe("doc")
    const iframeEntry = result.find((entry) => entry.key.startsWith("iframe:"))
    expect(iframeEntry?.key).toBe("iframe:docx:Office 在线预览")
    expect(iframeEntry?.props?.scheme).toBe("https://view.example/?src=$eurl")
  })

  it("iframe 设置支持正则键", () => {
    const iframePreviews = parseIframePreviews(JSON.stringify({ "/^docx?$/": { Office: "https://x" } }))
    const result = resolvePreviews(file("a.doc", ObjType.UNKNOWN), { iframePreviews })
    expect(result[0].key).toBe("iframe:/^docx?$/:Office")
  })

  it("归档排在纯文本之前（zip 不再落到「按文本打开」）", () => {
    const result = keys(resolvePreviews(file("bundle.zip", ObjType.UNKNOWN)))
    expect(result[0]).toBe("archive")
    expect(result).not.toContain("text")
  })

  it("种子有专用预览器，且不会掉进「按文本打开」", () => {
    const result = keys(resolvePreviews(file("a.torrent", ObjType.UNKNOWN)))
    expect(result[0]).toBe("torrent")
    expect(result).not.toContain("text")
  })

  it("url / plist / ipa 各有专用预览器", () => {
    expect(keys(resolvePreviews(file("a.url", ObjType.UNKNOWN)))[0]).toBe("url_open")
    expect(keys(resolvePreviews(file("a.plist", ObjType.UNKNOWN)))[0]).toBe("plist")
    expect(keys(resolvePreviews(file("a.ipa", ObjType.UNKNOWN)))[0]).toBe("ipa")
  })
})

describe("parseIframePreviews", () => {
  it("拒绝非法 JSON / 非对象 / 空值，静默降级为空表", () => {
    expect(parseIframePreviews("")).toEqual({})
    expect(parseIframePreviews("{oops")).toEqual({})
    expect(parseIframePreviews("[]")).toEqual({})
    expect(parseIframePreviews('{"docx":{}}')).toEqual({})
  })

  it("过滤掉非字符串的模板值", () => {
    expect(parseIframePreviews('{"docx":{"a":"https://x","b":123}}')).toEqual({
      docx: { a: "https://x" },
    })
  })
})

describe("convertUrl", () => {
  const args = { raw_url: "https://cdn.test/a b.pdf", d_url: "https://cdn.test/d/a.pdf", name: "a.pdf" }

  it("替换 $url 与 $name", () => {
    expect(convertUrl("https://v/?src=$url&name=$name", args)).toBe(
      "https://v/?src=https://cdn.test/a b.pdf&name=a.pdf",
    )
  })

  it("$eurl 做 encodeURIComponent，$burl 做 base64", () => {
    expect(convertUrl("$eurl", args)).toBe(encodeURIComponent(args.raw_url))
    expect(convertUrl("$burl", args)).toBe(btoa(args.raw_url))
  })

  it("$durl 使用下载直链而不是预览地址", () => {
    expect(convertUrl("$durl", args)).toBe(args.d_url)
  })
})

describe("guessObjType", () => {
  it("按注册表的类型声明反推（归档内条目后端不分类）", () => {
    expect(guessObjType("a.PNG")).toBe(ObjType.IMAGE)
    expect(guessObjType("a.mp4")).toBe(ObjType.VIDEO)
    expect(guessObjType("a.flac")).toBe(ObjType.AUDIO)
    expect(guessObjType("a.conf")).toBe(ObjType.TEXT)
    expect(guessObjType("a.bin")).toBe(ObjType.UNKNOWN)
    expect(guessObjType("noext")).toBe(ObjType.UNKNOWN)
  })

  it("pdf/md/html 这类只由扩展名命中的文件不会被误判成 TEXT", () => {
    // 这三类在注册表里没有 type 声明，猜出来是 UNKNOWN，仍由 exts 命中预览器
    expect(guessObjType("a.pdf")).toBe(ObjType.UNKNOWN)
    expect(keys(resolvePreviews(file("a.pdf", guessObjType("a.pdf"))))[0]).toBe("pdf")
  })
})
