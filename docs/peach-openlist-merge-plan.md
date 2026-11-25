# OpenList × Peach 功能合并与优化设计方案

> 状态：**设计稿（2026-11-24）**，待确认后分阶段实施。本文是「合并什么、怎么合并、先做哪个」的决策依据；
> 每项落地后在 §5 路线表打勾并补进度日志。

## 0. 一句话结论

OpenList.Net 今天已经是一个「**OpenList 的文件/驱动/协议底座 + Peach 形态的媒体门面**」的合体，
两边重叠的部分（媒体库、海报墙、播放、历史、想看、播放列表、口味、任务）已经做完；
**Peach 的四块产品能力里已有三块落地**（关注更新流 §62、想看→下载→入库 §62、数据清理 §63）；
**还差的是**想看链路里的「搜资源」、任务中心的写操作，以及回收站的留存清理。
此外还有 **OpenList 的两块底座能力**（云驱动、云离线下载工具）。
两者的交点只有一个：**Peach 的「想看→下载→整理入库」流水线要跑在 OpenList 的 `IOfflineDownloadTool` 之上**。

## 1. 现状盘点

### 1.1 项目构成

| 层 | 现状 |
|---|---|
| 后端 | .NET 10，11 个工程；Minimal API + `Resp<T>`；SQLite/Postgres 双迁移；665 个 TUnit 用例 |
| 文件域 | `/api/fs/*` 全量、Multipart 分片、归档（SharpCompress）、搜索索引、签名直链 `/d /p /ad /ap /ae` |
| 协议服务 | WebDAV `/dav`、S3 网关（含分片/Range/Copy/DeleteObjects）、FTP、SFTP（真实实现） |
| 驱动 | Local、S3、WebDAV、FTP、SFTP、SMB（占位）、AliyundriveOpen（完整）；云驱动地基（限流/OAuth 回写/回调配额）已就位 |
| 离线下载 | `IOfflineDownloadTool`：Aria2、qBittorrent、Transmission、SimpleHttp；前端 `OfflineDownloadDialog` |
| 媒体域 | 库/扫描/刮削（TMDB + 番号 + OpenSubtitles + NFO 读）/人物·片商·系列/播放列表/用户状态（收藏·想看·标记·未看）/口味/观看历史/字幕搜索/任务只读；公开·私有库分区 |
| 前端 | Vue 3 + TSX + Element Plus；两个门面（`/media/**` 公开、`/files/**` 登录）+ `/@manage/**` |
| 桌面 / CLI | Avalonia 托盘宿主；`openlist` 运维 CLI |
| 单用户 | 分享 / 站内信 / 角色 / BasePath 已移除；身份 = 匿名 / 管理员 / 系统内部 |

### 1.2 三方功能矩阵

图例：✓ 有 · ◐ 部分 · ✗ 无。**「本项目」列是对照源码与路由得出的，不是文档自述。**

| 能力 | OpenList (Go) | Peach v0.61 | 本项目 | 结论 |
|---|---|---|---|---|
| 文件管理（列表/上传/下载/复制/移动/批量/搜索/归档/预览） | ✓ | ◐ 目录浏览 | ✓ 完整 | 保持 |
| 存储驱动 | 89 个 | 本地 / SMB / 115 / PikPak（经 CloudDrive2） | 6 个 + 地基 | §3.7 逐步移植 |
| WebDAV / S3 / FTP / SFTP 服务端 | ✓ | ✗ | ✓ | 保持 |
| 离线下载 | aria2/qbit/transmission/115/pikpak/thunder… | 115（CD2 gRPC）/PikPak/qBittorrent **+ 番号/磁力搜索 + 完成后整理入库** | 4 个工具 + 对话框，无搜索→入库链路 | §3.2 核心合并点 |
| 元数据刮削 | ✗ | 多源 + 字段所有权 + NFO 读写 + 人工复核 + 合并 | TMDB / 番号 / OpenSubtitles，NFO 读 + 部分写 | §3.6 |
| 媒体库 / 海报墙 / 筛选 | ✗ | ✓ | ✓ | §4 美化 |
| 播放 | 预览级 | 内嵌 + 外部播放器 + 字幕轨道 + 倍速 + 旋转 + 时间轴预览 + 沉浸模式 | 内嵌 `<video>` + 字幕搜索 | §3.3 / §3.8 |
| 继续观看 / 历史 / 想看 / 收藏 / 标记 / 未看 | ✗ | ✓ | 后端 ✓；前端只有想看 / 历史 / 收藏，**标记 / 未看无页面** | §3.4 |
| 播放列表 | ✗ | ✓ | ✓ | 保持 |
| 口味画像 / 统计 | ✗ | ✓（独立 `/stats`：库·存储·观看） | 口味页内含统计；`/media/stats` 已有接口无独立页 | §3.4 |
| 关注演员 / 作者更新流 | ✗ | ✓（RSS/站点连接器 + 聚合） | ✓ 关注 / 更新流 / 检查（TMDB `combined_credits`；RSS 未做） | §3.5 |
| 人物 / 片商 / 系列页 | ✗ | performers / studios / creators / agencies / series / tags | persons / studios / series | §3.6 补标签 |
| 标签管理 / 实体合并 / 别名 | ✗ | ✓ | ✗ | §3.6 |
| 数据清理（重复文件 / 孤儿记录 / 回收站 / 垃圾文件） | ✗ | ✓ | ✓ 孤儿 / 重复 / 单条删除 / 清空回收站（`/@manage/media-cleanup`） | §3.1 |
| 任务中心 | ✓（`/api/task` 增删改查） | ✓ 暂停 / 恢复 / 取消 + 资源占用 | 只读（扫描 stop/cancel 后端已有） | §3.1 |
| 游客播放公开媒体 | — | — | ✗（§61.6 已知边界：`/p` 签名要登录） | §3.1 |
| 桌面端 | OpenList-Desktop | 托盘 / 自启 / macOS 应用内更新 | Avalonia 托盘宿主 | 保持 |
| 多用户 / 分享 / SSO / LDAP / WebAuthn | ✓ | ✗ | 单用户 + SSO/LDAP/WebAuthn | 保持 |

## 2. 合并原则与许可证边界

1. **OpenList（Go，AGPL-3.0）→ 本项目**：本项目本就是其移植，继续按「行为对齐」移植（API 形状、设置项、驱动语义），
   不引入 Go 源码文件；移植清单见 README「Not Yet Implemented」。
2. **Peach（AGPL-3.0）→ 本项目**：**只借鉴产品形态与结构，不复制代码、不复制文案与美术资源**。
   `docs/frontend-reference-analysis.md` 已把「零接触 Peach 代码」定为书面约束，本方案延续：
   下文所有 Peach 项都是「在我们自己的 Vue / .NET 栈上重新实现同一体验」。
3. **两个产品的主线不能互相稀释**：文件门面保持 OpenList 的「网盘聚合 + 全功能文件管理」；
   媒体门面保持 Peach 的「本地优先的个人媒体中心」。不把文件树内嵌进媒体外壳（§61.4 已定）。
4. **单用户前提不变**：所有新表不挂 `UserId`，身份只有匿名 / 管理员。

## 3. 差距清单与合并方案

优先级：**P0 = 一个独立 PR 可完成、无新表、立刻可见**；**P1 = 需要新表或跨域编排，是产品差异化的主战场**；
**P2 = 依赖外部服务或体量大，排后**。

### P0-1 任务中心写操作 + 数据清理入口

- **现状**：`/media/tasks` 只读（2.5s 轮询）；扫描 `POST /libraries/{id}/scan/stop`、`/scrape/cancel` 后端已有；
  `ITaskService` 支持 cancel / retry / delete / clear，但媒体任务与 `/api/task` 是两套。
- **方案**：
  1. `MediaEndpoints` 增加 `POST /tasks/{taskKey}/cancel|retry`，内部按 `taskKey` 前缀路由到扫描 / 刮削 / 索引 / 离线下载四类控制器；
  2. 前端 `pages/media/Tasks.tsx` 每行加「取消 / 重试」按钮，操作后立即失效轮询缓存；
  3. 任务行补「已处理 / 总数 · 当前文件」进度文案（Peach 的 `task-progress-surfaces` 形态）。
- **验收**：扫描中点取消 → 状态 `cancelled`，库页「扫描」按钮回到可点；重试 → 新 run_id。

### P0-2 外部播放器

- **现状**：`MediaPlayer.tsx` 只有内嵌 `<video>`。
- **方案**：
  1. 设置组「播放」新增 `external_players`（JSON 数组：`{id, name, scheme, targets}`，预置 mpv / VLC / PotPlayer / IINA / nplayer / Infuse 的 URL scheme 与平台过滤）；
  2. 播放页工具条加「用外部播放器打开」下拉，拿 `/api/fs/get` 的 `raw_url`（302 直链）拼 scheme；没有直链的驱动置灰并说明「该存储不支持直链」；
  3. 可选 `prefer_external_player` 开关：点播直接唤起。
- **验收**：Windows 上 PotPlayer / mpv 可拉起并从 `raw_url` 起播；无 scheme 支持的浏览器静默失败不报错弹窗。

### P0-3 游客播放公开媒体（补 §61.6）

- **方案**：新增 `GET /api/media/items/{id}/stream?file=`（匿名可达）：`CanSeeItemAsync` 通过后，服务端以系统身份经 `IFileSystemService` 打开流并代理输出（支持 `Range`），
  绕开需要登录的 `/p` 签名链路；私有库一律 404。前端播放器在游客态改用该端点。
- **验收**：游客在公开库点播 200 + 206 Range；私有库 id 404；`media_guest_access=false` 时 401。

### P0-4 独立统计页 + 标记 / 未看页面

- **现状**：`GET /media/stats` 已返回库 / 条目 / 时长分布 / 收藏人数，只被口味页消费；`/flagged`、`/unseen` 后端已有，前端无入口。
- **方案**：
  1. 新页 `/media/stats`：顶部 4 个数字卡（库数 · 条目数 · 总时长 · 已看比例），下面「按年份 / 按类型」两张条形图（纯 CSS/SVG，不引图表库）与「存储用量」表（复用 `/admin/storage` 的用量字段，缺失显示「—」）；
  2. 左栏第二组追加「统计」；想看页旁加「标记」「未看」两个 tab 式页面（复用 `MediaGrid`）。
- **验收**：e2e 导航计数 +3；数字与 `/media/stats` 一致。

### P0-5 美化第一批（§4.1–4.3）

海报墙 / 媒体首页 / 播放器三处，见 §4。

### P1-1 数据清理：重复文件 / 孤儿记录 / 回收站

- **现状**：**已落地（2026-11-25，见主计划 §63）**。孤儿、重复、单条删除、清空回收站四件都在
  `IMediaCleanupService` 里，管理台 `/@manage/media-cleanup` 提供「先预览再执行」两步操作。
  与本节方案的差异：① 重复确认只比对前 1 MB SHA-256（不读整文件）；② 回收站按**原路径镜像**
  （`/trash` + 源路径）而不是平铺，同名文件不会互相覆盖；③ **回收站留存任务没做** ——
  `media_trash_retention_days` 仍无消费方，目前只能手动「清空回收站」；④ 孤儿删除同时清
  观看会话与播放列表成员，并把想看 / 下载记录里指向该条目的 `ItemId` 置空。
- **方案**（对齐 Peach `/data-cleanup/*`，但只做我们用得上的三件）：
  1. **孤儿记录**：`GET /api/media/cleanup/orphans` — 扫描每库，列出 `MediaFile.Path` 已不存在的条目；`POST .../orphans/delete` 删行（保留观看记录）；
  2. **重复文件**：`GET /api/media/cleanup/duplicates?library=` — 按 `(Size, DurationSeconds)` 分组初筛，再对前 1 MB 做哈希确认；结果页给「保留此条 / 删除文件 + 行」；
  3. **回收站**：库设置 `trash_directory`，删除文件默认 `move` 到该目录而非直接删（`FileSystemService.MoveAsync`），7 天后由现有宿主任务清理；
  4. 管理台「媒体」下新增「数据清理」页，三块各一个卡片 + 「先预览再执行」的两步按钮。
- **验收**：删 1 个文件后跑孤儿扫描能列出并可清行；重复组内删除只删选中那个；开启回收站的库删除后文件出现在回收站目录。

### P1-2 想看 → 搜资源 → 云下载 → 自动入库（核心合并点）

这是唯一需要 **OpenList 底座 + Peach 产品形态** 同时出力的项。

- **现状**：想看清单与「提交下载 → 轮询 → 扫描入库」已落地（2026-11-25，主计划 §62）；**资源搜索仍是空的**
  —— 用户要自己粘磁力/直链，`IResourceSearchProvider` 与搜索弹层未做。所以本节真正的剩余项是第 1 步。
- **方案**：
  1. **资源搜索**（`src/OpenList.Media/Services/ResourceSearchService.cs`）：接口 `IResourceSearchProvider`，首个实现是 **索引站 RSS / JSON**（可配置站点列表，Peach 的「按番号搜种」形态），返回 `{title, size, magnet/url, seeders}`；
  2. **提交下载**：复用 `OfflineDownloadService.AddAsync(tool, targetDir, url)`，目标目录 = 想看条目所属库的目录 + 命名规则（`MediaNaming` 已有）；在 `MediaItem` 上记 `DownloadTaskId`；
  3. **完成回流入库**：新增 `MediaDownloadWatchHostedService`，轮询 `IOfflineDownloadTool.QueryAsync`，完成后触发该库的**局部扫描**（`IMediaScanService` 指定目录），再按标题 / 番号匹配到想看条目 → `Status=Ready`，并从想看移除、写入「最近入库」；
  4. 前端：想看卡片「找资源」→ 搜索结果弹层（标题 / 大小 / 做种数）→「下载到 <库名>」；任务中心出现该下载任务；详情页显示下载进度。
- **验收**：SimpleHttp 工具 + 本地 HTTP 种子/文件即可端到端：想看 → 搜索 → 下载 → 扫描 → 入库 → 想看消失。

### P1-3 关注演员更新流

- **现状**：**已落地（2026-11-25，见主计划 §62）**。人物页「关注」按钮、`/media/follow`（更新流 + 已关注
  两个页签）、`/api/media/follow/*` 五组端点、`MediaFollowCheckHostedService`（间隔取
  `media_follow_check_interval_hours`，默认关闭）都在跑。与本节方案的差异：差集键用 `(PersonId, ExternalId)`
  而不是 `LastSeenReleaseDate` 快照；更新流按 `Id` 倒序翻页（SQLite 不支持 SQL 层 DateTimeOffset 排序）。
  **仍未做**：RSS/站点连接器类 provider（只有 TMDB `combined_credits`）；更新流里的「找资源」还只是
  「加入待下载」，后半段见 P1-2。
- **原方案**（留档，实际落地见上）：
  1. 新表 `x_media_follows`（`PersonId` 唯一、`CreatedAt`、`LastSeenReleaseDate`），两份迁移；
  2. `IPersonFeedProvider`：首个实现 TMDB `person/{id}/combined_credits` 与上次快照做差集，产出「新作品」；之后可挂 RSS 连接器；
  3. `GET /api/media/follow/feed` 按发布日期倒序返回 `{personId, itemId?/externalTitle, releaseDate, posterUrl}`，已入库的条目带 `itemId` 直达详情，未入库的显示「找资源」（接 P1-2）；
  4. 前端：人物页「关注」按钮；新页 `/media/follow` 以时间线分组展示；左栏登录组追加「关注更新」。
- **验收**：关注后手动「检查更新」能把新作品列出来；取关后 feed 为空。（已由 §62 的 15 条用例覆盖等价语义）

### P1-4 115 / PikPak 云离线下载工具

- **现状**：`set_115` / `set_pikpak` 设置路由已存在，但没有对应 `IOfflineDownloadTool`。
- **方案**：按 OpenList Go 的 `offline_download_tool` 语义实现 `Pan115Tool`（Open API）与 `PikPakTool`（复用 AliyundriveOpen 已有的 OAuth 刷新地基），两者都走「提交 → 轮询状态 → 文件落盘到挂载目录」；
  这需要对应的 115 / PikPak **驱动**先存在（§3.7），否则下载完的文件没有路径可扫描。因此排在 §3.7 的 `115` / `pikpak` 驱动之后。

### P2-1 视频时间轴预览 / 悬停预览

- `docs/thumbnail-service-plan.md` 已有完整设计（图片 / 视频 / 音频 / PDF 缩略图已实现，视频时间轴精灵图未做）。补 `GET /api/media/items/{id}/sprite`（ffmpeg 抽帧 + 拼接 + 缓存），播放器进度条 hover 显示预览帧，海报卡 hover 播放 3s 静音片段。

### P2-2 标签管理 / 人物别名 / 实体合并

- `MediaTaxonomy` 已支持 genre；补 `Tag` 实体 + 条目多对多；人物页「合并到…」把 `MediaItem.PersonId` / 观看记录 / 关注迁到目标人物，保留重定向（旧 id → 新 id）。

### P2-3 刮削增强：字段所有权 + 人工复核

- 现在重刮会覆盖手改字段。引入 Peach 的「字段来源」思路：`MediaItem.FieldOwnersJson` 记录每个字段是 `manual` / `nfo` / `tmdb` / `code`，重刮只填空字段或来源更弱的字段；「复核」页列出低置信匹配（标题相似度 < 阈值）让用户二选一。

### P2-4 云驱动移植

- 按 AGENTS.md 的体量顺序：`wopan → mega → weiyun → teambition → dropbox → google_drive → onedrive → quark_open → 123_open → baidu_netdisk → 189 → lanzou → thunder → 115 → pikpak`，
  以 `AliyundriveOpen` 为模板。每加一个驱动，§3.7 的离线下载工具就可以多一个落地面。

## 4. 美化方案（媒体门面优先）

设计约束：**不引入新 UI 库、不引图表库**，继续用 `tokens.css` 的变量 + Element Plus；所有颜色只走 token。

### 4.1 海报墙（`/media/library`）

- 卡片：2:3 海报，底部渐变叠「标题 · 年份 · ★评分」；hover 抬起 2px + 海报轻微放大 1.03，时长不超过 160ms；
- 加载：**骨架网格**（固定 12 个灰块，比例与真卡一致），不再用转圈；
- 筛选条 sticky 置顶，库切换改成横向 chips，年份 / 类型用 Element Plus 的 `el-select` 多选并显示已选数量角标；
- 空态：一张插画位 + 「这个库还没有内容 · 去管理台扫描」按钮（登录态）。

### 4.2 媒体首页（`/media`）

- 顶部 **backdrop hero**（取「继续观看」首条或最近入库的 `BackdropPath`，底部渐变到页面底色，左下标题 + 一句简介 + 「继续播放 / 查看详情」）；
- 四条横滑 rail：继续观看 → 想看 → 最近入库 → 最近收藏；左右箭头只在 hover 出现，移动端整行横滑；
- 游客态不渲染「想看」rail（与导航一致，不出现假入口）。

### 4.3 播放页

- 独立沉浸路由 `/media/play/:id`（全屏黑底、无左栏），返回保留来源列表的滚动位置；
- 控制栏：3s 无操作自动隐藏；倍速 / 字幕轨道 / 外部播放器 / 下一集 收进右侧「⋯」；
- 片头 5s 显示标题卡片；结束前 10s 出「下一集」浮层（有 `SeriesId` 时）。

### 4.4 其它

- **管理台**：卡片圆角 / 阴影与媒体门面同 token；表格统一「紧凑」密度；表单两列布局在 ≥1100px 时启用。
- **响应式**：≤900px 左栏抽屉；rail 横滑；hero 高度压到 40vh。
- **暗色主题**：已有开关，校对海报叠字与骨架块的对比度（骨架块用 `--ol-surface-2`）。

## 5. 实施路线

| 阶段 | 内容 | 依赖 |
|---|---|---|
| **A（P0）** | 任务中心写操作 · 外部播放器 · 游客播放 · 统计页 + 标记/未看 · 美化第一批 | 无 |
| **B（P1）** | 数据清理 ✓（§63） · 想看→下载→入库 ✓（缺「搜资源」，§62） · 关注更新流 ✓（§62） | P0-1（任务中心）、P0-2（直链） |
| **C（P2）** | 时间轴预览 · 标签 / 合并 · 字段所有权 · 云驱动（逐个） | B 的下载链路 |

每个 PR 的门槛沿用现状：`dotnet build` + `dotnet tests/OpenList.Tests/bin/Debug/net10.0/OpenList.Tests.dll` 全绿、`pnpm typecheck | test | build` 全绿、涉及页面的 `verify-*.mjs` 通过；新表两份迁移。

## 6. 风险与不做的事

- **Peach 代码零复制**是硬约束；若未来要直接复用其前端，整个前端须 AGPL 化（`docs/frontend-stack-decision.md` R-AGPL 已评估并否决）。
- **Peach 的 JAV 专用源**（javdb / javbus / FANBOX 等）不在本方案内：本项目是通用媒体库（TMDB 主线 + 番号一条辅线），只保留「可配置站点列表」的通用搜索口。
- **Peach 的双机同步 / ledger 备份**不做：本项目已有 `backup-restore` 页，且单机定位。
- **云驱动逐个移植**周期最长，放在最后且不阻塞媒体域迭代。
