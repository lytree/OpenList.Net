# OpenList.Net

OpenList 的 C# / .NET 移植版本。参考实现：[OpenListTeam/OpenList](https://github.com/OpenListTeam/OpenList) (Go/Gin, v4)

## 当前状态

项目已完成基础架构搭建，所有 API 端点已迁移至 Minimal APIs，核心服务层和数据库层已实现。大部分存储驱动为 stub 状态，仅 Local Driver 有完整实现。

---

## 与 Go 参考实现的差距 Todo 列表

### P0：API 接口对齐（最高优先级）

完整对齐 Go 版本所有 API 路由，确保前端可正常对接。

- [ ] **`/api/authn/*`** — WebAuthn 端点路由已注册（返回 501），完整实现需 FIDO2 库
- [ ] **`/api/fs/torrent/parse`** — 种子解析（返回 501）
- [ ] **`/api/fs/torrent/upload_parse`** — 上传种子并解析（返回 501）
- [ ] **`/api/fs/torrent/rapid_upload`** — 种子快速上传（返回 501）
- [ ] **`/api/fs/torrent/generate`** — 生成种子（返回 501）
- [x] **`/api/fs/archive/meta`** — 归档元数据端点，支持 ZIP 格式
- [x] **`/api/fs/archive/list`** — 归档内文件列表端点，支持 ZIP 格式
- [x] **`/api/fs/archive/decompress`** — 归档解压端点，支持 ZIP 格式异步解压
- [x] **`/api/share/list` 分页与用户过滤** — 已实现数据库层分页 + 管理员看全部/普通用户看自己
- [ ] **`/api/admin/setting/set_aria2`** — 设置 Aria2 配置端点（返回 501）
- [ ] **`/api/admin/setting/set_qbit`** — 设置 qBittorrent 配置端点（返回 501）
- [ ] **`/api/admin/setting/set_transmission`** — 设置 Transmission 配置端点（返回 501）
- [ ] **`/api/admin/setting/set_115`** — 设置 115 配置端点（返回 501）
- [ ] **`/api/admin/setting/set_115_open`** — 设置 115 Open 配置端点（返回 501）
- [ ] **`/api/admin/setting/set_123_pan`** — 设置 123Pan 配置端点（返回 501）
- [ ] **`/api/admin/setting/set_123_open`** — 设置 123Open 配置端点（返回 501）
- [ ] **`/api/admin/setting/set_pikpak`** — 设置 PikPak 配置端点（返回 501）
- [ ] **`/api/admin/setting/set_thunder`** — 设置 Thunder 配置端点（返回 501）
- [ ] **`/api/admin/setting/set_thunderx`** — 设置 ThunderX 配置端点（返回 501）
- [ ] **`/api/admin/setting/set_thunder_browser`** — 设置 ThunderBrowser 配置端点（返回 501）
- [x] **`/d/*path` 下载限流** — DownloadRateLimiterMiddleware 全局注册，已覆盖签名路由
- [x] **`/p/*path` 代理限流** — 同上
- [x] **`/ad/*path`** — 归档下载，支持从 ZIP 中提取内部文件
- [x] **`/ap/*path`** — 归档代理，支持从 ZIP 中提取并代理内部文件
- [x] **`/ae/*path`** — 归档解压，支持从 ZIP 中提取内部文件下载
- [x] **`/sad/*`** — 分享归档下载，支持从 ZIP 中提取内部文件
- [x] **PathParse 中间件** — 通过路由参数 {*path} 直接实现，无需额外中间件
- [x] **SharingIdParse 中间件** — 通过路由参数 {sid} 直接实现，无需额外中间件
- [x] **SearchIndex 中间件** — 搜索和索引端点已加入 SearchIndex 配置检查（none 时返回 404）
- [x] **UploadRateLimiter 中间件** — UploadRateLimiterMiddleware 全局注册，已覆盖上传路由
- [x] **`/api/auth/sso`** — SSO 登录重定向，支持 GitHub/Microsoft/Google/Dingtalk/Casdoor 平台
- [x] **`/api/auth/sso_callback`** — SSO 登录回调，OAuth2 code 换 token 并获取用户信息
- [x] **`/api/auth/get_sso_id`** — 获取 SSO ID，返回 postMessage 或重定向
- [x] **`/api/auth/sso_get_token`** — SSO 获取 Token，自动注册用户并生成 JWT

### P1：核心驱动 100% 实现（高优先级）

- [x] **Local Driver 完善** — ShowHidden/Recursion/Thumbnail/DirectorySize/RecycleBinPath/MkdirPerm 已实现
- [x] **FTP Driver** — 基于 FluentFTP 实现完整 FTP 操作（连接/列表/上传/下载/目录创建/删除/重命名/Link）
- [x] **SFTP Driver** — 基于 SSH.NET 实现完整 SFTP 操作（连接/列表/上传/下载/目录创建/删除/重命名/Link）
- [x] **SMB Driver** — 基于 SMBLibrary 实现完整 SMB/CIFS 操作（含 Link 临时文件下载）
- [x] **WebDAV Driver** — 基于 WebDav.Client 实现完整 WebDAV 客户端操作（List/Link/Get/MakeDir/Move/Rename/Copy/Remove/Put）

### P2：核心服务层（高优先级）

- [x] **文件系统缓存** — 已实现基于 IMemoryCache 的目录缓存和链接缓存，支持 TTL 和缓存失效
- [x] **跨存储 Copy/Move** — Go 版本也不支持跨存储，同存储内已实现
- [x] **Put 流式上传** — 已实现 FsStream（流式上传）和 FsForm（表单上传），对齐 Go 版本 File-Path/Overwrite/X-File-Size/Last-Modified 请求头
- [ ] **离线下载实现** — 对接 aria2/qBittorrent 实现离线下载
- [x] **Archive 读取/解压** — 已实现 ZIP 格式的 meta/list/decompress 端点（基于 System.IO.Compression）
- [x] **密码保护目录** — fs list/get 支持 Meta 密码验证 + Readme/Header/Write 信息返回
- [x] **排序功能** — 按 Storage 配置的 OrderBy/OrderDirection 排序 + ExtractFolder（front/back）
- [x] **分页功能** — fs list 支持 Page/PerPage 分页（对齐 Go 版本 PageReq）

### P3：认证与安全（中优先级）

- [x] **TOTP 2FA 验证** — 实现 TOTP 验证逻辑（Otp.NET 库）+ QR 码图片生成（QRCoder），登录时 402 状态码提示需要 2FA
- [x] **LDAP 登录** — 实现 LDAP 认证（Novell.Directory.Ldap.NETStandard），支持 ManagerDN 绑定、用户搜索、自动注册
- [x] **SSO 登录** — 实现 SSO/OAuth2 登录流程，支持 GitHub/Microsoft/Google/Dingtalk/Casdoor
- [ ] **WebAuthn** — 实现 WebAuthn/FIDO2 认证
- [x] **Token 黑名单清理** — 惰性清理过期 token（ValidateToken 时触发）
- [ ] **登录限流优化** — 配置化限流 + 持久化

### P4：驱动基础设施（中优先级）

- [x] **DriverItem 配置定义** — 每个驱动已补充完整的 Common/Additional 配置项（Local/S3/WebDAV/FTP/SFTP/SMB）
- [x] **驱动缓存机制** — 已实现带过期时间的内存缓存（目录缓存 + 链接缓存 + 详情缓存）
- [ ] **驱动登录/刷新 Token** — OAuth2 refresh token 自动续期
- [x] **IDriverDirectUploader 实现** — get_direct_upload_info 端点已对接驱动直传接口
- [x] **IDriverWithDetails 实现** — 返回 StorageDetails（磁盘用量等），带 30 分钟缓存

### P5：数据库与持久化（中优先级）

- [x] **数据库迁移** — 改用 EF Core Migrations（InitialCreate 迁移已创建，支持 MigrateAsync + EnsureCreatedAsync 回退）
- [x] **Task 持久化** — 任务存储到数据库（内存缓存运行中任务 + DB 持久化）
- [x] **Search 索引持久化** — 索引存储到数据库（内存缓存 + DB 双层）
- [x] **Message 持久化** — 消息存储到数据库
- [x] **Token 黑名单持久化** — 黑名单存储到数据库（内存 + DB 双写）

### P6：WebDAV 服务端完善（中优先级）

- [x] **WebDAV 写入支持** — PUT 端点实际存储文件到驱动
- [x] **WebDAV 302 重定向** — 支持 WebdavPolicy = "302_redirect"
- [x] **WebDAV 代理 URL** — 支持 WebdavPolicy = "use_proxy_url"
- [x] **WebDAV 权限验证** — 用户 BasePath 限制和只读模式
- [x] **WebDAV Range 请求** — 代理模式支持 Range 请求转发

### P7：S3 兼容 API 完善（中优先级）

- [x] **S3 认证签名验证** — 已实现 AWS Signature V2/V4 验证，支持配置化 S3AccessKeys
- [x] **S3 PutObject 实际存储** — PUT 端点实际存储文件到驱动
- [x] **S3 HeadObject 元数据** — HEAD 返回 Content-Length/Content-Type/ETag
- [x] **S3 Multipart Upload** — 已实现 Initiate/UploadPart/Complete/Abort 分片上传
- [x] **S3 ACL 支持** — GetObjectAcl/PutObjectAcl 兼容实现
- [x] **S3 版本控制** — GetBucketVersioning 兼容响应（未启用版本控制）

### P8：其余云盘驱动（低优先级）

- [ ] Aliyundrive Driver
- [ ] AliyundriveOpen Driver
- [ ] AliyundriveShare Driver
- [ ] OneDrive Driver
- [ ] OneDriveApp Driver
- [ ] OneDriveShareLink Driver
- [ ] GoogleDrive Driver
- [ ] BaiduNetdisk Driver
- [ ] BaiduPhoto Driver
- [ ] PikPak Driver
- [ ] PikPakShare Driver
- [ ] QuarkUc Driver
- [ ] QuarkOpen Driver
- [ ] QuarkUcTv Driver
- [ ] Pan115 Driver
- [ ] Pan115Open Driver
- [ ] Pan115Share Driver
- [ ] Dropbox Driver
- [ ] Cloudreve Driver
- [ ] CloudreveV4 Driver
- [ ] Seafile Driver
- [ ] GooglePhoto Driver
- [ ] AzureBlob Driver
- [ ] 123Pan Driver
- [ ] 123Open Driver
- [ ] 123Link Driver
- [ ] 123Share Driver
- [ ] Terabox Driver
- [ ] Thunder / ThunderX / ThunderBrowser Driver
- [ ] Lanzou / ILanzou Driver
- [ ] ProtonDrive Driver
- [ ] Mega Driver
- [ ] Pan139 Driver
- [ ] Pan189 / Pan189Pc / Pan189Tv Driver
- [ ] Teambition Driver
- [ ] Teldrive Driver
- [ ] NeteaseMusic Driver
- [ ] MediaFire / YandexDisk Driver
- [ ] FeijiPan / DogeCloud Driver
- [ ] Chaoxing / CNB / Degoo / Doubao / DoubaoNew / DoubaoShare / Febbox Driver
- [ ] GitHub / GitHubReleases Driver
- [ ] Weiyun / Emby / Mediatrack Driver
- [ ] Alias Driver
- [ ] AListV3 Driver
- [ ] AutoIndex Driver
- [ ] Chunk Driver
- [ ] Crypt Driver
- [ ] HalalCloud / HalalCloudOpen Driver
- [ ] IPFSApi Driver
- [ ] KodBox Driver
- [ ] LenovoNasShare Driver
- [ ] Misskey Driver
- [ ] MoPan Driver
- [ ] OpenList / OpenListShare Driver
- [ ] Strm Driver
- [ ] USS Driver
- [ ] Virtual Driver
- [ ] WoPan Driver
- [ ] WPS Driver

### P9：中间件与基础设施（低优先级）

- [x] **Sign 签名验证** — 对齐 Go 版本 HMAC-SHA256 签名格式（base64url:expireTimestamp），支持配置 SignKey 和 LinkExpiration
- [x] **AuthMiddleware 清理** — 无冗余 AuthMiddleware，已使用 ASP.NET 内置认证
- [ ] **RateLimitMiddleware** — 支持分布式缓存
- [x] **请求日志** — RequestLoggingMiddleware 记录 HTTP 方法、路径、状态码和耗时

### P10：搜索功能（低优先级）

- [ ] **全文搜索** — 实现真正的全文搜索引擎
- [ ] **增量索引** — 实现增量索引构建
- [ ] **索引构建进度** — 实现真正的全量索引构建
- [ ] **数据库搜索后端** — 支持数据库搜索作为后备

### P11：配置与部署（低优先级）

- [x] **Docker 支持** — Dockerfile + docker-compose.yml（支持 SQLite/MySQL/PostgreSQL）
- [x] **配置验证** — 启动时验证关键配置项（MaxConnections、Database.Port 等）
- [x] **日志结构化** — Serilog 结构化日志（控制台 + 按天滚动文件，30天保留）
- [x] **HTTPS 配置** — 支持 CertFile/CertPassword 配置，Kestrel 双端口监听 HTTP+HTTPS
- [x] **Graceful Shutdown** — 注册 ApplicationStopping 回调，记录关闭日志
- [x] **CI/CD** — GitHub Actions（构建+测试+Docker 推送）

### P12：测试（低优先级）

- [ ] **单元测试** — 核心服务单元测试
- [ ] **集成测试** — API 集成测试
- [ ] **驱动测试** — 驱动功能测试
- [ ] **WebDAV 协议测试** — WebDAV 兼容性测试

---

## 已完成项

- [x] 项目基础架构（.slnx + CPM + Directory.Build.props）
- [x] Minimal API 端点迁移（所有 Controller 已转为 Endpoint 扩展方法）
- [x] JWT 认证基础实现
- [x] EF Core 数据库层（SQLite/MySQL/PostgreSQL/MSSQL 支持）
- [x] 驱动自动发现与注册（DriverFactory 反射扫描）
- [x] 驱动角色接口定义（IDriverGetter/Mkdir/Move/Rename/Copy/Remove/Put 等）
- [x] Local Driver 完整实现
- [x] CORS / Rate Limit 中间件
- [x] Sign 签名生成与验证（HMAC-SHA256）
- [x] Sharing 分享功能基础实现
- [x] Task 任务管理基础实现（内存存储）
- [x] S3 兼容 API 基础框架
- [x] WebDAV 服务端基础框架（PROPFIND/MKCOL/COPY/MOVE/LOCK/UNLOCK）
- [x] SPA 前端静态文件服务
- [x] Swagger 开发环境支持
- [x] PascalCase JSON 序列化
- [x] `/ping` 健康检查端点
- [x] `/api/auth/logout` — GET logout 端点（使 token 失效）
- [x] `/api/auth/sso` 系列 — SSO 路由已注册（返回 501 待实现逻辑）
- [x] `/api/fs/list` 和 `/api/fs/get` — 支持 GET+POST，需认证
- [x] `/api/fs/search`、`/api/fs/dirs`、`/api/fs/other` — 需认证
- [x] `/api/fs/put` — PUT 方法流式上传
- [x] `/api/fs/form` — PUT 方法表单上传
- [x] `/api/fs/link` — Admin 权限（RequireRole("2")）
- [x] `/api/fs/archive/meta` 和 `/api/fs/archive/list` — 路由已注册（返回 501 待实现逻辑）
- [x] `/api/admin/meta/get` — 获取单个 Meta
- [x] `/api/admin/user/get` — 获取单个 User
- [x] `/api/admin/user/del_cache` — 删除用户缓存
- [x] `/api/admin/user/sshkey/list` — 管理员查看所有用户 SSH Key
- [x] `/api/admin/user/sshkey/delete` — 管理员删除用户 SSH Key
- [x] `/api/admin/storage/get` — 获取单个 Storage
- [x] `/favicon.ico` — Favicon 重定向端点
- [x] `/robots.txt` — Robots.txt 端点（支持自定义内容）
- [x] `/manifest.json` — PWA Manifest 端点（对齐 Go 版本字段）
- [x] `/i/:link_name` — iOS Plist 流媒体端点
- [x] ForceHTTPS 中间件 — HTTP 到 HTTPS 重定向
- [x] MaxConnections 中间件 — 并发连接数限制
- [x] StoragesLoaded 中间件 — 检查存储是否已加载
- [x] FsUp 中间件 — 上传权限验证
- [x] `/api/share/enable` — 分享启用端点
- [x] `/api/share/disable` — 分享禁用端点
- [x] PublicSettingsResp — 补充 SSO/LDAP/WebAuthn/SharePreview 等公共设置字段
- [x] SharingService.ListSharingsAsync — 支持管理员查看全部分享
- [x] `/ping` 端点 — 返回纯文本 "pong"
- [x] `/api/admin/storage/load_all` — 手动重载所有存储
- [x] `/api/admin/setting/get` — 获取单个设置
- [x] `/api/admin/setting/delete` — 删除设置
- [x] `/api/admin/setting/default` — 恢复默认设置
- [x] `/api/admin/setting/set_*` — 离线下载工具设置路由已注册（返回 501 待实现逻辑）
- [x] `/api/admin/task/*` — Admin 下的 task 路由组（兼容旧脚本）
- [x] `/api/fs/batch_rename` 和 `/api/fs/regex_rename` — 批量/正则重命名
- [x] `/api/fs/remove_empty_directory` — 删除空目录
- [x] `/api/fs/get_direct_upload_info` — 直传上传信息端点
- [x] `/api/fs/add_offline_download` — 离线下载端点（stub）
- [x] `/api/admin/scan/*` — 扫描管理端点（stub）
- [x] `/api/admin/index/*` — 搜索索引管理端点
- [x] `/api/admin/message/*` — 消息管理端点
- [x] `/api/admin/driver/*` — 驱动信息端点
- [x] `/api/admin/storage/enable` 和 `/api/admin/storage/disable` — 存储启用/禁用
