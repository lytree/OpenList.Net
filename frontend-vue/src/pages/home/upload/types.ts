/**
 * 上传管线类型（对应旧前端 `src/pages/home/uploads/types.ts`）。
 *
 * 与旧实现的差异：
 * - Upload 签名把 password/overwrite/rapid 收进一个 opts 对象（旧版从全局
 *   store 里摸 password，Vue 侧目录密码是 useFileList 的局部状态，显式传递）。
 * - as-task 选项暂缺：.NET 后端的 /fs/put 是同步写盘，没有离线任务系统，
 *   传了也是 no-op，等任务中心落地再补。
 */

export type UploadStatus =
  | "pending"
  | "hashing"
  | "uploading"
  | "backending"
  | "success"
  | "error"

export interface UploadFileProps {
  /** 展示名：单文件是文件名，目录上传是 webkitRelativePath 相对路径。 */
  name: string
  /** 相对当前目录的目标路径（不含当前目录前缀）。 */
  path: string
  size: number
  /** 0-100。hashing 阶段复用为哈希进度。 */
  progress: number
  /** 字节/秒。 */
  speed: number
  status: UploadStatus
  msg?: string
}

export const StatusBadge = {
  pending: "info",
  hashing: "warning",
  uploading: "primary",
  backending: "primary",
  success: "success",
  error: "danger",
} as const

export type SetUpload = (key: keyof UploadFileProps, value: unknown) => void

export interface UploadOptions {
  /** 目标目录的访问密码（加密目录需要）。 */
  password: string
  /** 同名文件是否覆盖。 */
  overwrite: boolean
  /** 是否先算哈希尝试秒传。 */
  rapid: boolean
}

export type Upload = (
  uploadPath: string,
  file: File,
  setUpload: SetUpload,
  opts: UploadOptions,
) => Promise<Error | void>
