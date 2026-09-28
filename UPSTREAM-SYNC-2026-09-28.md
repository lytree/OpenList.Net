# OpenList 上游同步对比 & 补全报告

- **日期**: 2026-09-28
- **上游仓库**: https://github.com/OpenListTeam/OpenList （本地 `F:/Code/Dotnet/OpenList`）
- **本轮结论**: **上游 `main` 自上次同步以来没有新提交** —— HEAD 仍是 `54ae9d74`（2026-09-24 19:25 +0800，`fix(internal/db/searchnode)`），与上轮报告（`UPSTREAM-SYNC-2026-09-24.md`）的区间终点完全一致。
- **因此本轮重点**: 用上游代码重新对账 .NET 实现，把上轮识别出的缺口**实际补上**。

---

## 一、拉取过程（网络受限的绕行记录）

`github.com:443` 在当前环境不可达（`Connection was reset` / 直连超时），本地代理也无转发。经测试可用通道：

| 通道 | 结果 | 用途 |
|---|---|---|
| `api.github.com` | ✅ 200 | 查询 `main` HEAD、分支清单、提交时间 |
| `codeload.github.com` | ✅ 200 但仅 ~11 KB/s | 未采用（太慢） |
| `gitee.com/OpenListTeam/OpenList` | ✅ 200，17s 拉完 | 取到单提交快照，用于**内容**比对 |

验证结论：

1. `api.github.com` 的 `commits/main` 返回 `54ae9d74`，日期 `2026-09-24T11:25:29Z`；
2. gitee 快照（`204365b0`，同步于 11:32:46Z）与本地 `54ae9d74` 做 `git diff` → **无任何文件差异**；
3. 其余分支均早于 `main`（`next` 2025-08、`dev` 2025-12、`feat/s3-multipart` 2026-08-29、`feat/advanced-transfer-seeds` 2026-09-22）。

→ 本地上游仓库已是最新，无需再拉。

## 二、对账发现的缺口

| # | 缺口 | 性质 | 本轮处理 |
|---|---|---|---|
| 1 | `/d/` 注释承诺「驱动实现 `IFileSystemLinkProvider` 时 302」，代码实际总是流式传输 | 注释与实现不符 | ✅ 已实现 |
| 2 | `/p/` 代理下载未接驱动；已写好的 `CallbackRangeReader`（372 行，含测试）**没有任何生产调用方** | 基础设施闲置 | ✅ 已接入 |
| 3 | `AliyundriveOpen` 是骨架：`InitAsync` 不设 `System`，`Link`/`Put`/`RefreshToken` 全抛 `NotImplementedException` | 最大功能缺口 | ✅ 已移植核心 API |
| 4 | 阿里云「按用户共享的三档配额」（Go `limiter.go`）未移植，`CloudDriverBase` 只有单一 `limit_rate` | 配额正确性 | ✅ 已移植 |
| 5 | 其余 80+ 云驱动未移植 | 体量问题 | 未处理（见第五节建议） |

## 三、本轮补全内容

### 3.1 下载通道（打通 A/B 两处架构缺口）

| 文件 | 变更 |
|---|---|
| `Core/Drivers/IFileSystemDriverExtensions.cs` | 新增 `FileLink`（`Url` / `RangeReader` / `Expiration`）与 `ProxyRangeReader` 委托；`IFileSystemLinkProvider` 增加 `GetFileLinkAsync(path, proxy, ct)`，**默认实现返回 null** |
| `Server/Endpoints/FileDownloadHelper.cs` | 新增 `StreamViaProxyAsync`：按请求 Range 调驱动读端流式转发，`CallbackCapacityException` → **503 + Retry-After**，`HEAD` 只写响应头不触发读端 |
| `Server/Endpoints/SignEndpoints.cs` | 新增共用决策 `DownloadAsync`；`/d/`（含 HEAD）驱动有直链则 **302**，`/p/`（含 HEAD）驱动有区间读端则走代理，两者都回退通用 Zio 流式 |

**默认实现返回 null 是刻意的**：S3/WebDAV 的 `GetLinkAsync` 返回的是**未签名**地址，直接 302 会把原本可用的下载变成 403。只有显式实现 `GetFileLinkAsync` 的驱动（云盘驱动）才进入该通道 —— 现有 S3/WebDAV 行为完全不变。

### 3.2 阿里云盘驱动（骨架 → 可用）

新增 / 重写 5 个文件：

| 文件 | 内容 |
|---|---|
| `Cloud/AliyunLimiterRegistry.cs`（新，~180 行） | 按 user 共享的三档限流（list 3.9 / link 0.9 / other 14.9 req/s），引用计数 + 归零回收，`""` 为不回收的全局槽位（对齐 Go `globalLimiterUserID`） |
| `Cloud/AliyundriveOpen/AliyundriveOpenModels.cs`（新） | DTO：`AliyunFile` / `AliyunFiles` / `AliyunCreateResp` / `AliyunPartInfo` / `AliyunMoveOrCopyResp`，含 `EffectiveName`（`name` 空时回退 `file_name`，对齐 Go `fileToObj`） |
| `Cloud/AliyundriveOpen/AliyundriveOpenFileSystem.cs`（新，~270 行） | Zio 视图：路径 ↔ `file_id` 逐级解析 + 缓存、列目录、增删改移拷贝、元数据；写模式明确不支持（上传统一走 `IFileSystemUploader`） |
| `Cloud/AliyundriveOpen/AliyundriveOpenDriver.cs`（重写，~900 行） | 两阶段初始化、令牌刷新（在线续期 / 本地 OAuth + JWT `sub` 校验）、列目录翻页、直链与代理下载、CRUD、上传（秒传 + 分片 + complete）、请求封装含「令牌失效 → 刷新 → 只重试一次」 |
| `Cloud/HttpContentStream.cs`（新） | 直链下载的响应流包装（释放时一并释放 `HttpResponseMessage`） |

几个实现要点：

- **两阶段限流**：首个 `getDriveInfo` 只能落在全局槽位（此时不知道 `user_id`），拿到后立刻切换 —— 同账号多挂载点因此共享同一套配额。
- **`proof_code`**：MD5(access_token) 前 16 个 hex 字符对文件大小取模定位，取 8 字节 base64（对齐 Go `calProofCode`）。
- **不可 seek 的上传流**先落盘成临时文件（`FileOptions.DeleteOnClose`），因为秒传与分片都需要重读内容 —— Go 的 `FileStreamer` 原生支持 `RangeRead`，.NET 需此补偿。
- 未移植：Go 的 `InitReference`（同账号多挂载点共用 access token）、Move/Rename/Copy 后的同名残留清理。

### 3.3 顺手修掉的既有编译错误

`src/OpenList.Desktop/Services/NotificationService.cs`：类用了 `[RelayCommand]`（源生成器需要 partial）却未声明 `partial`，导致 **Desktop 项目无法编译**（与本轮改动无关，来自桌面端提交）。已修复。

## 四、验证结果

| 项目 | 结果 |
|---|---|
| `dotnet build src/OpenList.Drivers` | ✅ 0 错误 |
| `dotnet build src/OpenList.Server` | ✅ 0 警告 0 错误 |
| `dotnet build src/OpenList.Desktop --no-incremental` | ✅ 0 警告 0 错误（修复前为 1 错误） |
| `dotnet test`（全量） | ✅ **311 成功 / 1 跳过 / 0 失败**（共 312） |
| 本轮新增用例 | 22 个（`AliyundriveOpenApiTests`），全部通过 |

新测试全部通过注入的桩 `HttpMessageHandler` 拦截 HTTP，**不访问真实服务**：初始化与限流槽位切换、路径解析缓存、翻页、直链/代理通道选择、livp 回退、删除方式切换、令牌过期刷新重试、空令牌拒绝、JWT `sub` 不一致拒绝、限流注册表引用计数。

> 备注：全量构建时会报 `OpenList.Server.dll` 被占用 —— 是你的服务进程（PID 27196）正在运行，非代码问题。

## 五、建议的下一步

1. **真实账号验证**：挂一个阿里云盘存储，验证列举/播放（`/d/` 302 与 `/p/` 代理各试一次，重点看回调并发限制是否按预期生效）。
2. **补齐 `InitReference`**：同账号挂多个路径时目前各自持有 token，会让刷新次数翻倍。
3. **按体量由小到大移植下一个云驱动**：`wopan`(294 行) → `mega`(387) → `weiyun`(507) → …，`AliyunLimiterRegistry` 是三档配额驱动的现成范式。
4. **可选**：`fs/get` 的 `related` 字段（Go 侧已收窄为「仅视频文件」才返回，.NET 暂未实现该字段）。
