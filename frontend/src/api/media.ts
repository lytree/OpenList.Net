import { getToken, http } from "./client"
import type { Resp } from "./types"

/**
 * 媒体域 API（`/api/media/*`）。
 *
 * 后端直接返回实体 JSON（`MediaItem` / `MediaLibrary` / `MediaFile`），snake_case
 * 由实体上的 `[JsonPropertyName]` 提供 —— 这里的类型按**线上字段名**写，
 * 不照搬 C# 属性名（`genres` 是字符串、`poster_path` 可能是 VFS 路径）。
 */

/** 媒体库类型（与后端 MediaLibraryType 对齐）。 */
export enum MediaLibraryType {
  Movie = 0,
  TvShow = 1,
  Mixed = 2,
}

/** 刮削匹配状态。 */
export enum MediaItemStatus {
  Pending = 0,
  Matched = 1,
  Unmatched = 2,
  Ignored = 3,
}

export interface MediaLibrary {
  id: number
  name: string
  /** VFS 标准化绝对路径。 */
  path: string
  type: MediaLibraryType
  auto_scan: boolean
  metadata_language?: string | null
  created_by: number
  last_scan_at?: string | null
  /** 注意：实体的 CreatedAt/UpdatedAt 没有 [JsonPropertyName]，线上是 camelCase */
  createdAt: string
  updatedAt: string
}

export interface MediaItem {
  id: number
  library_id: number
  type: MediaLibraryType
  title: string
  original_title?: string | null
  year?: number | null
  tmdb_id?: string | null
  /** 番号（番号域库的检索键）。 */
  code?: string | null
  studio_id?: number | null
  series_id?: number | null
  metadata_provider?: string | null
  overview?: string | null
  /** VFS 内图片路径或外部 URL；空表示没有图。 */
  poster_path?: string | null
  backdrop_path?: string | null
  rating?: number | null
  /** JSON 数组字符串（后端原样透传列值）。 */
  genres?: string | null
  status: MediaItemStatus
  directory_path: string
  createdAt: string
  updatedAt: string
}

export interface MediaFile {
  id: number
  item_id: number
  path: string
  size: number
  duration_seconds?: number | null
  resolution?: string | null
  createdAt: string
}

/** 当前用户的个人状态（收藏/看过/进度）。所有字段都可能有默认值。 */
export interface UserMediaState {
  id: number
  user_id: number
  item_id: number
  favorite: boolean
  watched: boolean
  watch_later: boolean
  flagged: boolean
  flag_note?: string | null
  user_rating?: number | null
  /** 播放进度（秒）。 */
  progress_seconds: number
  last_played_at?: string | null
  updated_at: string
}

export interface MediaStatePatch {
  favorite?: boolean
  watched?: boolean
  watch_later?: boolean
  flagged?: boolean
  flag_note?: string
  user_rating?: number
  progress_seconds?: number
}

/** 分页载荷（与后端 PageResp<T> 对齐）。 */
export interface Page<T> {
  content: T[]
  total: number
}

/** 条目查询参数（全部可选；后端缺省 page=1 per_page=30）。 */
export interface MediaItemQuery {
  library_id?: number
  page?: number
  per_page?: number
  keyword?: string
  status?: MediaItemStatus
  genre?: string
  year_from?: number
  year_to?: number
  rating_min?: number
  person_id?: number
  studio_id?: number
  series_id?: number
  /** created（默认，最近入库）/ title / rating / year / updated —— 对齐后端 ApplySort。 */
  sort?: "created" | "updated" | "title" | "rating" | "year"
  desc?: boolean
  /** fresh / played / flagged / later / favorite / in-progress。 */
  state?: string
}

export interface MediaHome {
  recently_added: MediaItem[]
  continue_watching: MediaItem[]
  recently_favorited: MediaItem[]
}

// ---- 库 ------------------------------------------------------------------

export const mediaListLibraries = (): Promise<Resp<MediaLibrary[]>> =>
  http.get<MediaLibrary[]>("/media/libraries") as Promise<Resp<MediaLibrary[]>>

export type CreateMediaLibraryReq = {
  name: string
  path: string
  type: MediaLibraryType
  auto_scan?: boolean
  metadata_language?: string
}

export const mediaCreateLibrary = (req: CreateMediaLibraryReq): Promise<Resp<MediaLibrary>> =>
  http.post<MediaLibrary>("/media/libraries", req) as Promise<Resp<MediaLibrary>>

export const mediaUpdateLibrary = (
  id: number,
  req: Partial<CreateMediaLibraryReq>,
): Promise<Resp<MediaLibrary>> =>
  http.post<MediaLibrary>(`/media/libraries/${id}/update`, req) as Promise<Resp<MediaLibrary>>

export const mediaDeleteLibrary = (id: number): Promise<Resp<unknown>> =>
  http.post(`/media/libraries/${id}/delete`, {}) as Promise<Resp<unknown>>

export const mediaScanLibrary = (id: number): Promise<Resp<unknown>> =>
  http.post(`/media/libraries/${id}/scan`, {}) as Promise<Resp<unknown>>

export const mediaStopScan = (id: number): Promise<Resp<unknown>> =>
  http.post(`/media/libraries/${id}/scan/stop`, {}) as Promise<Resp<unknown>>

/** 扫描状态快照 / 跨库任务卡的公共字段（state: idle/running/completed/failed/cancelled）。 */
export interface MediaScanStats {
  found: number
  added: number
  updated: number
  removed: number
}

/** 扫描进度快照（`GET /libraries/{id}/scan`，无需登录校验 —— 后端以库 id 为粒度）。 */
export type MediaScanProgress = MediaScanStats & {
  state: string
  current_path?: string | null
  started_at?: string | null
  finished_at?: string | null
}

/** 扫描历史行（映射 x_media_scan_logs）。 */
export interface MediaScanLog extends MediaScanStats {
  id: number
  library_id: number
  task_id?: string | null
  error?: string | null
  started_at: string
  finished_at?: string | null
}

/** 跨库扫描任务项（`GET /media/tasks`，管理台任务卡 / 行内状态徽标的数据源）。 */
export type MediaScanTask = MediaScanStats & {
  library_id: number
  library_name: string
  path: string
  state: string
  current_path?: string | null
  started_at?: string | null
  finished_at?: string | null
  error?: string | null
  is_running: boolean
}

export const mediaGetScanProgress = (id: number): Promise<Resp<MediaScanProgress>> =>
  http.get<MediaScanProgress>(`/media/libraries/${id}/scan`) as Promise<Resp<MediaScanProgress>>

export const mediaListScanLogs = (id: number, limit = 20): Promise<Resp<MediaScanLog[]>> =>
  http.get<MediaScanLog[]>(`/media/libraries/${id}/scan/logs`, {
    params: { limit },
  }) as Promise<Resp<MediaScanLog[]>>

export const mediaListScanTasks = (): Promise<Resp<MediaScanTask[]>> =>
  http.get<MediaScanTask[]>("/media/tasks") as Promise<Resp<MediaScanTask[]>>

// ---- 条目 ----------------------------------------------------------------

export const mediaListItems = (query: MediaItemQuery): Promise<Resp<Page<MediaItem>>> =>
  http.get<Page<MediaItem>>("/media/items", { params: query }) as Promise<Resp<Page<MediaItem>>>

export const mediaGetItem = (id: number): Promise<Resp<MediaItem>> =>
  http.get<MediaItem>(`/media/items/${id}`) as Promise<Resp<MediaItem>>

export const mediaListItemFiles = (id: number): Promise<Resp<MediaFile[]>> =>
  http.get<MediaFile[]>(`/media/items/${id}/files`) as Promise<Resp<MediaFile[]>>

export const mediaScrapeItem = (id: number): Promise<Resp<unknown>> =>
  http.post(`/media/items/${id}/scrape`, {}) as Promise<Resp<unknown>>

export const mediaScrapeLibrary = (id: number): Promise<Resp<unknown>> =>
  http.post(`/media/libraries/${id}/scrape`, {}) as Promise<Resp<unknown>>

// ---- 个人状态 ------------------------------------------------------------

export const mediaGetState = (id: number): Promise<Resp<UserMediaState | null>> =>
  http.get<UserMediaState | null>(`/media/items/${id}/state`) as Promise<Resp<UserMediaState | null>>

export const mediaUpdateState = (id: number, patch: MediaStatePatch): Promise<Resp<UserMediaState>> =>
  http.post<UserMediaState>(`/media/items/${id}/state`, patch) as Promise<Resp<UserMediaState>>

export const mediaListInProgress = (page = 1, perPage = 12): Promise<Resp<Page<MediaItem>>> =>
  http.get<Page<MediaItem>>("/media/in-progress", { params: { page, per_page: perPage } }) as Promise<
    Resp<Page<MediaItem>>
  >

export const mediaListFavorites = (page = 1, perPage = 30): Promise<Resp<Page<MediaItem>>> =>
  http.get<Page<MediaItem>>("/media/favorites", { params: { page, per_page: perPage } }) as Promise<
    Resp<Page<MediaItem>>
  >

// ---- 聚合 ----------------------------------------------------------------

export const mediaHome = (limit = 12): Promise<Resp<MediaHome>> =>
  http.get<MediaHome>("/media/home", { params: { limit } }) as Promise<Resp<MediaHome>>

// ---- 聚合：筛选条 / 统计 / 人物 / 片商 / 系列 -----------------------------

/** 单个聚合桶（题材名或年份字符串 + 条目数）。 */
export interface MediaFacetCount {
  value: string
  count: number
}

/** `GET /media/facets` 结果。 */
export interface MediaFacets {
  total: number
  genres: MediaFacetCount[]
  years: MediaFacetCount[]
}

/** 评分分布桶，标签形如 `8.0-8.9`。 */
export interface MediaRatingBucket {
  label: string
  count: number
}

/** `GET /media/stats`：可见范围内的数量/体积/时长与分布。 */
export interface MediaStats {
  library_count: number
  item_count: number
  file_count: number
  total_bytes: number
  total_duration_seconds: number
  watched_count: number
  favorite_count: number
  by_year: MediaFacetCount[]
  by_genre: MediaFacetCount[]
  ratings: MediaRatingBucket[]
}

/** 创作者列表项（`item_count` 已按当前用户可见范围统计）。 */
export interface MediaPersonSummary {
  id: number
  name: string
  tmdb_id?: string | null
  profile_path?: string | null
  item_count: number
  /** `director` / `actor` / `writer` 等，可能多个。 */
  roles: string[]
}

/** 片商/系列列表项。 */
export interface MediaTaxonomySummary {
  id: number
  name: string
  item_count: number
}

/** 创作者实体。 */
export interface MediaPerson {
  id: number
  name: string
  tmdb_id?: string | null
  profile_path?: string | null
  createdAt: string
  updatedAt: string
}

/** 创作者作品条目（带参与角色）。 */
export interface PersonAggregateItem {
  item: MediaItem
  role: string
  character?: string | null
}

/** `GET /media/persons/{id}`：人物 + 名下作品。 */
export interface PersonAggregate {
  person: MediaPerson
  items: PersonAggregateItem[]
}

/** 个人口味画像（`GET /media/taste`，与库统计不同 —— 只算本人的收藏/看过/评分）。 */
export interface TasteGenreWeight {
  genre: string
  weight: number
}

export interface TastePersonWeight {
  personId: number
  name: string
  profilePath?: string | null
  weight: number
}

export interface TasteProfile {
  favoriteCount: number
  watchedCount: number
  totalWatchSeconds: number
  genres: TasteGenreWeight[]
  persons: TastePersonWeight[]
}

export const mediaFacets = (libraryId?: number): Promise<Resp<MediaFacets>> =>
  http.get<MediaFacets>("/media/facets", {
    params: { library_id: libraryId || undefined },
  }) as Promise<Resp<MediaFacets>>

export const mediaStats = (libraryId?: number): Promise<Resp<MediaStats>> =>
  http.get<MediaStats>("/media/stats", {
    params: { library_id: libraryId || undefined },
  }) as Promise<Resp<MediaStats>>

export const mediaTaste = (): Promise<Resp<TasteProfile>> =>
  http.get<TasteProfile>("/media/taste") as Promise<Resp<TasteProfile>>

export interface MediaPersonQuery {
  keyword?: string
  /** 限定角色类型，如 `director`。 */
  role?: string
  page?: number
  per_page?: number
}

export const mediaListPersons = (
  query: MediaPersonQuery = {},
): Promise<Resp<Page<MediaPersonSummary>>> =>
  http.get<Page<MediaPersonSummary>>("/media/persons", {
    params: query,
  }) as Promise<Resp<Page<MediaPersonSummary>>>

export const mediaGetPerson = (id: number): Promise<Resp<PersonAggregate>> =>
  http.get<PersonAggregate>(`/media/persons/${id}`) as Promise<Resp<PersonAggregate>>

export const mediaListPersonItems = (
  id: number,
  page = 1,
  perPage = 30,
): Promise<Resp<Page<MediaItem>>> =>
  http.get<Page<MediaItem>>(`/media/persons/${id}/items`, {
    params: { page, per_page: perPage },
  }) as Promise<Resp<Page<MediaItem>>>

/** 分类维度：片商（番号域主场）与系列共用同一套端点形状。 */
export type MediaTaxonomyKind = "studios" | "series"

export const mediaListTaxonomies = (
  kind: MediaTaxonomyKind,
  keyword?: string,
  page = 1,
  perPage = 30,
): Promise<Resp<Page<MediaTaxonomySummary>>> =>
  http.get<Page<MediaTaxonomySummary>>(`/media/${kind}`, {
    params: { keyword: keyword || undefined, page, per_page: perPage },
  }) as Promise<Resp<Page<MediaTaxonomySummary>>>

export const mediaListTaxonomyItems = (
  kind: MediaTaxonomyKind,
  id: number,
  page = 1,
  perPage = 30,
): Promise<Resp<Page<MediaItem>>> =>
  http.get<Page<MediaItem>>(`/media/${kind}/${id}/items`, {
    params: { page, per_page: perPage },
  }) as Promise<Resp<Page<MediaItem>>>

// ---- 播放列表 ------------------------------------------------------------

/** 播放列表封面预览项。`poster_path` 为空表示没有海报，不要发图片请求。 */
export interface MediaPlaylistPreviewItem {
  id: number
  title: string
  poster_path?: string | null
}

/** 播放列表摘要（带条目数与封面预览）。 */
export interface MediaPlaylistSummary {
  id: number
  name: string
  description?: string | null
  item_count: number
  /** 前几个条目 id（保留给只认 id 的客户端；本前端改用 preview_items）。 */
  preview_item_ids: number[]
  /** 封面预览：带标题与海报路径，仅列表接口填充。 */
  preview_items: MediaPlaylistPreviewItem[]
  created_at: string
  updated_at: string
}

/** 播放列表详情（含完整条目）。 */
export interface MediaPlaylistDetail {
  id: number
  name: string
  description?: string | null
  items: MediaItem[]
  created_at: string
  updated_at: string
}

export const mediaListPlaylists = (): Promise<Resp<MediaPlaylistSummary[]>> =>
  http.get<MediaPlaylistSummary[]>("/media/playlists") as Promise<Resp<MediaPlaylistSummary[]>>

export const mediaGetPlaylist = (id: number): Promise<Resp<MediaPlaylistDetail>> =>
  http.get<MediaPlaylistDetail>(`/media/playlists/${id}`) as Promise<Resp<MediaPlaylistDetail>>

export const mediaCreatePlaylist = (
  name: string,
  description?: string,
): Promise<Resp<MediaPlaylistSummary>> =>
  http.post<MediaPlaylistSummary>("/media/playlists", {
    name,
    description,
  }) as Promise<Resp<MediaPlaylistSummary>>

export const mediaUpdatePlaylist = (
  id: number,
  patch: { name?: string; description?: string },
): Promise<Resp<MediaPlaylistSummary>> =>
  http.post<MediaPlaylistSummary>(`/media/playlists/${id}/update`, patch) as Promise<
    Resp<MediaPlaylistSummary>
  >

export const mediaDeletePlaylist = (id: number): Promise<Resp<unknown>> =>
  http.post(`/media/playlists/${id}/delete`, {}) as Promise<Resp<unknown>>

export const mediaAddPlaylistItem = (
  id: number,
  itemId: number,
): Promise<Resp<MediaPlaylistDetail>> =>
  http.post<MediaPlaylistDetail>(`/media/playlists/${id}/items`, {
    item_id: itemId,
  }) as Promise<Resp<MediaPlaylistDetail>>

export const mediaRemovePlaylistItem = (
  id: number,
  itemId: number,
): Promise<Resp<MediaPlaylistDetail>> =>
  http.post<MediaPlaylistDetail>(`/media/playlists/${id}/items/remove`, {
    item_id: itemId,
  }) as Promise<Resp<MediaPlaylistDetail>>

/** 重排：传完整的新顺序；未列出的成员由后端追加到末尾。 */
export const mediaReorderPlaylist = (
  id: number,
  itemIds: number[],
): Promise<Resp<MediaPlaylistDetail>> =>
  http.post<MediaPlaylistDetail>(`/media/playlists/${id}/items/reorder`, {
    item_ids: itemIds,
  }) as Promise<Resp<MediaPlaylistDetail>>

/**
 * 便捷入口：给了 playlistId 就加进该列表，否则按 name 新建一个。
 * 详情页的「加入播放列表」对话框走这个，省一次「先建后加」的往返。
 */
export const mediaAddToPlaylist = (req: {
  item_id: number
  playlist_id?: number
  name?: string
}): Promise<Resp<MediaPlaylistDetail>> =>
  http.post<MediaPlaylistDetail>("/media/playlists/add", req) as Promise<
    Resp<MediaPlaylistDetail>
  >

// ---- 字幕 ----------------------------------------------------------------

/** 字幕候选（`downloadToken` 由 provider 自定义，挂载时原样回传）。 */
export interface SubtitleCandidate {
  id: string
  name: string
  language: string
  downloads: number
  format: string
  downloadToken?: string | null
}

export const mediaSearchSubtitles = (
  title: string,
  year?: number | null,
  language = "zh-cn",
): Promise<Resp<SubtitleCandidate[]>> =>
  http.get<SubtitleCandidate[]>("/media/subtitles/search", {
    params: { title, year: year ?? undefined, language },
  }) as Promise<Resp<SubtitleCandidate[]>>

/** 把选中的字幕下载到媒体文件同目录，返回落盘路径。 */
export const mediaApplySubtitle = (
  itemId: number,
  candidate: SubtitleCandidate,
): Promise<Resp<string>> =>
  http.post<string>(`/media/items/${itemId}/subtitles`, candidate) as Promise<Resp<string>>

/**
 * 条目海报/背景图的直链。
 *
 * 图片本身走专用端点（后端做可见性校验 + 读取），而不是 `poster_path` 拼 `/d`：
 * 一页 30 张海报逐条 fs/get 取签名不可接受。
 */
export function mediaImageLink(itemId: number, kind: "poster" | "backdrop" = "poster"): string {
  const token = getToken()
  // `<img>` 发不了 Authorization 头，令牌走查询参数（后端在 header 缺失时才读它）
  const auth = token ? `&access_token=${encodeURIComponent(token)}` : ""
  return `${location.origin}/api/media/items/${itemId}/image?kind=${kind}${auth}`
}
