# OpenList.Net vs OpenList (Go) 后端差异分析

- 对比基准:**OpenList Go `main` 分支**(对应前端 v4.2.3,截至 2026-07-16) ↔ **OpenList.Net 当前**(对应前端 v4.2.2)
- Go 参考源:[server/router.go](https://github.com/OpenListTeam/OpenList/blob/main/server/router.go)、[server/handles/auth.go](https://github.com/OpenListTeam/OpenList/blob/main/server/handles/auth.go)、[server/handles/task.go](https://github.com/OpenListTeam/OpenList/blob/main/server/handles/task.go)、[server/webdav.go](https://github.com/OpenListTeam/OpenList/blob/main/server/webdav.go)、[server/s3.go](https://github.com/OpenListTeam/OpenList/blob/main/server/s3.go)、[server/mcp.go](https://github.com/OpenListTeam/OpenList/blob/main/server/mcp.go)
- .Net 入口:[Program.cs](file:///f:/Code/Dotnet/OpenList.Net/src/OpenList.Server/Program.cs)、[Endpoints/](file:///f:/Code/Dotnet/OpenList.Net/src/OpenList.Server/Endpoints/)

> **Stub 语义说明**:OpenList.Net 的所有"未实现"端点均通过 `Results.Ok(Resp.Fail(..., RespCodes.NotImplemented))` 返回 **HTTP 200**,仅在 JSON body 的 `code` 字段为 501([Resp.cs](file:///f:/Code/Dotnet/OpenList.Net/src/OpenList.Core/Models/Resp.cs))。下文所称"stub"均指此模式,而非 HTTP 501。

---

## 0. 修复进度

| 编号 | 项 | 状态 |
|---|---|---|
| P0-1 | `/api/auth/logout` 方法不匹配 (GET vs POST) | ✅ 已修复 — 改为 `MapMethods("/logout", ["GET","POST"])`([AuthEndpoints.cs](file:///f:/Code/Dotnet/OpenList.Net/src/OpenList.Server/Endpoints/AuthEndpoints.cs)) |
| P0-2 | `/api/fs/list|get` 等不支持 `Any` 方法 | 待处理 — 当前仍为固定 `MapPost/MapGet`,前端如用 `GET /api/fs/list` 会 405 |
| P0-3 | `/api/fs/list|get` 不支持 guest 访客访问 | ✅ 已修复 — `/list`、`/get`、`/dirs`、`/search` 加 `.AllowAnonymous()`,无 JWT 时 `ClaimsPrincipal.ToUser()` 生成 guest([FsEndpoints.cs](file:///f:/Code/Dotnet/OpenList.Net/src/OpenList.Server/Endpoints/FsEndpoints.cs);测试 [FsEndpointsTests.cs](file:///f:/Code/Dotnet/OpenList.Net/tests/OpenList.Tests/FsEndpointsTests.cs)) |
| P0-4 | `torrent/generate` 缺失 | ✅ 已修复 — `POST /torrent/generate` stub 已注册,返回 `code=501`([FsEndpoints.cs](file:///f:/Code/Dotnet/OpenList.Net/src/OpenList.Server/Endpoints/FsEndpoints.cs)) |
| P1-5 | `/ad`、`/ap`、`/ae` 签名归档路由 | ✅ 已修复 — 6 条路由(GET+HEAD 各 3),复用签名校验,委托 `ArchiveListAsync`/`ArchiveDecompressAsync`,驱动未实现归档接口时返回 501([SignEndpoints.cs](file:///f:/Code/Dotnet/OpenList.Net/src/OpenList.Server/Endpoints/SignEndpoints.cs);测试 [SignEndpointsTests.cs](file:///f:/Code/Dotnet/OpenList.Net/tests/OpenList.Tests/SignEndpointsTests.cs)) |
| P1-6 | `/sad/:sid` 共享归档下载 | ✅ 已修复 — `GET/HEAD /sad/{sid}[/*path]`,复用 `ISharingService.VerifyAsync`,委托 `ArchiveDecompressAsync`;HEAD 走 `/sd/` 流处理避免解压副作用([ShareDownloadEndpoints.cs](file:///f:/Code/Dotnet/OpenList.Net/src/OpenList.Server/Endpoints/ShareDownloadEndpoints.cs);测试 [ShareDownloadEndpointsTests.cs](file:///f:/Code/Dotnet/OpenList.Net/tests/OpenList.Tests/ShareDownloadEndpointsTests.cs)) |
| P1-7 | 归档驱动接口实现 | 待处理 — `IFileSystemArchiveReader`/`IFileSystemArchiveDecompress` 仍无驱动实现,P1-5/P1-6 路由返回 501 |
| P1-8 | WebDAV `LOCK`/`UNLOCK` 真实实现 | ✅ 已修复 — `IWebDavLockStore` + `InMemoryWebDavLockStore`(单例,`ConcurrentDictionary`,懒清理过期锁)。LOCK 生成 token 并持久化,重复 LOCK 返回 423;UNLOCK 校验 `Lock-Token` 头,匹配返回 204/不匹配 409/缺失 400。`PROPPATCH` 仍为 no-op(与 Go 行为一致) |
| P2-9 | 消息系统架构差异决策 | 待处理 |
| P2-10 | MCP 端点 `Any /mcp` | 待处理 |
| P2-11 | Cloud 驱动 (Phase 9) | 待处理 |

---

## 1. AGENTS.md 中已过时/错误的声称

[AGENTS.md](file:///f:/Code/Dotnet/OpenList.Net/AGENTS.md) "Not Yet Implemented" 一节存在多处与代码不符的描述,需更新:

| AGENTS.md 声称 | 实际情况 |
|---|---|
| `/api/auth/sso/*` 3 个 stub 返回 501 | ✅ **已实现** 4 个路由(`/sso`、`/sso_callback`、`/get_sso_id`、`/sso_get_token`),委托 `ISsoService`。未启用时返回 `code=403` 而非 501 |
| `/api/authn/*` 4 个 WebAuthn stub | ✅ **已实现** 6 个路由(含 `/delete_authn`、`/getcredentials`),委托 `IWebAuthnService`。未启用时返回 `code=403` |
| `/api/auth/login/ldap` 缺失 | ✅ **已注册**,委托 `JwtService.LoginLdapAsync`。LDAP Url 未配置时返回 `code=501`,配置后做真实目录认证+自动建号 |
| `/api/fs/other` 缺失 | ✅ **已实现**,委托 `IFileSystemService.OtherAsync` |
| 11 个 `/api/admin/setting/set_{tool}` 缺失 | ✅ **全部已实现**(`set_aria2/qbit/transmission/115/115_open/123_pan/123_open/pikpak/thunder/thunderx/thunder_browser`) |

---

## 2. Go 有但 .NET 完全缺失的路由

### 2.1 MCP(Model Context Protocol)端点 — 整组缺失

Go [server/mcp.go](https://github.com/OpenListTeam/OpenList/blob/main/server/mcp.go) 在 `Init()` 调用 `MCP(g)`,注册 `Any /mcp`。.NET 在 [Program.cs](file:///f:/Code/Dotnet/OpenList.Net/src/OpenList.Server/Program.cs) 无任何 `/mcp` 注册。

### 2.2 ~~签名归档路由 — 整组缺失~~ ✅ 已实现

Go `router.go`:

```
GET/HEAD /ad/*path   → ArchiveDown
GET/HEAD /ap/*path   → ArchiveProxy
GET/HEAD /ae/*path   → ArchiveInternalExtract
```

.NET [SignEndpoints.cs](file:///f:/Code/Dotnet/OpenList.Net/src/OpenList.Server/Endpoints/SignEndpoints.cs) 已注册 6 条路由(GET+HEAD 各 3),复用 `ISignService` 签名校验,委托 `IFileSystemService.ArchiveListAsync` / `ArchiveDecompressAsync`。驱动未实现 `IFileSystemArchiveReader`/`IFileSystemArchiveDecompress` 时返回 `code=501`。详见 P1-5。

### 2.3 ~~共享归档下载 — 缺失~~ ✅ 已实现

Go:

```
GET/HEAD /sad/:sid        → SharingArchiveExtract
GET/HEAD /sad/:sid/*path  → SharingArchiveExtract
```

.NET [ShareDownloadEndpoints.cs](file:///f:/Code/Dotnet/OpenList.Net/src/OpenList.Server/Endpoints/ShareDownloadEndpoints.cs) 已注册 `GET/HEAD /sad/{sid}[/*path]`,复用 `ISharingService.VerifyAsync` 校验共享码/密码/过期/禁用/访问上限,委托 `ArchiveDecompressAsync`;HEAD 复用 `/sd/` 流处理避免解压副作用。详见 P1-6。

### 2.4 ~~`/api/fs/torrent/generate` — 缺失~~ ✅ 已注册 stub

Go:`g.POST("/torrent/generate", handles.GenerateTorrentForPath)`。.NET [FsEndpoints.cs](file:///f:/Code/Dotnet/OpenList.Net/src/OpenList.Server/Endpoints/FsEndpoints.cs) 已注册 `POST /torrent/generate` stub(返回 `code=501`),与 `parse`、`upload_parse`、`rapid_upload` 三个 stub 并列。底层未实现。

### 2.5 `/api/fs/regex_rename` — 缺失

Go:`g.POST("/regex_rename", handles.FsRegexRename)`。.NET 未注册(前端使用 `/fs/batch_rename` 替代)。

### 2.6 `/admin/task` 兼容路由 — 缺失

Go 在 admin 组下也挂一份 task 路由(注释:"retain /admin/task API to ensure compatibility with legacy automation scripts")。.NET 仅在 `/api/task/...` 下注册,无 `/api/admin/task/...` 别名。

### 2.7 Debug 路由 — 缺失

Go 当 `flags.Debug || flags.Dev` 时注册 `/debug/*`。.NET 无对应。

---

## 3. HTTP 方法 / 鉴权语义不一致

### 3.1 ~~`/api/auth/logout` 方法不匹配~~ ✅ 已修复

- Go: `g.GET("/logout", handles.LogOut)`
- .NET(修复前): `MapPost("/logout")`
- .NET(修复后): `MapMethods("/logout", ["GET","POST"])`([AuthEndpoints.cs](file:///f:/Code/Dotnet/OpenList.Net/src/OpenList.Server/Endpoints/AuthEndpoints.cs))
- 测试:[AuthEndpointsTests.cs](file:///f:/Code/Dotnet/OpenList.Net/tests/OpenList.Tests/AuthEndpointsTests.cs) `Logout_Accepts_Get_And_Post`

### 3.2 `/api/auth/login/2fa` 拆分差异

- Go:无独立 `/auth/login/2fa` 路由,2FA 流程内嵌在 `POST /auth/login` 的响应中
- .NET:独立 `POST /api/auth/login/2fa` 路由

前端需确认是否按 .NET 的拆分调用。

### 3.3 ~~`/api/fs/list|get|archive/meta|archive/list` 的访客访问~~ ✅ 部分已修复

- Go 把这 4 个端点放在 `fsAndShare` 组(使用 `Auth(true)` 中间件,允许 guest),而 `Auth(false)` 的 `_fs` 组放其他写操作
- .NET [FsEndpoints.cs](file:///f:/Code/Dotnet/OpenList.Net/src/OpenList.Server/Endpoints/FsEndpoints.cs) 已为 `/list`、`/get`、`/dirs`、`/search` 加 `.AllowAnonymous()`,无 JWT 时 `ClaimsPrincipal.ToUser()` 生成 guest 用户。`/archive/meta`、`/archive/list` 仍需鉴权(归档功能底层未实现,暂不开放 guest)

### 3.4 Go 用 `Any`(任意方法),.NET 用固定方法

Go 大量使用 `g.Any(...)` 注册路由,接受任意 HTTP 方法。.NET 明确指定 `POST/GET`。若前端/第三方按 Go 习惯用 `GET /api/fs/list` 调用,在 .NET 上会 405。受影响端点:

- `/api/fs/list`、`/api/fs/get`、`/api/fs/dirs`、`/api/fs/search`、`/api/fs/other`
- `/api/fs/archive/meta`、`/api/fs/archive/list`
- `/api/public/settings`、`/api/public/offline_download_tools`、`/api/public/archive_extensions`
- `/api/share/list`、`/api/task/...` 等

### 3.5 `/api/admin/setting/set` — .NET 多出

Go 只有 `/setting/save`(POST)。.NET [AdminEndpoints.cs](file:///f:/Code/Dotnet/OpenList.Net/src/OpenList.Server/Endpoints/AdminEndpoints.cs) 额外注册了 `POST /setting/set`,Go 没有此路由。属于 .NET 的额外端点,需保留以兼容前端。

---

## 4. Torrent 路由命名错位

Go 当前的 torrent 路由名:

```
POST /torrent/parse          ← .NET: stub ✓
POST /torrent/upload_parse   ← .NET: stub ✓
POST /torrent/rapid_upload   ← .NET: stub ✓
POST /torrent/generate       ← .NET: stub ✓ (已注册,返回 code=501)
```

.NET 还额外保留了 4 个旧版命名:`GET /torrent/list`、`GET /torrent/info`、`POST /torrent/add`、`POST /torrent/cancel`(均为 stub)。前端实际调用的是 Go 的新版命名。所有 torrent 路由底层均未实现(无 BitTorrent 引擎集成)。

---

## 5. Task 路由结构对比

Go [handles/task.go](https://github.com/OpenListTeam/OpenList/blob/main/server/handles/task.go) `SetupTaskRoute` 注册 **7 个 task 类型组**:

| Go task type | .NET 是否支持 |
|---|---|
| `/upload` | ✓ |
| `/copy` | ✓ |
| `/move` | ✓ |
| `/offline_download` | ✓ |
| `/offline_download_transfer` | ⚠️ 需核实(.NET 的 OfflineDownloadService 是否拆分 transfer) |
| `/decompress` | ⚠️ 需核实(`IFileSystemArchiveDecompress` 无实现,该 task type 即使注册也无法工作) |
| `/decompress_upload` | ⚠️ 需核实 |

每个 type 组下的子路由(undone/done/info/cancel/delete/retry/cancel_some/delete_some/retry_some/clear_done/clear_succeeded/retry_failed)在 .NET 中已全部实现。

---

## 6. 消息系统(Message)架构差异 — 重大分歧

| 维度 | Go | .NET |
|---|---|---|
| 路由数 | 2 个:`POST /admin/message/get`、`POST /admin/message/send` | 7 个:`list/get/create/update/delete/send/get` |
| 实现 | `message.HttpInstance` 单例(轻量 broker,Send/Receive) | `IMessageService` + EF Core 持久化 CRUD |
| 用户侧 | 无 `/me/message/*` 路由 | 额外有 `/me/message/{unread,list,read}` 3 个路由 |
| 数据模型 | `Message{Type, Content}` 内存消息 | 完整 `Message` 实体(标题、内容、已读、收件人等) |

.NET 的消息系统比 Go **更完整**(支持持久化、未读计数、用户收件箱),但 API 表面不兼容。Go 前端若调用 `POST /admin/message/get` 期望 broker 行为,.NET 返回的是 CRUD 结果,语义不同。

---

## 7. 签名下载/共享下载

| 路由 | Go | .NET |
|---|---|---|
| `GET/HEAD /d/*path` | ✓ | ✓ |
| `GET/HEAD /p/*path` | ✓ | ✓ |
| `GET/HEAD /ad/*path` | ✓ | ✓ 已实现(P1-5) |
| `GET/HEAD /ap/*path` | ✓ | ✓ 已实现(P1-5) |
| `GET/HEAD /ae/*path` | ✓ | ✓ 已实现(P1-5) |
| `GET/HEAD /sd/:sid[/*path]` | ✓ | ✓ |
| `GET/HEAD /sad/:sid[/*path]` | ✓ | ✓ 已实现(P1-6) |

中间件链差异:Go 的 `/d/*`、`/p/*` 经 `PathParse → sign.Verify → DownloadRateLimiter`,归档系列用 `sign.VerifyArchive`。.NET 的签名校验逻辑在 [SignService.cs](file:///f:/Code/Dotnet/OpenList.Net/src/OpenList.Infrastructure/Services/SignService.cs) 中实现,需核实是否区分 `Verify` vs `VerifyArchive`。

---

## 8. S3 / WebDAV 网关

### 8.1 S3

- Go [server/s3.go](https://github.com/OpenListTeam/OpenList/blob/main/server/s3.go):用 `g.Any("/*path", ...)` 把所有请求转给 `s3.NewServer()` 实例(基于 minio-go 的 S3 gateway 实现)。受 `conf.Conf.S3.Enable` 开关控制,未启用返回 403
- .NET [S3Endpoints.cs](file:///f:/Code/Dotnet/OpenList.Net/src/OpenList.Server/Endpoints/S3Endpoints.cs):显式注册 GET/HEAD/PUT/DELETE 七条路由
- **鉴权隐患**:.NET 的 `ResolveUserAsync` 在无 JWT 时硬编码返回 `Role=Admin` 的 guest 用户(代码注释自承为待办),生产环境需补 `IAuthService.ValidateTokenAsync`。Go 走完整 SigV4/JWT 校验

### 8.2 WebDAV

- Go:用 `webdav.Handler`(标准库风格)处理所有 WebDAV 动词,`LockSystem: webdav.NewMemLS()` 提供真实锁持久化(内存)
- .NET [WebDavEndpoints.cs](file:///f:/Code/Dotnet/OpenList.Net/src/OpenList.Server/Endpoints/WebDavEndpoints.cs):`PROPPATCH` 返回 200 OK(no-op,与 Go 行为一致);`LOCK`/`UNLOCK` 已通过 `IWebDavLockStore` + `InMemoryWebDavLockStore`(单例,`ConcurrentDictionary`,懒清理过期锁)实现真实锁管理 — LOCK 生成 token 并持久化(重复 LOCK 返回 423 Locked),UNLOCK 校验 `Lock-Token` 头(匹配 204 / 不匹配 409 / 缺失 400)。DI 注册在 [Program.cs](file:///f:/Code/Dotnet/OpenList.Net/src/OpenList.Server/Program.cs);测试 [WebDavEndpointsTests.cs](file:///f:/Code/Dotnet/OpenList.Net/tests/OpenList.Tests/WebDavEndpointsTests.cs)
- Go 还支持 Bearer Token 直通 admin(对比 `setting.GetStr(conf.Token)`),`.NET` 仅 Basic + JWT

---

## 9. Admin 路由差异

### 9.1 设置管理

| 路由 | Go | .NET |
|---|---|---|
| `GET /setting/get` | ✓ | ✓ |
| `GET /setting/list` | ✓ | ✓ |
| `POST /setting/save` | ✓ | ✓ |
| `POST /setting/delete` | ✓ | ✓ |
| `POST /setting/default` | ✓ | ✓ |
| `POST /setting/reset_token` | ✓ | ✓ |
| `POST /setting/set` | ✗ | ✓(.NET 额外) |
| 11 个 `set_*` 工具配置 | ✓ | ✓ |

### 9.2 Meta

Go 5 个路由(`list/get/create/update/delete`),.NET 6 个(多了 `/save`)。

### 9.3 Message

如第 6 节所述,Go 2 个 vs .NET 7 个 + `/me/message/*` 3 个。

### 9.4 用户管理

Go 8 个 + 2 个 sshkey = 10 个;.NET 8 个 + 2 个 sshkey + 1 个 `set_password` = 11 个(.NET 多出 `set_password`)。

---

## 10. 驱动层差异

| 维度 | Go | .NET |
|---|---|---|
| 驱动数 | ~50+(本地、阿里、115、百度、夸克、迅雷、PikPak、OneDrive、GoogleDrive、Dropbox、Terabox、Weiyun、Mega、Lanzou、119云、189云、Seafile、Teambition、MediaFire、ProtonDrive、SMB、Cloudreve、DogeCloud、Azure Blob、Chaoxing、CNB、Degoo、Doubao、Febbox、GitHub、Teldrive、DingTalk Docs、UC、YandexDisk、139云、Google Photo、Baidu Photo、FeijiPan、Aliyundrive Open、123pan Open、115 Open、ILanzou 等) | 5 个:`Local`、`S3`、`WebDAV`、`FTP`、`SFTP` + `SmbDriver`(stub,用 MemoryFileSystem 占位) |
| `IFileSystemArchiveReader` | ✓(多驱动) | ✗ 无实现 |
| `IFileSystemArchiveDecompress` | ✓ | ✗ 无实现 |
| `IFileSystemDirectUploader` | ✓ | ✅ 接口已定义,无实现 |
| `IFileSystemWithDetails` | ✓ | ✗ 无实现 |

**Cloud 驱动是 .NET 当前最大的功能空白**(AGENTS.md Phase 9 整个未启动)。

---

## 11. 其他差异汇总

| 项 | Go | .NET |
|---|---|---|
| `/api/me/sessions{,/:id}` | ✗ 无 | ✓ 占位(返回空数据,Phase 16 待实现) |
| `/api/me/2fa` | ✗ 无(只有 `/auth/2fa/*`) | ✓ 额外端点 |
| `/api/auth/hash_password` | ✗ 无 | ✓(Admin only) |
| `/api/auth/login/hash` | ✓ | ✓ |
| `/ping` 方法 | `Any`(任意方法) | `GET` only |
| `/api/admin/message/get` 是否存在 | ✓ POST | ✓ POST(但语义为按 ID 取 CRUD 记录,非 broker Receive) |
| `StoragesLoaded` 中间件 | ✓(全局,确保存储加载完成) | ✗ 无(在 `ZioMountService.LoadAllAsync` 启动时一次性加载,运行时不强制) |
| `MaxAllowed` 中间件(全局限流) | ✓(`conf.MaxConnections`) | ✗ 无(只有 per-IP rate limiter) |
| `ForceHttps` 中间件 | ✓ | ✗ 无(交由反向代理) |

---

## 12. 建议优先级排序

按"影响前端兼容性"和"功能完整性"排序:

**P0(直接破坏前端/客户端调用)**
1. ✅ `/api/auth/logout` 方法不匹配(GET vs POST) — 已修复
2. `/api/fs/list|get` 等不支持 `Any` 方法 → 用 GET 调用的客户端会 405
3. ✅ `/api/fs/list|get` 不支持 guest 访客访问 → 已为 `/list`、`/get`、`/dirs`、`/search` 加 `.AllowAnonymous()`
4. ✅ `torrent/generate` 缺失 → 已注册 stub(返回 `code=501`)

**P1(功能缺失,影响核心场景)**
5. ✅ `/ad`、`/ap`、`/ae` 签名归档路由(归档下载/代理/内嵌解压) — 已实现,驱动层未实现时返回 501
6. ✅ `/sad/:sid` 共享归档下载 — 已实现
7. `IFileSystemArchiveReader` / `IFileSystemArchiveDecompress` 驱动实现(归档列表/解压端点已注册但底层无实现)
8. ✅ WebDAV `LOCK`/`UNLOCK` 真实实现 — 已通过 `IWebDavLockStore` + `InMemoryWebDavLockStore` 实现

**P2(架构差异,需要决策)**
9. 消息系统重构决策:保留 .NET 的 CRUD 模型还是回归 Go 的 broker 模型?
10. MCP 端点(`Any /mcp`)
11. Cloud 驱动(13+ 个,Phase 9 整个未启动)

**P3(.NET 多出/向前兼容)**
12. `.NET` 多出的 `/setting/set`、`/me/2fa`、`/auth/hash_password`、`/me/sessions` 占位 —— 建议保留(不破坏 Go 前端)

---

## 13. AGENTS.md 建议更新清单

1. 移除"Stubbed (route exists, returns 501)"标题下关于 SSO、WebAuthn 的描述(已实现)
2. 移除"Completely missing"中 `/api/auth/login/ldap`、`/api/fs/other`、11 个 `set_*` 工具路由(已实现)
3. 修正 torrent stub 数量为 3 个 stub + 1 个缺失(`generate`)
4. 新增"MCP 端点缺失"条目
5. 新增"消息系统架构差异"说明
6. ~~新增"`/api/auth/logout` 方法不匹配"~~ ✅ 已修复,改为已修复说明
7. 新增"`/api/fs/list|get` guest 访客访问不支持"
8. 新增"`Any` vs 固定方法"兼容性说明
