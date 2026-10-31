# 缩略图服务技术方案（M4 遗留项）

> 状态：**已实现（图片 / 音频 / 视频；PDF 仍为 415）**。列表槽位与 E2E 轮次见 §0.2。对应
> `docs/frontend-reference-analysis.md` §200 与 `docs/unified-media-product-plan.md` §3211
> 中记为「留到 M4」的 `/p?type=thumb`。
>
> 实施时推翻了原设计的解码后端（Sdcb.FFmpeg P/Invoke + SkiaSharp），改为
> **全部交给外部 ffmpeg 进程**，理由见 §0.1。设计其余部分（端点契约、缓存键、并发与降级）
> 与实现一致。

---

## 0.1 实施记录（2026-10-31）

**结论：走 Jellyfin 那条路 —— 不把 FFmpeg 链进进程，而是当外部程序调用。**

| 原设计 | 实际实现 | 原因 |
|---|---|---|
| Sdcb.FFmpeg（P/Invoke 绑定 + 随包 native）抽帧 | `FfmpegProcessRunner` + `FfmpegTool`：探测路径、拼 argv、抓 stderr、超时强杀 | native 崩溃只死子进程；不必为 6 个 RID 各打一份解码器；LGPL 只需在 NOTICE 致谢（独立程序 ≠ 动态链接）；参数可复制出来手跑。代价是部署环境要有 ffmpeg，缺失时该类型降级为 404 `ffmpeg-unavailable` |
| SkiaSharp 缩放/编码 | 同上，`ffmpeg -vf scale=…,crop=…` | 一个后端覆盖图片/音频封面/视频，省掉两套 native（SkiaSharp + PDFium）与 ~17MB 资产；顺带绕开 ImageSharp 的 SixLabors 许可问题 |
| ATL 读音频封面 | `ffmpeg -map 0:v:0` | 内嵌封面本来就只是一条附带图像流，ffmpeg 直接能取 |
| PDFtoImage 渲染首页 | **未实现**，`ThumbKind.Pdf` 直接回 415 `pdf-unsupported` | ffmpeg 不解 PDF；接 PDFium 要再引一套 native，收益（PDF 封面）明显低于成本，留待需要时再加 |
| `ISeekableSource` / 自定义 AVIO 回调 | 不需要了 | ffmpeg 自己会用 pread（本地路径）或 HTTP Range（驱动直链）。只保留两个源：`IFileSystemLocalPath`（本地盘真实路径）与 `FileLink.Url`（云盘直链）；两者都没有 → 404 `no-seekable-source` |
| `thumb_video_read_budget_mb` 读取预算 | 去掉 | 该护栏是为「自己实现 AVIO 回调」设计的；交给 ffmpeg + 进程级 40s 超时 + 列表不下发视频缩略图，已经够用 |

**落点**

| 文件 | 作用 |
|---|---|
| `src/OpenList.Core/Interfaces/IThumbnailUrlBuilder.cs` | 列表/详情下发 `Obj.Thumb` 的接口（Infrastructure 只依赖它） |
| `src/OpenList.Core/Drivers/IFileSystemDriverExtensions.cs` | 新增 `IFileSystemLocalPath`（真实路径）与 `IThumbnailSwitch`（驱动级开关） |
| `src/OpenList.Infrastructure/Thumbnails/ThumbKind.cs` | 扩展名 → 类型（svg 明确不支持；视频不进列表） |
| `…/ThumbnailOptions.cs` | 设置项 → 快照；`ThumbnailOptionsProvider` 后台 20s 刷新 |
| `…/ThumbnailCache.cs` | 内存 LRU + 磁盘两级，写盘原子替换，按容量淘汰 |
| `…/FfmpegTool.cs` | 进程执行器（ArgumentList / stderr / 超时 Kill / 进程登记）与定位探测（设置 → `OPENLIST_FFMPEG` → PATH） |
| `…/ThumbnailService.cs` | 单飞、负缓存、类型分派、超时、预算外的并发闸门 |
| `…/ThumbnailUrlBuilder.cs` | `/p/{path}?type=thumb&w=&h=`（需要时带 `&sign=`） |
| `src/OpenList.Server/Endpoints/SignEndpoints.cs` | `?type=thumb` 分流；其它 `type` 值原样走下载 |
| `src/OpenList.Infrastructure/Data/DbInitializer.cs` | `Group 14 THUMB` 的 11 个设置项，`thumb_enable` 默认 **false** |

**从 Jellyfin 那里抄的三条硬约束**（都写进了代码注释）

1. **argv 逐个传**：`-vf` 的整条滤镜链必须是一个 argv 元素，拼字符串会被空格拆散（Jellyfin #18322）。
2. **stderr 必须留**：失败时把 stderr 尾部记进 `thumb failed: … detail=`，并把原因归类成可枚举值返回 `X-Thumb-Reason`。
3. **超时必须 Kill 整棵进程树**，且所有在跑进程登记在案，`Dispose` 时统一清理。

**测试**：`tests/OpenList.Tests/ThumbnailTests.cs`（23 条，缓存键/类型判定/参数拼装/设置映射/URL 条件/ffmpeg 缺失降级）
+ `tests/OpenList.Tests/ThumbnailEndpointTests.cs`（11 条，含「不传 type 时行为不变」「非法 w 不 400」「HEAD 不生成」「415/404 原因头」「真 ffmpeg 出图并二次命中缓存」）
+ `tests/OpenList.Tests/ThumbnailFfmpegTests.cs`（6 条，用仓库内 `.e2e/bin/ffmpeg.exe` 现录 3 秒 testsrc，断言产物是 320×320 的 JPEG）。

**还没做**：PDF 首页（阻塞原因见 §0.2）、`thumb_cache_max_mb` 的定时清理
（当前靠写入时每 64 次触发一次淘汰）。

---

## 0.2 实施记录（2026-10-31，第二批：列表槽位 + E2E）

| 项 | 落点 | 说明 |
|---|---|---|
| 列表 40px 缩略图槽位 | `frontend/src/components/ThumbIcon.tsx`（新）+ `pages/home/index.tsx`（表格名称列）+ `pages/home/ListRows.tsx`（手机卡片行） | 统一成一个组件：有 `thumb` 画图，没有/加载失败退图标。**只做一级降级**（缩略图 → 图标），不像网格那样再退原图 —— 40px 的槽位为一张全尺寸图付流量不划算。手机行用 36px，好塞进 52px 的行高 |
| E2E 轮次 `verify-thumb.mjs` | `frontend/scripts/verify-thumb.mjs` + `run-regression.sh` 的 `round_thumb`（`I/thumb`） | 28 条断言，覆盖 §12 的 9 条（并拆细）：默认关闭不下发 → 开启后下发 → 生成/命中/ETag → 改文件失效 → txt 415 → w=9999 夹到 1024 → 带封面 200 / 无封面 404 `no-cover` → PDF 415 → **不带 type 仍是原文件** → 表格/网格/手机三处槽位。fixture 由脚本用 ffmpeg 现造，PDF 用脚本内手拼的最小合法文件 |
| 资源 404 的口径 | 同上 | 列表对「无封面 mp3」必然发一次取不到图的请求（后端无法预知有没有内嵌封面），前端按约定降级为图标。E2E 把这类 404 与**脚本级异常**分开统计，否则这一轮永远红 |

**PDF 首页为什么这轮没做**：不是取舍变化，是环境阻塞 —— 本机 NuGet 源不可达（`dotnet package search PDFtoImage` 直接报「无法加载服务索引」），本地缓存里也没有 PDFtoImage/PDFium；同时机器上没有任何可用的 PDF 渲染器（`pdftoppm`/`mutool`/`gs` 都不存在）。要么联网补包（PDFtoImage 5.4.0，含 PDFium + SkiaSharp native，Docker 运行时还要补 `libfontconfig1`/`libstdc++`），要么改用外部渲染器（`pdftoppm`，与现有「外部 ffmpeg」同构，镜像里补一个 poppler-utils）。两条路都要动 Dockerfile 与发布体积，属于需要拍板的取舍，留到下一批。

---

## 0. 结论摘要

> 下表是**原始设计**。解码后端的最终选择被推翻（改成外部 ffmpeg 进程），
> 逐条对照见 §0.1；其余决策（端点形态、缓存键、默认关闭、列表不下发视频）与实现一致。

| 决策点 | 结论 | 关键理由 |
|---|---|---|
| 端点形态 | `GET /p/{path}?type=thumb&w=&h=` 挂在既有 `/p/` 上 | 对齐 Go 上游；签名、匿名放行、限流、Range 语义全部复用现成逻辑，不新增鉴权面 |
| 项目归属 | 新建 `src/OpenList.Thumbnails` | 解码器带 native 资产，隔离在独立程序集里，Server 按需引用，Infrastructure 的包图不被污染 |
| 图片解码/缩放 | **SkiaSharp**（MIT） | 与 PDF 渲染共用一套 native；WebP/AVIF 编解码开箱可用；避开 ImageSharp 的 SixLabors Split License（商用需付费） |
| 视频首帧 | **Sdcb.FFmpeg 7.0.0 + runtime 包**（P/Invoke 绑定 + 随包 native） | 唯一「加个 PackageReference 就能用」的跨平台 H.264/HEVC 解码路径；不需要用户在机器上装 ffmpeg |
| 音频封面 | **ATL**（MIT）读 tag + SkiaSharp 缩放 | ATL 覆盖 ID3v2/FLAC/MP4，纯托管，许可证干净 |
| PDF 首页 | **PDFtoImage 5.4.0** | 内部就是 PDFium + SkiaSharp，与图片链路共用 native |
| 远程文件随机读 | 新增 `ISeekableSource` 抽象，两实现：可 Seek 的 Stream / 驱动的 `ProxyRangeReader` | 视频抽帧必须随机读；VFS 的 `OpenFile` 对云盘是**不可 Seek 的顺序流**（`CallbackRangeReader.CanSeek => false`），拿它喂 FFmpeg 会直接失败 |
| 默认值 | `thumb_enable = false`（**默认关闭**） | 升级后不能凭空多出解码负载与远端流量；关闭时 `Obj.Thumb` 恒空，前端行为与今天完全一致 |
| 缓存 | 磁盘两级（内存 LRU + 文件），键含 `size+mtime+生成器版本` | mtime 变化即自然失效，不需要任何主动失效/钩子 |

一句话架构：**`/p/` 在鉴权之后按 `type=thumb` 分流 → 缓存查询（键含 mtime）→ 未命中则按文件类型选生成器 → 生成器只依赖 `ISeekableSource` / `IFileSystemService.ReadFileAsync` → 结果落盘并回填内存 LRU**。

---

## 1. 现状（已查证，含位置）

| 事实 | 位置 |
|---|---|
| `Obj.Thumb` 字段存在但**永远不被赋值** | `src/OpenList.Core/Models/Obj.cs:26`；赋值点只有 `FileSystemService.ToObj`（`FileSystemService.cs:973`），其中没有 `Thumb` |
| `/p/` 只做代理下载，**没有 `type` 参数分支** | `src/OpenList.Server/Endpoints/SignEndpoints.cs:55`（GET）与 `:82`（HEAD） |
| 下载决策：直链 302 / 驱动区间读 / VFS 流式 | `SignEndpoints.DownloadAsync`（`:204`） |
| VFS 的 `OpenFile` 是顺序流；云盘驱动明确 `CanSeek => false` | `src/OpenList.Drivers/Cloud/CallbackRangeReader.cs:250`、`AliyundriveOpenDriver.cs:1007`；`AliyundriveOpenFileSystem.OpenFileImpl:180` 直接返回 HTTP 响应流 |
| 驱动**已经有随机读通道**：`IFileSystemLinkProvider.GetFileLinkAsync(preferProxy: true)` → `FileLink.RangeReader` | `src/OpenList.Core/Drivers/IFileSystemDriverExtensions.cs:46,66` |
| 小文件整读已有统一入口：`ReadFileAsync(path, maxBytes, user, ct)`（超限直接拒，不截断） | `FileSystemService.cs:761` |
| 媒体域已有「读图」实现可参考（外链 302 / VFS 读出 / 一律 404） | `src/OpenList.Media/Endpoints/MediaImages.cs` |
| 设置项是后端下发的元组表，前端零硬编码 | `DbInitializer.DefaultSettings`（`:533`）、`frontend/src/api/types.ts:362` `SettingGroups` |
| 本地驱动已声明 `thumbnail` / `thumb_cache_folder` 两个配置，但**从未被读取**（死配置） | `src/OpenList.Drivers/Local/LocalDriver.cs:84-94, 135-140`；i18n 亦已有文案 `frontend/src/lang/zh/drivers.json:866` |
| 网格视图目前**直连原图**，并用 `loading="lazy"` 兜底 | `frontend/src/pages/home/GridView.tsx:21-54` |
| 列表行无缩略图，音频封面在等 `obj.thumb` | `GridView.tsx`；`frontend/src/pages/home/previews/AudioPreview.tsx:36` |
| 依赖里没有任何图像/视频解码库 | `Directory.Packages.props` |
| 限流中间件按 `/p/` 前缀匹配，缩略图请求会被包一层限速流 | `Middleware/RateLimitMiddleware.cs:19` |

---

## 2. 目标与非目标

**目标**

1. 任意挂载上的图片、音频、PDF、视频，都能通过一个 URL 拿到「小尺寸、可直接给 `<img>` 用」的图。
2. 列表/网格页不再拉原图：320px 的卡片不再下载 8MB 的 JPEG。
3. 生成结果落盘缓存，二次请求零解码；文件变更（mtime 变）自动失效。
4. 远端不可随机读时**优雅跳过**，不阻塞列表、不拖垮云盘配额。
5. 全程零配置可用（默认关闭），打开后零外部依赖（不需要用户装 ffmpeg）。

**非目标**

- 不做 CDN / 多机共享缓存（未来可换 `IThumbnailCache` 实现）。
- 不做按需转码 / HLS / 视频封面序列（那是播放器的事）。
- 不做「相册缩略图预生成」后台全盘任务（YAGNI，且对云盘等于全量下载）。
- 不动媒体库的海报/背景图链路（已由 `MediaImages` 覆盖，那套是刮削产物的代理，不是文件缩略图）。

---

## 3. 对外契约

### 3.1 端点

```
GET /p/{path}?type=thumb&w=320&h=320&at=3&fmt=webp&q=80&sign=...
HEAD /p/{path}?type=thumb&w=320&h=320   → 200 + 头信息，不生成
```

| 参数 | 缺省 | 约束 | 说明 |
|---|---|---|---|
| `w` / `h` | 320 / 320 | 16–1024，超范围按边界夹取 | `cover` 语义（等比填满后居中裁剪），不做留白 |
| `at` | 设置项 `thumb_video_position` | 数字=秒；`%` 结尾=时长百分比 | 仅视频 |
| `fmt` | `auto` | `auto\|webp\|jpeg\|png` | `auto` 按 `Accept` 协商，优先 webp |
| `q` | 80 | 1–100 | 编码质量 |
| `type` | 空 | `thumb` 以外一律走原下载逻辑 | **向后兼容的关键**：不传 `type` 时行为与今天 100% 一致 |

`type=thumb` 之外的 query 一律忽略，保证旧客户端、旧播放器不受影响。

### 3.2 响应

| 情况 | 状态 | 头 |
|---|---|---|
| 生成/命中 | 200 | `Content-Type`、`ETag: "v1-<key前16>"`、`Cache-Control: public, max-age=604800`、`X-Thumb-Cache: hit\|generated` |
| 文件不存在 / 无权 | 404 | 无体（**不返回 403**，与 `MediaImages` 同一口径：403 等于确认路径存在） |
| 类型不支持（txt/zip/svg…） | 415 | `X-Thumb-Reason: unsupported` |
| 生成失败（解码错、源不可随机读、超限） | 404 | `X-Thumb-Reason: no-seekable-source \| decode-failed \| too-large` |
| 并发饱和 | 503 | `Retry-After: 1` |

`X-Thumb-Cache` / `X-Thumb-Reason` 是给 E2E 与排障用的**可枚举诊断值**，不含路径与用户信息，因此不构成信息泄露。

`HEAD` 只回 200/404/415，不触发生成 —— 与 `StreamViaProxyAsync` 对 HEAD 的既有约定一致（`:118`）。

### 3.3 `Obj.Thumb` 下发

`ToObj`（`FileSystemService.cs:973`）改为在以下条件全部成立时填充：

1. 全局 `thumb_enable = true`；
2. 条目是文件，且扩展名落在 `{image, audio, pdf, video}` 可生成集合；
3. 文件大小 ≤ `thumb_max_source_mb`（图片/音频/PDF 默认 64MB，视频默认 8GB —— 视频不整读，大小只影响是否值得试）；
4. 所属驱动若声明了 `thumbnail` 配置项，则该驱动自身也开启；
5. 路径不是 SVG（见 §12）。

值形如 `{baseUrl}/p/{encoded}?type=thumb&w=320&h=320`，需签名时追加 `&sign=`。

`FsGetResp : Obj`，所以 `GetAsync` 只要复用同一段填充逻辑即可顺带带上 `thumb`；今天它逐字段搬运（`FileSystemService.cs:227-237`），需要补一行 `resp.Thumb = obj.Thumb`。

> 注意：`ListAsync` 会给**每个**条目生成一次 HMAC 签名。`ToObj` 本来就在签名链路内（`Sign` 字段），所以这是既有成本，不是新增成本。

### 3.4 上游兼容

Go 上游是 `GET /p/{path}?type=thumb`（挂载在 `down` 路由上）。本方案故意沿用同名参数与同一挂载点，原因不是"好看"，而是：签名策略（`ISignService.GetContextAsync`）、匿名放行、限流前缀、`/p/` 的 HEAD 语义全部现成；换一个新前缀就要把这四套逻辑各复制一遍，并且和前端 `downloadLink()` 的拼链方式分叉。

---

## 4. 模块划分

```
src/OpenList.Thumbnails/            ← 新项目 (net10.0, 无 ASP.NET 依赖)
  ThumbnailOptions.cs               全局选项（由设置项映射而来，见 §10）
  ThumbnailRequest.cs               规范化后的请求（路径、entry、kind、w/h/at/fmt/q）
  ThumbKind.cs                      Image | Video | Audio | Pdf
  IThumbnailGenerator.cs            单个类型的生成器契约
  ISeekableSource.cs                随机读抽象
  SeekableSources.cs                StreamSeekableSource / RangeReaderSeekableSource / 工厂
  ThumbnailCache.cs                 磁盘+内存两级缓存、单飞、负缓存、清理
  ThumbnailService.cs               编排：判定 kind → 查缓存 → 选生成器 → 写缓存
  Generators/ImageThumbnailGenerator.cs
  Generators/VideoThumbnailGenerator.cs
  Generators/AudioThumbnailGenerator.cs
  Generators/PdfThumbnailGenerator.cs
  Skia/SkiaCodec.cs                 解码/缩放/编码/格式协商（唯一碰 native 的地方）
```

`OpenList.Server` 增加 `ProjectReference`（`Program.cs` 注册 `IThumbnailService`）；
`SignEndpoints` 在 `DownloadAsync` 之前分流；`FileSystemService` 只依赖 `IThumbnailUrlBuilder`
（一个 20 行的接口，避免 Infrastructure 反向依赖解码程序集）。

这样 `OpenList.Infrastructure` 不需要引用 `OpenList.Thumbnails`，包图保持单向：
`Server → Thumbnails`、`Server → Infrastructure`、`Thumbnails → Core(接口) + Vfs`。

---

## 5. 解码后端选型

| 需求 | 候选 | 许可 | 体积（每 RID） | 判断 |
|---|---|---|---|---|
| 图片缩放/编码 | **SkiaSharp 3.x** | MIT | ~9MB native | ✅ 采用 |
| 同上 | ImageSharp 3.x | SixLabors Split（商用付费） | 纯托管 ~5MB | ❌ 许可风险，且不支持 PDF |
| 视频首帧 | **Sdcb.FFmpeg 7.0.0 + `Sdcb.FFmpeg.runtime.{win-x64,linux-x64,linux-arm64,osx-arm64}`** | 绑定 MIT / FFmpeg LGPL-2.1+ | ~45MB native | ✅ 采用 |
| 视频首帧 | FFMpegCore / Xabe.FFmpeg | MIT | 0（但要求用户机上有 ffmpeg 可执行文件） | ❌ 与"零外部依赖"冲突 |
| 视频首帧 | FFmpeg.AutoGen | LGPL | 0（要求系统 libav*） | ❌ 同上，Docker 还得额外装 |
| 音频封面 | **ATL** | MIT | 纯托管 | ✅ 采用 |
| 音频封面 | TagLibSharp | LGPL-2.1 | 纯托管 | 可用，但许可不如 MIT 干脆；ATL 格式覆盖足够 |
| PDF 首页 | **PDFtoImage 5.4.0** | MIT（内含 PDFium，BSD） | +~8MB | ✅ 采用（复用 SkiaSharp） |

**许可证要写进 NOTICE**：FFmpeg 是 LGPL-2.1+ 的**动态链接**使用。项目若以 MIT 发布，需要在分发物里附 LGPL 文本与"可替换库"的说明（Docker 镜像与桌面 bundle 都算分发）。这是采纳 Sdcb 方案的必要代价，必须提前确认，不能等发版才发现。

**Linux 运行时依赖**（易踩）：SkiaSharp 在 Linux 上需要 `libfontconfig1`（即便只做缩放，native 初始化就会探测字体），PDFium 需要 `libstdc++` / `libc++`。`Dockerfile` 的 runtime 阶段当前只装了 `libicu-dev`，必须补：

```dockerfile
RUN apt-get update && apt-get install -y --no-install-recommends \
      libicu-dev libfontconfig1 libstdc++6 \
 && rm -rf /var/lib/apt/lists/*
```

**打包影响**：`build/build.cs` 的 `Publish` 目标走 `dotnet publish -r <rid>`，native 资产会随 RID 自动带入，但**桌面 bundle 体积会从 ~70MB 涨到 ~130MB**；`artifacts/` 里 multi-RID 全量发布的时间也会变长。若不可接受，退路是「桌面版不带视频缩略图」（条件编译 `THUMB_VIDEO`），但那样两套产物行为不一致，不推荐。

---

## 6. 生成管线

### 6.1 类型判定（零 IO）

按扩展名与 `Obj.Type` 同一套表（`FileSystemService.cs:999-1006`），额外加：

- `pdf` → `ThumbKind.Pdf`
- `svg` → **不生成**（见 §12）
- 其余未知 → 415，且不产生负缓存（避免为每个 txt 写一条）

### 6.2 图片

```
ReadFileAsync(path, thumb_max_source_mb, user)   // 整读，超限即拒
  → SkiaCodec.Decode(bytes)                       // 源图 > 100MP 直接放弃（防解码炸弹）
  → CoverCrop(w, h)                               // 等比填满 + 居中裁剪
  → Encode(fmt, q)                                // webp 优先；编码失败降级 jpeg
```

**为什么整读而不是流式**：JPEG 的 EXIF 方向、渐进式解码都要完整数据；再叠加 `ReadFileAsync` 已有的 `maxBytes` 上限，内存与安全边界都是现成的。缩略图源图 64MB 上限对应 8K 图绰绰有余。

**EXIF 方向**：SkiaSharp 的 `SKCodec.EncodedOrigin` 需要显式应用，否则手机竖拍的照片在卡片里全部躺倒。用 `SKBitmap` 旋转 90/180/270 后进裁剪。

### 6.3 音频封面

```
ISeekableSource(source) ─┬─ 源 ≤ 32MB → 整读进内存，ATL 解析
                         └─ 源 > 32MB → 直接用 seekable 流喂 ATL
  → ATL: track.EmbeddedPictures[0].PictureData
  → 无封面 → 404(no-cover)，同时负缓存
  → 有封面 → SkiaCodec.Decode → CoverCrop → Encode
```

封面通常几十到几百 KB，ATL 需要 seek 才能定位 FLAC 的 PICTURE 块与 MP4 的 `moov/covr`，所以走 `ISeekableSource`。**不解析 ID3 里非图片的 APIC 变体**（如 `image/` 之外的 payload），ATL 已做过滤。

### 6.4 PDF 首页

```
ISeekableSource → ReadOnlySeekableStream 包装（Seek 走随机读）
  → PDFtoImage.Conversion.SavePng(stream, page: 0, options: { Dpi = 96, WithAnnotations=false })
  → SkiaCodec.CoverCrop
```

限制：单页渲染 DPI ≤ 150、PDF 页数 > 500 时仅渲染首页（PDFium 打开成本与页数关系不大，但这是防畸形的兜底）。加密 PDF（需要密码）→ `decode-failed`，不做密码尝试。

### 6.5 视频首帧 —— 本方案的技术核心

**问题**：FFmpeg 必须能 seek（`moov` 常在文件尾，`-ss` 定位要随机读）。而本项目对云盘的读取通道是**不可 Seek 的顺序流**。

**方案**：`ISeekableSource` 双实现，按驱动能力择优。

```csharp
public interface ISeekableSource : IAsyncDisposable
{
    long Length { get; }
    /// 读取 [offset, offset+buffer.Length)；返回实际字节数，0 表示 EOF。允许返回少于请求量。
    ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct);
}
```

| 实现 | 适用 | 做法 |
|---|---|---|
| `StreamSeekableSource` | 本地盘 / SMB / FTP / S3（`OpenFile` 的流 `CanSeek`） | 单流 + `SemaphoreSlim(1)` 串行化 `Position` 设置与读取 |
| `RangeReaderSeekableSource` | 云盘（阿里云盘等实现了 `IFileSystemLinkProvider`） | 每次 `ReadAsync` 调 `RangeReader(offset, len)`；内部做 **64KB 对齐预读窗口**（一次 HTTP 拿 256KB，覆盖后续小读），并按 offset 单调性判断是否顺读、顺读时复用同一请求流 |
| 无实现 | 驱动既不可 Seek 又无 RangeReader | 返回 `null` → 视频 404 `no-seekable-source`，**不尝试整片下载** |

流量护栏（必须，否则一个「按 8K 视频 50% 位置抽帧」的请求能把云盘配额打爆）：

- 单次生成会话读取总量硬上限 `thumb_video_read_budget_mb`（默认 64MB），超限放弃；
- 预读窗口滑动上限 4MB（避免退化为顺序读全片）；
- 会话内 seek 次数上限 64（FFmpeg 对畸形文件会疯狂 seek）。

**FFmpeg 接线**：用 Sdcb.FFmpeg 的 `IOContext` 自定义 `read`/`seek` 回调（`avio_alloc_context` 的托管包装），回调内部把 `ReadAsync(...).GetAwaiter().GetResult()` 跑在 `Task.Run` 出来的工作线程上 —— sync-over-async 在这里是必须的（FFmpeg 回调是同步 C ABI），所以整个生成过程**不能在 ASP.NET 请求线程上同步阻塞**，见 §9。

伪代码（具体签名以 Sdcb.FFmpeg 7.x 实测为准）：

```csharp
using var src = seekable;
using var io = IOContext.Create(
    read: (buf, size) => src.ReadAsync(srcPos, buf, ct).GetAwaiter().GetResult(),
    seek: (offset, whence) => { srcPos = Resolve(offset, whence); return srcPos; });
using var demuxer = MediaDemuxer.Open(io, null, null, ...);
var (streamIndex, ts) = PickStreamAndTimestamp(demuxer, at);   // 优先视频流
demuxer.Seek(ts, streamIndex, SeekFlags.Backward);
using var frame = demuxer.ReadFrameToBitmap(streamIndex, ct);  // 首帧有效画面
→ SkiaCodec.FromBitmap(frame) → CoverCrop → Encode
```

失败的常见原因与处置：无视频流（纯音频 mp4）→ 415；编码不支持（AV1/HEVC 视 runtime 包编译选项）→ `decode-failed` 并负缓存 30 分钟（**比 5 分钟长**，因为这类失败是文件级的确定失败，重试无意义）。

---

## 7. 缓存设计

### 7.1 缓存键

```
key = SHA1( $"v1|{path}|{size}|{mtimeUtcTicks}|{w}x{h}|{fmt}|{q}|{atKey}|{generatorVersion}" )
文件 = {thumb_cache_folder}/{key[0..2]}/{key}.{ext}
```

- **必须含 `size` 与 `mtime`**：这是唯一可靠的失效信号。远端文件的 `mtime` 会随覆盖上传变化，`size` 兜住"mtime 未变但内容变了"的异常盘。
- **必须含 `generatorVersion`**（代码常量，改生成算法时 +1）：否则升级后老缓存会一直命中。
- `atKey` 对视频是规范化后的秒数（`10%` → 实际秒），对其它类型为空。
- 目录用前两位分桶，避免单目录几十万文件导致 `readdir` 退化（ext4/NTFS 在 10 万级就会明显变慢）。

### 7.2 目录与容量

- 默认 `{dataDir}/thumbnails`，`dataDir` 的解析与 `Program.cs:235`（`DBFile` 所在目录）一致，桌面版自动落在 `DataDir` 设置下。
- 设置项 `thumb_cache_folder` 可覆盖；**LocalDriver 里那个同名死配置应删掉**（否则管理端出现两个语义相同、互不生效的输入框），改为"驱动级 `thumbnail` 开关保留、缓存目录只认全局"。
- 容量上限 `thumb_cache_max_mb`（默认 2048）。超限时按 `LastAccessTimeUtc` 从老到新淘汰。

### 7.3 两级查找

1. **内存 LRU**（`MemoryCache`，默认 256 项 / 32MB）：只存"键 → 字节"，给翻页回看、刷新列表这类高频重复请求省一次磁盘读。
2. **磁盘**：命中后回填内存，并 `File.SetLastAccessTimeUtc`（Windows 需先关掉 NTFS 的最后访问时间禁用策略才有效，因此**淘汰算法不能只依赖 atime**：写盘时把"最近使用时间"记在内存字典里，进程重启则退化为按 mtime 淘汰）。

### 7.4 单飞（single-flight）与负缓存

- 同一个 key 的并发生成只跑一份：`ConcurrentDictionary<string, Lazy<Task<byte[]>>>`，完成后移除。**没有这一条，一屏 40 张图会触发 40 次相同的解码**（浏览器对同一 URL 会去重，但不同样式的 `srcset` 不会）。
- 负缓存：`ConcurrentDictionary<key, (DateTimeOffset ExpireAt, string Reason)>`，TTL 5 分钟（`decode-failed` 30 分钟），**只在内存里**，不落盘 —— 落盘会让"临时网络抖动"永久性地钉死一个文件。

### 7.5 清理任务

注册到既有 `ITaskService`（后台任务），每天一次；或最简实现：`IHostedService` 里 `PeriodicTimer(24h)`。行为：容量超限淘汰 + 删除 30 天未使用项。**不做**：文件删除时同步清缓存（要遍历所有 key，得不偿失，靠容量淘汰即可）。

---

## 8. 并发、超时、限流、可观测

| 维度 | 策略 |
|---|---|
| 全局并发 | `SemaphoreSlim(max(2, ProcessorCount))`，等待超 2s 直接 503（宁可不给图，也不能把请求线程池吃掉） |
| 分类型并发 | 视频单独 `SemaphoreSlim(2)`：一次抽帧吃满一个核且可能持续几十秒 |
| 超时 | 图片 5s、音频 5s、PDF 10s、视频 30s（`CancellationTokenSource.CreateLinkedTokenSource(ct, timeout)`） |
| 限流 | 无需改动：`/p/` 前缀已被 `RateLimitMiddleware` 覆盖，缩略图响应几十 KB，限速无感 |
| 日志 | 只在 `Warning` 及以上记：`thumb failed: path=… kind=… reason=… elapsed=…`；成功路径**不记日志**（一屏 40 张会把日志打爆） |
| 指标（可选） | 复用 `ITaskService` 或在 `/api/admin/system/info` 加 `thumb_cache_hit_rate`。非必须，P4 再做 |

**线程模型**（必须写清楚，否则实现时一定会踩）：解码与抽帧一律 `Task.Run` 到线程池，绝不在请求线程上同步等待 FFmpeg 回调；`ReadAsync(...).GetAwaiter().GetResult()` 只允许出现在 FFmpeg 回调内部（工作线程上），且外层已有 `ct` 与超时兜底。

---

## 9. 设置项

> 实际落地的键与下表有两处差异（原因见 §0.1）：`thumb_video_read_budget_mb` 未实现
> （护栏改由「进程 40s 超时 + 列表不下发视频」承担），并新增 `thumb_ffmpeg_path`
> （等价于 Jellyfin 的 `<EncoderAppPath>`）。表中 `thumb_cache_folder` 现在**只认全局**，
> `LocalDriver` 里那个同名死配置已删除。

新增一组 `Group 14: THUMB`（前端 `SettingGroups` 加一行，`lang/{zh,en}/settings.json` 补文案）。

| key | 类型 | flag | 默认 | 说明 |
|---|---|---|---|---|
| `thumb_enable` | Bool | Public | `false` | 总开关；关时 `Obj.Thumb` 恒空、端点 415 |
| `thumb_max_size` | Number | Public | `320` | 下发 `Obj.Thumb` 时拼的边长（前端卡片尺寸变了只改这里） |
| `thumb_quality` | Number | Public | `80` | 编码质量 |
| `thumb_types` | String | Public | `jpg,jpeg,png,webp,bmp,gif,avif,mp4,mkv,mov,webm,mp3,flac,m4a,ogg,pdf` | 允许生成的扩展名白名单，与 `image_types` 等分开（预览类型 ≠ 缩略图类型） |
| `thumb_cache_folder` | String | Private | `""`（= `{dataDir}/thumbnails`） | 缓存目录 |
| `thumb_cache_max_mb` | Number | Private | `2048` | 容量上限 |
| `thumb_max_source_mb` | Number | Private | `64` | 整读类（图片/音频/PDF）源文件上限 |
| `thumb_video_position` | String | Public | `10%` | 抽帧位置（秒数或百分比），对齐上游 `video_thumb_pos` 语义 |
| `thumb_video_enable` | Bool | Public | `true` | 视频子开关（想让 Docker 镜像瘦身的人可以关掉，此时不加载 FFmpeg native） |
| `thumb_video_read_budget_mb` | Number | Private | `64` | 单次抽帧的远端读取预算 |
| `thumb_concurrency` | Number | Private | `0`（自动） | 全局并发上限 |

`thumb_enable` 用 `Public` 而不是 `Private`：前端要据此决定"是否给 `<img>` 拼 `type=thumb`"（虽然 `Obj.Thumb` 已经带了信号，但公开设置能让前端在首屏渲染前就选对策略，避免一次 404 再回退）。

**驱动级开关**：`LocalDriver` 的 `thumbnail` 保留并**真正接上**（读 `Addition.Thumbnail`）；`thumb_cache_folder` 从 `ConfigSchema` 与 `LocalAddition` 中删除。其它驱动暂不加该字段 —— 有全局开关 + 白名单已经够用，per-driver 复制一份只会让"为什么这个盘不出图"变成两个地方排查。

---

## 10. 前端接入

| 位置 | 改动 |
|---|---|
| `GridView.tsx:47` `thumbSrc` | 已优先用 `obj.thumb`，**无需重构**；只要后端下发就自动生效。补充 `onerror` → 切换成图标（远端临时失败不应留裂图） |
| `GridView.tsx` `<img>` | 加 `srcset`（`w=320 1x, w=480 2x`）+ `sizes`，高 DPI 屏不糊 |
| 列表行（`ListRows`） | 新增 40×40 缩略图槽位（`obj.thumb` 为空则图标）。**行高固定 40px**，否则图片加载会让列表跳动 |
| `AudioPreview.tsx:36` | 已用 `props.obj.thumb`，自动生效；补 `onerror` 隐藏 |
| 详情/播放页（可选） | 用 `w=640` 作视频占位图，避免黑屏 |
| 设置页 | 无需改渲染逻辑（后端下发即渲染），只补 `SettingGroups` 分组与 i18n 文案 |
| `Obj` 类型 | `thumb: string` 已存在（`api/types.ts:29`），无改动 |

懒加载：`loading="lazy"` 已有；再叠一个 `IntersectionObserver` 是过度设计 —— 浏览器原生 lazy 对 `<img>` 已经足够，且不必自己维护哨兵元素。

---

## 11. 安全

| 风险 | 处置 |
|---|---|
| SVG 缩略图 = 存储型 XSS 载体 | **一律不对 svg 生成缩略图**（返回 415）。SkiaSharp 不解 SVG，天然规避；但要在白名单里显式排除，防止有人"顺手加个 svg 支持" |
| 路径穿越 | 全部走 `UPath` + `MountFileSystem`（既有校验）；`thumb_cache_folder` 若被配成站点静态目录也安全，因为文件名是 SHA1 |
| 越权读取 | 复用 `SignEndpoints.IsAllowedAsync`：`sign_all` 开启时同样要求 `?sign=`。**不要**给缩略图单独开一条"永远放行"的捷径 |
| 解码炸弹（1 亿像素 PNG） | 先读 `SKCodec.Info`，`width*height > 100_000_000` 直接放弃；`thumb_max_source_mb` 兜住体积 |
| CPU 放大攻击（`w=1` 解码 8K） | w/h 上限 1024 + 全局并发信号量 + 503 快速失败 |
| 缓存投毒 | 键含 path+size+mtime，攻击者无法让别人读到别人的图（路径无关的键不共享） |
| 磁盘打满 | `thumb_cache_max_mb` 硬上限 + 淘汰任务；`thumb_enable=false` 时不写盘 |

---

## 12. 测试计划

**单元（`tests/OpenList.Tests/ThumbnailTests.cs`，TUnit）**

1. 缓存键：`(path, size, mtime, w, h, fmt, q, at, version)` 任一变化 → 键不同；同输入稳定。
2. 图片生成：10×10 PNG → 320×320 webp，`SKCodec` 复核边长与格式；`fmt=jpeg` 时 `Content-Type` 为 `image/jpeg`。
3. EXIF 方向：带 `Orientation=6` 的 JPEG → 输出宽高互换。
4. 不支持类型（txt/svg/zip）→ 415，且**不写负缓存**。
5. 负缓存：伪造 source 计数器，两次调用同一失败 key → 只解码一次。
6. 单飞：20 个并发同 key → 生成器只被调用 1 次。
7. 视频源不可 Seek 且无 RangeReader → `no-seekable-source`，且**零字节下载**（用 teapot 计数器断言）。
8. 超限：源文件 > `thumb_max_source_mb` → `too-large`，不读内容。
9. 读取预算：伪造 RangeReader 累计字节 > budget → 中止（断言抛出的异常类型）。

**E2E（`frontend/scripts/verify-thumb.mjs`，新增一轮 `thumb`）—— 已落地，见 §0.2（实际 28 条断言，下列 9 条为原始清单）**

-fixture 用 `gen_fixtures.py` 现成的图片/音频/PDF；视频用 `ffmpeg` 造 3s 的 `testsrc`（回归脚本已把 ffmpeg 放进 PATH）。

1. 开启 `thumb_enable` 后 `fs/list` 的图片条目 `thumb` 非空；关闭时为空（`POST /api/admin/setting/save`）。
2. `GET {thumb}` → 200、`Content-Type: image/webp`、`X-Thumb-Cache: generated`；再来一次 → `hit`。
3. 改了文件（重写内容 + bump mtime）→ 同 URL 返回新图（`hit` 变 `generated`，或直接用返回字节的 hash 比对）。
4. txt → 415 + `X-Thumb-Reason: unsupported`。
5. `w=9999` → 200 且实际边长 ≤ 1024。
6. 音频 mp3 带封面图 → 200；无封面 → 404 `no-cover`。
7. PDF → 200（首页）。
8. `type=thumb` 缺省行为不变：不传 `type` 时仍是原文件字节（回归保护，**这条最重要**）。
9. 网格页截图断言：`<img>` 的 `src` 含 `type=thumb`（视觉回归用 `verify-v2` 的路子，不做像素比对）。

接入 `run-regression.sh`：新增 `round_thumb`，排在 `round_media` 之后（不依赖媒体库，但会自建 `/e2e` 挂载）。注意 E2E 里要 `localStorage` 不切语言、`locale: "en-US"`（既有约定）。

---

## 13. 性能预算（用于验收）

| 场景 | 目标 |
|---|---|
| 40 张 12MP JPEG 的网格首屏（本地盘，冷缓存） | ≤ 4s（并发 8，单张 ~80ms） |
| 同上，命中磁盘缓存 | ≤ 400ms（40×10ms） |
| 一屏 40 个 txt | 零生成、零负缓存，全部 415（≤ 50ms） |
| 云盘 2GB 视频抽首帧 | 远端读取 ≤ 64MB，耗时 ≤ 30s 或明确失败 |
| 缓存目录 2000 张 320px webp | ≤ 60MB |

---

## 14. 实施阶段

| 阶段 | 交付 | 验收 |
|---|---|---|
| **P0 骨架 + 图片** | 新项目、`ISeekableSource`（仅 Stream 实现）、`ThumbnailCache`、`/p?type=thumb` 分流、`Obj.Thumb` 下发、设置项（`thumb_enable/max_size/quality/types/cache_*`）、前端网格 `<img>`+srcset、`verify-thumb.mjs` 前 5 条 | 单元 1/2/3/4/5/6 + E2E 1–5、8、9 |
| **P1 音频封面** | ATL + `CoverCrop` | 单元 + E2E 6 |
| **P2 PDF** | PDFtoImage + `ReadOnlySeekableStream` | E2E 7 |
| **P3 视频** | `RangeReaderSeekableSource`、Sdcb.FFmpeg 接线、预算护栏、`Dockerfile` 依赖、`build.cs` RID 包 | 单元 7/8/9 + 手工验阿里云盘 |
| **P4 收尾** | 清理任务、列表行 40px 缩略图（§0.2 已完成）、详情页占位图、指标、`AGENTS.md` 与两份 docs 的遗留项标记为已完成 | 全量回归 15 轮 + 新增 1 轮（`I/thumb` 已落地，现为 16 轮） |

**P0 就能独立上线**（图片是收益最大的那一档：网格与图墙覆盖了绝大多数浏览场景），后面每阶段都可单独回滚（关 `thumb_video_enable` → 关 `thumb_enable`）。

---

## 15. 风险清单

| 风险 | 影响 | 缓解 |
|---|---|---|
| FFmpeg 的 LGPL 合规 | 发版阻塞 | 现在就确认许可证策略；NOTICE + 可替换库说明；实在不行退化为「视频缩略图走用户自备 ffmpeg」 |
| native 体积（桌面 +60MB） | 分发体验 | 桌面版可 `thumb_video_enable=false` 且构建时不带 FFmpeg runtime（`<ExcludeAssets>`），代价是两套产物行为不同 —— 需产品决策 |
| Docker 缺 `libfontconfig1` | 镜像里 SkiaSharp 直接崩 | `Dockerfile` runtime 阶段补包，并在 CI 里加一条冒烟（生成一张图） |
| 云盘随机读放大流量 | 用户配额被打爆 | `thumb_video_read_budget_mb` + 预读窗口 + 只有「用户点开视频详情」才请求（列表页不对视频下发 `thumb`，见下） |
| 列表页对视频下发 `thumb` 会让浏览器立刻抽帧 | 一个 20 个视频的目录 = 20 次远端抽帧 | **列表/网格对视频不下发 `thumb`**（只对图片/音频/PDF 下发）；视频缩略图只在详情页显式请求。这条要写进 `ToObj` 的条件里 |
| `ToObj` 增加逻辑影响 list 性能 | 列表变慢 | 判定全部是纯内存操作（扩展名 + size + 设置快照），禁止任何 IO；设置项按请求读一次快照，不逐条查库 |
| 缓存目录被配成网络盘 | 写盘极慢 | 文档里注明「必须本地盘」；首次写入失败时降级为"内存缓存 + 每次重生成"并记 Warning |

---

## 16. 待决问题（需要拍板）

1. **LGPL 是否可接受**？（决定视频缩略图走 Sdcb 内嵌，还是改为"有 ffmpeg 才启用"。用户已选"内嵌 NuGet"，但许可证这一层仍需确认发布策略。）
2. 桌面版是否接受 +60MB？（若否，`thumb_video_enable` 在桌面默认 false。）
3. `thumb_enable` 的默认值是 `false`（保守，升级零影响）还是 `true`（开箱即用，但升级瞬间可能出现一批生成请求）？我倾向 `false`，并在这里写明"这是刻意的"。
4. 网格卡片尺寸是否就按 320 定稿？（决定 `thumb_max_size` 默认值与 `srcset` 断点。）
