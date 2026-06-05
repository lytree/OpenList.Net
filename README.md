# OpenList.Net

OpenList 的 C# / .NET 移植版本。参考实现：[OpenListTeam/OpenList](https://github.com/OpenListTeam/OpenList) (Go/Gin, v4)

## 当前状态

项目已完成基础架构搭建，所有 API 端点已迁移至 Minimal APIs，核心服务层和数据库层已实现。大部分存储驱动为 stub 状态，仅 Local Driver 有完整实现。

---

## 与 Go 参考实现的差距 Todo 列表

### P0：API 接口对齐（最高优先级）

完整对齐 Go 版本所有 API 路由，确保前端可正常对接。

- [ ] **`/api/auth/logout`** — Go 版本有 GET logout 端点（使 token 失效），.NET 版本缺失
- [ ] **`/api/auth/sso`** — SSO 登录重定向端点
- [ ] **`/api/auth/sso_callback`** — SSO 登录回调端点
- [ ] **`/api/auth/get_sso_id`** — 获取 SSO ID 端点
- [ ] **`/api/auth/sso_get_token`** — SSO 获取 Token 端点
- [ ] **`/api/authn/webauthn_begin_login`** — WebAuthn 登录开始
- [ ] **`/api/authn/webauthn_finish_login`** — WebAuthn 登录完成
- [ ] **`/api/authn/webauthn_begin_registration`** — WebAuthn 注册开始（需认证）
- [ ] **`/api/authn/webauthn_finish_registration`** — WebAuthn 注册完成（需认证）
- [ ] **`/api/authn/delete_authn`** — 删除 WebAuthn 凭证（需认证）
- [ ] **`/api/authn/getcredentials`** — 获取 WebAuthn 凭证列表（需认证）
- [ ] **`/api/fs/list` 权限** — Go 版本需认证访问（`middlewares.Auth(true)` 允许 guest），.NET 版本无认证
- [ ] **`/api/fs/get` 权限** — 同上
- [ ] **`/api/fs/search` 权限** — Go 版本需认证 + SearchIndex 中间件，.NET 版本无认证
- [ ] **`/api/fs/dirs` 权限** — Go 版本需认证，.NET 版本无认证
- [ ] **`/api/fs/other` 权限** — Go 版本需认证，.NET 版本无认证
- [ ] **`/api/fs/put`** — Go 版本用 PUT 方法 + FsUp 中间件 + 流式上传，.NET 版本用 POST 且无上传逻辑
- [ ] **`/api/fs/form`** — Go 版本有 PUT /form 表单上传端点，.NET 版本缺失
- [ ] **`/api/fs/link`** — Go 版本需 Admin 权限，.NET 版本仅 RequireAuthorization
- [ ] **`/api/fs/torrent/parse`** — 种子解析（Go 版本已实现，.NET 返回 501）
- [ ] **`/api/fs/torrent/upload_parse`** — 上传种子并解析（.NET 返回 501）
- [ ] **`/api/fs/torrent/rapid_upload`** — 种子快速上传（.NET 返回 501）
- [ ] **`/api/fs/torrent/generate`** — 生成种子（.NET 返回 501）
- [ ] **`/api/fs/archive/meta`** — 归档元数据端点，.NET 版本缺失
- [ ] **`/api/fs/archive/list`** — 归档内文件列表端点，.NET 版本缺失
- [ ] **`/api/fs/list` 支持 GET** — Go 版本 fs/list 和 fs/get 同时支持 GET/POST（`Any`），.NET 仅 POST
- [ ] **`/api/fs/get` 支持 GET** — 同上
- [ ] **`/api/share/list` 分页** — Go 版本支持 PageReq 分页 + 管理员看全部/普通用户看自己，.NET 版本忽略分页
- [ ] **`/api/share/enable`** — Go 版本有 enable/disable 端点，.NET 版本缺失
- [ ] **`/api/share/disable`** — 同上
- [ ] **`/api/admin/meta/get`** — Go 版本有获取单个 Meta 端点，.NET 版本缺失
- [ ] **`/api/admin/user/get`** — Go 版本有获取单个 User 端点，.NET 版本缺失
- [ ] **`/api/admin/user/del_cache`** — Go 版本有删除用户缓存端点，.NET 版本缺失
- [ ] **`/api/admin/user/sshkey/list`** — Go 版本管理员可查看所有用户 SSH Key，.NET 版本缺失
- [ ] **`/api/admin/user/sshkey/delete`** — Go 版本管理员可删除用户 SSH Key，.NET 版本缺失
- [ ] **`/api/admin/storage/get`** — Go 版本有获取单个 Storage 端点，.NET 版本缺失
- [ ] **`/api/admin/storage/load_all`** — Go 版本有手动重载所有存储端点，.NET 版本缺失
- [ ] **`/api/admin/setting/get`** — Go 版本有获取单个设置端点，.NET 版本缺失
- [ ] **`/api/admin/setting/delete`** — Go 版本有删除设置端点，.NET 版本缺失
- [ ] **`/api/admin/setting/default`** — Go 版本有恢复默认设置端点，.NET 版本缺失
- [ ] **`/api/admin/setting/set_aria2`** — 设置 Aria2 配置端点
- [ ] **`/api/admin/setting/set_qbit`** — 设置 qBittorrent 配置端点
- [ ] **`/api/admin/setting/set_transmission`** — 设置 Transmission 配置端点
- [ ] **`/api/admin/setting/set_115`** — 设置 115 配置端点
- [ ] **`/api/admin/setting/set_115_open`** — 设置 115 Open 配置端点
- [ ] **`/api/admin/setting/set_123_pan`** — 设置 123Pan 配置端点
- [ ] **`/api/admin/setting/set_123_open`** — 设置 123Open 配置端点
- [ ] **`/api/admin/setting/set_pikpak`** — 设置 PikPak 配置端点
- [ ] **`/api/admin/setting/set_thunder`** — 设置 Thunder 配置端点
- [ ] **`/api/admin/setting/set_thunderx`** — 设置 ThunderX 配置端点
- [ ] **`/api/admin/setting/set_thunder_browser`** — 设置 ThunderBrowser 配置端点
- [ ] **`/api/admin/task/*`** — Go 版本 admin 下也有 task 路由组（兼容旧脚本），.NET 版本缺失
- [ ] **`/d/*path` 下载限流** — Go 版本有 DownloadRateLimiter 中间件，.NET 版本无
- [ ] **`/p/*path` 代理限流** — 同上
- [ ] **`/ad/*path`** — Go 版本已实现归档下载，.NET 版本签名验证但处理逻辑与 /d 相同
- [ ] **`/ap/*path`** — Go 版本已实现归档代理，.NET 返回 501
- [ ] **`/ae/*path`** — Go 版本已实现归档内部解压，.NET 返回 501
- [ ] **`/sad/*`** — Go 版本已实现分享归档解压，.NET 返回 404
- [ ] **`/ping`** — Go 版本有 `/ping` 健康检查端点，.NET 版本缺失
- [ ] **`/favicon.ico`** — Go 版本有 favicon 端点，.NET 版本缺失
- [ ] **`/robots.txt`** — Go 版本有 robots 端点，.NET 版本缺失
- [ ] **`/i/:link_name`** — Go 版本有 Plist 端点（iOS 流媒体），.NET 版本缺失
- [ ] **PathParse 中间件** — Go 版本签名路由有路径规范化中间件，.NET 版本缺失
- [ ] **StoragesLoaded 中间件** — Go 版本全局检查存储是否已加载，.NET 版本缺失
- [ ] **FsUp 中间件** — Go 版本上传端点有 FsUp 中间件验证上传权限，.NET 版本缺失

### P1：核心驱动 100% 实现（高优先级）

- [ ] **Local Driver 完善** — 补充 ShowHidden/Recursion/Thumbnail 配置项、符号链接处理、隐藏文件过滤
- [ ] **FTP Driver** — 基于 FluentFTP 实现完整 FTP 操作（连接/列表/上传/下载/目录创建/删除/重命名）
- [ ] **SFTP Driver** — 基于 SSH.NET 实现完整 SFTP 操作
- [ ] **SMB Driver** — 基于 SMBLibrary 实现完整 SMB/CIFS 操作
- [ ] **WebDAV Driver** — 基于 HttpClient 实现完整 WebDAV 客户端操作（PROPFIND/GET/PUT/MKCOL/MOVE/COPY/DELETE）

### P2：核心服务层（高优先级）

- [ ] **文件系统缓存** — 实现内存缓存层（按路径缓存列表结果、带 TTL 过期、刷新策略）
- [ ] **跨存储 Copy/Move** — 支持跨不同存储驱动的 copy/move（通过中间临时文件）
- [ ] **Put 流式上传** — 实现 FsStream（流式上传）和 FsForm（表单上传）
- [ ] **离线下载实现** — 对接 aria2/qBittorrent 实现离线下载
- [ ] **Archive 读取/解压** — 实现 IDriverArchiveReader 和 IDriverArchiveDecompress
- [ ] **密码保护目录** — fs list/get 支持 Meta 密码验证
- [ ] **排序功能** — 按 Storage 配置的 OrderBy/OrderDirection 排序
- [ ] **分页功能** — fs list 支持 Page/PerPage 分页

### P3：认证与安全（中优先级）

- [ ] **TOTP 2FA 验证** — 实现 TOTP 验证逻辑（Otp.NET 库）+ QR 码图片生成
- [ ] **LDAP 登录** — 实现 LDAP 认证（System.DirectoryServices 或 Novell.Directory.Ldap）
- [ ] **SSO 登录** — 实现 SSO/OAuth2 登录流程
- [ ] **WebAuthn** — 实现 WebAuthn/FIDO2 认证
- [ ] **Token 黑名单清理** — 定期清理过期 token
- [ ] **登录限流优化** — 配置化限流 + 持久化

### P4：驱动基础设施（中优先级）

- [ ] **DriverItem 配置定义** — 每个驱动补充完整的 Common/Additional 配置项
- [ ] **驱动缓存机制** — 实现带过期时间的内存缓存
- [ ] **驱动登录/刷新 Token** — OAuth2 refresh token 自动续期
- [ ] **IDriverDirectUploader 实现** — 实现直传上传逻辑
- [ ] **IDriverWithDetails 实现** — 返回 StorageDetails（磁盘用量等）

### P5：数据库与持久化（中优先级）

- [ ] **数据库迁移** — 改用 EF Core Migrations
- [ ] **Task 持久化** — 任务存储到数据库
- [ ] **Search 索引持久化** — 索引存储到数据库
- [ ] **Message 持久化** — 消息存储到数据库（当前无 DB 表）
- [ ] **Token 黑名单持久化** — 黑名单存储到数据库

### P6：WebDAV 服务端完善（中优先级）

- [ ] **WebDAV 写入支持** — PUT 端点实际存储文件
- [ ] **WebDAV 302 重定向** — 支持 WebdavPolicy = "302_redirect"
- [ ] **WebDAV 代理 URL** — 支持 WebdavPolicy = "use_proxy_url"
- [ ] **WebDAV 权限验证** — 用户 BasePath 限制和只读模式
- [ ] **WebDAV Range 请求** — 代理模式支持 Range 请求转发

### P7：S3 兼容 API 完善（中优先级）

- [ ] **S3 认证签名验证** — AWS Signature V2/V4 验证
- [ ] **S3 Multipart Upload** — 分片上传
- [ ] **S3 ACL 支持** — 基础 ACL 操作
- [ ] **S3 版本控制** — 版本控制 API

### P8：其余云盘驱动（低优先级）

- [ ] Aliyundrive Driver
- [ ] OneDrive / OneDriveApp / OneDriveShareLink Driver
- [ ] GoogleDrive Driver
- [ ] BaiduNetdisk Driver
- [ ] BaiduPhoto Driver
- [ ] PikPak Driver
- [ ] QuarkUc Driver
- [ ] Pan115 Driver
- [ ] Dropbox Driver
- [ ] Cloudreve Driver
- [ ] Seafile Driver
- [ ] GooglePhoto Driver
- [ ] AzureBlob Driver
- [ ] 123Pan Driver
- [ ] Terabox Driver
- [ ] Thunder / ThunderX / ThunderBrowser Driver
- [ ] Lanzou / ILanzou Driver
- [ ] ProtonDrive Driver
- [ ] Mega Driver
- [ ] Pan139 / Pan189 Driver
- [ ] Teambition Driver
- [ ] Teldrive Driver
- [ ] NeteaseMusic Driver
- [ ] MediaFire / YandexDisk / FeijiPan / DogeCloud Driver
- [ ] Chaoxing / CNB / Degoo / Doubao / Febbox Driver
- [ ] GitHub / Weiyun / Emby / Mediatrack Driver

### P9：中间件与基础设施（低优先级）

- [ ] **Sign 签名验证** — SignMiddleware 实际验证签名
- [ ] **AuthMiddleware 清理** — 移除冗余 AuthMiddleware 代码
- [ ] **RateLimitMiddleware** — 支持分布式缓存
- [ ] **请求日志** — 实现请求日志中间件
- [ ] **健康检查端点** — 实现 `/ping` 端点

### P10：搜索功能（低优先级）

- [ ] **全文搜索** — 实现真正的全文搜索引擎
- [ ] **增量索引** — 实现增量索引构建
- [ ] **索引构建进度** — 实现真正的全量索引构建
- [ ] **数据库搜索后端** — 支持数据库搜索作为后备

### P11：配置与部署（低优先级）

- [ ] **Docker 支持** — Dockerfile 和 docker-compose.yml
- [ ] **配置验证** — 启动时配置项验证
- [ ] **日志结构化** — Serilog 等结构化日志
- [ ] **HTTPS 配置** — HTTPS 配置选项
- [ ] **Graceful Shutdown** — 优雅关闭逻辑
- [ ] **CI/CD** — GitHub Actions

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
