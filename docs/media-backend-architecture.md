# 媒体域后端分层（重构后）

> 适用于 2026-10-25 之后的 `main`。此前媒体功能寄居在 `OpenList.Core` / `OpenList.Infrastructure` / `OpenList.Server` 三处，
> 本次重构把它收拢成两个独立项目，表结构不变、HTTP 契约不变。

## 1. 依赖方向

```
                 ┌──────────────────────────┐
                 │  OpenList.Media.Domain   │  实体 / 枚举 / DTO / 服务接口
                 │  (无 EF、无 Host、无 DI) │
                 └───────────┬──────────────┘
                             │ 被引用
        ┌────────────────────┴─────────────────────┐
        ▼                                          ▼
┌───────────────────────────┐          ┌───────────────────────────┐
│ OpenList.Infrastructure   │          │      OpenList.Media       │
│  Data/MediaModelConfiguration         │  服务 / provider / 端点    │
│  （EF 映射：表名、索引）   │          │  / 后台扫描 / DI 模块      │
└───────────┬───────────────┘          └───────────┬───────────────┘
            │                                      │
            └──────────────► OpenList.Core ◄────────┘
                        （User / Resp / PageResp / VFS 抽象）
                                      ▲
                                      │
                             OpenList.Server（宿主）
```

关键约束：

- **Domain 不依赖 EF Core**：实体上没有 `[Table]`/`[Column]` 之类的持久化特性，映射集中在 Infrastructure。
- **Infrastructure 不依赖 Media**：只依赖 `Media.Domain`，因此 EF 模型可以引用媒体实体，而媒体服务可以自由使用 `OpenListDbContext`，不形成环。
- **Server 不依赖任何媒体实现类型**：只调用 `AddMediaModule()` 与 `MapMediaModule()` 两行。

## 2. 项目职责

| 项目 | 内容 | 不应出现的东西 |
|---|---|---|
| `OpenList.Media.Domain` | `Domain/`：`MediaLibrary` `MediaScanLog` `MediaItem` `MediaFile` `MediaPerson` `MediaItemPerson` `MediaStudio` `MediaSeries` `UserMediaState` `MediaPlaylist` `MediaPlaylistItem` `MediaGenres`；`Abstractions/`：9 个服务/provider 接口与请求、响应 DTO | EF 特性、`HttpContext`、DI 注册 |
| `OpenList.Media` | `MediaModule.cs`（DI + 路由）、`Services/`（库、扫描编排、扫描执行器、命名规则、聚合、播放列表、个人状态、元数据、字幕、演职员与片商/系列落库）、`Providers/`（TMDB、code 番号、OpenSubtitles）、`Endpoints/`（6 个子域文件）、`Hosting/`（定时扫描） | 直接 `Program.cs` 级的静态初始化 |
| `OpenList.Infrastructure` | `Data/MediaModelConfiguration.cs`（表名前缀、唯一索引、`Genres` 非映射声明）、`OpenListDbContext.Media*` DbSet | 媒体业务逻辑 |

原有文件去向：

| 重构前 | 重构后 |
|---|---|
| `Core/Models/Media*.cs`、`Core/Models/UserMediaState.cs` | `Media.Domain/Domain/` |
| `Core/Interfaces/IMedia*.cs`、`IMetadataProvider.cs`、`ISubtitleService.cs` | `Media.Domain/Abstractions/` |
| `Infrastructure/Services/Media*.cs`、`Media/*.cs` | `Media/Services/`、`Media/Providers/` |
| `Infrastructure/Services/MediaGenreJson.cs` | 删除，语义并入 `MediaItem.Genres` / `MediaGenres` |
| `Server/Endpoints/MediaEndpoints.cs` | `Media/Endpoints/*.cs`（1 个 377 行文件 → 7 个按子域拆分的文件） |
| `Server/Hosting/MediaAutoScanHostedService.cs` | `Media/Hosting/`，由 `AddMediaModule()` 注册 |
| `Server/Endpoints/AuthEndpoints.cs` 内的 `AuthClaimsExtensions` | `Core/Security/ClaimsPrincipalExtensions.cs`（模块项目不能反向引用宿主） |

## 3. 请求链路

```
HTTP → Endpoints/*.cs        MediaQuery 归一查询串（空串→null、非法数字→默认值）
     → I*Service（Media）    权限校验（CanManageMediaLibrary / BasePath 可见性）、分页裁剪、排序
     → OpenListDbContext     EF 查询（Infrastructure，媒体映射见 MediaModelConfiguration）
```

两条权限口径，全域一致：

1. **管理类**（库 CRUD、扫描、刮削）：`user.CanManageMediaLibrary()`，失败返回 403。
2. **浏览类**（条目、聚合、状态、列表）：先按库可见性（`BasePath` 覆盖库路径），再按**条目目录**可见性过滤；
   库可见但条目在别人目录下的情况必须被过滤，聚合接口（facets/stats/persons/home）与列表接口口径相同。

## 4. 数据表与迁移

- 媒体表：`{prefix}media_libraries`、`media_scan_logs`、`media_items`、`media_files`、`media_persons`、`media_item_persons`、`media_studios`、`media_series`、`user_media_states`、`media_playlists`、`media_playlist_items`。
- 建表**只由 EF 迁移负责**：`AddMediaSchema`（9 张表）+ `AddMediaCollections`（个人状态三列 + 两张列表表）
  + `AddMediaMetadataSource`（元数据来源列）+ `AddMediaCodeDomain`（番号域：items 三列 + studios/series 两表）。
- `DbInitializer` 里原先手写的媒体建表 DDL（约 280 行）与补列逻辑已删除。那条路径是给 Go 时代 `EnsureCreated` 遗留库兜底的，
  手写 DDL 与 EF 模型必然漂移（第一次升级就漏了 `WatchLater`/`Flagged`/`FlagNote`，需要第二遍 ALTER 才补上）。
- **兼容性口径**：媒体域不承诺兼容"无 `__EFMigrationsHistory` 的旧库"。这类库的其它功能照常工作，媒体接口会因缺表报错，
  需要按 README 重建数据库（媒体元数据可重新扫描恢复，个人状态与播放列表会丢）。

## 5. 扫描子域的内部拆分

```
MediaScanService（单例）        并发互斥、进度登记、取消、扫描历史、任务卡
  └─ MediaLibraryScanner（瞬时） 遍历文件系统 → 收集视频；把结果落库（三步保存）
       └─ MediaNaming（静态纯函数） 扩展名判定 / 条目目录归属 / 库类型判定 / 标题与年份解析
```

拆分的直接原因：命名规则是用户可见行为（`Blade Runner 2049 (2017)` 曾被解析成 `Blade Runner`），
但过去埋在 445 行的扫描服务里无法单测。现在 `MediaNaming` 是纯函数，`tests/OpenList.Tests/MediaNamingTests.cs` 覆盖了
括号年份优先、裸年份回退、库根散文件归属、Mixed 库季检测、标题清洗等用例。

## 6. HTTP 契约（`/api/media`，46 条，全部需登录）

| 子域 | 路由 |
|---|---|
| 库与扫描 | `GET/POST /libraries`、`GET /libraries/{id}`、`POST /libraries/{id}/update|delete|scan|scan/stop`、`GET /libraries/{id}/scan`、`GET /libraries/{id}/scan/logs` |
| 条目与刮削 | `GET /items`、`GET /items/{id}`、`GET /items/{id}/files|credits`、`POST /items/{id}/scrape?provider=|match`、`POST /libraries/{id}/scrape` |
| 个人状态 | `GET/POST /items/{id}/state`、`GET /favorites|in-progress|watch-later|flagged|unseen|taste` |
| 播放列表 | `GET/POST /playlists`、`GET /playlists/{id}`、`POST /playlists/{id}/update|delete|items|items/remove|items/reorder`、`POST /playlists/add` |
| 聚合 | `GET /facets|stats|home|tasks|persons|persons/{id}|persons/{id}/items`、`GET /studios|studios/{id}/items|series|series/{id}/items` |
| 字幕 | `GET /subtitles/search`、`POST /items/{id}/subtitles` |

路由清单由 `Media/Endpoints/*.cs` 生成，重构前后逐条比对一致。

## 7. 模块化收益与边界

已获得的收益：

- 媒体域可整块裁剪：删掉 `AddMediaModule()` / `MapMediaModule()` 两行即可下线，其它功能与迁移不受影响。
- 契约层（Domain）可被未来任何宿主复用（桌面端、独立服务、测试工程）。
- 扫描命名规则可单测；`Genres` 只有一套解析口径（`MediaGenres` / `MediaItem.Genres`）。

仍是已知边界（对应方案文档第 7 节待补项）：

- 扫描任务不持久化，进程重启后无法续跑。
- 归档/下载联动、关注聚合未开始。

## NFO 双向化（已完成）

元数据现在有两个来源，写入时以 `MediaItem.MetadataProvider` 区分（迁移 `AddMediaMetadataSource`）：

**local（扫描期）**：`MediaLibraryScanner.WalkAsync` 顺带收集 `.nfo` / 海报 / 背景图 sidecar；
`PersistAsync` 对新增条目、以及仍 Pending 且无来源的存量条目应用本地元数据：

- NFO 选择：`<条目名>.nfo`（Kodi）> `movie.nfo`/`tvshow.nfo`（Jellyfin）> 目录内唯一 NFO；
  多个且无法区分时放弃（宁缺勿错）。解析在 `MediaNfoReader`（纯函数、可单测），
  兼容 Kodi/Jellyfin/TinyMediaManager 的常见变体（多根元素、5 分制评分换算、
  premiered 年份回退、逗号竖线分隔的多值）。
- 图片不复制不下载，直接引用存储内路径（`poster.jpg`/`folder.jpg`/`<条目名>-poster.jpg` 等）。
- 命中任何 sidecar 即标记 `provider=local, Status=Matched`——批量 TMDB 刮削只挑 Pending，自动跳过。
- 演职员（director/credits/actor）写入 `MediaPersonSync`（与刮削回写共用同一套去重逻辑）。

**tmdb（刮削）**：`MediaMetadataService.ApplyCoreAsync` 分两档——
`overwrite`（TMDB 来源或人工 Match）全量写回；`overwrite=false`（local 条目手动刮削）
**只补缺**：不覆盖 NFO 已有字段，演职员仅在为空时补，本地已有图片跳过下载，
且目标位置已有 `.nfo` 时**不再写回**（用户手写的 NFO 是事实来源，不能被抹掉）。

规则类代码集中在两个可测点：`MediaNfoReader`（解析）与 `MediaNaming` 的 sidecar
选择函数（`SelectNfoPath` / `SelectImagePaths` / `ClassifyImageFile`），
对应 `tests/OpenList.Tests/MediaNfoTests.cs`。

## 番号域（已完成）

`MediaItem` 新增 `Code`（番号，如 `MIDE-883`）/ `StudioId` / `SeriesId` 三列，
新实体 `MediaStudio` / `MediaSeries`（名称即自然键，全局共享；迁移 `AddMediaCodeDomain`）。
四个来源汇入同一套数据：

- **扫描期解析**：`MediaNaming.ExtractCode`（纯函数）从条目目录/文件名识别番号，
  识别优先级为 括号包裹 > FC2 系列 > 字母-数字（`MIDE-883`）> 字母数字连写（`MIDE883`）；
  后两种会排除疑似年份的数字段（1900-2099 纯 4 位数，`Blade2049` 不算番号）。
  新建条目直接落 `Code`；存量条目（番号功能上线前扫的）重扫时幂等回填，不改状态与来源。
- **NFO sidecar**：`MediaNfoReader` 解析 `<studio>` 与 `<series>`（回退 `<set>`，
  兼容 Kodi 9+ 的 name 子元素与旧版纯文本两种写法），经 `MediaTaxonomySync`
  按名取或建维度行后关联网到条目。
- **code provider（刮削）**：`IMetadataProvider` 的第二个实现（`Name=code`）。
  番号没有公益的统一数据源，provider 不内置任何站点，而是读设置项
  `media_code_api_url`（含 `{code}` 占位符的 JSON API 模板，未配置则 provider 不可用），
  响应按宽松映射解析（title/year/studio/series/genres/poster/actors 等，字段缺失即留空）。
  刮削入口：`POST /items/{id}/scrape?provider=code`；省略 provider 时若条目已有番号且
  code provider 已配置则自动走 code，否则走默认 TMDB。**批量刮削固定走 TMDB 不变**。
- **应用规则**：`ApplyCoreAsync` 对 code provider 走专用路径——番号写入 `Code`
  （不碰 `TmdbId`），片商/系列只在 code 路径维护（TMDB 结果不含这两个字段，
  不触碰既有关联）；NFO 写回时 `uniqueid type=code` 并带 `<studio>`/`<set>`。

查询侧：`GET /items` 的 `keyword` 同时命中标题与番号，另支持 `studio_id` / `series_id` 过滤；
聚合新增 `GET /studios|studios/{id}/items|series|series/{id}/items`
（口径与 persons 相同：只统计当前用户可见条目的关联数，无关联的维度行不返回）。
测试见 `tests/OpenList.Tests/MediaCodeDomainTests.cs`（解析、NFO、provider 映射、
扫描回填、取或建、聚合口径）。
