import { http } from "./client"
import type {
  FsGetResp,
  FsListResp,
  Obj,
  PEmptyResp,
  PResp,
  RenameObj,
  Resp,
} from "./types"

/**
 * 文件系统 API（`/api/fs/*`）。
 *
 * M0 只落地首页文件管理真正用到的一组；`/fs/archive/*`、`/fs/torrent/*`
 * 等归档/种子接口在 M3（预览器）阶段补齐，届时连同取消令牌一起加。
 */

export const fsGet = (
  path = "/",
  password = "",
  signal?: AbortSignal,
): Promise<FsGetResp> => http.post<FsGetResp["data"]>("/fs/get", { path, password }, { signal }) as Promise<FsGetResp>

export const fsList = (
  path = "/",
  password = "",
  page = 1,
  per_page = 0,
  refresh = false,
  signal?: AbortSignal,
): Promise<FsListResp> =>
  http.post<FsListResp["data"]>(
    "/fs/list",
    { path, password, page, per_page, refresh },
    { signal },
  ) as Promise<FsListResp>

/** 后端 `fs/dirs` 响应：初始目录下深度 3 以内的**全路径**子目录列表。 */
export type DirsResp = { dirs: string[] }

export const fsDirs = (path = "/", password = "", forceRoot = false): PResp<DirsResp> =>
  http.post<DirsResp>("/fs/dirs", { path, password, force_root: forceRoot })

export const fsMkdir = (path: string): PEmptyResp =>
  http.post("/fs/mkdir", { path })

export const fsRename = (
  path: string,
  name: string,
  overwrite: boolean,
): PEmptyResp => http.post("/fs/rename", { path, name, overwrite })

export const fsBatchRename = (
  src_dir: string,
  rename_objects: RenameObj[],
): PEmptyResp => http.post("/fs/batch_rename", { src_dir, rename_objects })

export const fsMove = (
  src_dir: string,
  dst_dir: string,
  names: string[],
  overwrite = false,
  skip_existing = false,
): PEmptyResp =>
  http.post("/fs/move", { src_dir, dst_dir, names, overwrite, skip_existing })

export const fsCopy = (
  src_dir: string,
  dst_dir: string,
  names: string[],
  overwrite = false,
  skip_existing = false,
  merge = false,
): PEmptyResp =>
  http.post("/fs/copy", {
    src_dir,
    dst_dir,
    names,
    overwrite,
    skip_existing,
    merge,
  })

export const fsRemove = (dir: string, names: string[]): PEmptyResp =>
  http.post("/fs/remove", { dir, names })

/**
 * 递归删除目录下的空子目录。
 *
 * ⚠️ 字段名与 Go 上游不同：Go 的 `FsRemoveEmptyDirectoryReq` 用 `src_dir`，
 * 本仓库 C# 端复用 `PathReq`（字段名 `path`）。所以**必须传 path**，
 * 传 `src_dir` 会被 JSON 绑定忽略、`Path` 落到默认值 `"/"`——那样会去扫整站根目录。
 */
export const fsRemoveEmptyDirectory = (path: string): PEmptyResp =>
  http.post("/fs/remove_empty_directory", { path })

/**
 * 递归移动（把 `src_dir` 整个搬到 `dst_dir`，含所有子目录）。
 *
 * `conflict_policy`：0 = 跳过已存在（Go `ConflictPolicy` 默认值），1 = 覆盖。
 * 与 `/fs/move` 的区别是后者按「names 列表」在**同一层**移动，前者是整个子树。
 */
export const fsRecursiveMove = (
  src_dir: string,
  dst_dir: string,
  conflict_policy = 0,
): PEmptyResp => http.post("/fs/recursive_move", { src_dir, dst_dir, conflict_policy })

export interface ArchiveDecompressReq {
  src_dir: string
  dst_dir: string
  /** 归档文件名（数组，取第一个），与 `src_dir` 拼出归档路径。 */
  name: string[]
  archive_pass?: string
  inner_path?: string
  cache_full?: boolean
  /** 解压到同名新目录（多一层 folder）。 */
  put_into_new_dir?: boolean
  overwrite?: boolean
}

/** 解压归档文件（后端委托给驱动的归档解压接口）。 */
export const fsArchiveDecompress = (req: ArchiveDecompressReq): PEmptyResp =>
  http.post("/fs/archive/decompress", req)

/** 列举归档内部条目（用于解压前确认可读，驱动不支持时返回 501）。 */
export const fsArchiveList = (path: string, password = "", archive_pass = "") =>
  http.post<{ content: { name: string; size: number; is_dir: boolean; modified: string }[]; total: number }>(
    "/fs/archive/list",
    { path, password, archive_pass },
  )

/** 离线下载的「下载完成后删除策略」。数值与 Go `model.DeletePolicy` 一致。 */
export enum DeletePolicy {
  DeleteOnUploadSucceed = 0,
  DeleteOnUploadFailed = 1,
  DeleteNever = 2,
  DeleteAlways = 3,
  UploadDownloadStream = 4,
}

/** 提交离线下载任务（后端据此调用 aria2/qbit 等，未配置时返回 501）。 */
export const fsAddOfflineDownload = (
  path: string,
  urls: string[],
  tool: string,
  delete_policy: DeletePolicy,
): PEmptyResp => http.post("/fs/add_offline_download", { path, urls, tool, delete_policy })

/** 站点已启用的离线下载工具名（Public 设置派生，未启用时为空数组）。 */
export const fetchOfflineDownloadTools = (): PResp<string[]> =>
  http.get<string[]>("/public/offline_download_tools")

export type SearchNode = {
  parent: string
  name: string
  is_dir: boolean
  size: number
  path: string
  type: number
}

export const fsSearch = (
  parent: string,
  keywords: string,
  password = "",
  scope = 0,
  page = 1,
  per_page = 100,
): Promise<Resp<{ content: SearchNode[]; total: number }>> =>
  http.post<{ content: SearchNode[]; total: number }>("/fs/search", {
    parent,
    keywords,
    scope,
    page,
    per_page,
    password,
  })

export const fsPutFile = (
  path: string,
  password: string,
  overwrite: boolean,
  body?: BodyInit,
): PEmptyResp =>
  http.put("/fs/put", body, {
    headers: {
      "File-Path": encodeURIComponent(path),
      Password: password,
      Overwrite: String(overwrite),
    },
  })
