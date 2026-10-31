# 功能设置缺失项与待实现完整项 — 审计清单

> 生成日期：2026-10-31。审计方法：对 `src/OpenList.Infrastructure/Data/DbInitializer.cs` 的 134 个种子设置逐键交叉核对后端（`src/**/*.cs`，含 `const string XxxKey` 常量与 `GetAsync("key")` 字面量两种读取模式）与前端（`frontend/src/**`，排除 lang）的字符串引用，并人工复核了 `GetPublicAsync`、`AppConfiguration` 等间接消费路径，避免仅凭 grep=0 误判。

## 执行进度（第一批，2026-10-31）

已接线并通过 `dotnet build` / `dotnet test`（579 项，578 通过、1 项既有跳过）/ `tsc --noEmit` / `vitest`（240 项）/ `vite build`：

| 项 | 实现位置 | 说明 |
|---|---|---|
| ✅ `ldap_login_enabled` 一致化 | `LdapAuthenticator` 改为「设置表优先、appsettings 兜底」，新增 `IsEnabledAsync`；`JwtService` / `/admin/system/info` 统一走它 | 设置页开关成为真正的总开关；登录页、系统信息、认证三处同一口径 |
| ✅ appsettings LDAP 迁移 | `DbInitializer.AdoptLdapConfigAsync` | 只写 appsettings 的老部署启动时一次性导入设置表（幂等，管理员清空地址后不再回灌） |
| ✅ `ldap_manager_dn` / `ldap_manager_password` / `ldap_user_search_base` / `ldap_user_search_filter` / `ldap_skip_tls_verify` | `LdapAuthenticator.ResolveAsync` | 搜索模式改用服务账号绑定；过滤器支持属性名与 `(uid=%s)` 两种写法 |
| ✅ `ldap_default_dir` / `ldap_default_permission` | `JwtService.LoadLdapUserDefaultsAsync` | LDAP 首次登录自动开通的账号按设置取默认目录与权限位 |
| ✅ `ldap_login_tips` | `stores/settings.ts` + 登录页 | 设置文案优先于 i18n 默认文案 |
| ✅ `favicon` | `MiscEndpoints` | `/favicon.ico` 先读 `favicon`，回退 `logo` |
| ✅ `robots_txt` | `MiscEndpoints` | 空值回退默认「允许所有爬虫」 |
| ✅ `announcement` | `pages/home/AnnouncementCard.tsx` | 首页列表上方 Markdown 公告，空值不渲染 |
| ✅ `package_download` | `pages/home/index.tsx` | 关闭时右键菜单与批量条都不出现打包入口 |
| ✅ `hide_files` / `ignore_system_files` | `ListFilterOptions` + `FileSystemService.ListAsync` | 20s 快照；过滤在分页与索引发布之前；非法正则跳过并告警 |
| ✅ `audio_autoplay` / `video_autoplay` | `AudioPreview` / `VideoPreview` | 去掉硬编码 `autoplay={true}` |
| ✅ `share_preview` / `share_preview_download_by_default` | `pages/share/index.tsx` | 单文件分享内联预览（排除归档与编辑器），下载优先开关生效 |
| ✅ `max_index_depth` | `AdminEndpoints.ResolveIndexDepthAsync` | 请求未指定深度时取设置值（默认 20） |

### 执行进度（第二批，2026-10-31）

| 项 | 实现位置 | 说明 |
|---|---|---|
| ✅ `audio_types` / `video_types` / `image_types` / `text_types` | `previews/registry.ts`（`extSettingsKey` + `parseExtList`） | 配了就**整表替换**内置扩展名；`parseExtList` 兼容中英文逗号、`.mkv` 写法、空白分隔；空值回落内置表（避免「设成空串 → 所有音频都不可预览」） |
| ✅ `external_previews` | `previews/ExternalPreview.tsx` | 与 `iframe_previews` **同结构**（`键 → {显示名: URL 模板}`，键支持扩展名列表或 `/正则/`），差在打开方式：交给本机程序（`vlc://`、`potplayer://`），被弹窗拦截时给按钮兜底 |
| ✅ `preview_archives_by_default` | `registry.resolvePreviews({ archiveDefault })` | 为假时归档 `order` 由 15 压到 900：打开压缩包先看到「下载/信息」，条目仍在下拉里可选 |
| ✅ `share_archive_preview` | `FsEndpoints`（`/fs/archive/list` 补 `ShareAccess`）+ `api/sharePublic.ts` + `pages/share/index.tsx` | 新增 `shareFsArchiveList`（`Authorization: <分享码>`）。**关键修复**：该端点此前没有接入分享码解析，游客请求会退化成 `BasePath="/"` 的匿名访客去读服务器上的同名路径（表现为 501/404）；现在与 `/fs/list`、`/fs/get` 同一套解析 |
| ✅ `share_preview_archives_by_default` | 同上 | 分享页归档的默认位开关，与首页同口径 |
| ✅ `pagination_type` | `composables/useFileList.ts` | `all` 时 `paginate(..., 0)` 原样返回、`pages===1` 让分页条自动隐藏；`pagination` 保持原行为 |
| ✅ `customize_head` / `customize_body` | `Program.cs` SPA fallback + `Hosting/SpaHtmlInjector.cs` | 注入 `index.html`；两段都为空时仍走 `SendFileAsync`（不整页读内存）；找不到标记时追加而非丢弃 |

**分享页归档的已知限制**：归档内条目只能列、不能点开预览 —— 单条目内联预览走 `/ae/{path}?inner=`，需要登录态签名，游客拿不到；要补需新增 `/sae/{code}/...` 之类的分享版端点。解压按钮在分享上下文隐藏（写操作对 Guest 恒 403）。

**第二批冒烟实测**（隔离实例 5097，Local 驱动 + 真实分享码）：访客带 `Authorization: <分享码>` 调 `/api/fs/archive/list` 返回 `a.txt` / `sub/b.md` 两条；无效分享码返回 `404 sharing not found`（不再误当匿名访客）；匿名无码仍 401。`customize_head`/`customize_body` 写入后 `/` 的 HTML 立即出现对应片段。

### 执行进度（第三批，2026-10-31）：P2 任务并发控制

| 项 | 实现位置 | 说明 |
|---|---|---|
| ✅ 8 个 `*_task_threads_num` 设置键 | `DbInitializer`（group 11 `traffic`） | 原本只有 `move_task_threads_num` 种子；补齐 copy/upload/download/decompress×2/offline×2，默认 5，`0` = 不限。同时补 `download_task_threads_num` 的中英文案（此前 lang 里根本不存在，只有审计文档提到它） |
| ✅ 设置 → 上限快照 | `Infrastructure/Tasks/TaskConcurrency.cs`（`TaskConcurrencyLimits` / `TaskConcurrencyProvider`） | 与缩略图同一套 20s 后台刷新，任务创建时只读内存；缺键/非法值退回默认 5（**不是 0**：写错一个字符不该变成不限流） |
| ✅ 按类型的并发闸门 | 同上 `ConcurrencyGate` | 自管计数 + 等待队列，而不是 `SemaphoreSlim`：上限来自设置表、随时可改，信号量容量不可变，重建会丢失已占名额。改上限时原地唤醒等待者，`0` 时全部放行 |
| ✅ `ITaskService` 接线 | `TaskService.RunAsync` / `GateFor` | 先申请名额（期间 `Pending`），拿到才置 `Running`；名额在 `finally` 归还，取消排队任务不会吞名额 |
| ✅ 任务体改为线程池调度 | `TaskService.ScheduleRun` | **关键修复**：复制/移动的任务体是同步 IO（`File.Copy`），此前 `_ = RunAsync(...)` 会在调用线程上一路同步跑到结束 —— 批量复制的 N 个任务被顺序串起来、并发上限形同虚设，`fs/copy` 请求本身也被阻塞。改为 `Task.Run` 后创建立即返回、闸门真正生效 |

**第三批冒烟实测**（隔离实例，Local 驱动 + 6×300MB 文件）：

- `copy_task_threads_num=1`：6 个 copy 任务串行，`done` 列表里每个 `start_time` 恰是上一个的 `end_time`；
- `=3`：`/api/fs/copy` 请求 **0.127s** 返回（修复前会阻塞整个复制时长），`/api/task/copy/undone` 采样稳定为 `running=3 pending=3`；
- `=0`：6 个任务全部 `running=6`，无人排队；
- 改设置后 20s 内生效（首刷读到新值即可），无需重启。

**并发控制的语义边界**：这是「同类型同时运行的任务数」，不是「单任务内部的文件级并行」。复制/移动是「一个文件一个任务」，所以两者等价；解压/离线下载的并发键已接线，但当前代码里还没有产生这些任务类型的入口（`Decompress*`、`OfflineDownload*` 尚无 `CreateAsync` 调用点），接入后即自动受限。

### 执行进度（第四批，2026-10-31）：P3 缩略图收尾

| 项 | 实现位置 | 说明 |
|---|---|---|
| ✅ 列表 40px 缩略图槽位 | `frontend/src/components/ThumbIcon.tsx`（新） | 有 `thumb` 画图、无/失败退图标；**只做一级降级**（不像网格再退原图）。桌面表格名称列 40px，手机卡片行 36px（塞进 52px 行高）。失败态按 `(name, thumb)` 重置，复用实例不会一直显示图标 |
| ✅ 网格 `src` 含 `type=thumb` 断言 | `verify-thumb.mjs` | 原来只有实现没有断言，现在三处槽位（表格 / 网格 / 手机行）都在回归里钉住 |
| ✅ E2E `I/thumb` 轮 | `frontend/scripts/verify-thumb.mjs`（新）+ `run-regression.sh` 的 `round_thumb` | 28 条断言；fixture 由脚本用 ffmpeg 现造，PDF 用脚本内手拼的最小合法文件（xref 偏移现算）。默认轮次列表已加 `thumb`（共 16 轮） |
| ⛔ PDF 首页 | 仍 415 `pdf-unsupported` | 环境阻塞，不是取舍变化：NuGet 不可达 + 本地无 PDFtoImage/PDFium + 机器无 `pdftoppm`/`mutool`/`gs`。两条路（联网补 PDFtoImage / 改外部 pdftoppm）都要动 Dockerfile 与发布体积，需拍板 |
| ✅ 测试契约同步（第三批遗留） | `FsEndpointsTests.WaitTasksAsync` | 任务体改成线程池后，`/fs/move`、`/fs/copy` 的 HTTP 200 只代表**已入队**。原测试「返回即可重建同名文件」的假设随之失效，全量跑时偶发 `file is being used by another process`（移动任务还在跑）。补了按类型轮询 `/api/task/{type}/undone` 的 helper；生产行为正确，改的是测试 |
| ⏳ 详情页占位图、指标 | — | 两份文档都只写了名字、没有口径，留待需求明确 |

**第四批验证**：前端 `vitest` **252 项全绿**（新增 5 条 `ThumbIcon.spec.ts`）、`tsc --noEmit`、`vite build` 通过；E2E `I/thumb` **28/28 PASS**（含「不带 type 仍是原文件」这条回归保护）；后端未改，仍为 594 项（593 通过 / 1 跳过）。

**E2E 实测口径**（隔离实例 5055 + Local 驱动）：`thumb=null` → 开启后 `http://…/p/e2e/photo.jpg?type=thumb&w=320&h=320` → 首次 `generated` 5444B → 二次 `hit` 且 ETag 相同 → 改写源文件后 `generated` 5981B；`w=9999` 输出 1024×1024 PNG；无封面 mp3 → 404 `no-cover`；PDF → 415 `pdf-unsupported`；列表三处槽位分别渲染 3 张图 + 4 个图标位（无封面音频与视频/PDF 走图标）。

### 执行进度（第五批，2026-10-31）：直链参数 + 归档编码

| 项 | 实现位置 | 说明 |
|---|---|---|
| ✅ `forward_direct_link_params` / `ignore_direct_link_params` | `Infrastructure/Services/DirectLinkParams.cs`（新）+ `SignEndpoints.DownloadAsync` | `/d/` 302 时按开关把请求参数带到直链上。规则：**覆盖而非追加**（同名参数用请求值替换，追加重复参数会让上游按第一个值处理，看起来像"设置了没生效"）；`sign` 恒不转发（本站签名不该外泄给上游）；空值丢弃；黑名单大小写不敏感、支持中英文逗号/分号/换行/空白分隔。只在请求确实带了 `sign` 以外的参数时才查设置表 |
| ✅ `non_efs_zip_encoding` | `SharpCompressArchiveSupport`（`ResolveEncoding` / `ArchiveEncoding.Default`）+ `FileSystemService.ResolveZipEntryEncoding` | 旧式 ZIP（Windows"压缩文件夹"、一批老工具）条目名按本地代码页写且不置 UTF-8 标志位（bit 11），SharpCompress 默认按 UTF-8 解 → 中文名乱码。现在按设置解码；`CodePagesEncodingProvider` 在静态构造里注册（.NET 默认不带 GBK/936）；编码名写错时退回默认而非抛异常 |
| ✅ 顺带修的缺陷 | `FileSystemService.ArchiveListAsync` | 归档列表**此前把 `archive_pass` 丢了**：`reader.ListArchiveEntriesAsync(relPath, ct)` 直接忽略了密码（接口根本没有该参数）。加密包列表必然失败。接口补 `password` 与 `entryEncoding` 两个可选参数，`LocalDriver` / 解压 / 单条目预览三处一并透传 |

**第五批验证**：后端 `dotnet test` **608 项 / 607 通过 / 1 跳过**（新增 14 项：8 条 `DirectLinkParamsTests`、4 条 `LegacyZipEncodingTests`、2 条 `ArchiveEncodingSettingTests`）；前端 `vitest` 252、`tsc --noEmit` 通过。

**测试怎么造出"老 ZIP"**：`ZipArchive` 写出的条目名恒为 UTF-8 且置位 bit 11，造不出这种包，因此新增 `tests/OpenList.Tests/LegacyZipBuilder.cs` 手工拼 zip（stored、无标志位、CRC32 现算），条目名直接给 GBK 字节。负向断言同样重要：**不配置编码时必须解不出中文名**，否则这条设置就是"本来就好、加了没用"。

**接线验证**：`ArchiveEncodingSettingTests` 走完整 HTTP（登录 → 写设置 → `POST /api/fs/archive/list`），守的是「设置是否真的被读出来传给驱动」——驱动层单测再全，读漏了也没用。

**第五批冒烟实测**（隔离实例 5422 + Local 驱动 + 手工拼的 GBK zip，条目名 `中文目录/说明.txt`）：

| 步骤 | 结果 |
|---|---|
| 未配置编码 | `POST /api/fs/archive/list` → `["����Ŀ¼/˵��.txt"]`（乱码，与预期一致） |
| 写入 `non_efs_zip_encoding=gbk` 后（无需重启） | 同一请求 → `["中文目录/说明.txt"]` |
| `/d/enc/legacy.zip?download=1&sign=x` | 200，150 字节（Local 走流式，转发规则对无直链的驱动不产生影响） |
| `/p/enc/legacy.zip` | 200，150 字节 |

直链参数转发只作用于「驱动给出直链 → 302」的分支，本机没有实现了 `IFileSystemLinkProvider` 的驱动可测，因此该部分由 `DirectLinkParamsTests` 的 8 条纯函数用例覆盖（覆盖而非追加、sign 恒丢、空值丢弃、fragment 保留、黑名单大小写不敏感）。

### 复核结论：剩下的键各自卡在哪里（2026-10-31，第五批收尾）

把"还没接"的键逐个复核了一遍，分成三类 —— **A 类是真待办，B 类是复核后判为不适用（N/A），C 类是新功能**。这样下次不用再重新判断一遍。

**A. 真待办，等一个决策**

| 项 | 卡点 |
|---|---|
| `proxy_types` / `share_force_proxy` | 存储已有「按存储」的代理开关（`web_proxy` / `webdav_policy`），全局白名单要定 ① 匹配驱动名还是挂载粒度 ② 与存储显式 `web_proxy=false` 的优先级。`share_force_proxy` 另外发现**恒为真**（见 B 类） |
| FTP/SFTP 7 键、S3 3 键 | 实际走 `AppConfiguration`（`FtpHostedService` / `S3Endpoints`）。要么按 LDAP 的先例做成「设置优先、配置兜底 + 启动导入」，要么删种子并隐藏设置组 |
| PDF 首页缩略图 | 环境阻塞：NuGet 不可达 + 本机无任何 PDF 渲染器。两条路（联网补 PDFtoImage / 改外部 `pdftoppm`）都要动 Dockerfile 与发布体积 |

**B. 复核后判定为不适用（建议保留种子但在文档/设置页标明，不必写代码）**

| 键 | 为什么不接 |
|---|---|
| `filter_readme_scripts` | Readme / Markdown 预览全链路是 `marked → DOMPurify(USE_PROFILES.html)`，脚本永远没有执行通道。「不过滤脚本」是唯一不该提供的选项 —— 开关关掉也不该放行 |
| `proxy_ignore_headers` | 代理链路只从驱动拿一个 `Stream`，**从不转发上游响应头**（`FileDownloadHelper` 自己写 Content-Type / Length / Range），没有可过滤的对象。等做「上游头透传」时再接 |
| `share_force_proxy` | 分享下载 `/sd/` 一直是服务器流式，从不 302 到云盘直链 —— 开关的两个状态实际是同一个 |
| `hide_storage_details` / `hide_storage_details_in_manage_page` / `show_disk_usage_in_plain_text` | 前端没有「存储详情 / 磁盘用量」面板（首页无侧栏、管理台存储页不含用量），无对象可隐藏 |
| `allow_indexed` / `allow_mounted` | 本项目没有 alist 那种「meta 索引目录 / 路径挂载」条目（根列表的挂载点由 `MountFileSystem` 真实合成），没有可开关的条目类型。要用得先做 meta 索引/挂载功能本身 |
| `home_icon` / `share_icon` / `share_summary_content` / `home_container` / `settings_layout` | 缺口径：站点图标已由 `/favicon.ico` + `favicon` 键覆盖；分享页摘要要**服务端按路径渲染 meta** 才对爬虫有意义，纯 SPA 注入无效；`home_container` 的字面默认值 `10`、`settings_layout` 的 `responsive,columns` 都没有可对照的行为定义 |
| `filename_char_mapping` / `privacy_regs` | 语义待定：本项目不设 `Content-Disposition`（下载名由 URL 末段决定），没有「下载文件名」可映射；把脱敏套到列表显示会出现「列表名字 ≠ 真实路径 → 点不开」 |
| `ocr_api` / `audio_cover` | 属新功能（OCR、音频封面抓取），不是给已有能力接线 |
| `media_default_provider` / `media_subtitle_language` | 前者：provider 注册链路固定，选择器没有分支可切；后者：字幕请求未带语言参数，属媒体功能补全 |

**C. 需要新功能而非接线**

`handle_hook_after_writing` / `handle_hook_rate_limit` —— 只有开关与限流值，**没有「往哪个 URL 发」的目标设置**；写后通知还要定 payload、重试、签名。这属于新功能设计，不该硬接一个 bool 上去。

## P0 — 前后端不一致（用户可感知的坑，建议最先处理）

| # | 项 | 现状 | 代码依据 |
|---|---|---|---|
| 1 | `ldap_login_enabled` | 登录页根据该 DB 设置展示 LDAP 登录提示，但后端 LDAP 是否启用实际由 **appsettings 的 `Ldap` 段**决定（配置了 Url 即启用），两者互不联动——用户在后台打开开关，登录页变了，认证却仍走配置 | 前端消费：`frontend/src/stores/settings.ts`；后端启用逻辑：`src/OpenList.Infrastructure/Services/LdapAuthenticator.cs:26-31`（`config.Ldap`） |
| 2 | `favicon` | 设置页可配 `favicon`，但 `/favicon.ico` 实际读取的是 **`logo`** 键（302 跳转），`favicon` 键零消费 | `src/OpenList.Server/Endpoints/MiscEndpoints.cs:55-62` |

## P1 — 空壳设置（有种子、设置页可见，但零行为）

设置保存后只是写库，不产生任何效果。多为「Go 版行为迁移时只带了设置没带逻辑」。按价值排序：

### 1a. 有真实行为价值、建议接线的（Go 版均有对应功能）

| 组 | 键 | 应有行为 / 代码依据 |
|---|---|---|
| G1 | `robots_txt` | `/robots.txt` 目前返回**固定字符串** `User-agent: *\nAllow: /`，不读设置 — `MiscEndpoints.cs:48-50` |
| G1 | `announcement` | 首页公告，前端无任何渲染点 |
| G1 | `allow_indexed`、`allow_mounted` | 目录列表中索引/挂载条目的显示开关 |
| G3 | `package_download` | 打包下载开关（后端 `/package` 端点无此校验） |
| G3 | `share_preview*` 4 键 | 分享页预览行为开关（`share_preview`、`share_archive_preview`、`share_preview_archives_by_default`、`share_preview_download_by_default`）；分享页 `frontend/src/pages/share/index.tsx` 未读取 |
| G3 | `preview_archives_by_default`、`preview_download_by_default`（后者前端已消费，前者未消费） | 预览默认行为；`PreviewHost.tsx:42` 只接了 `preview_download_by_default` |
| G3 | `audio_types`、`video_types`、`image_types`、`text_types`、`external_previews` | 预览类型路由目前是前端硬编码扩展名表，`PreviewHost.tsx` 只消费 `iframe_previews` |
| G3 | `audio_autoplay` | 前端 `AudioPreview.tsx:63` 硬编码 `autoplay={true}`，设置完全不生效；`video_autoplay` 同理 |
| G3 | `non_efs_zip_encoding` | ✅ 已接线（第五批）：`SharpCompressArchiveSupport` 按设置解码非 UTF-8 标志位的条目名 |
| G4 | `hide_files`、`ignore_system_files` | 文件列表过滤（前端 `useFileList.ts` 未实现；后端列表端点未消费） |
| G4 | `pagination_type` | 分页模式（前端硬编码为页码分页） |
| G4 | `ignore_direct_link_params`、`forward_direct_link_params` | ✅ 已接线（第五批）：`DirectLinkParams`，作用在 `/d/` 的 302 目标上（`SignService` 只算签名，与参数无关） |
| G4 | `filename_char_mapping`、`privacy_regs` | 文件名映射与隐私正则脱敏，无消费 |
| G4 | `handle_hook_after_writing`、`handle_hook_rate_limit`（Private） | 写后 Hook 未实现 |
| G4 | `proxy_ignore_headers`（Private） | 代理头过滤未实现 |
| G6 | `max_index_depth` | `SearchService.cs` 索引深度硬编码，不读此键（`search_index`、`auto_update_index`、`ignore_paths` 已正常消费） |

### 1b. 功能走 appsettings、DB 设置是摆设的（建议二选一：接线或删种子+隐藏设置组）

| 组 | 键 | 真实配置源 / 代码依据 |
|---|---|---|
| G8 | `ldap_server`、`ldap_manager_dn`、`ldap_manager_password`、`ldap_user_search_base`、`ldap_user_search_filter`、`ldap_default_dir`、`ldap_default_permission`、`ldap_skip_tls_verify`、`ldap_login_tips`（9 键） | `LdapAuthenticator.cs:26` 读 `AppConfiguration.Ldap`；DB 设置组整体不生效（并直接导致 P0-1 的不一致） |
| G10 | `ftp_public_host`、`ftp_pasv_port_map`、`ftp_mandatory_tls`、`ftp_implicit_tls`、`ftp_tls_public_cert_path`、`ftp_tls_private_key_path`、`sftp_disable_password_login`（FTP/SFTP 组全部） | `FtpHostedService.cs:30-35` 读 `AppConfiguration.FTP`；SFTP 同理走 appsettings |
| G9 | `s3_buckets`、`s3_access_key_id`、`s3_secret_access_key` | S3 网关（`S3Endpoints.cs`）按「存储挂载→桶」映射+用户认证工作，不消费这三个键 |
| G7 | `sso_organization_name`、`sso_application_name`、`sso_jwt_public_key`（SSO 组中这 3 键） | `SsoService.cs` 消费了 G7 其余键（client_id/secret/endpoint/oidc_username_key 等），仅这 3 个无消费 |

### 1c. 纯展示类空壳（价值低，可最后处理或随需求逐个接）

G2 品牌组 12 键全部无行为：`home_icon`、`share_icon`、`share_summary_content`、`home_container`、`settings_layout`、`customize_head`、`customize_body`、`filter_readme_scripts`、`show_disk_usage_in_plain_text`、`hide_storage_details`、`hide_storage_details_in_manage_page`、`favicon`（见 P0-2）。
G3 其余：`ocr_api`、`audio_cover`。
G11：`move_task_threads_num` 已接（第三批，见下）—— 8 个并发键统一由 `TaskConcurrencyProvider` 消费。
G12：`media_default_provider`（provider 选择未消费此键，实际注册链路固定）、`media_subtitle_language`（OpenSubtitles 请求未携带语言参数，前端字幕请求也未读取）。

## P2 — lang 孤儿键（设置从未存在，功能本身缺失）

> **状态（2026-10-31）**：8 个任务并发键已全部完成（见「执行进度（第三批）」）。本段剩余 `proxy_types` / `share_force_proxy` 两项，需先定义语义再动手，理由见段末。

存在于 `frontend/src/lang/zh/settings.json` 但**未种子、后端也不读**——功能整体没做：

- ✅ `copy_task_threads_num`、`upload_task_threads_num`、`download_task_threads_num`、`move_task_threads_num`（已种子）、`decompress_download_task_threads_num`、`decompress_upload_task_threads_num`、`offline_download_task_threads_num`、`offline_download_transfer_task_threads_num` — 已接线（`TaskConcurrencyProvider` + `ConcurrencyGate`）
- `proxy_types` — 本地代理类型白名单未实现
- `share_force_proxy` — 分享强制走代理未实现

**proxy 两项为什么没跟着做**：存储已经有一套**按存储**的代理开关（`web_proxy` / `webdav_policy = native_proxy|use_proxy_url|302_redirect`），驱动的 `OnlyProxy` / `PreferProxy` 也参与默认值。`proxy_types` 是**全局**按「驱动名」强制一批存储走本地代理，`share_force_proxy` 再对分享链接加一层。落地前必须先定两件事，否则会做出与存储自身设置打架的行为：

1. 白名单匹配的是驱动名（`Local` / `S3` / `OneDrive`）还是存储挂载类型？Go 版是驱动名，但同名驱动可能有多个挂载，粒度差异要先确认；
2. 全局白名单与存储的自选 `web_proxy=false` 冲突时谁优先（强制代理会推翻管理员在存储里显式关掉的设置）。

这两点定了之后改动只在链接决策处（`fs/get` 的 raw_url ↔ `/d/` 代理地址）收敛，不涉及存储模型。

## P3 — 缩略图功能遗留（docs/thumbnail-service-plan.md §51 明确「还没做」）

> **状态（2026-10-31 第三批）**：2、3 已完成；1 被环境阻塞；4 的文档部分已清理，剩「详情页占位图 / 指标」两项本身没有明确需求。

1. ❌ **PDF 首页**：`ThumbKind.Pdf` 仍回 415 `pdf-unsupported`。**不是取舍变化，是环境阻塞**：NuGet 源不可达（`dotnet package search PDFtoImage` 失败）+ 本地缓存无 PDFtoImage/PDFium + 机器上没有任何 PDF 渲染器（`pdftoppm`/`mutool`/`gs` 均不存在）。两条可选路径：① 联网补 PDFtoImage 5.4.0（含 native，Docker 要补 `libfontconfig1`/`libstdc++`）；② 改成外部 `pdftoppm`（与现有「外部 ffmpeg」同构，镜像补 poppler-utils）。两条都要动 Dockerfile 与发布体积，需拍板。
2. ✅ **E2E `verify-thumb.mjs`**：已落地为 `I/thumb` 轮（`run-regression.sh` 的 `round_thumb`），**28 条断言**全绿。fixture 由脚本用 ffmpeg 现造（图片/音频/视频），PDF 用手拼的最小合法文件。注意：列表对「无封面 mp3」必然发一次 404，E2E 把资源 404 与脚本级异常分开统计。
3. ✅ **列表行 40px 缩略图槽位** + 网格页 `src` 断言：新增 `components/ThumbIcon.tsx`（表格名称列 40px、手机卡片行 36px），网格断言进 E2E。视频/PDF 在列表里仍不下发 thumb（防一屏 20 次远端抽帧），槽位直接显示图标。
4. 文档标记已清理（本文件 + `thumbnail-service-plan.md` §0.1/§0.2/§14/§12）。**详情页占位图**与**指标**两项，`AGENTS.md` 与 plan 都只写了名字、没有定义口径（指标是命中率？是 dashboard 面板？），留待需求明确；媒体网格的「海报加载失败换占位块」已有实现（`components/MediaGrid.tsx`）。

## 已确认正常、无需处理

- G5 aria2/qBittorrent/Transmission：`AdminEndpoints.cs` 读取（连接测试）+ `GetOfflineDownloadToolsAsync`
- G11 限速 4 键：`RateLimitManager.cs` 消费
- G11 并发 8 键：`TaskConcurrencyProvider` + `ConcurrencyGate` 消费（2026-10-31 第三批）
- G12：`media_tmdb_api_key`（TmdbMetadataProvider）、`media_metadata_language`（MediaMetadataService）、`media_opensubtitles_api_key`（OpenSubtitlesProvider）、`media_auto_scan_interval_hours`（MediaAutoScanHostedService）均已接线
- G14 缩略图全组 11 键：`ThumbnailOptions.cs` 消费
- G4 `sign_all`/`link_expiration`（SignService）、`ignore_paths`（SearchService）已接线
- 前端专属（后端无行为属正常）：`main_color`、`readme_autorender`、`default_page_size`、`iframe_previews`、`preview_download_by_default`
- G0 各云盘 temp_dir/callback 键：`AdminEndpoints.cs` 消费

## 建议实施顺序（2026-10-31 更新）

| 序 | 项 | 状态 |
|---|---|---|
| 1 | **P0** `ldap_login_enabled` 口径、`favicon` 读错键 | ✅ 第一批 |
| 2 | **P1-1a** 高价值行为接线（robots/announcement/package_download/share_preview*/hide_files/预览类型表/autoplay/max_index_depth…） | ✅ 第一、二批 |
| 3 | **P1-1b** LDAP 9 键改为「设置优先 + 配置兜底 + 启动导入」 | ✅ 第一批 |
| 4 | **P2** 任务并发 8 键 | ✅ 第三批 |
| 5 | **P3** 缩略图：E2E 轮次 + 列表 40px 槽位 | ✅ 第四批（PDF 未做，见 A 类） |
| 6 | **P1-1a 收尾** `non_efs_zip_encoding`、`ignore/forward_direct_link_params` | ✅ 第五批 |
| 7 | **P1-1b 收尾** FTP/SFTP 7 键、S3 3 键 | ⏳ 等决策（设置驱动 or 删种子） |
| 8 | **P2 收尾** `proxy_types` | ⏳ 等决策（驱动名 vs 挂载粒度、与存储 `web_proxy` 的优先级） |
| 9 | **P3 收尾** PDF 首页 | ⛔ 环境阻塞（NuGet 不可达 + 无 PDF 渲染器） |
| 10 | **P1-1c** 纯展示类 | 🚫 复核后判为 N/A 或属新功能，见上方「复核结论」B/C 类 |
