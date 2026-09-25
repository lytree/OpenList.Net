# OpenList Go 上游同步对比报告

- **对比日期**: 2026-09-24
- **上游仓库**: https://github.com/OpenListTeam/OpenList （本地 `F:/Code/Dotnet/OpenList`）
- **版本区间**: `3a31b438` → `54ae9d74`，共 **12 个新提交**
- **当前 .NET 项目**: `F:/Code/Dotnet/OpenList.Net` @ `5d40e71`

---

## 一、需要跟进的差异（行动项）

### 1. aliyundrive_open：代理下载回调并发限制（893457cd）⚠️ 功能缺口

Go 侧新增 `drivers/aliyundrive_open/callback.go`（295 行）+ 大量测试：

- 按阿里云**用户身份**共享回调准入（admission）信号量，permit 覆盖整个响应体生命周期
- 仅对"确认是回调容量不足"的拒绝做重试，保留直连重定向与服务端下载限流
- 回调容量耗尽映射为 **S3 SlowDown** 响应
- 同一提交还包含：**link cache 按 redirect/proxy 模式区分 key**（避免代理↔直连缓存互串）、proxy 侧"先关闭上一个 range body 再开下一个"
- driver 新增配置项 `CallbackConcurrency`（meta.go），`Link()` 非 redirect 模式下挂 `RateLimitRangeReader` 回调读端

**.NET 现状**: `AliyundriveOpenDriver.cs` 仅 246 行，**完全没有 callback/代理下载回调机制，也没有 link cache**。若要完整对齐 aliyundrive_open 的 proxy 下载行为（阿里云盘对回调有并发配额限制，超限会 403/限速），这是当前最大的功能缺口。

### 2. pikpak：token 过期自动账密重登（e73a80c7）⚠️ 驱动缺口

Go 侧改动（`drivers/pikpak/util.go`）：

- 登录前**清空过期 AccessToken**（避免 captcha 请求携带失效 bearer）并**始终刷新 captcha token**
- 登录/刷新后校验返回 token 非空（空 token 直接报错，防止写坏存储）
- 登录成功后回写 `Addition.RefreshToken` 并 `MustSaveDriverStorage` 持久化
- 错误码 4122/4121/16（access_token 过期）与 9（captcha 过期）时，对 `/v1/auth/`、`/v1/shield/captcha/` 自身请求**不再递归重试**，防止无限循环

**.NET 现状**: 无 PikPak 驱动。若后续移植，需把上述防死循环 + 空 token 校验 + token 回写持久化一并带上。

### 3. teldrive：listing 使用有效根路径（cadbf872）⚠️ 驱动缺口

Go 侧修复 teldrive 驱动列目录时的根路径取值。**.NET 无 teldrive 驱动**，仅在决定移植该驱动时相关。

### 4. fs/get 相关对象（related objects）仅视频文件触发（9de3f69b）ℹ️ 功能缺口

Go 侧 `fs/get` 响应中的 `related` 字段改为：**仅当目标是非目录且扩展名属于 VIDEO 类型时**，才列出同目录相关对象（原先对所有文件都做一次 `fs.List`，纯浪费）。

**.NET 现状**: `FsEndpoints` / `FileSystemService` 中未找到 related objects 实现，`fs/get` 本身就不带该字段。要么接受功能缺失，要么移植时按新逻辑（仅视频）实现。

---

## 二、已确认无需跟进（.NET 已对齐或架构不同）

| 提交 | 内容 | 结论 |
|---|---|---|
| 54ae9d74 | searchnode 删除路径 bug：Go `stdpath.Split("a/b")` 返回带尾斜杠的 dir，导致删除条件 `parent = "a/"` 永远匹配不上 | .NET `SearchService.RemovePathAsync` 用 `NormalizeParent` + `Path == / Parent == / StartsWith("/")` 整树删除，无此 bug，**无需跟进** |
| 6c600910 | fs/get 目录返回 FOLDER 类型 | .NET `GetObjType` 已是 `isDir ? TypeFolder : GetFileType(name)`，**已对齐** |
| b51d1c82 | S3 GetObject range 响应体关闭 | Go 的问题是 range body 打开下一个前未关上一个；.NET 走本地 Zio 流 + `Results.Stream`（框架负责释放，`RangeStream` 包裹内层流），架构不同，**暂无需跟进**（建议顺手确认 `RangeStream.Dispose` 会级联释放内层流） |
| f286862c | stream parked readers 按 offset 索引替代全量扫描 | Go `stream.Parker` 特有机制，.NET 无对应实现，**N/A** |
| 90acfa18 | context 重构：集中请求来源（origin）访问 | Go 内部重构，.NET 用 `HttpContext`，**N/A** |
| 1462d63a | 分段下载保留取消错误 | Go `internal/net` 多分片下载场景；.NET 下载为单流转发（`FileDownloadHelper`），**暂不适用**；将来若做并行分片下载需注意 |
| 40f4f654 | FTP 服务器上传：EOF 处理 + 按实际读取字节数探测 content-type | Go 是 FTP **服务端**（`server/ftp/fsup.go`）；.NET 只有 FTP **客户端**驱动，无 FTP 服务器，**N/A** |
| c16701b9 | CI 构建脚本（cgo-actions 版本） | Go 构建链，**无关** |

---

## 三、结论

本次上游 12 个提交里，**对 .NET 移植有实质影响的是 4 项功能/驱动缺口**：

1. **aliyundrive_open 回调并发限制 + link cache redirect/proxy 分离** —— 优先级最高（影响真实使用稳定性）
2. **PikPak 驱动**（含自动重登录）—— 整个驱动缺失
3. **teldrive 驱动** —— 整个驱动缺失
4. **fs/get related objects（仅视频）** —— 小功能，可做可不做

其余 8 项均为 Go 特有实现细节修复或内部重构，.NET 侧现有实现不受影响。

> 建议：下一步优先补齐 aliyundrive_open 的 callback 限流机制（对齐 `CloudDriverBase` 现有的 `RequestRateLimiter` 风格），link cache（含 redirect/proxy key 分离）可作为配套一起设计。
