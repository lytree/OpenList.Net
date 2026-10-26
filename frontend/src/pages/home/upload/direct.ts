import { http } from "~/api/client"
import type { SetUpload, Upload } from "./types"

/**
 * HTTP 直传（对应旧前端 direct.ts）。
 *
 * 流程：`POST /fs/get_direct_upload_info` 拿到预签名 URL 与所需请求头，
 * 浏览器直接把字节发给对象存储/网盘，不占用服务端带宽。
 *
 * 与旧实现的差异：.NET 的 `DirectUploadInfo` 只有 `url`/`headers`/`method`
 * 三个字段（没有 Go 版的 `upload_url`/`chunk_size`），因此只做单请求直传；
 * 若后端未来补 `chunk_size`，这里已保留 Content-Range 分片分支。
 */

interface DirectUploadInfo {
  url: string
  headers?: Record<string, string>
  method?: string
  /** 可选：>0 时按分片 + Content-Range 上传（.NET 暂不返回）。 */
  chunk_size?: number
}

/** 速度计算器：500ms 节流，避免 setUpload 抖动过快。 */
function createSpeedCalculator(throttleMs = 500) {
  let lastLoaded = 0
  let lastTime = Date.now()

  return (loaded: number, setUpload?: SetUpload) => {
    const now = Date.now()
    const timeDiff = (now - lastTime) / 1000

    if (timeDiff >= throttleMs / 1000) {
      const speed = (loaded - lastLoaded) / timeDiff
      setUpload?.("speed", speed)
      lastLoaded = loaded
      lastTime = now
    }
  }
}

export const HttpDirectUpload: Upload = async (
  uploadPath,
  file,
  setUpload,
  opts,
) => {
  // 目标父目录（get_direct_upload_info 按 path + file_name 定位）
  const dir = uploadPath.split("/").slice(0, -1).join("/") || "/"

  const resp = await http.post<DirectUploadInfo | null>("/fs/get_direct_upload_info", {
    path: dir,
    file_name: file.name.replace(/^.*\//, ""),
    file_size: file.size,
    tool: "HttpDirect",
  }, {
    headers: {
      Password: opts.password,
      Overwrite: String(opts.overwrite),
    },
  })

  const uploadInfo = resp.data
  if (!uploadInfo || !uploadInfo.url) {
    throw new Error("Http Direct Upload not supported")
  }

  const chunkSize = uploadInfo.chunk_size || 0
  const method = uploadInfo.method || "PUT"

  if (chunkSize > 0) {
    return await uploadChunked(
      file,
      uploadInfo.url,
      chunkSize,
      method,
      uploadInfo.headers,
      setUpload,
    )
  }
  return await uploadSingle(file, uploadInfo.url, method, uploadInfo.headers, setUpload)
}

async function uploadSingle(
  file: File,
  uploadURL: string,
  method: string,
  headers?: Record<string, string>,
  setUpload?: SetUpload,
): Promise<undefined> {
  const xhr = new XMLHttpRequest()
  const calcSpeed = createSpeedCalculator()

  return new Promise((resolve, reject) => {
    xhr.upload.addEventListener("progress", (e) => {
      if (e.lengthComputable && setUpload) {
        const progress = (e.loaded / e.total) * 100
        setUpload("progress", progress)
        calcSpeed(e.loaded, setUpload)
      }
    })

    xhr.addEventListener("load", () => {
      if (xhr.status >= 200 && xhr.status < 300) {
        resolve(undefined)
      } else {
        reject(new Error(`Upload failed with status ${xhr.status}`))
      }
    })

    xhr.addEventListener("error", () => {
      reject(new Error("Upload failed"))
    })

    xhr.open(method, uploadURL)

    if (headers) {
      Object.entries(headers).forEach(([key, value]) => {
        xhr.setRequestHeader(key, value)
      })
    }

    xhr.send(file)
  })
}

async function uploadChunked(
  file: File,
  uploadURL: string,
  chunkSize: number,
  method: string,
  headers?: Record<string, string>,
  setUpload?: SetUpload,
): Promise<undefined> {
  const totalChunks = Math.ceil(file.size / chunkSize)
  const calcSpeed = createSpeedCalculator()
  let uploadedBytes = 0

  for (let i = 0; i < totalChunks; i++) {
    const start = i * chunkSize
    const end = Math.min(start + chunkSize, file.size)
    const chunk = file.slice(start, end)

    const xhr = new XMLHttpRequest()

    await new Promise<void>((resolve, reject) => {
      xhr.upload.addEventListener("progress", (e) => {
        if (e.lengthComputable && setUpload) {
          const totalLoaded = uploadedBytes + e.loaded
          const progress = (totalLoaded / file.size) * 100
          setUpload("progress", progress)
          calcSpeed(totalLoaded, setUpload)
        }
      })

      xhr.addEventListener("load", () => {
        if (xhr.status >= 200 && xhr.status < 300) {
          uploadedBytes += chunk.size
          resolve()
        } else {
          reject(new Error(`Upload chunk ${i + 1} failed with status ${xhr.status}`))
        }
      })

      xhr.addEventListener("error", () => {
        reject(new Error(`Upload chunk ${i + 1} failed`))
      })

      xhr.open(method, uploadURL)

      xhr.setRequestHeader("Content-Range", `bytes ${start}-${end - 1}/${file.size}`)

      if (headers) {
        Object.entries(headers).forEach(([key, value]) => {
          xhr.setRequestHeader(key, value)
        })
      }

      xhr.send(chunk)
    })
  }

  return undefined
}
