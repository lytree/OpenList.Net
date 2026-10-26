import { http } from "~/api/client"
import type { SetUpload, Upload } from "./types"
import { calculateHash } from "./util"

/**
 * 表单上传（`PUT /fs/form`，multipart/form-data，`File-Path` 头给目标路径）。
 * 部分网关对流式 PUT 兼容不佳时的备选通道；后端会逐个处理表单里的文件。
 */
export const FormUpload: Upload = async (uploadPath, file, setUpload, opts) => {
  const { password, overwrite, rapid } = opts
  let oldTimestamp = Date.now()
  let oldLoaded = 0

  const form = new FormData()
  form.append("file", file)

  const headers: Record<string, string | number | boolean> = {
    "File-Path": encodeURIComponent(uploadPath),
    "Last-Modified": file.lastModified,
    Password: password,
    Overwrite: String(overwrite),
    // Content-Type 交给浏览器带 boundary；手动设死会丢 boundary 导致 400
  }
  if (rapid) {
    setUpload("status", "hashing")
    setUpload("progress", 0)
    const { md5, sha1, sha256 } = await calculateHash(file, (p) => {
      setUpload("progress", p | 0)
    })
    headers["X-File-Md5"] = md5
    headers["X-File-Sha1"] = sha1
    headers["X-File-Sha256"] = sha256
  }

  setUpload("status", "uploading")
  const resp = await http.put("/fs/form", form, {
    headers,
    onUploadProgress: (progressEvent) => {
      if (!progressEvent.total) return
      const complete = ((progressEvent.loaded / progressEvent.total) * 100) | 0
      setUpload("progress", complete)

      const timestamp = Date.now()
      const duration = (timestamp - oldTimestamp) / 1000
      if (duration > 1) {
        const speed = (progressEvent.loaded - oldLoaded) / duration
        setUpload("speed", speed)
        oldTimestamp = timestamp
        oldLoaded = progressEvent.loaded
      }

      if (complete === 100) {
        setUpload("status", "backending")
      }
    },
  })
  if (resp.code !== 200) {
    throw new Error(resp.message || "upload failed")
  }
}
