import { useSettingsStore } from "~/stores/settings"
import { FormUpload } from "./form"
import { StreamUpload } from "./stream"
import { HttpDirectUpload } from "./direct"
import { MultipartUpload } from "./multipart"
import type { Upload } from "./types"

/**
 * 上传通道注册表（对应旧前端 uploads.ts）。
 *
 * 分片上传统一采用官方 multipart 协议（/fs/multipart/*），GO / .NET 后端共用
 * 同一 API 契约；后端对不支持分片的存储返回 data:null，客户端自动回退流式。
 * HTTP Direct 只在当前目录的 fs/list 报告了 direct_upload_tools 时可选。
 */

export interface Uploader {
  name: string
  upload: Upload
  available: () => boolean
}

export function useUploaders(getDirectUploadTools: () => string[] | undefined): Uploader[] {
  const settings = useSettingsStore()
  return [
    {
      name: "Multipart",
      upload: MultipartUpload,
      available: () => settings.getBool("multipart_enabled"),
    },
    {
      name: "HTTP Direct",
      upload: HttpDirectUpload,
      available: () => getDirectUploadTools()?.includes("HttpDirect") || false,
    },
    {
      name: "Stream",
      upload: StreamUpload,
      available: () => true,
    },
    {
      name: "Form",
      upload: FormUpload,
      available: () => true,
    },
  ]
}
