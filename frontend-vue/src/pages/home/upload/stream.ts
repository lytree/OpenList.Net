import { http } from "~/api/client"
import type { SetUpload, Upload } from "./types"
import { calculateHash } from "./util"

/**
 * 流式上传（`PUT /fs/put`，body 即文件字节，`File-Path` 头给目标路径）。
 * 任何存储都可用，是上传管线的兜底通道。
 */
export const StreamUpload: Upload = async (
  uploadPath,
  file,
  setUpload,
  opts,
) => {
  const { password, overwrite, rapid } = opts
  let oldTimestamp = Date.now()
  let oldLoaded = 0

  const headers: Record<string, string | number | boolean> = {
    "File-Path": encodeURIComponent(uploadPath),
    "Content-Type": file.type || "application/octet-stream",
    "Last-Modified": file.lastModified,
    Password: password,
    Overwrite: String(overwrite),
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
  const resp = await http.put("/fs/put", file, {
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
        // 本地字节已发完；后端可能还在往存储写
        setUpload("status", "backending")
      }
    },
  })
  if (resp.code !== 200) {
    throw new Error(resp.message || "upload failed")
  }
}
