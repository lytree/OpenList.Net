# OpenList.Net API 接口逻辑文档

> 所有响应统一包装为 `Resp<T>`: `{ "code": 200, "message": "success", "data": ... }`
> JSON 命名策略: **camelCase**

---

## 1. Auth API (`/api/auth`)

源文件: `AuthEndpoints.cs`, `AuthEndpoints.Sso.cs`, `AuthEndpoints.WebAuthn.cs`

### 1.1 POST `/api/auth/login` — 用户名密码登录

**认证**: 公开  
**请求体**: `LoginReq { username, password, otpCode? }`

**逻辑流程**:
1. 获取客户端 IP，检查 `_loginCache` 中该 IP 失败次数是否 ≥ 5（MaxAuthRetries）
2. 超过限制 → 返回 429
3. 调用 `IAuthService.LoginAsync(username, password, otpCode)`
4. 返回 `"2FA_REQUIRED"` → 递增失败计数，返回 402
5. 返回 `null` → 递增失败计数，设置 5 分钟自动过期，返回 401
6. 返回有效 token → 清除失败计数，返回 `{ token }`

**响应**: `Resp<LoginResp>` — `{ token: string }`

### 1.2 POST `/api/auth/login/hash` — 哈希密码登录

**认证**: 公开  
**请求体**: `LoginHashReq { username, passwordHash, otpCode? }`

**逻辑流程**: 与 1.1 相同，调用 `LoginHashAsync` 替代明文密码

### 1.3 POST `/api/auth/login/ldap` — LDAP 登录

**认证**: 公开  
**请求体**: `LoginReq { username, password }`

**逻辑流程**: 与 1.1 类似，调用 `LoginLdapAsync`，不支持 2FA

### 1.4 GET `/api/auth/logout` — 登出

**认证**: 需要授权  
**逻辑**: 从 Authorization 头提取 Bearer token，调用 `IAuthService.InvalidateTokenAsync` 使其失效

### 1.5 POST `/api/auth/2fa/generate` — 生成 2FA 密钥

**认证**: 需要授权  
**逻辑**:
1. 从 Claims 获取 userId
2. 检查用户是否为 Guest → 403
3. 调用 `IAuthService.Generate2FAAsync(userId)` → 返回 `{ qr, secret }`

### 1.6 POST `/api/auth/2fa/verify` — 验证 2FA

**认证**: 需要授权  
**请求体**: `Verify2FAReq { code, secret }`  
**逻辑**: 验证 2FA 代码，Guest 用户禁止操作

### 1.7 SSO 端点

#### GET `/api/auth/sso` — 发起 SSO 登录

**认证**: 公开  
**逻辑**:
1. 检查 `SsoLoginEnabled` 配置
2. 根据 `SsoLoginPlatform` (Github/Microsoft/Google/Dingtalk/Casdoor) 构建 OAuth 授权 URL
3. 支持兼容模式 (`SsoCompatibilityMode`)，影响 redirect_uri 格式
4. 302 重定向到 OAuth 提供方

#### GET `/api/auth/sso_callback` — SSO 回调

**认证**: 公开  
**逻辑**:
1. 从 OAuth 提供方获取授权码
2. 根据平台交换 access_token 并获取用户信息 (userId, username)
3. `method=get_sso_id`: 返回包含 sso_id 的 HTML/postMessage
4. `method=sso_get_token`: 查找或自动注册用户，生成 JWT token 返回

#### GET `/api/auth/get_sso_id`, `/api/auth/sso_get_token` — 兼容模式回调

与 `sso_callback` 共用 `HandleSsoCallback` 逻辑

### 1.8 WebAuthn 端点 (`/api/authn`)

#### GET `/api/authn/webauthn_begin_login` — 开始 WebAuthn 登录

**认证**: 公开  
**逻辑**: 调用 `WebAuthnService.BeginLoginAsync` 生成断言选项

#### POST `/api/authn/webauthn_finish_login` — 完成 WebAuthn 登录

**认证**: 公开  
**请求体**: `AuthenticatorAssertionRawResponse`  
**逻辑**: 验证断言 → 获取 userId → 查找用户 → 生成 JWT token

#### GET `/api/authn/webauthn_begin_registration` — 开始注册 WebAuthn 凭据

**认证**: 需要授权  
**逻辑**: 生成注册选项并缓存

#### POST `/api/authn/webauthn_finish_registration` — 完成注册

**认证**: 需要授权  
**请求体**: `AuthenticatorAttestationRawResponse`

#### POST `/api/authn/delete_authn` — 删除 WebAuthn 凭据

**认证**: 需要授权  
**参数**: `credentialId`

#### GET `/api/authn/getcredentials` — 获取凭据列表

**认证**: 需要授权

---

## 2. Fs API (`/api/fs`)

源文件: `FsEndpoints.*.cs`

### 2.1 FsList — 列出目录内容

**路由**: `GET|POST /api/fs/list`  
**认证**: AllowAnonymous（内部解析用户，支持 Guest）  
**请求体**: `ListReq { path, password?, page, perPage, refresh }`

**逻辑流程**:
1. 参数校验: `page ≥ 1`, `perPage` 默认 int.MaxValue
2. **分享路径分支**: `path` 以 `/@s` 开头 → 进入分享列表逻辑
   - 解析 sharingId 和 subPath
   - 验证分享有效性（过期/禁用/密码）
   - 按 IP 递增访问计数
   - 分页并返回（无签名）
3. **常规列表**:
   - 解析用户（禁止被禁用的 Guest）
   - `user.JoinPath(path)` 拼接完整路径
   - 获取最近 Meta，校验访问权限（CanAccess）
   - 校验写入权限（canWriteContentAtPath）用于 refresh 和 directUploadTools
   - 调用 `fs.ListAsync` 获取对象列表
   - 分页
   - 判断是否需要签名（EnableSign / Meta 密码加密 / sign_all）
   - 获取存储挂载详情（MountDetails）
   - 构建响应：content（含签名和缩略图）、total、readme、header、write 权限、directUploadTools

### 2.2 FsGet — 获取单个文件/目录信息

**路由**: `GET|POST /api/fs/get`  
**认证**: AllowAnonymous（内部解析用户）  
**请求体**: `GetReq { path, password? }`

**逻辑流程**:
1. **分享路径分支**: `/@s` 前缀 → 分享文件获取
   - 验证分享有效性
   - 构建 RawURL 指向 `/sd/{sid}/{path}`
2. **常规获取**:
   - 解析用户，拼接路径，校验权限
   - 调用 `fs.GetAsync` 获取对象
   - 确定 provider（驱动名称）
   - 计算签名（encrypt / sign_all）
   - **RawURL 决策**:
     - `MustProxy()` 或 `WebProxy` → 生成代理 URL（DownProxyURL 或 `/p{path}?sign=...`）
     - 否则使用对象自带 URL 或 `fs.LinkAsync` 获取链接
   - 获取相关（Related）文件：同级同名前缀文件
   - 附加 readme、header（从 Meta）

### 2.3 FsPut — 流式上传

**路由**: `PUT /api/fs/put`  
**认证**: 需要授权  
**请求头**: `File-Path`（URL 编码的目标路径）, `Overwrite?`, `Last-Modified?`, `As-Task?`, `X-File-Size?`

**逻辑流程**:
1. 从 `File-Path` 头解码路径
2. 用户 JoinPath
3. 检查父目录写入权限（CanWriteContent || CanWriteContentBypass）
4. 检查 `ignore_system_files` 设置
5. 检查 Overwrite（默认 true）
6. 解析 size（Content-Length 或 X-File-Size）、lastModified
7. `As-Task=true` → `fs.PutAsTaskAsync`; 否则 → `fs.PutAsync`

### 2.4 FsForm — 表单上传

**路由**: `PUT /api/fs/form`  
**认证**: 需要授权  
**请求**: Multipart form，字段名 `file`

**逻辑流程**: 与 2.3 类似，额外检查 `NoUpload` 驱动配置

### 2.5 FsLink — 获取文件直链

**路由**: `POST /api/fs/link`  
**认证**: 仅管理员 (`RequireRole("2")`)  
**请求体**: `LinkReq { path }`

**逻辑流程**:
1. 检查驱动 `NoLinkURL` → 返回代理 URL `/p{path}?sign=...`
2. 否则调用 `fs.LinkAsync` 获取直链

### 2.6 FsGetDirectUploadInfo — 获取直传信息

**路由**: `POST /api/fs/get_direct_upload_info`  
**认证**: 需要授权  
**请求体**: `DirectUploadReq { path, tool?, fileName?, fileSize? }`

**逻辑**: 获取目录对应存储的驱动，若实现 `IDriverDirectUploader` 则调用 `GetDirectUploadInfoAsync`

### 2.7 FsMkdir — 创建目录

**路由**: `POST /api/fs/mkdir`  
**认证**: 需要授权  
**请求体**: `MkdirReq { path }`

**逻辑**: 校验 CanWriteContent + CanWrite 权限 → `fs.MakeDirAsync`

### 2.8 FsRename — 重命名

**路由**: `POST /api/fs/rename`  
**认证**: 需要授权  
**请求体**: `RenameReq { path, name, overwrite }`

**逻辑流程**:
1. 校验 `CanRename` 权限
2. 验证新名称（禁止 `.`, `..`, `/`, `\`）
3. 校验父目录 CanWrite 权限
4. 非覆盖模式检查目标是否存在
5. `fs.RenameAsync`

### 2.9 FsBatchRename — 批量重命名

**路由**: `POST /api/fs/batch_rename`  
**认证**: 需要授权  
**请求体**: `BatchRenameReq { srcDir, renameObjects: [{ srcPath, newName }] }`

**逻辑**: 遍历 renameObjects 逐个调用 `fs.RenameAsync`

### 2.10 FsRegexRename — 正则重命名

**路由**: `POST /api/fs/regex_rename`  
**认证**: 需要授权  
**请求体**: `RegexRenameReq { srcDir, regex, replace }`

**逻辑**: 列出目录所有对象，对匹配正则的文件执行 `Regex.Replace` 生成新名称

### 2.11 FsMove — 移动文件

**路由**: `POST /api/fs/move`  
**认证**: 需要授权  
**请求体**: `MoveReq { srcDir, dstDir, names, overwrite, skipExisting }`

**逻辑流程**:
1. 校验 `CanMove` 权限
2. 校验源/目标目录 CanWrite
3. 遍历 names，路径拼接并防遍历（`HasPrefix` 检查）
4. 非覆盖模式检查目标是否存在（支持 skipExisting）
5. 逐个调用 `fs.MoveAsync`

### 2.12 FsRecursiveMove — 递归移动

**路由**: `POST /api/fs/recursive_move`  
**认证**: 需要授权  
**请求体**: `RecursiveMoveReq { srcDir, dstDir, conflictPolicy }`

**逻辑流程**:
1. BFS 遍历源目录所有子文件
2. `conflictPolicy`: `overwrite` | `cancel` | `skip`
3. 收集所有待移动文件后批量执行 `fs.MoveAsync`

### 2.13 FsCopy — 复制文件

**路由**: `POST /api/fs/copy`  
**认证**: 需要授权  
**请求体**: `CopyReq { srcDir, dstDir, names, overwrite, skipExisting, merge }`

**逻辑**: 与 Move 类似，校验 CanRead(源) + CanWrite(目标)，支持 merge 模式 (`fs.MergeAsync`)

### 2.14 FsRemove — 删除文件

**路由**: `POST /api/fs/remove`  
**认证**: 需要授权  
**请求体**: `RemoveReq { dir, names }`

**逻辑**: 校验 `CanRemove` 权限 + CanWrite → 逐个 `fs.RemoveAsync`

### 2.15 FsRemoveEmptyDirectory — 递归删除空目录

**路由**: `POST /api/fs/remove_empty_directory`  
**认证**: 需要授权  
**请求体**: `RemoveEmptyDirReq { srcDir }`

**逻辑**: BFS 从叶子节点向上递归删除空目录

### 2.16 FsSearch — 搜索文件

**路由**: `GET|POST /api/fs/search`  
**认证**: AllowAnonymous  
**请求体**: `SearchReq { parent, keywords, page, perPage, scope }`

**逻辑流程**:
1. 检查 `SearchIndex` 配置是否启用
2. 调用 `search.SearchAsync`
3. `scope` 过滤: 0=全部, 1=目录, 2=文件
4. 按 BasePath 和 CanAccess 过滤结果

### 2.17 FsDirs — 列出子目录

**路由**: `GET|POST /api/fs/dirs`  
**认证**: AllowAnonymous  
**请求体**: `DirsReq { path, password, forceRoot }`

**逻辑**: `forceRoot` 仅管理员可用，列出指定路径下的子目录

### 2.18 FsOther — 驱动自定义操作

**路由**: `GET|POST /api/fs/other`  
**认证**: AllowAnonymous  
**请求体**: `OtherReq { path, method, data, password }`

**逻辑**: 获取对象后调用 `fs.OtherAsync`

### 2.19 FsAddOfflineDownload — 添加离线下载任务

**路由**: `POST /api/fs/add_offline_download`  
**认证**: 需要授权  
**请求体**: `AddOfflineDownloadReq { urls, path, tool, deletePolicy }`

**逻辑**: 校验 `CanAddOfflineDownloadTasks` 权限 + CanWrite → `downloadService.AddUrlsAsync`

### 2.20 Archive 子组 (`/api/fs/archive`)

#### GET|POST `archive/meta` — 获取压缩包元数据

**认证**: AllowAnonymous（内部权限检查 `CanReadArchives`）  
**请求体**: `ArchiveMetaReq { path, password?, archivePass? }`

**逻辑**: 获取压缩包元数据（内容树、是否加密），生成 RawURL (`/ad` 或 `/ae`)

#### GET|POST `archive/list` — 列出压缩包内容

**认证**: AllowAnonymous  
**请求体**: `ArchiveListReq { path, innerPath?, archivePass?, page, perPage, refresh }`

**逻辑**: 支持分页列出压缩包内文件

#### POST `archive/decompress` — 解压文件

**认证**: 需要授权  
**请求体**: `ArchiveDecompressReq { srcDir, dstDir, names, archivePass?, innerPath?, ... }`

**逻辑**: 校验 `CanDecompress` 权限 + 目标 CanWrite → 为每个源文件创建解压任务

### 2.21 Torrent 子组 (`/api/fs/torrent`)

> **所有端点目前返回 501 (未实现)**

| 端点 | 说明 |
|---|---|
| `POST torrent/parse` | 解析 Base64 种子文件 |
| `POST torrent/upload_parse` | 上传并解析种子文件 |
| `POST torrent/rapid_upload` | CAS 秒传（189PC 驱动） |
| `POST torrent/generate` | 为文件生成种子 |

---

## 3. Admin API (`/api/admin`)

源文件: `AdminEndpoints.cs`, `AdminEndpoints.Storage.cs`, `AdminEndpoints.Operations.cs`  
**所有端点**: 仅管理员 (`RequireRole("2")`)

### 3.1 用户管理

| 端点 | 方法 | 逻辑 |
|---|---|---|
| `user/list` | GET | 分页列出用户 |
| `user/get` | GET | 按 ID 获取用户 |
| `user/create` | POST | 创建用户（Mapster 映射 → User） |
| `user/update` | POST | 更新用户（密码、权限、BasePath、角色、禁用状态） |
| `user/delete` | POST | 删除用户 |
| `user/cancel_2fa` | POST | 取消用户 2FA |
| `user/del_cache` | POST | 删除用户缓存 |
| `user/sshkey/list` | GET | 列出用户 SSH 密钥 |
| `user/sshkey/delete` | POST | 删除 SSH 密钥 |

### 3.2 存储管理

| 端点 | 方法 | 逻辑 |
|---|---|---|
| `storage/list` | GET | 分页列出存储，并发获取 MountDetails（3s 超时） |
| `storage/get` | GET | 按 ID 获取存储（无 MountDetails） |
| `storage/create` | POST | 创建存储 |
| `storage/update` | POST | 更新存储 |
| `storage/delete` | POST | 删除存储 |
| `storage/enable` | POST | 启用存储 |
| `storage/disable` | POST | 禁用存储 |
| `storage/load_all` | POST | 后台异步重新加载所有存储 |

### 3.3 Meta 管理

| 端点 | 方法 | 逻辑 |
|---|---|---|
| `meta/list` | GET | 分页列出 Meta |
| `meta/get` | GET | 按 ID 获取 Meta |
| `meta/create` | POST | 创建 Meta（验证 Hide 字段正则合法性） |
| `meta/update` | POST | 更新 Meta |
| `meta/delete` | POST | 删除 Meta |

### 3.4 分享管理

| 端点 | 方法 | 逻辑 |
|---|---|---|
| `share/list` | GET | 分页列出所有分享（附带 CreatorName/Role） |
| `share/create` | POST | 创建分享 |
| `share/update` | POST | 更新分享 |
| `share/delete` | POST | 删除分享 |

### 3.5 驱动管理

| 端点 | 方法 | 逻辑 |
|---|---|---|
| `driver/list` | GET | 列出所有驱动信息（Common + Additional + Config） |
| `driver/names` | GET | 列出驱动名称 |
| `driver/info` | GET | 获取指定驱动详情 |

### 3.6 设置管理

| 端点 | 方法 | 逻辑 |
|---|---|---|
| `setting/list` | GET | 列出设置项（支持按 group/groups 过滤） |
| `setting/get` | GET | 获取设置项（key 或 keys） |
| `setting/save` | POST | 批量保存设置项 |
| `setting/delete` | POST | 删除设置项 |
| `setting/default` | POST | 获取默认设置 |
| `setting/reset_token` | POST | 重置 API Token |
| `setting/set_aria2` | POST | 设置 aria2 配置并重新初始化 |
| `setting/set_qbit` | POST | 设置 qBittorrent 配置并重新初始化 |
| `setting/set_transmission` | POST | 501 未实现 |
| `setting/set_115` ~ `set_thunder_browser` | POST | 501 未实现 |

### 3.7 索引管理

| 端点 | 方法 | 逻辑 |
|---|---|---|
| `index/build` | POST | 后台异步构建全量索引（需 SearchIndex 启用） |
| `index/update` | POST | 增量更新指定路径索引 |
| `index/stop` | POST | 停止索引构建 |
| `index/clear` | POST | 清除索引 |
| `index/progress` | GET | 获取索引构建进度 |

### 3.8 消息管理

| 端点 | 方法 | 逻辑 |
|---|---|---|
| `message/get` | POST | 获取当前用户消息 |
| `message/send` | POST | 发送消息给指定用户 |

### 3.9 扫描管理

| 端点 | 方法 | 逻辑 |
|---|---|---|
| `scan/start` | POST | 开始扫描指定路径 |
| `scan/stop` | POST | 停止扫描 |
| `scan/progress` | GET | 获取扫描进度（obj_count, is_done） |

### 3.10 任务管理 (Admin)

路由模式: `/api/admin/task/{taskType}/{action}`  
taskType: `upload`, `copy`, `move`, `offline_download`, `offline_download_transfer`, `decompress`, `decompress_upload`

每种类型注册以下端点:

| 端点 | 方法 | 逻辑 |
|---|---|---|
| `undone` | GET | 列出未完成的任务（管理员看全部） |
| `done` | GET | 列出已完成的任务 |
| `info` | POST | 获取任务详情 |
| `cancel` | POST | 取消任务 |
| `delete` | POST | 删除任务 |
| `retry` | POST | 重试任务 |
| `cancel_some` | POST | 批量取消（返回错误字典） |
| `delete_some` | POST | 批量删除 |
| `retry_some` | POST | 批量重试 |
| `clear_done` | POST | 清除所有已完成任务 |
| `clear_succeeded` | POST | 清除所有成功任务 |
| `retry_failed` | POST | 重试所有失败任务 |

---

## 4. Share API (`/api/share`)

源文件: `ShareEndpoints.cs`  
**所有端点**: 需要授权，Guest 禁止

### 4.1 GET|POST `share/list` — 列出分享

**逻辑**: 管理员看所有分享，普通用户看自己的

### 4.2 GET `share/get` — 获取分享详情

**逻辑**: 仅管理员或创建者可访问

### 4.3 POST `share/create` — 创建分享

**逻辑流程**:
1. Guest 禁止
2. 至少 1 个文件
3. 验证自定义 ID（≤64 字符，字母/数字/下划线/连字符/CJK）
4. 管理员可指定 CreatorName
5. 非管理员需要 `CanShare` 权限，自定义 ID 需要 `CanCustomizeShareID`
6. 非管理员分享路径必须在 BasePath 下

### 4.4 POST `share/update` — 更新分享

**逻辑**: 仅管理员或创建者可更新；支持 NewID 变更（需权限）

### 4.5 POST `share/delete` — 删除分享

### 4.6 POST `share/enable` — 启用分享

### 4.7 POST `share/disable` — 禁用分享

---

## 5. Task API (`/api/task`)

源文件: `TaskEndpoints.cs`  
**所有端点**: 需要授权

路由模式: `/api/task/{taskType}/{action}`  
taskType: `upload`, `copy`, `move`, `offline_download`, `offline_download_transfer`, `decompress`, `decompress_upload`

与 Admin 任务管理结构相同，但:
- **普通用户只能看到/操作自己的任务**（按 CreatorId 过滤）
- 管理员可看/操作所有任务
- `clear_done` / `clear_succeeded` / `retry_failed` 传入 userId 过滤

---

## 6. Me API (`/api/me`)

源文件: `MeEndpoints.cs`  
**所有端点**: 需要授权

### 6.1 GET `/api/me` — 获取当前用户信息

**逻辑**: 从 Claims 获取 userId → 查询用户 → Mapster 映射为 `UserResp`

### 6.2 POST `/api/me/update` — 更新个人资料

**逻辑**: Guest 禁止；可更新 username、password、sso_id

### 6.3 GET `/api/me/sshkey/list` — 列出 SSH 密钥

### 6.4 POST `/api/me/sshkey/add` — 添加 SSH 密钥

**请求体**: `AddSshKeyReq { name, key }`

### 6.5 POST `/api/me/sshkey/delete` — 删除 SSH 密钥

---

## 7. Public API (`/api/public`)

源文件: `PublicEndpoints.cs`  
**所有端点**: 公开

### 7.1 GET|POST `public/settings` — 获取公共设置

**返回**: 站点标题、Logo、Favicon、Footer、自定义 Head、文件类型列表、SSO 配置、LDAP 配置、WebAuthn 配置、分享预览设置等

### 7.2 GET|POST `public/offline_download_tools` — 离线下载工具列表

**返回**: `["aria2", "qBittorrent"]`

### 7.3 GET|POST `public/archive_extensions` — 支持的压缩包扩展名

**返回**: `[".zip", ".tar", ".tar.gz", ...]`

---

## 8. Sign Routes (签名下载)

源文件: `SignEndpoints.cs`  
**所有端点**: 公开（通过签名参数验证权限）

### 8.1 GET|HEAD `/d/{*path}` — 签名下载

**参数**: `sign_query` (查询参数)

**逻辑流程**:
1. 验证签名 (`sign.Verify(signValue, path)`)
2. 检查 ShouldProxy（MustProxy / WebProxy / proxy_types 扩展名匹配）
3. 需要代理 → 转为代理模式
4. 否则获取链接: HTTP → 302 重定向；本地文件 → 直接返回文件（支持 Range）

### 8.2 GET|HEAD `/p/{*path}` — 签名代理

**逻辑**: 验证签名 → 获取链接 → HTTP 链接通过 HttpClient 代理转发流；本地文件直接返回

### 8.3 GET|HEAD `/ad/{*path}` — 签名压缩包下载

**参数**: `inner`（内部路径）, `pass`（压缩包密码）

**逻辑**: 验证 archive 签名 → 无 inner 则下载压缩包本身；有 inner 则提取内部文件返回流

### 8.4 GET|HEAD `/ap/{*path}` — 签名压缩包代理

与 `/ad` 类似，但代理模式（支持 Range）

### 8.5 GET|HEAD `/ae/{*path}` — 签名压缩包提取下载

**逻辑**: 验证 archive 签名 → 提取内部文件 → 作为附件返回

### 8.6 GET `/i/{linkName}` — iOS Plist 生成

**逻辑**: Base64 解码 linkName → 解析 URL 和文件名 → 生成 Apple OTA 安装 plist XML

---

## 9. Sharing Download Routes

源文件: `SharingDownloadEndpoints.cs`  
**所有端点**: 公开（通过分享密码验证）

### 9.1 GET|HEAD `/sd/{sid}/{*path?}` — 分享文件下载

**逻辑流程**:
1. 获取分享，验证有效性（Disabled / MaxAccessed / Expires / Files）
2. 验证密码（`pwd` 查询参数或 `X-Sharing-Password` 头）
3. 多文件分享不允许下载根路径
4. 递增访问计数
5. 获取链接 → 重定向或直接返回文件

### 9.2 GET|HEAD `/sad/{sid}/{*path?}` — 分享压缩包下载

**逻辑**: 需 `ShareArchivePreview` 配置启用；支持 `inner` 和 `pass` 参数提取压缩包内文件

---

## 10. S3 兼容 API (`/s3`)

源文件: `S3Endpoints.cs`  
**认证**: AWS Signature V4/V2 或 Bearer Token 或公开访问

### 10.1 GET `/s3` — ListBuckets

**逻辑**: 列出所有存储作为 S3 Buckets

### 10.2 HEAD `/s3/{bucket}` — HeadBucket

**逻辑**: 检查 bucket（存储挂载路径）是否存在

### 10.3 GET `/s3/{bucket}` — ListObjects / GetBucketVersioning

**逻辑**: `?versioning` → 返回空版本配置；否则列出对象（支持 prefix, max-keys）

### 10.4 GET `/s3/{bucket}/{*key}` — GetObject / GetObjectAcl

**逻辑**: `?acl` → 返回固定 ACL XML；否则获取文件链接并重定向或返回文件

### 10.5 HEAD `/s3/{bucket}/{*key}` — HeadObject

**逻辑**: 返回 Content-Length、Content-Type、Last-Modified、ETag

### 10.6 PUT `/s3/{bucket}/{*key}` — PutObject / UploadPart

**逻辑**:
- `?uploadId&partNumber` → 分片上传: 保存到临时文件，计算 MD5 ETag
- `?acl` → 接受兼容请求
- 否则 → 普通 PutObject: 调用 `fs.PutAsync`

### 10.7 POST `/s3/{bucket}/{*key}` — InitiateMultipartUpload / CompleteMultipartUpload

**逻辑**:
- `?uploads` → 生成 uploadId，创建内存状态
- `?uploadId` → 按序合并所有分片临时文件 → `fs.PutAsync` → 清理临时文件

### 10.8 DELETE `/s3/{bucket}/{*key}` — DeleteObject / AbortMultipartUpload

**逻辑**: `?uploadId` → 中止分片上传并清理临时文件；否则 → `fs.RemoveAsync`

---

## 11. WebDAV API (`/dav`)

源文件: `WebDavEndpoints.cs`  
**认证**: WebDavAuthMiddleware 处理（Basic Auth / Bearer Token）

### 11.1 GET|HEAD `/dav/{*path}` — 获取文件/目录

**逻辑**:
1. ResolvePath 校验用户 BasePath 限制
2. 目录 → PROPFIND 结果
3. 文件 → 根据 WebdavPolicy:
   - `302_redirect` → 重定向到直链
   - `use_proxy_url` → 重定向到 DownProxyURL
   - `native_proxy` → 服务端代理（支持 Range，ProxyRange 转发 Range 头）

### 11.2 PUT `/dav/{*path}` — 上传文件

**逻辑**: 校验父目录存在 → `fs.PutAsync`

### 11.3 DELETE `/dav/{*path}` — 删除

**逻辑**: `fs.RemoveAsync`

### 11.4 MKCOL `/dav/{*path}` — 创建目录

**逻辑**: `fs.MakeDirAsync`

### 11.5 COPY `/dav/{*path}` — 复制

**逻辑**: 从 `Destination` 头解析目标路径 → `fs.CopyAsync`

### 11.6 MOVE `/dav/{*path}` — 移动

**逻辑**: 从 `Destination` 头解析目标路径 → `fs.MoveAsync`

### 11.7 PROPFIND `/dav/{*path}` — 属性查询

**逻辑**: 返回 DAV multistatus XML（resourcetype、displayname、contentlength、lastmodified）；Depth 控制递归深度

### 11.8 PROPPATCH `/dav/{*path}` — 属性修改

**逻辑**: 返回空的 207 multistatus（不实际修改）

### 11.9 LOCK / UNLOCK — 锁定

**逻辑**: LOCK 返回空 lockdiscovery；UNLOCK 返回 204

### 11.10 OPTIONS `/dav/{*path}` — 能力查询

**逻辑**: 返回 Allow 和 DAV 头

---

## 中间件管道

| 中间件 | 功能 |
|---|---|
| `CorsMiddleware` | 处理 OPTIONS 预检请求 |
| `RateLimitMiddleware` | 100 请求/分钟/IP 限流 |
| `RequestLoggingMiddleware` | 请求日志记录 |
| `ForceHttpsMiddleware` | 强制 HTTPS 重定向 |
| `StoragesLoadedMiddleware` | 确保存储已加载 |
| `MaxConnectionsMiddleware` | 最大并发连接限制 |
| `DownloadRateLimiterMiddleware` | 下载速率限制 |
| `UploadRateLimiterMiddleware` | 上传速率限制 |
| `FsUpMiddleware` | 上传权限中间件 |
| `WebDavAuthMiddleware` | WebDAV Basic Auth / Bearer 认证 |
