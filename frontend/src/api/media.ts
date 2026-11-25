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
  /** 私有库：只对登录用户可见，游客在列表 / 首页 / 聚合里都看不到。 */
  private: boolean
  /** 回收站目录（VFS 路径）：为空表示删除文件时直接删，不做回收。 */
  trash_directory?: string | null
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
  /**
   * created（默认，最近入库）/ title / rating / year / updated /
   * favorited（按当前用户收藏时间，未收藏的排后面）—— 对齐后端 ApplySort。
   */
  sort?: "created" | "updated" | "title" | "rating" | "year" | "favorited"
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
  /** 私有库：只对登录用户可见。 */
  private?: boolean
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

/**
 * 单个库的刮削状态。
 *
 * 三个计数刻意分开返回：`pending` 会被批量任务消费，`unmatched` 是自动匹配失败过、
 * 批量任务**不会**再碰的（重试只能逐条走详情页），`matched` 才是真正完成的。
 * 只看总数的话，「点完没反应」与「早就刮完了」在界面上长得一模一样。
 */
export interface MediaScrapeStatus {
  library_id: number
  library_name: string
  /** 等待刮削的条目数（批量任务的取数范围）。 */
  pending: number
  /** 自动匹配失败、批量任务不会再处理的条目数。 */
  unmatched: number
  /** 已写入元数据的条目数。 */
  matched: number
  /** 是否有批量刮削正在运行。 */
  running: boolean
  /** 本轮计划处理的条目数；未运行时为 0。 */
  batch_size: number
  /** 本轮已处理条目数；未运行时为 0。 */
  batch_done: number
  /** 本轮累计失败条目数。 */
  failed: number
  /** 本轮最近一次失败的错误信息。 */
  error?: string | null
}

/** 各库刮削状态（一次取全部库，管理台 2.5s 轮询一次即可）。 */
export const mediaListScrapeTasks = (): Promise<Resp<MediaScrapeStatus[]>> =>
  http.get<MediaScrapeStatus[]>("/media/scrape/tasks") as Promise<Resp<MediaScrapeStatus[]>>

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

/**
 * 中止正在跑的一轮整库刮削。
 *
 * 后端只发取消请求（轮询每条之间隔 250ms），`running` 会在下一次轮询时变 false；
 * 已刮削的条目都已落库，中断后重新点「刮削」会从剩余的 Pending 继续。
 */
export const mediaCancelScrape = (id: number): Promise<Resp<unknown>> =>
  http.post(`/media/libraries/${id}/scrape/cancel`, {}) as Promise<Resp<unknown>>

// ---- 个人状态 ------------------------------------------------------------

export const mediaGetState = (id: number): Promise<Resp<UserMediaState | null>> =>
  http.get<UserMediaState | null>(`/media/items/${id}/state`) as Promise<Resp<UserMediaState | null>>

export const mediaUpdateState = (id: number, patch: MediaStatePatch): Promise<Resp<UserMediaState>> =>
  http.post<UserMediaState>(`/media/items/${id}/state`, patch) as Promise<Resp<UserMediaState>>

/**
 * 批量取个人状态（列表页/播放队列打「已看」角标用）。
 *
 * 返回里**每个请求过的可见 id 都有一条**（没有状态行的给默认值），
 * 不可见的条目会被后端静默剔除 —— 所以渲染侧要按 `item_id` 建 map，而不是按下标对齐。
 */
export const mediaBatchStates = (itemIds: number[]): Promise<Resp<UserMediaState[]>> =>
  http.post<UserMediaState[]>("/media/items/states", { item_ids: itemIds }) as Promise<Resp<UserMediaState[]>>

// ---- 观看会话 ------------------------------------------------------------

/** 一次播放心跳。`closing` 为真表示这次上报同时结束了会话（换文件 / 播完 / 关播放器）。 */
export interface WatchReportPayload {
  /** 客户端为这次播放生成的标识；一次播放一个，换文件换新的。 */
  token: string
  item_id: number
  file_id?: number | null
  position_seconds: number
  /** 当前片长（秒）。比例类度量（最远/离开位置）靠它当分母，缺省时服务端不更新这些列。 */
  duration_seconds?: number
  /** 自上次上报以来新增的拖动次数。传增量不传累计：丢一发只少记一次，不会把已有数据算错。 */
  seeks?: number
  closing?: boolean
  completed?: boolean
}

export const mediaReportWatch = (payload: WatchReportPayload): Promise<Resp<unknown>> =>
  http.post<unknown>("/media/watch/report", payload) as Promise<Resp<unknown>>

/**
 * 观看历史的一条（按条目聚合）。
 *
 * 字段分两层，别混着读：`last_at` / `watched_seconds` / `completed` 只统计**当前时间窗**内的会话；
 * `play_count` / `max_reached` / `leave_ratio` / `seek_count` / `skimmed` 是**条目级**的
 * （来自用户状态表），切时间窗不会让续播点和划水判定跟着变。
 */
export interface WatchHistoryEntry {
  item_id: number
  title: string
  poster_path?: string | null
  year?: number | null
  last_at: string
  /** 窗口内累计观看秒数。 */
  watched_seconds: number
  /** 窗口内是否有一次看到结尾。 */
  completed: boolean
  /** 播放次数（条目级）。 */
  play_count?: number
  /** 累计观看秒数（条目级，跨会话累加）。 */
  play_seconds?: number
  /** 最近一次上报的片长（秒）。比例类度量的分母。 */
  duration_seconds?: number | null
  /** 到过的最远位置比例 0–1（条目级）。 */
  max_reached?: number
  /** 最近一次离开位置比例 0–1（条目级），播完为 1；续播点用它。 */
  leave_ratio?: number | null
  /** 累计拖动次数（条目级）。 */
  seek_count?: number
  /** 是否划水：到过 60% 以上，但真实观看比最远位置少 25% 以上。 */
  skimmed?: boolean
}

export interface WatchDailyStat {
  /** `yyyy-MM-dd`（UTC）。 */
  date: string
  seconds: number
}

export interface WatchStats {
  window_days: number
  total_seconds: number
  days: WatchDailyStat[]
}

export const mediaWatchHistory = (days = 30, limit = 12): Promise<Resp<WatchHistoryEntry[]>> =>
  http.get<WatchHistoryEntry[]>("/media/watch/history", { params: { days, limit } }) as Promise<
    Resp<WatchHistoryEntry[]>
  >

export const mediaWatchStats = (days = 30): Promise<Resp<WatchStats>> =>
  http.get<WatchStats>("/media/watch/stats", { params: { days } }) as Promise<Resp<WatchStats>>

export const mediaListInProgress = (page = 1, perPage = 12): Promise<Resp<Page<MediaItem>>> =>
  http.get<Page<MediaItem>>("/media/in-progress", { params: { page, per_page: perPage } }) as Promise<
    Resp<Page<MediaItem>>
  >

export const mediaListFavorites = (page = 1, perPage = 30): Promise<Resp<Page<MediaItem>>> =>
  http.get<Page<MediaItem>>("/media/favorites", { params: { page, per_page: perPage } }) as Promise<
    Resp<Page<MediaItem>>
  >

/**
 * 想看清单（个人 `watch_later` 标记的条目）。
 *
 * 与「媒体库 + `state=later`」的差别只在形状：这个端点直接给 `Page<MediaItem>`，
 * 不带筛选组合，适合独立页面整页翻。仅登录用户可读（游客没有「想看」这个概念）。
 */
export const mediaListWatchLater = (page = 1, perPage = 30): Promise<Resp<Page<MediaItem>>> =>
  http.get<Page<MediaItem>>("/media/watch-later", {
    params: { page, per_page: perPage },
  }) as Promise<Resp<Page<MediaItem>>>

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
  /** 统计窗口天数，`0` = 不限（全量）。由后端归一后回显，前端据此标注口径。 */
  windowDays: number
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

/**
 * 口味画像。`days` 是「最近 N 天内看过」的窗口，缺省/0 表示全量。
 *
 * 有观看会话的条目以 `x_media_watch_sessions` 为准；完全没有会话的旧条目
 * 才回退到状态行时间（`LastPlayedAt`/`UpdatedAt`），见后端 `TasteWindow`。
 */
export const mediaTaste = (days?: number): Promise<Resp<TasteProfile>> =>
  http.get<TasteProfile>("/media/taste", {
    params: { days: days || undefined },
  }) as Promise<Resp<TasteProfile>>

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

/**
 * 单个片商/系列（详情页标题与条目数）。
 *
 * 之前详情页只能靠列表页跳转时塞进 URL 的 `?name=` 显示标题，直接输地址就退化成
 * 「片商 #3」；有了这个接口，标题不再依赖来源，`item_count` 也不必再从分页 total 猜。
 * 不存在时后端给 404（`code === 404`），前端按「找不到」渲染而不是编个名字出来。
 */
export const mediaTaxonomyDetail = (
  kind: MediaTaxonomyKind,
  id: number,
): Promise<Resp<MediaTaxonomySummary>> =>
  http.get<MediaTaxonomySummary>(`/media/${kind}/${id}`) as Promise<Resp<MediaTaxonomySummary>>

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

/**
 * 人物头像直链（`/media/persons/{id}/image`）。
 *
 * 与条目图同一套：专用端点做可见性校验后直读，外链头像后端 302 转出去。
 * 没有头像的人物会 404 —— 调用方必须**先看 `profile_path` 是否为空**再决定要不要请求，
 * 否则一页 30 张占位头像就是 30 条 404（浏览器会把它记进控制台）。
 */
export function mediaPersonImageLink(personId: number): string {
  const token = getToken()
  const auth = token ? `?access_token=${encodeURIComponent(token)}` : ""
  return `${location.origin}/api/media/persons/${personId}/image${auth}`
}

// ======================================================================
// 关注创作者 → 新作品更新流
// ======================================================================

/**
 * 关注状态表：`person_id` → 是否已关注。
 *
 * 一次问一批（详情页的演职员 chips、人物列表都可能一次要问几十个）。
 * 后端对游客只返回**可见**人物的状态 —— 私有库里的人物不会出现，不能借这个接口
 * 确认它存在。
 */
export type FollowStateMap = Record<string, boolean>

/** 已关注的创作者（`GET /media/follow`）。 */
export interface FollowedPerson {
  person_id: number
  name: string
  profile_path?: string | null
  tmdb_id?: string | null
  created_at: string
  /** 最近一次**成功**检查的时间；空表示还没成功检查过。 */
  last_checked_at?: string | null
  /** 最近一次检查的失败原因（成功时为空）。 */
  last_error?: string | null
  /** 库里还没有的作品数。 */
  new_count: number
}

/** 更新流里的一条作品（`GET /media/follow/feed`）。 */
export interface FollowFeedEntry {
  id: number
  person_id: number
  person_name: string
  person_profile_path?: string | null
  /** `movie` / `tv`。 */
  media_type: string
  /** TMDB id 字符串。 */
  external_id: string
  title: string
  release_date?: string | null
  poster_url?: string | null
  role?: string | null
  first_seen_at: string
  /** 已入库且可见的条目 id；空 = 还没入库。 */
  item_id?: number | null
  /** 已加入想看（且尚未入库）的想看记录 id；空 = 没加过。 */
  want_id?: number | null
}

/** 一次「检查更新」的结果。 */
export interface FollowCheckResult {
  /** 本次实际检查的人物数（后台启动时为 0）。 */
  checked_persons: number
  /** 本次新增的更新流条目数（后台启动时为 0）。 */
  new_entries: number
  /** 是否已转入后台执行（此时上面两个计数无意义）。 */
  started: boolean
}

/**
 * 批量取关注状态。
 *
 * 注意后端的键是**字符串**（JSON 对象键只能是字符串），调用方按 `String(id)` 取值。
 */
export const mediaFollowState = (personIds: number[]): Promise<Resp<FollowStateMap>> =>
  http.get<FollowStateMap>("/media/follow/state", {
    params: { person_ids: personIds.join(",") },
  }) as Promise<Resp<FollowStateMap>>

/** 已关注的创作者列表。 */
export const mediaListFollowed = (): Promise<Resp<FollowedPerson[]>> =>
  http.get<FollowedPerson[]>("/media/follow") as Promise<Resp<FollowedPerson[]>>

/** 关注一位创作者（幂等）；首次关注会立刻检查一次，把他的现有作品落成更新流。 */
export const mediaFollowPerson = (personId: number): Promise<Resp<unknown>> =>
  http.post(`/media/follow/${personId}`, {}) as Promise<Resp<unknown>>

/** 取消关注（幂等）：该创作者的更新流条目一并消失。 */
export const mediaUnfollowPerson = (personId: number): Promise<Resp<unknown>> =>
  http.delete(`/media/follow/${personId}`) as Promise<Resp<unknown>>

/** 更新流：按「首次见到」倒序分页；`personId` 可只看某一位。 */
export const mediaFollowFeed = (
  page = 1,
  perPage = 30,
  personId?: number,
): Promise<Resp<Page<FollowFeedEntry>>> =>
  http.get<Page<FollowFeedEntry>>("/media/follow/feed", {
    params: { page, per_page: perPage, person_id: personId ?? undefined },
  }) as Promise<Resp<Page<FollowFeedEntry>>>

/**
 * 检查更新。给 `personId` 时同步检查这一位并返回实际结果；不给就后台跑全部，
 * 返回的 `started` 表示是否真的开跑（已有一轮在跑时是 false）。
 */
export const mediaCheckFollow = (personId?: number): Promise<Resp<FollowCheckResult>> =>
  http.post<FollowCheckResult>("/media/follow/check", {}, {
    params: { person_id: personId ?? undefined },
  }) as Promise<Resp<FollowCheckResult>>

// ======================================================================
// 想看（库外作品）与下载
// ======================================================================

/** 想看记录（映射 x_media_wants）。 */
export interface MediaWant {
  id: number
  external_id: string
  media_type: string
  title: string
  year?: number | null
  poster_path?: string | null
  overview?: string | null
  release_date?: string | null
  created_at: string
  /** 入库后的条目 id；空 = 还没入库。 */
  item_id?: number | null
}

/** 加入想看的请求（与更新流里的一条作品同形）。 */
export interface AddMediaWantReq {
  external_id: string
  media_type?: string
  title: string
  year?: number | null
  poster_path?: string | null
  overview?: string | null
  release_date?: string | null
}

/** 下载状态（与后端 MediaDownloadStatus 对齐）。 */
export enum MediaDownloadStatus {
  Pending = 0,
  Downloading = 1,
  Completed = 2,
  Failed = 3,
  Cancelled = 4,
}

/** 下载记录（映射 x_media_downloads）。 */
export interface MediaDownload {
  id: number
  want_id: number
  item_id?: number | null
  library_id: number
  tool: string
  task_id?: string | null
  title: string
  url: string
  target_directory: string
  status: MediaDownloadStatus
  /** 0–100。 */
  progress: number
  error?: string | null
  created_at: string
  updated_at: string
  completed_at?: string | null
  ingested_at?: string | null
}

/** 想看列表；`includeIngested` 为 false 时只列还没入库的。 */
export const mediaListWants = (
  page = 1,
  perPage = 30,
  includeIngested = false,
): Promise<Resp<Page<MediaWant>>> =>
  http.get<Page<MediaWant>>("/media/wants", {
    params: { page, per_page: perPage, include_ingested: includeIngested },
  }) as Promise<Resp<Page<MediaWant>>>

/** 加入想看（幂等）。 */
export const mediaAddWant = (req: AddMediaWantReq): Promise<Resp<MediaWant>> =>
  http.post<MediaWant>("/media/wants", req) as Promise<Resp<MediaWant>>

/** 移出想看（连同它的下载记录）。 */
export const mediaRemoveWant = (id: number): Promise<Resp<unknown>> =>
  http.delete(`/media/wants/${id}`) as Promise<Resp<unknown>>

/** 下载记录列表；`wantId` 可限定某条想看。 */
export const mediaListDownloads = (wantId?: number): Promise<Resp<MediaDownload[]>> =>
  http.get<MediaDownload[]>("/media/downloads", {
    params: { want_id: wantId ?? undefined },
  }) as Promise<Resp<MediaDownload[]>>

/** 提交下载：把资源地址交给离线下载，产物落到指定媒体库目录。 */
export const mediaSubmitDownload = (req: {
  want_id: number
  library_id: number
  url: string
  tool?: string
}): Promise<Resp<MediaDownload>> =>
  http.post<MediaDownload>("/media/downloads", req) as Promise<Resp<MediaDownload>>

/** 取消下载（下载器那边的任务一并取消）。 */
export const mediaCancelDownload = (id: number): Promise<Resp<unknown>> =>
  http.delete(`/media/downloads/${id}`) as Promise<Resp<unknown>>

/**
 * 库外作品的图片地址。
 *
 * 想看 / 更新流里的海报是 TMDB 的相对路径（`/abc.jpg`）或外链；条目海报走的是
 * `/media/items/{id}/image`，而这两处还没有条目 id，只能直接拼 TMDB 图片基地址。
 */
export function mediaExternalImage(path?: string | null): string | null {
  if (!path) return null
  if (/^https?:\/\//i.test(path)) return path
  return `https://image.tmdb.org/t/p/w342${path.startsWith("/") ? "" : "/"}${path}`
}

// ======================================================================
// 数据清理：孤儿条目 / 重复文件 / 单条删除 / 回收站
// ======================================================================

/** 孤儿条目：文件已全部消失、只剩数据库行（`GET /media/cleanup/orphans`）。 */
export interface OrphanItem {
  item_id: number
  title: string
  directory_path: string
  library_id: number
  library_name: string
  /** 该条目原本有几个文件行（全都不存在）；本来就没文件时为 0。 */
  missing_files: number
}

/** 重复组里的一个条目。 */
export interface DuplicateItem {
  item_id: number
  title: string
  directory_path: string
  library_id: number
  /** 命中的那个文件（组内该条目的第一个文件）。 */
  file_path: string
  duration_seconds?: number | null
  resolution?: string | null
}

/** 一组经哈希确认的重复文件（同库、同大小、同时长、同前 1 MB）。 */
export interface DuplicateGroup {
  size_bytes: number
  /** 组内文件数（同一条目的多版本各算一个）。 */
  count: number
  items: DuplicateItem[]
}

/** 列孤儿条目；`libraryId` 为 0 表示全部可见库。 */
export const mediaListOrphans = (libraryId = 0): Promise<Resp<OrphanItem[]>> =>
  http.get<OrphanItem[]>("/media/cleanup/orphans", {
    params: { library_id: libraryId },
  }) as Promise<Resp<OrphanItem[]>>

/** 删除孤儿条目：只删数据库行，不碰文件（文件早已不在）。 */
export const mediaDeleteOrphans = (itemIds: number[]): Promise<Resp<unknown>> =>
  http.post("/media/cleanup/orphans/delete", { item_ids: itemIds }) as Promise<Resp<unknown>>

/** 列重复文件组；`libraryId` 为 0 表示全部可见库。 */
export const mediaListDuplicates = (libraryId = 0): Promise<Resp<DuplicateGroup[]>> =>
  http.get<DuplicateGroup[]>("/media/cleanup/duplicates", {
    params: { library_id: libraryId },
  }) as Promise<Resp<DuplicateGroup[]>>

/** 删除重复条目：按所属库的回收站规则处理文件，再删行。 */
export const mediaDeleteDuplicates = (itemIds: number[]): Promise<Resp<unknown>> =>
  http.post("/media/cleanup/duplicates/delete", { item_ids: itemIds }) as Promise<Resp<unknown>>

/** 删除单个条目；`deleteFiles` 为 true 时按回收站规则连文件一起处理。 */
export const mediaDeleteItem = (itemId: number, deleteFiles: boolean): Promise<Resp<unknown>> =>
  http.post(`/media/cleanup/items/${itemId}/delete`, { delete_files: deleteFiles }) as Promise<Resp<unknown>>

/** 清空某个库的回收站目录（库未配置回收站时后端回 400）。 */
export const mediaEmptyTrash = (libraryId: number): Promise<Resp<unknown>> =>
  http.post("/media/cleanup/trash/empty", { library_id: libraryId }) as Promise<Resp<unknown>>
