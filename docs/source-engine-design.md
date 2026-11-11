# 通用数据源引擎（Source Engine）设计

> 目标：在 OpenList.Net 内提供一个"一个引擎、N 个数据源"的拉取层 —— 数据源可插拔（声明式清单或代码插件），
> 每个数据源可独立配置请求信封、响应解密、鉴权、限流与缓存，对上统一输出领域对象（媒体条目 / 播放信息 / 通用条目）。
>
> 参考项目：[HGhub](https://github.com/qfeng3168/HGhub)（Rust + Tauri 2）。本文只借鉴其**架构思路**，
> 不复制其代码（该项目自 v1.1.0 起为 PolyForm Noncommercial 1.0.0，且其实现高度绑定单一上游站点）。

---

## 1. 结论（TL;DR）

1. **不要把它做成"存储驱动"**。存储驱动的契约是 `IFileSystem`（路径读写），而数据源是"按业务语义拉取并解密"，
   两者生命周期、错误语义、缓存键都不同。新增独立模块 `OpenList.Sources`，用与 `MediaModule` 相同的
   "`AddSourceModule()` / `MapSourceModule()`" 方式挂载，保持可整块移除。
2. **一个引擎 = 一条固定流水线**：`解析源 → 传输 → 拆信封 → 解码 → 解密 → 映射 → 缓存`。
   每个环节都是接口，数据源只提供"参数"或"实现其一"。
3. **解密必须是可组合的密码管线**，而不是 `if (站点 == X) AES(...)`：
   `base64 → gzip → aes-cbc(pkcs7) → 嗅探魔数`，支持多密钥轮换、明文直通（magic 嗅探）与失败回退。
4. **声明式清单（JSON）+ 代码插件**双轨：常见"信封 + AES 封面 + HLS"型站点零代码接入；
   非标站点写一个 `ISourceAdapter` 子类，靠程序集扫描自动发现（复用 `MountService` 的发现范式）。
5. **安全默认值**：仅 HTTPS、主机白名单、DNS 解析后固定地址（防 SSRF/DNS rebinding）、重定向逐跳复检、
   密钥永不回显、代理 URL 使用 HMAC 签名防开放代理、幂等请求才重试。
6. **落地顺序**：抽象 + 清单 + 信封 + AES + 管理 CRUD + 探针（P0）→ HLS 代理 + 封面缓存 + 媒体域映射（P1）
   → 代码插件 + 多密钥/熔断/指标（P2）。每阶段都能独立验收，且 P0 不依赖任何真实外部站点（用本地假上游做测试）。

---

## 2. 当前项目分析：可复用的地基与缺口

### 2.1 可直接复用的四块地基

| 地基 | 位置 | 在引擎中的用途 |
|---|---|---|
| 程序集自动发现 | [MountService.cs](file:///e:/github/OpenList.Net/src/OpenList.Infrastructure/Services/MountService.cs) 扫描 `OpenList*` 程序集中的 `DriverBase` 子类 | 同样扫描 `ISourceAdapter` 子类，新增源 = 新建一个类 |
| 配置 Schema 驱动 UI | [DriverBase.cs](file:///e:/github/OpenList.Net/src/OpenList.Core/Drivers/Base/DriverBase.cs) 的 `ConfigItems` + `DriverConfigItemAttribute` | 数据源清单表单复用同一套"后端声明字段、前端渲染表单"的模式 |
| 模块化领域挂载 | [MediaModule.cs](file:///e:/github/OpenList.Net/src/OpenList.Media/MediaModule.cs) 的 `AddMediaModule/MapMediaModule`，含命名 `HttpClient`、Scoped/Singleton 生命周期约定、`IHostedService` | 新模块照此封装；`AddHttpClient("source:{id}")` 与现有 `tmdb`/`opensubtitles` 同构 |
| 网络与限流积木 | [CloudHttpClient.cs](file:///e:/github/OpenList.Net/src/OpenList.Drivers/Cloud/CloudHttpClient.cs)、[RequestRateLimiter.cs](file:///e:/github/OpenList.Net/src/OpenList.Drivers/Cloud/RequestRateLimiter.cs)、[OAuthTokenRefresher.cs](file:///e:/github/OpenList.Net/src/OpenList.Drivers/Cloud/OAuthTokenRefresher.cs)、`StorageAdditionSink`（token 回写） | 传输层、每秒请求数限流、OAuth 刷新与持久化直接复用，不重写 |

另外：设置分组见 [SettingItem.cs](file:///e:/github/OpenList.Net/src/OpenList.Core/Models/SettingItem.cs)（现有 0–14 组），
新引擎建议新增 `SettingGroup.Source = 15`；后台长任务走现有 `TaskService` / `IOfflineDownloadTool` 模式；
管理端页面沿用 `frontend/src/pages/manage/*` 与 `ManageLayout` 导航。

### 2.2 现有缺口

- `OpenList.Media` 的元数据只来自 TMDB/本地 NFO（`IMetadataProvider`），**没有"外部内容源"概念**；
  媒体库是"扫描本地挂载目录"，不是"从上游拉条目"。
- 没有任何**响应信封**抽象（当前上游是标准 REST/JSON）。
- 没有任何**通用解密**抽象（缩略图/播放链路都不需要 AES 解封面）。
- 没有"按源隔离的 HTTP 策略"（超时/代理/限流/重试），现有各驱动各自为政。

---

## 3. 参考项目 HGhub 拆解：值得学的与不能照搬的

### 3.1 它的实际结构

| 层 | 实现 | 说明 |
|---|---|---|
| 线路切换 | `net.rs` 的 `BASE` + `line.json` + `normalize_base` + `trusted_auth_origin` | 上游有多个镜像；登录/注册只允许固定线路，镜像仅游客浏览 |
| 请求信封 | `POST /api/edge/request`，body 为 `{method,path,body(Base64),content_type}` | 所有业务接口收敛为一个入口；返回 `{status,data,msg}`，`status==1` 才算成功 |
| 重试 | `with_retry`：仅 GET / 无副作用 POST 重试；408/425/429/5xx 可重试，401/403/404 立即失败 | 逐跳重建客户端，代理隧道错误会 `heal_client` |
| 网络安全 | `network_policy.rs`：HTTPS、禁内网/回环、DNS 解析后固定地址、重定向逐跳复检 | 这是最值得照搬的部分 |
| 封面解密 | `media.rs`：AES-128-CBC + 固定 key/IV + 去 PKCS#7 填充；**先用魔数嗅探，已是明文则跳过解密** | 密钥硬编码在客户端，属于"防抓取"而非真安全 |
| HLS 代理 | 重写 m3u8 中 `KEY/MAP/MEDIA/PART` 等所有 `URI=`；子清单继续重写；key/分片内存缓存 | 绕过 WebView 的 CORS；AES-128 HLS 由播放器拿 key 自行解密 |
| 可观测性 | 结构化播放日志，只记录资源 ID 哈希、不记录搜索词/账号/token | 脱敏思路可借鉴 |

### 3.2 结论

- **值得学**：单入口信封、线路抽象、网络策略、幂等重试、魔数嗅探直通、HLS URI 全量重写、日志脱敏。
- **不能照搬**：
  - 硬编码 AES key/IV（应做成"源配置 + 密钥轮换"）；
  - Base64 信封被当成"加密"（它只是编码，必须明确分层：编码 ≠ 加密 ≠ 鉴权）；
  - 单站点硬编码路径（本引擎要允许多源共存）；
  - 许可证：代码不可复制，仅参考架构。

---

## 4. 引擎总体设计

### 4.1 分层

```
┌────────────────────────── 消费方 ──────────────────────────┐
│  Media 域（外部条目 / 播放）   管理端 API   通用业务端点      │
└──────────────────────────────┬──────────────────────────────┘
                               │ ISourceQueryService / ISourceRegistry
┌──────────────────────────────▼──────────────────────────────┐
│                    OpenList.Sources（新项目）                │
│                                                              │
│  SourceRegistry ── 清单加载/校验/热更新，代码插件扫描         │
│        │                                                     │
│  SourcePipeline（固定七段）                                   │
│   1 Resolve   选中源 + 端点 + 变量替换                        │
│   2 Transport 命名 HttpClient / 代理 / 网络守卫 / 限流 / 重试  │
│   3 Envelope  拆信封（edge-base64 / plain-json / form / ...）  │
│   4 Decode    解编码（base64 / gzip / urlencode）              │
│   5 Decrypt   密码管线（aes-cbc / aes-ctr / rsa / xor / ...）  │
│   6 Map       JSON → DTO（JMESPath 子集或 JSONPath 映射）      │
│   7 Cache     内存 LRU + 磁盘（ETag/TTL），按源隔离             │
│                                                              │
│  横切：SourceSecretStore / SourceRateLimiter / SourceMetrics  │
└───────┬──────────────────────────────────┬───────────────────┘
        │                                  │
┌───────▼──────────┐              ┌────────▼───────────────┐
│ 代码适配器         │              │ 声明式清单（JSON）       │
│ MySiteAdapter     │              │ manifest + 密码参数      │
│ : ISourceAdapter  │              │ （由通用适配器执行）      │
└───────────────────┘              └────────────────────────┘
```

原则：

- **一个源一个 `HttpClient` 配置**（命名客户端 `source:{id}`），互不污染连接池与超时。
- **单例只做无状态/运行态**（Registry、限流器、指标），**Scoped 才碰 DbContext**（与媒体域约定一致）。
- **任何上游失败都不冒泡成 500**：统一转 `SourceResult<T>`（`Ok / SourceError(code,message,retryable)`），
  由消费方决定降级（返回空列表 / 提示 / 重试）。

### 4.2 关键接口（P0 骨架）

```csharp
namespace OpenList.Sources.Abstractions;

/// 数据源描述：清单与代码插件共用同一运行时模型。
public sealed record SourceDescriptor
{
    public required string Id { get; init; }              // 稳定标识，作为缓存/密钥/日志的隔离键
    public required string DisplayName { get; init; }
    public SourceKind Kind { get; init; }                 // Manifest | Code
    public required IReadOnlyList<Uri> BaseUrls { get; init; }
    public EnvelopeSpec Envelope { get; init; } = EnvelopeSpec.None;
    public AuthSpec? Auth { get; init; }
    public CryptoSpec Crypto { get; init; } = CryptoSpec.None;
    public NetworkPolicy Network { get; init; } = NetworkPolicy.Default;
    public int RequestsPerSecond { get; init; }           // <=0 不限
}

/// 代码插件：非标站点实现它；标准站点由 ManifestAdapter 统一执行。
public interface ISourceAdapter
{
    string Name { get; }                                  // 自动发现用（如 "MySite"）
    Task<SourceProbeResult> ProbeAsync(SourceContext ctx, CancellationToken ct);
    Task<SourcePage<SourceItem>> ListAsync(SourceQuery query, SourceContext ctx, CancellationToken ct);
    Task<SourceItem?> GetAsync(string id, SourceContext ctx, CancellationToken ct);
    Task<PlaybackInfo?> ResolvePlaybackAsync(string id, int episode, SourceContext ctx, CancellationToken ct);
}

/// 请求信封：把"业务调用"包装成上游唯一入口。
public interface IEnvelopeCodec
{
    string Name { get; }                                  // "edge-base64" / "plain-json" / "form"
    HttpRequestMessage Build(EnvelopeSpec spec, SourceRequest request, Uri endpoint);
    Task<JsonElement> UnwrapAsync(HttpResponseMessage response, EnvelopeSpec spec, CancellationToken ct);
}

/// 密码管线：Transform 可串联，支持流式（大文件/播放列表）。
public interface ICryptoTransform
{
    string Name { get; }                                  // "aes-cbc" / "aes-ctr" / "rsa-oaep" / "xor"
    ReadOnlyMemory<byte> Transform(ReadOnlyMemory<byte> input, CryptoContext ctx);
}

/// 密钥来源：字面量 / 设置项 / 环境变量 / 远端 key 端点 / 派生（SHA-256、PBKDF2）。
public interface ISourceKeyResolver
{
    Task<byte[]> ResolveAsync(KeyRef keyRef, SourceContext ctx, CancellationToken ct);
}
```

`SourceContext` 携带 `SourceDescriptor`、当前线路 `BaseUrl`、token、`ILogger`、取消令牌；
**不携带 DbContext**，持久化由外层服务负责。

### 4.3 流水线伪代码

```csharp
public async Task<SourceResult<T>> ExecuteAsync<T>(
    SourceDescriptor source, SourceRequest req, CancellationToken ct)
{
    var endpoint = source.ResolveEndpoint(req.Operation);     // ① 解析 + 变量替换
    var http = _clients.Create(source);                        // ② 每源独立客户端
    var request = _envelopes.Resolve(source.Envelope)
                            .Build(source.Envelope, req, endpoint);

    using var response = await _policy.SendAsync(http, request, source.Network, ct);
    var json = await _envelopes.Resolve(source.Envelope)
                               .UnwrapAsync(response, source.Envelope, ct);
    var bytes = _decoders.Decode(json, source.Envelope.Decoding); // ③ 解码（base64/gzip）
    bytes = await _crypto.RunAsync(bytes, source.Crypto.Response, source, ct); // ④ 解密
    return _mapper.Map<T>(bytes, req.Mapping);                 // ⑤ 映射
}
```

> 封面/播放列表这类二进制资源走另一条短管线：`Transport → 嗅探(已是明文?) → Decrypt → 缓存`，
> 见 §7。

---

## 5. 数据源描述模型

### 5.1 声明式清单（覆盖 80% 的"信封 + AES + HLS"站点）

```jsonc
{
  "id": "demo-edge",
  "displayName": "示例源",
  "baseUrls": ["https://api.example.com"],
  "envelope": {
    "kind": "edge-base64",
    "path": "/api/edge/request",
    "request": { "methodField": "method", "pathField": "path",
                 "bodyField": "body", "contentTypeField": "content_type" },
    "response": { "statusField": "status", "dataField": "data",
                  "messageField": "msg", "successValues": [1] }
  },
  "auth": {
    "mode": "bearer",                       // none | bearer | cookie | header | basic | oauth2
    "loginPath": "/api/account/loginByPassword",
    "loginBody": { "username": "${username}", "password": "${password}" },
    "tokenPath": "data.token"
  },
  "endpoints": {
    "list":   { "path": "/api/videos?page=${page}&size=${size}" },
    "detail": { "path": "/api/videos/${id}" },
    "play":   { "method": "POST", "path": "/api/videos/${id}/episodes/${ep}/playback",
                "body": {} }
  },
  "crypto": {
    "cover": {
      "chain": ["aes-cbc"],
      "key": { "from": "secret", "name": "cover_key" },
      "iv":  { "from": "literal", "value": "aabbccddeeff00112233445566778899" },
      "padding": "pkcs7",
      "sniff": ["jpeg", "png", "gif", "webp"]      // 命中则直通，不解密
    },
    "playlist": { "mode": "hls-rewrite", "keyViaProxy": true }
  },
  "network": { "requireHttps": true, "allowedHosts": ["*.example.com"], "maxRedirects": 3 },
  "requestsPerSecond": 3
}
```

字段来源分三档（`from`）：`literal` / `secret`（加密存库）/ `setting`（公开或私有设置项）。
**清单本身不含密钥明文**，密钥由 `ISourceSecretStore` 单独管理。

### 5.2 代码插件（非标站点）

- 放在 `src/OpenList.Sources.Adapters/`（或同一程序集内），实现 `ISourceAdapter`；
- 启动时扫描 `OpenList*` 程序集，按 `Name` 注册（复用 `MountService.DiscoverDriverTypes` 的思路，
  但要排除抽象类与无参构造失败的类型，并对重复 `Name` 报警）；
- 代码插件可以声明自己**不参与**声明式流水线，但仍复用传输/限流/网络策略/缓存。

### 5.3 多线路（镜像）

- `SourceDescriptor.BaseUrls` 是有序候选；运行态记录"当前可用线路"；
- 线路切换必须重新校验 token 归属（参考 HGhub `trusted_auth_origin`：**凭据只发往受信线路**）；
- 探针接口逐线路测速/可用性，失败自动切下一条（可选，默认手动）。

---

## 6. 解密子系统（重点）

### 6.1 分层：编码、加密、鉴权三者分开

| 层 | 例子 | 正确做法 |
|---|---|---|
| 编码 | Base64、URL encode、gzip | 归 `IDecoder`，不参与"加密"语义 |
| 加密 | AES-CBC/CTR/ECB、SM4、RSA、XOR | 归 `ICryptoTransform` 链 |
| 鉴权/完整性 | HMAC-SHA256、签名 query（`auth_key=exp-...`） | 归 `ISignatureVerifier` + 传输层 |

HGhub 的 `body` Base64 只是编码；把它叫"信封"合适，叫"加密"会误导后续维护者。

### 6.2 密码链（Chain）设计

`CryptoSpec.Chain` 是**有序变换列表**，顺序 = 解密顺序：

```jsonc
// 例：先 Base64 解码，再 gzip 解压，最后 AES-128-CBC 解密
"response": {
  "chain": ["base64", "gzip", "aes-cbc"],
  "key": { "from": "secret", "name": "api_key" },
  "iv":  { "from": "literal", "value": "..." },
  "padding": "pkcs7"
}
```

每条变换的输入/输出都是 `byte[]`（`ReadOnlyMemory<byte>`），便于与 `Stream` 版本共用实现。
大对象（播放列表/分片）走 `ICryptoTransform.TransformStream`，避免整块 `byte[]`。

内置实现（P0 只做前四个，其余按需）：

| 名称 | 说明 | 备注 |
|---|---|---|
| `base64` / `gzip` / `deflate` / `urlencode` | 解码链 | 与加密链共用接口，但语义归 `IDecoder` |
| `aes-cbc` | AES-128/192/256-CBC + PKCS7/None/Zero | 支持 key/iv 显式或由 key 派生 iv |
| `aes-ecb` | 无 IV | 部分站点用 |
| `aes-ctr` | 流式 | 适合大文件 |
| `sm4-cbc` | 国产站点常见 | 需引入 BouncyCastle |
| `rsa-oaep` / `rsa-pkcs1` | 解会话密钥 | 用于"每次请求随机密钥"场景 |
| `xor` | 单字节/循环 XOR | 只作兼容，标记为弱 |
| `chacha20` | 少量源 | 可选 |
| `custom:{id}` | 代码插件注册 | 无法声明式描述时 |

### 6.3 密钥管理

- **来源优先级**：`secret`（加密存库）> `setting` > `literal` > 环境变量；
- **多密钥轮换**：`keys: [{id, value, validFrom, validTo}]`，按时间/尝试顺序逐个试；
- **密钥不落日志**：日志只输出 `keyId` 与指纹前 8 位哈希；
- **静态加密**：`x_source_secrets.value` 用 ASP.NET Data Protection（密钥环落在 `data/keys/`）加密；
  现有 `x_settings` 是明文，本引擎不沿用明文存密钥；
- **管理端回显**：只回 `configured: true/false` 与掩码，永不返回明文（保存时传空表示"不修改"）。

### 6.4 明文直通与失败回退（借鉴 HGhub 的魔数嗅探）

```csharp
static bool LooksLikePlain(ReadOnlySpan<byte> data) =>
    data.StartsWith(new byte[] { 0xFF, 0xD8 }) ||          // JPEG
    data.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 }) || // PNG
    data.StartsWith("GIF8"u8) ||
    (data.StartsWith("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8));

// 顺序：
// 1) 若已命中明文魔数 → 直接返回（避免对明文做一次无意义解密）
// 2) 尝试当前 keyId 解密；PKCS#7 校验失败 → 尝试下一把 key
// 3) 全部失败 → SourceError("decrypt_failed")，并记录脱敏后的 keyId 列表
```

要点：

- 填充校验失败**不能**静默当成"无填充"返回乱码，应作为"该 key 不对"的信号继续尝试；
- 长度不是 16 的倍数时直接判失败（CBC/ECB）；
- 解密后再次魔数嗅探，识别真实 MIME，避免把错误的 `Content-Type` 传给前端；
- 对响应体设上限（如封面 16 MB、JSON 8 MB），防止解压炸弹（gzip 解压后二次限流）。

### 6.5 与播放链路的关系

HLS 的 AES-128 解密**不在服务端做**（那是播放器的事），引擎只做：

1. 把 m3u8 中 `#EXT-X-KEY` / `#EXT-X-MAP` / `#EXT-X-MEDIA` / `#EXT-X-PART` 的 `URI=` 改写到受控代理；
2. 代理 key 请求（key 本身不缓存到公开可枚举路径，按源 + 完整性哈希缓存）；
3. 分片直通（可加 LRU 缓存，上限按字节数而非条数）。

> 若未来需要服务端转封装/解密 DRM，那是独立子系统，不应混入本引擎（合规风险也更高）。

---

## 7. 传输、安全与缓存

### 7.1 网络守卫（必须默认开启）

对应 HGhub `network_policy.rs`，在 .NET 侧用 `SocketsHttpHandler.ConnectCallback` 做"解析后固定地址"：

- 仅允许 `https`（清单可显式 `requireHttps=false` 作为兼容开关，默认禁止）；
- 主机白名单 `allowedHosts`（支持 `*.example.com`），未知主机拒绝；
- 解析结果拒绝：回环、私网（10/8、172.16/12、192.168/16、169.254/16）、IPv6 ULA/链路本地、`::1`；
- **DNS rebinding 防护**：解析一次，连接时只连已校验的 IP（TTL 30s 后重新校验）；
- 重定向：禁用自动跟随（`AllowAutoRedirect=false`），逐跳复用同一套校验，最多 3–5 跳；
- 代理：`Source.Network.Proxy` 或全局设置，代理 URL 在日志中只保留 `scheme://host:port`。

### 7.2 重试

| 场景 | 行为 |
|---|---|
| GET / 无副作用 POST（如取播放地址） | 最多 3 次 |
| 有副作用（登录、收藏、评论） | 1 次，不重试 |
| 401/403/404 | 立即失败，不重试 |
| 408/425/429/5xx、超时、连接重置 | 指数退避 + 抖动（350ms 起） |
| 安全拒绝（内网/降级/重定向非法） | 立即失败，不重试 |

### 7.3 限流与并发

- 复用 [RequestRateLimiter.cs](file:///e:/github/OpenList.Net/src/OpenList.Drivers/Cloud/RequestRateLimiter.cs)（令牌桶，容量 1）；
- 每源 `RequestsPerSecond`；全局并发上限取设置项（默认 8）；
- 429 时读 `Retry-After`（上限 60s），并临时降速（自适应，P2）。

### 7.4 缓存

| 对象 | 介质 | 键 | TTL |
|---|---|---|---|
| JSON 列表/详情 | 内存 LRU（每源上限） | `source:{id}:{operation}:{normalizedQuery}` | 60s–5min，可配置 |
| 封面 | 内存 + 磁盘（`data/source-cache/{id}/{sha256}`） | URL 的 SHA-256 | URL 变化即失效；24h |
| m3u8 | 内存 | `source:{id}:playlist:{url}` | 60s |
| key | 内存/磁盘 | `source:{id}:key:{sha256(url)}` | 24h |
| 分片 | 内存 LRU（按字节上限） | URL 哈希 | 1h |

缓存必须按 `sourceId` 隔离，避免不同源同 URL 串味；磁盘缓存挂在 `data/` 下并纳入现有备份白名单（或明确排除）。

---

## 8. 数据模型与 API

### 8.1 EF 表（新增，两个 Provider 各一份迁移）

```
x_sources
  Id(text, pk) | DisplayName | Kind(int) | ManifestJson(text, null) | Enabled | Order
  LastProbeAt | LastProbeStatus | LastError(text, null) | CreatedAt | UpdatedAt

x_source_secrets
  Id(pk) | SourceId(fk) | Name | ValueEncrypted(text) | UpdatedAt
  unique(SourceId, Name)
```

> 不要复用 `x_storages`：存储有挂载路径、驱动名、Addition 等语义，混用会让备份/导入导出与权限判定复杂化。
> 迁移按 AGENTS.md 的约定：`OPENLIST_DB_PROVIDER=sqlite|postgres dotnet ef migrations add ...`，**不要**手写 `CREATE TABLE`。

### 8.2 管理端 API（`/api/admin/source`，Admin）

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/list` | 源列表（含探针状态，密钥只回 `configured`） |
| GET | `/get?id=` | 源详情（清单 + 密钥掩码） |
| POST | `/create` / `/update` | 清单校验（JSON Schema）+ 保存；`Kind=Code` 时只存启用状态与参数 |
| POST | `/delete?id=` | 删除（级联删密钥与缓存目录） |
| POST | `/enable` / `/disable` | 启停 |
| POST | `/probe?id=` | 探针：逐线路测连通性、鉴权、一次最小列表拉取 |
| POST | `/secret/set` | 写密钥（空值 = 不修改；显式 `clear=true` 才删除） |
| GET | `/adapters` | 已发现的代码插件（Name/DisplayName/ConfigItems） |

### 8.3 业务端 API（`/api/source`，登录 + 权限/访客开关）

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/list` | 已启用的源（id、名称、能力位：清单/详情/播放/搜索） |
| GET | `/{id}/list?query=&page=` | 统一分页列表 → `SourcePage<SourceItem>` |
| GET | `/{id}/get?itemId=` | 详情 |
| GET | `/{id}/play?itemId=&ep=` | 播放信息（URL 已过白名单/签名改写） |
| GET | `/{id}/proxy?u=&sig=` | 封面/播放列表/分片代理（HMAC 签名，防开放代理） |

响应统一 `Resp<T>`；错误码沿用现有约定（403 权限、404 不存在、502 上游失败、429 限流）。

---

## 9. 与媒体域的集成方式

三种可选姿势，建议 **A + C**：

**A. 作为"外部条目源"接入媒体首页**
- 新增 `ExternalSourceSectionService`：调 `ISourceQueryService.ListAsync`，把 `SourceItem` 映射为轻量卡片 DTO；
- 与现有 `MediaCatalogService` 的聚合 section 并列，避免污染 `MediaItem` 表；
- 播放走 `/{id}/play` 返回的受控 URL（不写入媒体库、不触发扫描）。

**B. 作为 `IMetadataProvider` 的外挂（不推荐）**
- TMDB provider 的契约是"按标题找元数据"，而数据源是"按源拉条目"，语义不同；
- 硬套会导致刮削失败时把上游条目当成 TMDB 结果。

**C. 独立"资源"页（推荐同时做）**
- 前端新增 `#/sources`（用户侧）与 `#/@manage/sources`（管理侧）；
- 用户侧只展示已启用源，支持列表/搜索/播放；
- 管理侧负责清单编辑、密钥、探针、缓存清理。

---

## 10. 前端设计

- 管理页：`frontend/src/pages/manage/sources/`，沿用现有表格 + 抽屉表单风格；
  - 清单编辑：JSON 编辑器（CodeMirror 已在项目内，见 `verify-editor`）+ "校验"按钮；
  - 常用站点用表单模式（信封类型、线路、AES 参数），高级用户切 JSON；
  - 密钥字段：密码框，占位符显示"已配置/未配置"，保存空值不覆盖；
  - 探针结果：逐线路状态码/延迟/错误分类（脱敏）。
- 用户页：`frontend/src/pages/sources/`，卡片网格 + 详情 + 播放器（复用现有播放组件）。
- 文案：`frontend/src/lang/{en,zh}/sources.json`，与 `media.json` 同构。

---

## 11. 安全与合规边界

1. **不做 DRM 破解**：引擎只执行源配置里声明的对称/非对称解密，不实现 Widevine/FairPlay 绕过。
2. **不内置任何第三方站点的密钥或清单**。仓库只提供引擎与示例（指向 `example.com` 的假清单）；
   实际源由部署者自行配置，责任边界在部署者。
3. **参考项目许可证**：HGhub 自 v1.1.0 起为 PolyForm Noncommercial 1.0.0，禁止商业使用；
   本设计不复制其代码，若未来需要借鉴具体实现，必须单独评估许可证兼容性。
4. **SSRF / 开放代理**：见 §7.1 与 §8.3 的 HMAC 签名代理。
5. **密钥与日志**：密钥加密存库、日志脱敏、管理端掩码；`/probe` 失败信息截断 160 字符并去掉 query。
6. **审计**：源的新增/修改/启停/密钥变更写入现有管理操作日志（如无则新增结构化日志事件）。

---

## 12. 分阶段实施与验收

### P0（引擎骨架，不接真实站点也能验收）
- [ ] 新建 `src/OpenList.Sources` + `src/OpenList.Sources.Abstractions`（或合并为一个项目）
- [ ] `SourceDescriptor` / `ISourceAdapter` / `IEnvelopeCodec` / `IDecoder` / `ICryptoTransform` / `ISourceKeyResolver`
- [ ] 内置 `plain-json`、`edge-base64` 信封；`aes-cbc`、`base64`、`gzip` 变换
- [ ] 网络守卫（HTTPS/白名单/私网拒绝/重定向复检）+ 每源 HttpClient + 限流 + 重试
- [ ] `x_sources` / `x_source_secrets` + 双 Provider 迁移 + Data Protection 加密
- [ ] `/api/admin/source/*` CRUD + 探针；`/api/source/{id}/list|get`
- [ ] 单测：信封往返、AES 已知明文/密文、魔数直通、错误 key 回退、SSRF/重定向拒绝、重试策略
- [ ] 本地假上游（Kestrel `WebApplicationFactory`）跑通端到端，不依赖公网

### P1（媒体能力）
- [ ] 封面代理 + 磁盘缓存 + MIME 嗅探
- [ ] m3u8 全量 URI 重写 + key/分片代理 + 签名 URL
- [ ] 媒体首页"外部源"section + 用户侧 `#/sources` 页
- [ ] e2e：`frontend/scripts/verify-sources.mjs`（假上游 + AES 封面 + m3u8 + 签名代理）

### P2（规模化）
- [ ] 代码插件自动发现 + `/adapters`
- [ ] 多密钥轮换、熔断、自适应降速、指标（每源成功率/延迟）
- [ ] 清单 JSON Schema + 前端表单双模式编辑
- [ ] 计划任务：按 `TaskService` 定时刷新/预取（可选）

### 验收口径
- 新增一个"信封 + AES 封面 + HLS"型数据源**只写 JSON**，不改代码；
- 换一个非标站点**只新增一个 `ISourceAdapter` 类**，不改引擎；
- 关闭/删除某源不影响其他源与媒体库；
- 所有解密失败都能定位到 `sourceId + keyId + stage`，且日志不含密钥明文。

---

## 13. 风险与待决事项

| 风险 | 缓解 |
|---|---|
| 上游接口非版本化、随时变更 | 清单可热更新；端点/字段映射全部外置；探针 + 失败告警 |
| 站点用 JS 加密/挑战（如 `sign` 参数） | P2 预留 `custom:{id}` 插件；不引入浏览器内核到主进程 |
| 密钥硬编码在客户端仍会泄露 | 说明这是"防抓取"不是"安全"；密钥放服务端 + 轮换 + 限流 |
| 版权/ToS 风险 | 不内置源与密钥；文档明确部署者责任；不做 DRM 绕过 |
| 缓存膨胀 | 按源/按字节上限 + 后台清理 HostedService + 管理端"清缓存" |
| 多实例部署 | 缓存与限流改为进程内（可接受）；如需共享，密钥/清单在 DB，缓存可换 Redis（后续） |

**待决**：
1. 该引擎的首个落地场景是"媒体首页外部源"还是"通用条目 API"？
2. 是否允许 HTTP 源（默认禁止）？
3. 代码插件放在同一程序集（简单）还是独立项目（可热插拔）？
