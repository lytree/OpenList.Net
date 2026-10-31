import { describe, expect, it } from "vitest"
import { ObjType } from "~/api/types"
import {
  convertUrl,
  guessObjType,
  parseExternalPreviews,
  parseExtList,
  parseIframePreviews,
  resolvePreviews,
} from "./registry"
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

  it("swf 走 Flash 预览器，且不会掉进「按文本打开」", () => {
    const result = keys(resolvePreviews(file("legacy.swf", ObjType.UNKNOWN)))
    expect(result[0]).toBe("flash")
    expect(result).not.toContain("text")
  })

  it("swf 被后端归为 IMAGE 时仍默认选 Flash（否则只会显示裂图）", () => {
    // 后端扩展名表沿袭 Go：swf 在 image 里。image 预览器会命中它，
    // 必须靠 order(19 < 20) 压过去。
    const entries = keys(resolvePreviews(file("legacy.swf", ObjType.IMAGE)))
    expect(entries[0]).toBe("flash")
    expect(entries).toContain("image")
  })

  it("video360 只对带全景标记的视频出现，且默认仍选普通播放器", () => {
    for (const name of [
      "pano_360.mp4",
      "VR-Concert.mp4",
      "equirect-demo.mp4",
      "demo panorama.mp4",
    ]) {
      const entries = resolvePreviews(file(name, ObjType.VIDEO))
      expect(entries[0].key).toBe("video")
      expect(keys(entries)).toContain("video360")
    }
  })

  it("没有全景标记的视频不出现 video360（避免污染所有视频的预览下拉）", () => {
    for (const name of ["Blade Runner 2049.mp4", "vroom.mp4", "vr5.mp4", "movie.mp4"]) {
      expect(keys(resolvePreviews(file(name, ObjType.VIDEO)))).not.toContain("video360")
    }
    // 非视频类型即使名字带 360 也不该出现
    expect(keys(resolvePreviews(file("pano_360.jpg", ObjType.IMAGE)))).not.toContain("video360")
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

describe("设置驱动的扩展名与归档默认位", () => {
  it("parseExtList 兼容中英文逗号、点号前缀与空白", () => {
    expect(parseExtList("mp3, flac，M4A")).toEqual(["mp3", "flac", "m4a"])
    expect(parseExtList(".mkv .mp4")).toEqual(["mkv", "mp4"])
    // 空值不能让调用方拿到空表：那样所有媒体预览都会消失
    expect(parseExtList("   ")).toBeUndefined()
    expect(parseExtList("")).toBeUndefined()
    expect(parseExtList(undefined)).toBeUndefined()
  })

  it("audio_types 整表替换内置音频扩展名", () => {
    expect(keys(resolvePreviews(file("a.m4b", ObjType.UNKNOWN)))[0]).not.toBe("audio")
    expect(keys(resolvePreviews(file("a.m4b", ObjType.UNKNOWN), { typeExts: { audio: ["m4b"] } }))[0]).toBe(
      "audio",
    )
  })

  it("text_types 让自定义文本扩展名可预览", () => {
    const result = resolvePreviews(file("a.weird", ObjType.UNKNOWN), { typeExts: { text: ["weird"] } })
    expect(keys(result)[0]).toBe("text")
  })

  it("preview_archives_by_default=false：下载排最前，归档仍在下拉里", () => {
    const result = keys(resolvePreviews(file("bundle.zip", ObjType.UNKNOWN), { archiveDefault: false }))
    expect(result[0]).toBe("download")
    expect(result).toContain("archive")
  })

  it("默认（未传 archiveDefault）仍保持归档优先，兼容既有行为", () => {
    expect(keys(resolvePreviews(file("bundle.zip", ObjType.UNKNOWN)))[0]).toBe("archive")
  })

  it("external_previews 按扩展名注入外置预览条目", () => {
    const externalPreviews = parseExternalPreviews(JSON.stringify({ mp4: { VLC: "vlc://$eurl" } }))
    const result = resolvePreviews(file("a.mp4", ObjType.VIDEO), { externalPreviews })
    const entry = result.find((e) => e.key.startsWith("external:"))
    expect(entry?.key).toBe("external:mp4:VLC")
    expect(entry?.props?.scheme).toBe("vlc://$eurl")
  })

  it("guessObjType 与主列表共用可配置扩展名（归档内外表现一致）", () => {
    expect(guessObjType("a.m4b")).toBe(ObjType.UNKNOWN)
    expect(guessObjType("a.m4b", { audio: ["m4b"] })).toBe(ObjType.AUDIO)
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
