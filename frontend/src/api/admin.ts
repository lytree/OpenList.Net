import { http } from "./client"
import type {
  AdminUser,
  CreateStorageReq,
  CreateUserReq,
  DriverInfo,
  DriverMap,
  IndexBuildReq,
  IndexProgress,
  Meta,
  PageResp,
  PResp,
  ScanProgress,
  ScanStartReq,
  SettingItem,
  StorageWithDriver,
  UpdateUserReq,
} from "./types"

/**
 * 管理台 API（M2）。
 *
 * 后端路由见 `OpenList.Server/Endpoints/AdminEndpoints.cs`（全部 RequireAuthorization("admin")）。
 * 与旧 SolidJS 前端一致：列表返回 `{ content, total }` 信封；保存接口收顶层
 * `CreateStorageReq` + 原样 `addition` JSON 字符串（驱动专属配置）。
 */

/** `GET /admin/driver/list` → Resp<Dictionary<string, DriverInfo>>。 */
export const listDrivers = (): PResp<DriverMap> => http.get<DriverMap>("/admin/driver/list")

/** `GET /admin/driver/info?driver=` → Resp<DriverInfo>。 */
export const getDriverInfo = (driver: string): PResp<DriverInfo> =>
  http.get<DriverInfo>("/admin/driver/info", { params: { driver } })

/** `GET /admin/storage/list` → Resp<PageResp<StorageWithDriver>> = { content, total }。 */
export const listStorages = (): PResp<PageResp<StorageWithDriver>> =>
  http.get<PageResp<StorageWithDriver>>("/admin/storage/list")

/** `GET /admin/storage/get?id=` → Resp<StorageWithDriver>。 */
export const getStorage = (id: number): PResp<StorageWithDriver> =>
  http.get<StorageWithDriver>("/admin/storage/get", { params: { id } })

/** `POST /admin/storage/create` → Resp<{ id }>。 */
export const createStorage = (req: CreateStorageReq): PResp<{ id: number }> =>
  http.post<{ id: number }>("/admin/storage/create", req)

/** `POST /admin/storage/update` → Resp<{}>。 */
export const updateStorage = (
  req: CreateStorageReq,
): PResp<Record<string, never>> => http.post<Record<string, never>>("/admin/storage/update", req)

/** `POST /admin/storage/delete?id=` → Resp<{}>。 */
export const deleteStorage = (id: number): PResp<Record<string, never>> =>
  http.post<Record<string, never>>("/admin/storage/delete", undefined, { params: { id } })

/** `POST /admin/storage/enable?id=` → Resp<{}>。 */
export const enableStorage = (id: number): PResp<Record<string, never>> =>
  http.post<Record<string, never>>("/admin/storage/enable", undefined, { params: { id } })

/** `POST /admin/storage/disable?id=` → Resp<{}>。 */
export const disableStorage = (id: number): PResp<Record<string, never>> =>
  http.post<Record<string, never>>("/admin/storage/disable", undefined, { params: { id } })

/** `POST /admin/storage/load_all` → Resp<{}>（重新加载全部非禁用挂载）。 */
export const loadAllStorages = (): PResp<Record<string, never>> =>
  http.post<Record<string, never>>("/admin/storage/load_all")

// ---------------------------------------------------------------------------
// 用户管理
// ---------------------------------------------------------------------------

/** `GET /admin/user/list` → Resp<{ content, total }>。 */
export const listUsers = (): PResp<PageResp<AdminUser>> =>
  http.get<PageResp<AdminUser>>("/admin/user/list")

/** `GET /admin/user/get?id=` → Resp<AdminUser>。 */
export const getUser = (id: number): PResp<AdminUser> =>
  http.get<AdminUser>("/admin/user/get", { params: { id } })

/** `POST /admin/user/create` → Resp<User>。 */
export const createUser = (req: CreateUserReq): PResp<AdminUser> =>
  http.post<AdminUser>("/admin/user/create", req)

/** `POST /admin/user/update` → Resp<{}>。 */
export const updateUser = (req: UpdateUserReq): PResp<Record<string, never>> =>
  http.post<Record<string, never>>("/admin/user/update", req)

/** `POST /admin/user/delete?id=` → Resp<{}>。内置管理员会被后端拒绝。 */
export const deleteUser = (id: number): PResp<Record<string, never>> =>
  http.post<Record<string, never>>("/admin/user/delete", undefined, { params: { id } })

/** `POST /admin/user/set_password`（body：`{ id, password }`）。 */
export const setUserPassword = (
  id: number,
  password: string,
): PResp<Record<string, never>> =>
  http.post<Record<string, never>>("/admin/user/set_password", { id, password })

/** `POST /admin/user/cancel_2fa?id=`。 */
export const cancelUser2Fa = (id: number): PResp<Record<string, never>> =>
  http.post<Record<string, never>>("/admin/user/cancel_2fa", undefined, { params: { id } })

/** `POST /admin/user/reset_admin_password`（body：`{ password }`，仅内置管理员）。 */
export const resetAdminPassword = (password: string): PResp<Record<string, never>> =>
  http.post<Record<string, never>>("/admin/user/reset_admin_password", { password })

// ---------------------------------------------------------------------------
// 路径元数据
// ---------------------------------------------------------------------------

/** `GET /admin/meta/list` → Resp<{ content, total }>。 */
export const listMetas = (): PResp<PageResp<Meta>> => http.get<PageResp<Meta>>("/admin/meta/list")

/** `POST /admin/meta/save` — Id 为 0 走新增，否则走更新（后端按 Id 分派）。 */
export const saveMeta = (meta: Meta): PResp<Meta> => http.post<Meta>("/admin/meta/save", meta)

/** `POST /admin/meta/delete?id=`。 */
export const deleteMeta = (id: number): PResp<Record<string, never>> =>
  http.post<Record<string, never>>("/admin/meta/delete", undefined, { params: { id } })

// ---------------------------------------------------------------------------
// 搜索索引 / 手动扫描
// ---------------------------------------------------------------------------

/** `GET /admin/index/progress` → Resp<{ obj_count, is_done, last_done_time, error }>。 */
export const getIndexProgress = (): PResp<IndexProgress> =>
  http.get<IndexProgress>("/admin/index/progress")

/** `POST /admin/index/build` — 全量重建。 */
export const buildIndex = (req: IndexBuildReq): PResp<Record<string, never>> =>
  http.post<Record<string, never>>("/admin/index/build", req)

/** `POST /admin/index/update` — 增量更新指定路径。 */
export const updateIndex = (req: IndexBuildReq): PResp<Record<string, never>> =>
  http.post<Record<string, never>>("/admin/index/update", req)

/** `POST /admin/index/stop`。 */
export const stopIndex = (): PResp<Record<string, never>> =>
  http.post<Record<string, never>>("/admin/index/stop")

/** `POST /admin/index/clear`。 */
export const clearIndex = (): PResp<Record<string, never>> =>
  http.post<Record<string, never>>("/admin/index/clear")

/** `GET /admin/scan/progress` → Resp<{ obj_count, is_done }>。 */
export const getScanProgress = (): PResp<ScanProgress> =>
  http.get<ScanProgress>("/admin/scan/progress")

/** `POST /admin/scan/start`（body：`{ path, limit }`）。 */
export const startScan = (req: ScanStartReq): PResp<Record<string, never>> =>
  http.post<Record<string, never>>("/admin/scan/start", req)

/** `POST /admin/scan/stop`。 */
export const stopScan = (): PResp<Record<string, never>> =>
  http.post<Record<string, never>>("/admin/scan/stop")

// ---------------------------------------------------------------------------
// 站点设置
// ---------------------------------------------------------------------------

/**
 * `GET /admin/setting/list` → Resp<SettingItem[]>。
 *
 * 后端不带 `group` 参数时返回**全部**设置项（约 120 条），前端按 `group` 自行分组；
 * 一次性拉全量可以避免切分组时重复请求。
 */
export const listSettings = (): PResp<SettingItem[]> =>
  http.get<SettingItem[]>("/admin/setting/list")

/**
 * `POST /admin/setting/save` — 合并写：存在的更新、不存在的插入。
 * 只需发送**改动过**的项（含 key/value/type/flag/group/options 元信息，后端会一并覆盖）。
 */
export const saveSettings = (items: SettingItem[]): PResp<Record<string, never>> =>
  http.post<Record<string, never>>("/admin/setting/save", items)

/** `POST /admin/setting/default?group=` — 恢复某分组的默认值。 */
export const resetSettingsGroup = (group: number): PResp<Record<string, never>> =>
  http.post<Record<string, never>>("/admin/setting/default", undefined, { params: { group } })

/** `POST /admin/setting/reset_token` — 轮换 JWT 签名密钥（会让所有已签发 token 失效）。 */
export const resetToken = (): PResp<Record<string, never>> =>
  http.post<Record<string, never>>("/admin/setting/reset_token")
