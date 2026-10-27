# 统一新产品改造方案 v3（Vue 3 + TSX + Element Plus）

> **v3 修订（2026-10-25）**：前端定为 **Vue 3 + TSX（vue-jsx）+ Element Plus**，包管理 pnpm，**渲染以 VDOM 为优先**。React 方案（v2）作废。
> 已锁定约束：**硬切冻结**（SolidJS 冻结，仅修致命 bug）、**全量对齐**（新前端覆盖现有全部功能后才切换）。
> **后端方案不受影响**：`/api/media` 媒体域建设（第 7 节）可并行推进。
>
> **M0 已验证**：`frontend-vue/` 可构建、类型检查零错误、无头浏览器真实渲染通过（Element Plus 表格/表单项/右键菜单全部正常，控制台零告警）。
>
> **路径说明（M5 之后）**：`frontend-vue/` 已改名为 `frontend/`，旧 SolidJS 子模块退到 `frontend-legacy/`（见第 32 节）。
> 第 11–31 节写于改名之前，正文里出现的 `frontend-vue/` 均指今天的 `frontend/`；可执行的命令路径已统一改过。


## 0. 决策定案

| 决策点 | 定案 | 说明 |
|---|---|---|
| 框架 | **Vue 3.6（当前 3.6.0-rc.12）+ TypeScript + Vite 8** | 团队长期主栈 |
| 编写方式 | **TSX（vue-jsx 3.3.0-rc.4）**，不用 SFC 模板 | 组件即函数，统一 TSX |
| 渲染模式 | **VDOM 优先**：`vapor: false` 全量 VDOM 编译 | 主线零 Vapor 特技；插件生态完全兼容；主包 172.79 KB |
| Vapor | **仅作可选优化通道**（`*.vapor.tsx`），不进入首版范围 | 启用三步见第 3 节，已实测可行 |
| UI 库 | **Element Plus 2.14.7** + `@element-plus/icons-vue` | 表格/表单/弹窗/右键菜单全覆盖 |
| 包管理 | **pnpm 12** | 与仓库既有前端一致 |
| 起点代码 | **MIT 纯净重写（唯一路径）** | Peach 是 React，Vue 路线下复用为零，许可证问题消失 |
| 切换策略 | 硬切冻结 | SolidJS 移入 `frontend-legacy/` |
| 首版范围 | 全量对齐 | 按第 6 节验收表勾验 |
| 输出路径 | 必须保持 `frontend/dist` | Dockerfile / `build.cs` / Desktop 三处引用零改动 |

## 1. 技术栈（已安装并验证）

| 层 | 选型 | 版本 |
|---|---|---|
| 运行时 | vue | **3.5.22**（稳定版；原 3.6.0-rc 因 EP peer 冲突降级，见第 21.3 节） |
| TSX 编译 | vue-jsx | 3.3.0-rc.4（**精确锁定**） |
| 构建 | vite | 8.3.5 |
| UI | element-plus / icons | 2.14.7 / 2.3.2 |
| 路由 | vue-router | 4.6.4 |
| 状态 | pinia | 3.0.4 |
| 服务端状态 | @tanstack/vue-query | 5.104.5 |
| 工具 | @vueuse/core | 12.8.2 |
| 图表 | echarts / vue-echarts | 5.6.0 / 7.0.3 |
| i18n | 自写 60 行（复用 `lang/en/*.json`） | 无依赖（原 vue-i18n 已移除） |
| HTTP | axios（沿用 `Resp<T>` 解包） | 1.20.0 |
| 哈希 | js-sha256（登录密码哈希，纯 JS 支持 http 局域网） | 0.11.1 |
| 测试 | vitest + @vue/test-utils + jsdom | 3.2.7 / 2.5.1 / 25.0.1 |

**锁版本策略**：

1. `vue` 用**精确版本**并配 `pnpm-workspace.yaml` 的 `overrides`（把 `vue` 与 `@vue/*` 全锁同一版）。
   只锁 `dependencies` 不够：`element-plus` 的 peer 是 `^3.3.7`，prerelease 不满足该区间，
   pnpm 会给它另装一份 vue，造成同页两份 `runtime-core`（详见第 21.3 节）。
2. `vue-jsx` 仍是 RC（3.3.0-rc.4），升级须走独立分支 + `pnpm test` + `scripts/verify-v2.mjs` 全量回归。

## 2. 目录结构（`frontend-vue/`，已建立）

```
frontend-vue/
  package.json            # dev / build / preview / typecheck / test
  pnpm-workspace.yaml     # pnpm 12 构建脚本白名单（allowBuilds: esbuild, vue-demi）
  vite.config.ts          # vue-jsx(vapor:false) + /api 代理 + Vapor 启用开关（注释）
  vitest.config.ts        # jsdom 环境 + ~ 别名
  tsconfig.json           # jsx: preserve, jsxImportSource: vue-jsx
  src/
    main.tsx              # createApp + pinia + router + vue-query（含默认 query 策略）
    App.tsx               # ElConfigProvider（语言跟随 i18n）+ 品牌色注入 + RouterView
    router.ts             # hash 路由 + 守卫（登录/管理员）+ 401 统一跳转
    api/
      config.ts           # apiBase 推导（OPENLIST_CONFIG / VITE_API_URL / 同源）
      client.ts           # axios + 信封归一 + token + 401 钩子 + unwrap/ApiError
      types.ts            # Resp/Obj/User/权限位（以 C# User.cs 为真源）
      auth.ts             # 登录三通道 + WebAuthn 断言 + 密码哈希
      fs.ts               # /api/fs/*（M1 主体）
      settings.ts         # /api/public/settings
    i18n/index.ts         # 复用 lang/en/*.json；{{param}} 插值 + 英文回退
    lang/en/*.json        # 从旧前端整体复制（含新增 global.nav_* 键）
    stores/               # pinia：user（身份/权限）、settings（站点设置）
    composables/          # useTheme（明暗）、useBrandColor（--el-color-primary 派生）
    layouts/BasicLayout.tsx  # el-container 外壳：侧栏 + 头部 + 内容区
    pages/                # home / login / media(M4 占位) / manage(M2 占位) / spike
    styles/               # tokens.css（--ol-* 桥接 --el-*）、layout.css
```

切换动作（✅ 已于 2026-10-26 执行，见第 32 节）：`git mv frontend frontend-legacy` → `git mv frontend-vue frontend`，输出仍为 `frontend/dist`。

## 3. 渲染模式：VDOM 优先（实测依据）

### 3.1 为什么 VDOM 优先

Element Plus 是 VDOM 组件库，性能热点（el-table 内部）也在 VDOM 层——全量 Vapor 对 EP 无收益。而 Vapor 在 3.6 仍是 RC，且实测存在三个硬伤：

1. **Vapor 运行时不在主入口**：`vue@3.6.0-rc.12` 的 `createVaporApp` / `vaporInteropPlugin` 只存在于 `dist/vue.runtime-with-vapor.esm-browser[.prod].js`，主入口导入会得到 `undefined`（页面空白）。
2. **`createVaporApp` 缺 `config` API**：pinia / vue-router / vue-query 在 install 时访问 `app.config.globalProperties`，直接抛 `Cannot read properties of undefined (reading 'config')`。
3. **`vapor: true` + `createApp` 渲染成 `[object Object]`**：Vapor 根组件被 VDOM 容器当文本插入。

### 3.2 收益数据

| 配置 | 主包（gzip） | 结论 |
|---|---|---|
| VDOM 优先（vapor:false，无 alias/interop） | **172.79 KB（63.50 KB）** | 采用 |
| VDOM 宿主 + Vapor 叶子（alias + interop） | 266.39 KB（96.23 KB） | 仅在确需 Vapor 时启用 |

### 3.3 Vapor 可选启用三步（已实测通过）

需要为播放墙等高频更新区域启用 Vapor 时：
1. `vite.config.ts` 解开 `vue` alias（dev / prod 分别指向 `vue.runtime-with-vapor.esm-browser[.prod].js`）；
2. `main.tsx` 改为 `createApp(App).use(vaporInteropPlugin)`（注意不能换 `createVaporApp`）；
3. 目标文件命名 `*.vapor.tsx`，仅该文件走 Vapor 编译。

验证结果：EP 表格 3 行、11 个表单项、右键菜单、Vapor 叶子全部正常，控制台零告警。

## 4. TSX 约定（Element Plus + vue-jsx，已实测）

| # | 约定 | 症状/原因 |
|---|---|---|
| 1 | **插槽只走 `v-slots`，不要再写 children** | 同时写触发 `TS2710: 'v-slots' are specified twice` |
| 2 | **插槽参数不手写窄类型** | EP 2.14 自带 `ResolveSlots`，手写 `{ row }: { row: Row }` 与 `DefaultRow` 冲突 |
| 3 | **组件必须显式 import** | JSX 无全局组件解析；`<el-button>` 会被当原生元素 |
| 4 | **禁用 vdom 对象指令** | `v-loading` / `v-infinite-scroll` 在 Vapor 下会被跳过 → 统一用 `ElLoading.service()` 等服务式 API |
| 5 | 表单校验用 `el-form` + `rules` | 内置 async-validator，无需 RHF/zod |
| 6 | `v-model={ref.value}`，事件用 `onXxx` | 与标准 Vue JSX 一致 |
| 7 | **EP 组件的原生事件属性不能写在组件上** | `onKeyup`/`onKeydown` 不在 EP 的 props 里，`tsc` 直接报 `Property 'onKeyup' does not exist`；改成在外层原生 `<div>` 上监听（登录页回车提交即此法） |
| 8 | **pinia setup store 的 ref/computed 已解包** | 写 `settings.siteTitle.value` 会拿到 `undefined`，正确写法是 `settings.siteTitle` |
| 9 | **i18n 占位符是双花括号** | 现有字典用 `{{path}}`（@solid-primitives 风格），仅少量 `{number}`；必须一次扫描同时兼容两种，分两遍 replace 会让缺失参数被二次解析 |
| 10 | `ElCard`/`ElResult` 的 header/extra 也必须走 `v-slots` | `<template v-slot:header>` 是 SFC 语法，JSX 里无效且静默不渲染 |
| 11 | pnpm 12 的构建脚本白名单字段是 `allowBuilds`（映射） | `onlyBuiltDependencies` 在 12.x 被忽略，会持续报 `ERR_PNPM_IGNORED_BUILDS`；用 `pnpm approve-builds <pkg>` 生成 |

## 5. hope-ui → Element Plus 映射表

| 类别 | hope-ui（实际使用） | Element Plus |
|---|---|---|
| 布局 | Box / Center / Flex / Grid / HStack / VStack / Spacer / Container | div + CSS flex/grid；`el-row/el-col`、`el-space`、`el-container` 按需 |
| 排版 | Heading / Text / Anchor | 原生标签 + `el-link` |
| 按钮 | Button / IconButton | `el-button`（type/plain/text/circle）+ icons-vue |
| 输入 | Input / Textarea / FormHelperText | `el-input`（textarea autosize）+ `el-form-item` error/help |
| 选择 | Select 全套（Trigger/Content/Listbox/Option/Value…） | `el-select` + `el-option`（filterable/multiple/remote） |
| 勾选 | Checkbox / `hope`(styled) | `el-checkbox` / `el-checkbox-group` |
| 浮层 | Modal 全套（7 个） | `el-dialog`（header/default/footer 插槽）、`el-drawer` |
| 菜单 | Menu / Trigger / Content / Item | `el-dropdown`（**`trigger="contextmenu"` 原生右键菜单**） |
| 提示 | Tooltip | `el-tooltip` |
| 反馈 | Spinner / Progress / Badge / Skeleton | `ElLoading.service()` / `el-progress` / `el-badge` / `el-skeleton` |
| 图片 | Image / ImageWithError | `el-image`（`preview-src-list` 自带灯箱） |
| 通知 | notificationService | `ElMessage` / `ElNotification` |
| 主题 | HopeProvider / useColorMode(Value) / globalCss / hope | `html.dark` + `--el-*` CSS 变量 + `ElConfigProvider` |
| 交互 | createDisclosure | `ref` / `useToggle`（@vueuse/core） |

## 6. 全量对齐验收表（切换前置条件）

**A. 认证与初始化**：登录（含 WebAuthn）、初始化向导（含 EnvCheck）、密码页、token 刷新、权限守卫
**B. 文件管理**：列表/网格视图、排序、筛选、多选与批量操作、右键菜单（含 open-with）、拖拽上传、文件夹树、工具栏、面包屑、搜索、分页、Readme
**C. 上传管线（5 模式）**：form / stream / direct / chunked / multipart + hash-worker 秒传 + 进度/速度/取消
**D. 预览器（27 项）**：aliyun_video、archive、asciinema、audio、doc、download、flash(Ruffle)、html、iframe、image、info、ipa、markdown、markdown+word-wrap、pdf、plist、ppt、text-editor(Monaco)、torrent、url、video、video360、video_box（artplayer+ASS）**E. 管理台 12 模块**：storages（**后端驱动动态表单**，最大单点）、users、settings、tasks、shares、indexes、metas、plugins、messenger、backup-restore、dashboard、about
**F. 媒体域**：现有 7 tab + 新增 wall / person / follow
**G. 通用**：暗色主题、语言切换（en）、CDN 设置、键盘快捷键、浏览历史、通知中心
**H. 桌面端**：`frontend/dist` 打包、设置页、日志页零改动

## 7. 后端媒体域（与前端并行）

**结构重构（2026-10-25 已完成）**：媒体域从 `Core` / `Infrastructure` / `Server` 三处收拢为两个独立项目 ——
`OpenList.Media.Domain`（实体 + 接口契约，不依赖 EF）与 `OpenList.Media`（服务 / provider / 端点 / DI 模块）。
宿主只保留 `AddMediaModule()`、`MapMediaModule()` 两行；端点由 1 个 377 行文件拆为 6 个按子域划分的文件（42 条路由逐条比对一致）；
`DbInitializer` 里约 280 行手写媒体建表 DDL 已删除（媒体表只由 EF 迁移创建）；扫描命名规则抽成可单测的 `MediaNaming`。
分层、依赖方向、权限口径与迁移策略见 [`docs/media-backend-architecture.md`](./media-backend-architecture.md)。

已完成功能（`/api/media`，46 条路由 + 9 个服务/provider）：库 CRUD/扫描、条目多维查询、TMDB 刮削、NFO **双向**（读入+写出）、番号域（Code/片商/系列 + code provider）、个人状态（收藏/在看/稍后/标记/口味）、播放列表、facets/stats/home、创作者与片商/系列聚合、OpenSubtitles 字幕、离线下载三通道。

待补（按依赖排序）：
1. ~~**NFO 双向化**~~（已完成）：扫描解析 `.nfo` + poster/fanart 作为初始元数据（provider=local），刮削只补缺——见 `docs/media-backend-architecture.md` 的 NFO 双向化一节
2. ~~**番号域**~~（已完成）：`MediaItem` 加 `Code`/`StudioId`/`SeriesId`；新实体 `MediaStudio`/`MediaSeries`；番号 provider 作为 `IMetadataProvider` 第二实现（读设置项 `media_code_api_url` 的自定义 JSON API，不内置站点）——见 `docs/media-backend-architecture.md` 的番号域一节
3. **云下载管线**：`DownloadRule` → 下载完成回调归档 → 自动触发增量扫描
4. **关注聚合**：`FollowEntry`/`FollowPost` + `IFollowSiteAdapter`（最后做）
5. **任务断点续跑**：扫描/刮削任务持久化，重启恢复
6. **个人状态视图下推**：`/favorites`、`/watch-later`、`/unseen` 等目前先取全量再内存排序（SQLite 不支持 `ORDER BY DateTimeOffset`），
   可用 `DateTimeOffsetToBinaryConverter` 改存 ticks 后在 SQL 层排序分页（需一对迁移，重建库代价低）

## 8. 里程碑

| 阶段 | 内容 | 人日 | 状态 |
|---|---|---|---|
| M0 | 脚手架 + 渲染基线 + 主题 token + API 客户端 + i18n + 登录/鉴权 + 外壳布局 | 8 | ✅ 完成（2026-10-25，见第 11 节） |
| M1 | 文件管理全量 + 上传管线 5 模式 | 22 | ✅ **完成**：浏览体验（21）+ 操作集 7 项（22）+ 预览器第二批（23）+ 批量重命名（24）+ 上传 + 分享 |
| M2 | 管理台 12 模块（storages 动态表单优先原型） | 18 | **9 个模块 ✅**：storages/users/metas/indexes/tasks/settings/shares/media/messenger（第 34 节）；剩 4 个见第 16–19 节（`plugins` 无 .NET 对应物，见 §34.6 第 6 条） |
| M3 | 预览器 27 项 | 13 | ✅ **完成**：registry 现覆盖 **22 个注册预览器**（第 16/23/25/26 节 + 第 31 节 video360/flash）；`aliyun_video` 与 `video_box` 按 §397/§398 结论不做 |
| M4 | 媒体域（wall 可评估 Vapor 加速）+ 对接后端新增能力 | 14 | ✅ **全部收口**：浏览/详情/播放/进度（27）+ 管理台（28）+ 人物/片商/系列/口味/播放列表/字幕（30）+ 整库刮削（33） |
| M5 | 切换发布 + A–H 全量回归 + 桌面打包验证 | 5 | ✅ **完成**：`frontend/` 已是自有 Vue 应用（第 32 节），构建链路改源码构建、A–H **11 轮 231 项全绿**、server/desktop 两个 bundle 实测可跑 |
| **合计** | | **80** | |

## 9. 风险与对策

| 风险 | 对策 |
|---|---|
| **Vue 版本 / EP peer 冲突** | 已收敛为 Vue 3.5.22 + `pnpm overrides` 全锁（症状与根因见 21.3）；vue-jsx 仍为 RC，升级前必须跑单测 + 真机脚本；Vapor 待 3.6 正式版 |
| EP 插槽/组件边缘行为 | 已验表格插槽、表单项、对话框标记、右键菜单；待验 ElSelect 远程搜索、ElTree 拖拽、ElUpload 自定义、ElTable 虚拟滚动 |
| 指令生态差异 | 禁用 vdom 对象指令；服务式 API 写入规范；团队 lint 约束 |
| EP 全量 CSS 360 KB | M1 起接 `unplugin-element-plus` 按需样式 |
| 上游断供 | 已接受；建立「上游功能移植清单」按需手工移植 |
| storages 动态表单 | M2 首个做；driver `ConfigItems` → 动态表单渲染器一次成型 |
| 团队 TSX 规范 | 固化第 4 节约定；接 vue-jsx 官方 ESLint 插件 |

## 10. M1 首批任务（文件管理）

1. ~~`pages/home` 改造为文件浏览器~~ ✅ 已完成并联调通过（2026-10-25，见第 12 节）。
2. 操作集：新建文件夹、重命名、批量重命名、复制、移动、删除、删除空目录（`api/fs.ts` 已就绪）。
3. 上传管线：form / stream / direct / chunked / multipart 五模式 + hash 秒传；先做 **stream + chunked** 两条主干，direct 视 `direct_upload_tools` 探测结果再补。
4. 下载与直链：文件直链、打包下载、`Content-Disposition` 解析（旧前端 `useDownload`）。
5. 选中态与快捷键：`useSelection` + 右键菜单（沿用 Spike 页已验证的 `ElDropdown` contextmenu 用法）。
6. 每完成一块补一个 vitest 用例（路径拼接、排序、分片切分），把 `src/utils` 的纯函数优先搬过来。
7. 小项：补 favicon（当前 404）；`unplugin-element-plus` 按需样式接入。

## 11. M0 交付记录（2026-10-25 实测）

**产出**：`frontend-vue/` 共 27 个 `.ts/.tsx`（含 3 个 spec），从空目录到「能登录、能守卫、能换主题、能换语言」的可运行基座。

**验证结果**：

| 检查 | 结果 |
|---|---|
| `pnpm typecheck` | 0 错误（strict 模式） |
| `pnpm test` | 3 文件 / 17 用例全绿（i18n 6、HTTP 信封 9、密码哈希 2） |
| `pnpm build` | 通过，路由级分包（home 8.96 KB / login 6.64 KB / BasicLayout 23 KB，主包 230 KB→gzip 77 KB） |
| 无头 Edge 真实渲染（首页） | DOM 10,141 字节：外壳、2 个菜单项、6 组描述项、登录态标签全部就位，控制台零告警 |
| 无头 Edge 真实渲染（`#/@login`） | 3 个输入框（用户名/密码/OTP）、记住我、游客入口全部渲染；Passkey 按钮按设置开关正确隐藏 |

**关键实现决策**：

- **不用 vue-i18n**，改自写 60 行 i18n：现有字典用 `{{param}}` 语法且 `global.json` 含 `|`（vue-i18n 会当复数分隔符静默错译）。自写实现可把「取值/插值/回退」三条语义用单测锁死。
- **权限位以 C# 为真源**：`PermissionBits` 显式写位号（`manage_media = 14`），不再沿用旧前端「数组下标当位号」的写法 —— 那份 14/15 位与本仓库后端不一致。
- **401 处理上移到路由层**：`client.ts` 只暴露 `onUnauthorized` 钩子，HTTP 层不依赖路由，便于单测；`router.ts` 里清 token + 带 `redirect` 回登录页。
- **品牌色派生**：`main_color` → 一整套 `--el-color-primary-light-3/5/7/8/9` + `dark-2`，深色模式下浅色阶改混深底（混白会刺眼）。
- **`remember` 只记用户名**：旧前端把密码写进 localStorage，属于安全问题，本次不复刻。

**待办（M1 开工前）**：~~与真实后端做一次联调冒烟~~ ✅ 已完成（2026-10-25，见第 12 节）。

## 12. M1 文件浏览器交付 + 真机联调记录（2026-10-25）

**产出**：`pages/home` 从占位页改造为完整文件浏览器 —— `utils/path.ts`（join/parent/base/ext 语义与旧前端逐一对齐，含 `.gitignore` 算扩展名的约定）、`utils/format.ts`（humanSize/formatTime）、`composables/useFileList.ts`（路径↔路由查询双向同步、面包屑栈、排序、加载/错误/文件三种状态、`refresh` 强制刷新穿透到后端）、`utils.utils.spec.ts`（7 用例）。

**真机联调（后端 127.0.0.1:5244 + Vite dev /api 代理 + Playwright/Edge）**：

| 链路 | 结果 |
|---|---|
| 明文登录 `POST /api/auth/login` | 200，JWT 签发，`sha256(pwd + "-https://github.com/alist-org/alist")` 盐值核对无误 |
| 哈希登录 `/api/auth/login/hash` | 200 |
| `GET /api/fs/list` 响应结构 | keys 与前端 `Obj` 类型完全一致（name/size/is_dir/modified/created/sign/thumb/type/hashinfo/hash_info） |
| 游客浏览根目录 | 表格 2 行（Pic/Shared）、面包屑、排序、「共 2 项」全部渲染 |
| **端到端登录**（表单填写→提交） | JWT 落入 localStorage，头部出现 admin 头像，管理员菜单「Management」按权限位显隐 |
| **目录进入** | 点击 Pic → URL `?path=/Pic` → 面包屑 Home/Pic → **真实列表 61 项**，存储驱动徽标「Local」显示 |
| 控制台 | 仅 favicon 404（已列入 M1 小项） |

**结论**：前端↔后端全链路（登录→鉴权→列表→导航）真机验证通过，`api/fs.ts` 其余端点（mkdir/rename/move/copy/remove）走同一 `client.ts` 通道，风险已排除。M1 剩余：操作集 UI → 上传管线 → 下载直链 → 右键菜单。

## 13. M1 操作集交付 + e2e 实测记录（2026-10-25）

**产出**：文件操作集全量落地 —— `composables/useFileOps.ts`（mkdir/rename/move/copy/remove 统一执行器：错误归一、成功提示、强制刷新、选择态与 busy 门控）、`components/FolderPickerDialog.tsx`（目标目录选择器）、`lang/en/ops.json`（操作文案）、首页接线（新建按钮 / 多选列 / 批量操作栏 / 行内 ⋯ 菜单 / 三类对话框）。

**FolderPicker 设计要点**：后端 `fs/dirs` 一次返回初始目录下**深度 3 以内的全路径子目录**（`DirsResp.Dirs`，修正了前端原 `Obj[]` 的错误类型）。选择器据此一次加载 + 前端按当前目录过滤直接子级；进入子目录不发请求，面包屑**向上跨出已加载子树**时才重新请求 —— 这解决了「从当前目录选不到兄弟目录」的问题。

**e2e 实测（真实后端 + Playwright/Edge，隔离目录 `ol-m1-*`，全程后清理还原）**：

| 操作 | 结果 |
|---|---|
| 新建文件夹（UI prompt） | ✅ 列表即时可见（此前的 M1 切片已验） |
| 重命名（UI prompt，初始值填充） | ✅ hello.txt → world.txt |
| 批量复制到兄弟目录 | ✅ dst 出现 world.txt，源保留 |
| 批量移动 | ✅ a 清空、dst 收齐（同名冲突被后端 409 正确拒绝 —— overwrite=false 语义） |
| 行内菜单删除 | ✅ after-del=[]，确认框正常 |
| 存储级写权限 | 根目录 `/` 为虚拟聚合（无单一驱动）→ `write:false`，操作 UI 按设计隐藏；挂载点内 admin 正常出现 |

**本轮踩坑（固化进 TSX/联调规范）**：

1. **TSX 带连字符的事件属性直接写**：`onSelection-change={…}` 合法；`{"onSelection-change": …}`（引号键对象）是非法 JSX。
2. **`v-slots` 与 children 互斥**（TS2710）：ElDropdown 这类「触发器 + 命名插槽」组件，触发器内容也要放进 `v-slots.default`。
3. **`fs/dirs` 返回 `{dirs: string[]}`**（全路径、深度 3），不是对象数组 —— 类型必须以 C# `DirsResp` 为真源。
4. **JWT 签名密钥每次重启变化**：后端重启后所有旧 token 401（且 401 会触发路由守卫清 localStorage）——联调脚本每次必须重新登录。这同时也是产品层面待办：生产部署应固定 `Jwt:Key`，否则每次重启全员掉线。
5. **工作目录决定 DB**：`dotnet run`（CWD=项目目录）与 exe 直启（CWD=bin）会命中**不同的 data.db** —— bin 下的陈旧库曾导致 401 假象。已把 bin 下的陈旧库改名 `data.db.stale-bak`；联调统一以项目目录为 CWD。
6. **e2e 沙箱环境进程寿命 <90s**：后端必须与测试同命令内联启动、单脚本完成全部交互与服务端验证（fetch 走浏览器同源代理）。

**M1 进度**：文件浏览器 ✅ + 操作集 ✅。剩余：上传管线（stream/chunked 优先）→ 下载直链 → 右键菜单与快捷键 → favicon / unplugin-element-plus 按需样式。

## 14. M1 上传管线交付 + e2e 实测记录（2026-10-25）

**交付内容**（`frontend-vue/src/pages/home/upload/`，对应旧前端 `uploads/` 全量移植）：

| 通道 | 文件 | 说明 |
|---|---|---|
| Multipart（默认） | `multipart.ts` | `/fs/multipart/*` 官方协议：init 幂等续传、3 并发升序分片、429/409 流控耐心重试（上限 400）、complete 掉线后轮询 status 至 404 终态；≤10MB 自动回退 Stream |
| Stream | `stream.ts` | `PUT /fs/put`，兜底通道，任何存储可用 |
| Form | `form.ts` | `PUT /fs/form`，网关兼容备选；Content-Type 交浏览器带 boundary |
| HTTP Direct | `direct.ts` | `/fs/get_direct_upload_info` 预签名直传（.NET 的 `DirectUploadInfo` 只有 url/headers/method，单请求直传；chunk_size 分支预留） |

- **UI**（`UploadPanel.tsx`）：工具栏「Choose Files」按钮 → el-dialog；拖拽/选文件/选目录（webkitdirectory + 递归读目录条目）、通道选择、覆盖/秒传选项（localStorage 持久化）、逐文件进度（状态徽标/速度/失败重试/清理已完成）；关面板自动刷新列表。as-task 暂缺（.NET 无离线任务系统，传了也是 no-op）。
- **秒传哈希**：`hash-worker.ts`（hash-wasm，worker 内流式 md5/sha1/sha256）。
- **direct_upload_tools** 从 fs/list 响应透出（`useFileList.directUploadTools`），HTTP Direct 仅在目录报告该工具时可选。

**后端修复**（e2e 抓到的真 bug）：`MultipartUploadService.UploadChunkAsync` 用同步 `Stream.CopyTo` 读 Kestrel 请求体（RateLimitedReadStream 同步 Read）→ 触发 AllowSynchronousIO 异常，所有分片上传 500。改为三段式：持锁校验 → 不持锁 `CopyToAsync` 落临时文件 → 持锁登记（拷贝期间不占会话锁，会话在拷贝期间终结则丢弃临时分片）。全量后端测试 461 通过。

**e2e 实测**（`frontend-vue/scripts/e2e-upload.mjs` API 级 + `e2e-upload-ui.mjs` Playwright 无头 Edge，全部 ALL PASS）：

| 项 | 结果 |
|---|---|
| 登录 → /Shared 建隔离目录 → 流式/表单/分片上传 | ✅ |
| 分片协议语义：chunk_size 回显 10MB、total_chunks=3、status.received=[[0,1]] 区间 | ✅ |
| complete → 轮询至 completed → fs/get 校验大小 → raw_url 下载 md5 与源一致 | ✅ |
| UI：登录 → 上传面板 → 注入 2 文件（txt + 6MB bin）→ 双双 Success → 关面板列表出现新文件 | ✅ |
| 控制台零报错（favicon 404 除外）、清理还原 | ✅ |

**本轮踩坑（固化）**：

1. **直跑 DLL 不读 launchSettings**：必须显式 `ASPNETCORE_URLS=http://127.0.0.1:5244`，否则监听 5000 造成「后端没起」假象。
2. **spawn 出来的服务进程树要自管**：`pnpm dev`（shell 包装）kill 不掉孙进程且 stdio 管道会让 bash 任务假死 —— e2e 里直接 `node node_modules/vite/bin/vite.js`。
3. **登录路由是 `/#/@login`**：`/#/login` 会命中 catch-all 重定向回首页；waitForURL 不能用 `/#\/…/` 正则（匹配登录页自身）。
4. **登录页第一个 input 是只读的语言选择器**（el-select combobox），fill 必须按 `input[placeholder]:not([readonly])` 定位。
5. **分片 e2e 断言「错误对象有没有 data 键」**区分服务端信封与传输层失败 —— 与 Vue `api/client.ts` 注释的约定严格对应。

**M1 进度**：文件浏览器 ✅ + 操作集 ✅ + 上传管线 ✅。剩余：下载直链（页面级）→ 右键菜单与快捷键 → favicon / unplugin-element-plus 按需样式。

## 15. M1 收尾：下载直链 / 右键菜单与快捷键 / favicon 与按需样式（2026-10-25）

**交付内容**：

| 模块 | 文件 | 说明 |
|---|---|---|
| 下载直链 | `utils/link.ts` | 客户端拼 `/d{path}?sign=`（对齐 SignEndpoints；`Obj.sign` 需要时才下发，有则带上）。行菜单/右键菜单「Download / Copy link」、文件页「Download」按钮 |
| 右键菜单 | `components/FileContextMenu.tsx` | 自实现（solid-contextmenu 不可移植）：Teleport + fixed 定位、视口越界翻转、外点/Esc/选项点击三种关闭。行菜单（打开/下载/复制直链+写操作按权限显隐）+ 空白菜单（新建/上传/刷新/全选/粘贴） |
| 快捷键 | `composables/useShortcuts.ts` | Ctrl+A 全选（走 `ElTable.toggleAllSelection` 保持勾选框同步）、Delete 删除（确认框）、F2 重命名、Ctrl+C/X/V 内存剪贴板（工具栏显示 ✂/⧉ 计数 tag，点击即粘贴）、Escape 逐级取消。输入态与弹窗打开时自动让位 |
| favicon | `public/favicon.svg` + index.html | SVG 图标，favicon 404 从控制台清除 |
| 按需样式 | unplugin-element-plus | 移除全量 `dist/index.css`，组件样式按导入注入（ElMessage/ElMessageBox 同样覆盖） |
| dev 代理 | vite.config.ts | 补 `/d /p /ad /ap /ae` 签名路由代理——否则 dev 下点「下载」落进 SPA fallback 返回 index.html（e2e 抓到） |

**i18n 修正**：`lang/en/home.json` 补 `toolbar.select_all` / `toolbar.paste`（旧字典顶层缺失，缺键会整串 key 上 UI）。

**e2e**（`scripts/e2e-ctx-menu.mjs`，25 断言 ALL PASS）：行右键五项、Esc 关闭、空白菜单三项、Ctrl+A/Delete/Ctrl+C/Escape、粘贴流程、`/d` 直链内容一致、文件页下载按钮与 raw_url、favicon 200、控制台零报错、清理还原。验证口径：typecheck 0 错误、28 测试全绿、构建通过。

**M1 进度**：文件浏览器 ✅ + 操作集 ✅ + 上传管线 ✅ + 下载直链/右键/快捷键/favicon/按需样式 ✅。**M1 全部完成**，下一阶段 M2（打包下载/搜索/布局切换或按方案第 10 节推进）。

## 16. M2 storages 管理台 + M3 预览器交付记录（2026-10-25）

### 16.1 M2：storages 模块（驱动动态表单）

| 文件 | 作用 |
|---|---|
| `api/admin.ts` | `driver/list`、`driver/info`、`storage/list|get|create|update|delete|enable|disable|load_all` |
| `api/types.ts` | `DriverInfoItem`（name/type/default/options/required/help）、`DriverInfo`（common/additional/config）、`StorageWithDriver`、`CreateStorageReq`、`PageResp` |
| `components/DriverForm.tsx` | 按后端 `type` 字符串渲染控件：`bool→ElSwitch`、`number/float→ElInputNumber`、有 `options` 的 `string/select→ElSelect`、`text→ElInput textarea`、其余 `ElInput` |
| `pages/manage/storages/Storages.tsx` | 列表 + 编辑/启停/删除/重载全部 |
| `pages/manage/storages/StorageEdit.tsx` | 驱动下拉 + 动态表单 + 公共字段；编辑态从 `addition` JSON 回填 |
| `layouts/ManageLayout.tsx` | 管理台外壳（12 模块子导航，仅 Storages 实装） |

**关键决策（避免保存错位）**：后端 `Create/Update` 只吃 `CreateStorageReq` 顶层字段 + **原样存 `addition` JSON**，所以动态表单做「分流」——`common` 里命中顶层列的（mount_path/order/remark/web_proxy/webdav_policy/enable_sign/extract_folder/order_by/order_direction）走顶层，其余 common + 全部 additional 进 `addition`，与后端 round-trip 逐字一致。

**联调顺带验证**（M3 联调时复用同一 token）：`POST /admin/storage/create` → 200 + 返回含 id 的对象，`POST /admin/storage/delete?id=3` → 200 且列表回到 2 条。

### 16.2 M3：预览器（10 项落地 / 27 项目标）

**结构**（`frontend-vue/src/pages/home/previews/`）：

| 文件 | 作用 |
|---|---|
| `types.ts` | `PreviewDef`（key/type/exts/provider/match/order/available）、`PreviewEntry`、`PreviewChildProps` |
| `registry.ts` | 注册表 + `resolvePreviews` 排序 + `parseIframePreviews`/`convertUrl`（设置驱动的外部 iframe 预览） |
| `PreviewHost.tsx` | 预览器切换（写 URL `?preview=`）、`onErrorCaptured` 收敛为提示、下载/新窗口/全屏 |
| `useTextContent.ts` | 流式取文本 + `TextDecoder` 多编码 + 字节缓存 |
| 组件 ×10 | `ImagePreview` / `VideoPreview` / `AudioPreview` / `PdfPreview` / `MarkdownPreview` / `TextPreview` / `HtmlPreview` / `IframePreview` / `InfoPreview` / `DownloadPreview` |
| `registry.spec.ts` | 13 用例：匹配优先级、兜底顺序、iframe 注入、`convertUrl` 变量 |

**排序模型（三档）**：真实预览（`10–90`，iframe 设置项插入 90）→ 文件信息（有真实预览时 200，否则 300）→ 下载（有真实预览时 1000 兜底，否则 250 排最前）→ 「按文本打开」手选项（1001，仅未知小文件出现）。即**有预览时下载是兜底，没预览时下载是主操作**。

**媒体源**：统一 `raw_url`（后端 `/p/{path}?sign=` 签名代理）→ 兜底 `/d` 直链。实测 `/p` 返回正确 `Content-Type`、`Accept-Ranges: bytes`、无 `Content-Disposition`，因此 `<video>` 可拖进度条、PDF 可在 iframe 内嵌而不是被下载。

**安全与健壮性**：
- markdown 走 `marked → DOMPurify → v-html`，渲染后统一给外链加 `target=_blank rel=noopener`；
- HTML 预览用 `srcdoc + sandbox="allow-scripts allow-forms allow-popups allow-modals"`，**故意不给 `allow-same-origin`**（否则可读父页面 localStorage 里的 token）；并注入 `<base>` 指向 `/p/` 同目录，让相对引用的 css/图片可用；
- svg 归到图片（`<img>` 渲染不执行脚本）而不是 HTML；
- 文本取数用 `ReadableStream` 读到上限即 `cancel()`，即使后端忽略 Range 也不会把几百 MB 日志拉进内存；>64 MB 直接提示下载；切换编码只重新 decode（字节缓存），不重复请求。

**实测（后端 `localhost:5244` 直接托管 `frontend/dist`，与生产同源；Playwright + Chromium）**：

| 项 | 结果 |
|---|---|
| 目录 `/Preview` 列表 | 7 行全部渲染 |
| `readme.md` | h1 / 表格 / 代码块全部命中；下拉 4 项且顺序为 `Markdown → Text → Info → Direct Download` |
| `note.txt` | 等宽显示、保留缩进与长行 |
| `page.html` | iframe 内 style 生效、`<script>` 执行（"脚本已执行"） |
| `demo.pdf` | iframe 内嵌 Chrome 阅读器，`src` 为签名 `/p` 地址 |
| `pixel.png` | 渲染成功，滚轮缩放 → `scale(1.12)` |
| `data.csv` | 走文本预览（后端 type=4） |
| `unknown.bin` | 默认落在下载页（下载 + 新窗口） |
| 切到 Info | 名称/类型/大小/时间/路径/直链齐全 |
| `tone.wav` | `audio.duration = 1`，播放器可播 |
| `gbk.txt` | UTF-8 下乱码 → 切 GBK 后正确显示中文 |
| 暗色主题 | `html.dark` 下 markdown/文本对比度正常 |
| 控制台 | **零错误**（favicon 已修） |

**验证口径**：`pnpm typecheck` 0 错误；`pnpm test` 41 用例全绿（新增 13）；`pnpm build` 通过（`MarkdownPreview` 独立分包 76.59 KB，仅打开 md 时加载）。

**剩余 17 项与原因**：

| 未做项 | 原因 |
|---|---|
| doc / xls / ppt | 需引入 `docx-preview` / `xlsx` / `pptxjs` 一类库（约 3 个依赖、数 MB），需先定「按需 CDN vs 打包」策略 |
| archive | 后端只有 `fs/archive/decompress`，**没有列目录接口**，做不了归档内浏览 |
| torrent | 后端 `fs/torrent/*` 显式返回 `NotImplemented` |
| aliyun_video | 需阿里云盘播放器 SDK 与 provider 身份，当前 Local 环境无法验证 |
| flash / ipa / plist / asciinema / video360 / video_box / url / text-editor(Monaco) / markdown_with_word_wrap | 低频或强依赖专用运行时（Ruffle / Monaco / artplayer） |

**本轮踩坑（固化）**：

1. **`/tmp` 在 Git Bash 与 Node 里不是同一个路径**：bash 的 `/tmp` 是 `C:\Users\<user>\AppData\Local\Temp`，Node 的 `/tmp` 解析成 `F:\tmp` —— 跨语言传文件路径必须用 Windows 绝对路径或环境变量。
2. **`node_modules/.vite` 被 safe-delete 保护卡住**：新增依赖后 Vite 要重建依赖缓存（内部 `rm` 69 项 > 阈值 50），dev server 直接退出。绕过：改用「后端托管 `dist`」做同源联调（`DistDir=<repo>/frontend/dist dotnet run`），顺带把生产同源路径也验了。
3. **同 URL hash 导航后 `waitForSelector` 会命中上一次的 DOM**：`page.goto('#/?path=...')` 不重新加载文档，断言会抢跑到旧目录。e2e 里先 `goto('about:blank')` 再进目标 hash。
4. **后端把 `pdf` 判成 `type=0`（UNKNOWN）**：预览器不能只依赖 `ObjType`，扩展名兜底是必需的（`registry` 里每个定义同时声明 `type` 与 `exts`）。

## 17. M2 第二批：users / metas / indexes / tasks（2026-10-25）

### 17.1 交付清单

| 文件 | 作用 |
|---|---|
| `api/types.ts` | 追加 `AdminUser`（`otp`/`is_built_in`）、`CreateUserReq`/`UpdateUserReq`、`Meta`、`IndexProgress`、`IndexBuildReq`、`ScanProgress`、`TaskInfo` |
| `api/admin.ts` | 追加 `user/*`（list/get/create/update/delete/set_password/cancel_2fa/reset_admin_password）、`meta/list|save|delete`、`index/*`、`scan/*` |
| `api/tasks.ts` | **新增**：`TASK_TYPES`（15 个 snake_case 别名）+ `listTasks`/`taskAction`/`taskBulkAction`/`taskBatchAction` |
| `pages/manage/users/Users.tsx` | 列表 + 新增/编辑对话框（角色、base_path、9 个权限位复选框、禁用、LDAP）+ 改密 + 取消 2FA + 删除 |
| `pages/manage/metas/Metas.tsx` | 路径元数据列表 + 编辑对话框（密码/读写白名单/隐藏/readme/header + 7 个 `*_sub` 开关） |
| `pages/manage/indexes/Indexes.tsx` | 索引进度轮询 + 重建/增量/停止/清空；手动扫描开始/停止 |
| `pages/manage/tasks/Tasks.tsx` | 类型下拉 + 进行中/已完成页签 + 进度条 + 取消/重试/删除 + 清空成功/重试失败 |
| `router.ts` / `ManageLayout.tsx` | 4 条子路由 + 侧栏 4 项放开（`(soon)` 仅剩 shares/plugins/settings/backup-restore/dashboard/about） |

### 17.2 关键决策（都以 C# 后端为真源）

1. **用户更新是「部分更新」，且只发改过的字段**：`UpdateUserReq` 里传 `null` 会被写成 null，所以密码留空就不发该字段；内置管理员（`is_built_in`）连 `username`/`role` 都不发，前端同时禁用控件，避免后端 403 让用户困惑。
2. **权限位按位号算**：`PermissionBits`（真源 `User.cs` 注释）—— 位 0–7 + 媒体管理位 14。旧的 ol-fe 用数组下标当位号，搬到本仓库会全错，这里明确不复用。
3. **索引/扫描进度只能做二态**：后端把进度压成 `{ obj_count, is_done }`，没有 total，所以 UI 不画百分比，只显示「Running/Idle + 条目数」；轮询仅在 `is_done === false` 时挂 2s 定时器，完成即自动摘掉。
4. **任务类型必须 snake_case**：后端 `TypeAliases` 显式映射 `offline_download` 这类写法，直接传 C# 枚举名会被拒；`GET /task/{type}/{done}` 返回**裸数组**，不是 `{content,total}` 信封。
5. **管理台内容区从 980px 放宽到 1280px**：users/metas 的固定列宽合计约 1150px，980 会把「操作」列截断。

### 17.3 顺带修掉两个后端缺陷（否则 Metas 模块完全不可用）

| 缺陷 | 现象 | 修法 |
|---|---|---|
| `x_metas` 有 4 个 Go 时期遗留列（`WebdavPolicy`/`WebdavRead`/`WebdavWrite`/`WebdavManagement`，**NOT NULL 且无默认值**）没被 `Meta` 实体映射 | 任何 meta 插入都 500：`NOT NULL constraint failed: x_metas.WebdavPolicy` | `Meta` 增加 4 个 `[JsonIgnore] int` 兼容属性（不参与 API、`UpdateAsync` 不覆盖以保留旧值）；`DbInitializer` 补列（默认 0）；`MigrateSchemaAsync` 改为**两条初始化路径都执行**（迁移文件是历史快照，模型新增列未必有对应迁移） |
| 旧库 `x_metas.Hide` 是 `INTEGER NOT NULL`，但 C# 侧 `Hide` 是 `string?` | 前端传 `hide: null` 时 500：`NOT NULL constraint failed: x_metas.Hide` | 后端 `MetaService` 创建/更新统一兜底 `?? string.Empty`；前端也固定传空串 |

### 17.4 真机验收（后端托管 `dist`，Edge + Playwright，8 项全绿）

| 用例 | 结果 |
|---|---|
| users 列表 | 3 行（admin/guest/新建），内置标记 2 个 |
| users 新增 | 行出现，`POST /admin/user/create` 落库 |
| users 编辑 base_path | 行内变为 `/docs`，重查 API 确认 `base_path=/docs` |
| users 改密 | `set_password` 成功提示，无报错 |
| metas 新增 / 编辑 write | 行出现；开关写入后 `write=true`，列表显示 `ON` |
| indexes / scan | 状态 `Idle → Running → Idle`（开始/停止都生效），`obj_count` 实时增长 |
| tasks 两种页签 | 进行中/已完成空态文案正确，切页签会重新拉取 |
| 控制台 | **零错误** |
| 暗色主题 | 本轮截图曾用错键（见 18.5 勘误），2026-10-25 已用 `theme-mode` 重拍，users 页两种主题均正常 |

`pnpm typecheck` 0 错误、`pnpm test` 41/41、`pnpm build` 通过（新增 4 个路由级分包）。

### 17.5 未覆盖与遗留

- **Tasks 只验到空态**：本地没有可产生任务的源（跨存储复制/离线下载需要两个可用存储或 aria2/qbit 配置），有数据时的进度条与批量操作未真机验证。接口契约（裸数组、`state` 数值枚举、`total_bytes`）已按源码核对。
- **M2 剩余模块**：shares / plugins / settings / backup-restore / dashboard / about。其中 settings 工作量最大（分组表单 + 站点/样式/预览/SSO/LDAP/S3 多组），建议单独一轮。
- **内置账号 `guest` 也带 `is_built_in: true`**：前端因此禁用其删除按钮，但它的角色/密码仍可改 —— 与后端「内置管理员」保护语义不同，若产品上需要保护 guest，应让后端在 `UpdateAsync` 里也拒绝。

**本轮踩坑**：

1. **Playwright 浏览器未安装**：`chromium.launch()` 报 `chrome-headless-shell` 缺失，改用本机 Edge（`executablePath: C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe`）即可，无需下载。
2. **页面上有多个 `.el-dialog`（含隐藏的）**：`waitForSelector('.el-dialog', {state:'visible'})` 会命中第一个（隐藏）而超时，必须写成 `.el-dialog:visible`。
3. **`ElSelect`/`ElSwitch` 也会渲染 `<input>`**：按 `input.nth(i)` 定位表单字段会错位，正确做法是 `.el-form-item` 按 label 文本过滤后再取控件；一个 form-item 里可能有多个 switch（父开关 + `*_sub`），需 `.first()`。

## 18. M2 第三批：settings 站点设置（2026-10-25）

### 18.1 交付清单

| 文件 | 作用 |
|---|---|
| `api/types.ts` | `SettingItem`（key/value/help/type/flag/options/group）、`SettingValueType`、`SettingFlag`、`SettingGroups`（12 组编号 → 文案键） |
| `api/admin.ts` | `listSettings`、`saveSettings`、`resetSettingsGroup`、`resetToken` |
| `components/SettingForm.tsx` | 设置项渲染器：`bool→ElSwitch`、`number/float→ElInputNumber`、`select→ElSelect`（options 逗号分隔）、`text/multipath→textarea`、其余 `ElInput`；`flag=2/3` 只读 |
| `pages/manage/settings/Settings.tsx` | 12 个分组选项卡 + 关键词搜索 + 脏值跟踪 + 只提交改动项 + 分组恢复默认 + 重置签名密钥 + 离开前未保存提示 |

### 18.2 关键决策

1. **一次拉全量、前端分组**：`GET /admin/setting/list` 不带 `group` 时返回全部（实测 121 项），切分组零请求。
2. **只提交改动项**：`POST /admin/setting/save` 是合并写；把整组发回去会把后端新增、但前端未加载的项覆盖成旧值。脏值判定用 `baseline` 快照逐键比对。
3. **值一律按字符串处理**：后端 `SettingItem.Value` 是 string，`number` 只是控件层面的提示。前端若把 number 存成 `30`（number），保存后可能与后端默认值 `"30"` 的形态不一致。
4. **`group=0`（Single）隐藏**：那 11 项是各驱动专用的临时目录（`115_temp_dir` 等），语义上属于驱动配置，不属于站点设置。
5. **保存后强制刷新公开设置 store**（`load(true)`）：`site_title`/`logo`/`main_color` 都在 `/api/public/settings` 里，不重拉就要等刷新页面才生效——实测保存后侧栏站点名立刻变。
6. **选项卡只显示分组名**：12 个分组 + 计数会撑爆 700px 宽的标签栏（出现滚动箭头），去掉计数并缩短 `Single Sign-On → SSO` 后正好放下。

### 18.3 真机验收（Edge + Playwright，8 项全绿）

| 用例 | 结果 |
|---|---|
| 设置加载 | 12 个选项卡，Site 组 5 项，无控制台错误 |
| 脏值计数 | 改 `site_title` 后按钮变为 `Save changes (1)` |
| 保存生效 | `GET /api/public/settings` 返回 `site_title="OpenList M2 Settings"` |
| 即时生效 | 侧栏 `.ol-brand-title` 同时变为新标题（store 强制重拉） |
| 分组内搜索 | 关键字 `announce` → 只剩 `announcement` 一行 |
| 切分组 | Style 组 14 项，select/number/textarea/bool 四类控件均正常渲染 |
| 分组恢复默认 | Traffic 组 `POST /admin/setting/default?group=11` 成功 |
| 还原 | `site_title` 改回 `OpenList` 并复查公开设置 |

`pnpm typecheck` 0 错误、`pnpm test` 41/41、`pnpm build` 通过（`Settings` 路由级分包 19.41 KB + 16.66 KB CSS）。

### 18.4 遗留

- **`help` 基本为空**：后端种子数据里 `help` 全为 `null`，帮助文案改由 `settings.<key>-tips`（124 条文案里已有一部分）兜底；两者都没有时只显示键名。
- **`multipath` / `float` 目前没有实例**：后端现有 121 项只用到 `string/number/bool/select/text`，这两条分支已实现但未被真机覆盖。
- **重置签名密钥未做端到端**：会立刻让当前 token 失效（页面不会自动跳登录），验证成本高，仅按接口契约实现 + 二次确认文案。
- **M2 剩余**：shares / plugins / backup-restore / dashboard / about。

### 18.5 勘误：主题持久化键是 `theme-mode`

做本轮暗色截图时发现：`useTheme` 用的是 `localStorage["theme-mode"]`（值 `light|dark|auto`），
而不是 `theme`。第 17 节当时用 `localStorage.setItem("theme","dark")` 拍的「暗色」截图
**实际是浅色**，第 17.4 节「含暗色主题」的结论已作废并按正确键重测（results 见 17.4 表格末行）。

- 正确做法：`localStorage.setItem("theme-mode","dark")` 后再 `page.goto`；或直接点顶栏主题按钮。
- 教训：主题这类「设置在 localStorage 里」的能力，断言不能只看「截图有变化」，要断言
  `document.documentElement.classList.contains("dark")`。M3 的暗色截图恰好是 UI 切换出来的，
  所以未被这个问题波及。

### 18.6 顺带修正：users 表格在 1024 级视口被裁

管理台内容区实际可用宽度约 940px（1440 − 220 侧栏 − 200 二级侧栏 − 40 内边距），
而 users 表各列固定宽合计 1050px，「操作」列（4 个按钮）默认被裁掉右半。

- 低频/危险动作（取消 2FA、删除）收进 `ElDropdown`，操作列 360 → 230px；
- 其余列宽同步收窄（ID 70→56、Role 110→96、Permission 150→120、2FA 80→66 等），合计约 900px，整表不再横向溢出。

## 19. M2 第四批：shares 分享管理（2026-10-25）

### 19.1 交付清单

| 文件 | 作用 |
|---|---|
| `api/types.ts` | `ShareInfo` / `ShareReq`（含 `new_id`、`accessed` 等易错字段的注释） |
| `api/shares.ts` | `listShares`/`getShare`/`createShare`/`updateShare`/`deleteShare`/`enableShare`/`disableShare`（注意：走 `/api/share`，**不是** admin 组） |
| `pages/manage/shares/Shares.tsx` | 列表（分享码+复制、路径、创建者、密码、过期、访问次数、状态）+ 新建/编辑对话框 + 启停/删除/复制直链 |

### 19.2 三个必须遵守的接口约定

1. **定位用分享码（`?id=`）**，不是数据库主键；改码要用 `new_id`，且仅管理员可改。
2. **`accessed` 会被 `UpdateAsync` 无条件覆盖**。第一版实现用列表快照回传，实测把访问次数从 1 清零到 0；
   已改为「打开编辑时先 `GET /share/get?id=` 拉最新值」再回传。
3. **创建要求 `files` 至少一项**，第一项成为分享根路径（`Sharing.Path`）；`/sd/{code}` 的空相对路径即取该根路径。

### 19.3 真机验收（Edge + Playwright，9 项全绿）

| 用例 | 结果 |
|---|---|
| 新建分享 | `POST /share/create` → `code=..., max_accessed=5`，行出现在列表 |
| 直链可下载 | 匿名 `GET /sd/{code}?pwd=s3cret` → 200，正文 `hello from share smoke test` |
| 密码生效 | 匿名 `GET /sd/{code}`（不带 pwd）→ **403** |
| 编辑保留计数 | 改 `max_accessed` 5→10，`accessed` 保持 1（修复前会变 0） |
| 禁用/启用 | `disabled=true→false`，禁用期间直链 403 |
| 复制直链 | 剪贴板得到 `http://localhost:5244/sd/{code}` |
| 删除 | 列表回到 0 |
| 暗色主题 | 正常（已用 `theme-mode` 键，非 `theme`） |
| 控制台 | 零错误 |

### 19.4 能力缺口：分享只能「直链」，不能浏览目录

后端目前只有：

- `/sd/{sid}[/{path}]`：**单文件**流式下载（受密码/过期/禁用/次数约束）；
- `/sad/{sid}[/{path}]`：分享内归档解压。

**没有**「按分享码列目录」的公开接口（`/api/share/get` 也要求登录），所以做不出 AList 那种可浏览的分享页；
目录分享生成出来的链接点开只会 404。UI 顶部已用 warning 明确提示，避免用户误以为分享可用。

要补齐需要两步（**已于第 20 节完成**）：
1. 让 `/api/fs/list|get` 支持「分享码鉴权」：`Authorization: <shareCode>` 时解析成受限用户（BasePath = 分享路径），
   这也是 Go 原实现的 `Auth(true)` + `FsAndShare` 组语义；
2. 前端加公开路由 `#/s/:code`，用分享码调用 `fs/list` 渲染列表 + 密码输入框。

### 19.5 需要你确认的一个安全姿态（未改动）

实测**匿名**请求 `POST /api/fs/list`（不带任何 token）返回 200，并列出 `/` 下所有挂载（`Pic`、`Shared`），
`POST /api/fs/get` 也能拿到任意文件的签名 `raw_url`。

- 这可能**符合** AList/OpenList「整站默认公开、靠 meta 密码保护单个目录」的产品模型；
- 但 `FsEndpoints` 里的注释写的是「让未登录访客**浏览公开分享**」，与实际行为（可浏览全部）不符。

我没有改鉴权逻辑（会牵动 guest 访问、`/p` 签名、S3/WebDAV 等多条链路）。如果这套部署不打算对外公开整站，
建议把这两个读端点改成「必须登录，或带合法分享码」——这也是 19.4 第 1 步的同一处改动。

### 19.6 M2 剩余模块全部卡在「后端没有接口」

| 模块 | 现状 |
|---|---|
| plugins | 全仓搜不到 `/api/plugin` 路由 |
| backup-restore | 无备份/恢复端点 |
| dashboard | 无统计/仪表盘端点（`/api/public/settings` 只有站点配置） |
| about | 无版本信息端点，只能写静态页 |

这四个要么先补后端，要么暂时保持 `(soon)` 禁用态。M2 已完成 8/12：storages、users、metas、indexes、tasks、settings、shares（+ 原本的 manage 外壳）。

## 20. 分享码鉴权 + 公开分享页（2026-10-25）

第 19.4 节记的缺口在这一轮补上了：分享链接现在**真的能打开并浏览目录**。

### 20.1 后端：把 `Authorization: <code>` 解析成受限访客

| 文件 | 改动 |
|---|---|
| `src/OpenList.Server/Endpoints/ShareAccess.cs`（新） | `ResolveAsync(ctx, shares, authenticated, password)`：未登录 + `Authorization` 非空 + **不含 `.`** 时按分享码校验，通过后返回 `Role=Guest / Permission=0 / BasePath=分享路径` 的用户 |
| `FsEndpoints.cs` | `/fs/list`、`/fs/get`、`/fs/dirs` 三个读端点注入 `ISharingService`，在 `JoinGuard` **之前**替换用户 |

要点：

1. **判定分享码用「有没有点」**：本项目 JWT 固定三段式；已认证请求直接跳过解析，避免把用户 token 当分享码。
2. **越界靠 `BasePath` 兜**：受限用户的 `BasePath = share.Path`，`JoinGuard` 会把请求路径拼到分享根下；实测 `/../Shared` 返回 `403 using relative path is not allowed`。
3. **密码复用请求体的 `password` 字段**：`FsListReq/FsGetReq` 本来就有该字段（原本给 meta 密码用），分享场景下传给 `VerifyAsync`。
4. **失败原样返回 `VerifyAsync` 的信封**：`wrong password` / `sharing disabled` / `sharing expired` / `sharing max accessed` / `sharing not found`，前端据此分流。

### 20.2 前端：独立成页的 `#/s/:code`

| 文件 | 作用 |
|---|---|
| `api/sharePublic.ts`（新） | **独立 axios 实例、不带登录 token**；`shareFsGet`/`shareFsList`（`Authorization: code`）+ `shareDownloadLink` |
| `pages/share/index.tsx`（新） | 密码闸门 / 目录列表 + 面包屑 / 单文件下载卡片 / 禁用·过期友好文案 / 暗色适配 |
| `router.ts` | 新增顶层路由 `/s/:code`（`layout: blank`，不套应用外壳） |
| `pages/manage/shares/Shares.tsx` | 「复制直链」改为「复制分享链接」`#/s/{code}`，顶部提示改成 info 说明 |

**为什么必须单独建 axios 实例**：后端只在「请求未认证」时才解析分享码。如果管理员带着自己的 JWT 打开分享链接，
分享解析被跳过，页面会按他自己的权限列出**整站根目录**而不是分享内容 —— 用独立实例（不注入 token）保证行为一致。

密码记忆：解密后的密码存 `sessionStorage["share-pwd:<code>"]`，同一标签页刷新/进出子目录不再重复询问，关标签即失效。

### 20.3 真机验收（Edge + Playwright，10 项）

| 用例 | 结果 |
|---|---|
| 密码闸门 | 首次打开显示密码框 |
| 错误密码 | `Wrong password, please try again` |
| 正确密码 | 列出 `sub/`、`hello.txt`、`README.md` |
| 子目录 + 面包屑 | 进入 `sub` 后列出 `inner.txt`，面包屑 `Root / sub` |
| 下载 | 点下载得到 `/sd/{code}/hello.txt?pwd=…`，请求 200 且正文正确 |
| 单文件分享 | 渲染文件卡片，下载 200 |
| 已禁用分享 | 显示 `This share has been disabled by its owner`（不再误显示密码框） |
| 越界防护 | 分享码 + `/../Shared` → 403 |
| 随机码 | `404 sharing not found` |
| 匿名无分享码 | **行为不变**：仍能列 `/`（本轮未收紧，见下） |

`pnpm typecheck` 0 错误、`pnpm build` 通过、控制台零错误、暗色正常。

### 20.4 仍然存在的限制

1. **匿名无分享码仍可浏览整站**：这只是「加了一条受控通道」，并没有关掉原来的口子。要收口就需要在
   `ShareAccess` 解析不到分享码时返回 401/403（或按 `guest` 账号的 `disabled` 状态判定）。这会改变
   「公开站点」的行为，需要你确认产品定位后再动 —— 代码位置很集中，改一处即可。
2. **多文件分享只暴露第一个路径**：`Sharing.Path` 取 `files[0]`，受限用户的 BasePath 也只有这一段。
   真正的多文件分享需要虚拟根目录（把 `files` 拼成一个合成列表），当前未实现。
3. **meta 密码与分享密码叠加**：若分享路径本身设了 meta 密码，访客还要再输一次（`AccessGateAsync` 用的是同一个
   `password` 字段，两者会互相顶掉）。未做区分，属已知边界。
4. **只读**：分享访客权限位为 0，写操作一律 403；没有「可写分享」。

## 21. M1 浏览体验收尾：目录树 / 网格 / 图墙 / 过滤 / 分页 / README（2026-10-26）

本轮把验收表 B 项（文件管理）里**只差浏览侧**的几块补齐，并顺手挖出并修掉一个隐蔽的
「双 Vue 运行时」问题（它让整个组件树在点击后静默失效）。

### 21.1 交付清单

| 文件 | 作用 |
|---|---|
| `utils/list.ts`（新） | 纯函数层：`filterObjs`（文件名子串，大小写不敏感）、`paginate`（越界夹取）、`pageList`（页码序列含省略号）、`PAGE_SIZES` |
| `composables/useViewLayout.ts`（新） | 视图偏好本地持久化：`ol-view-mode`（list/grid/image）、`ol-tree-visible` |
| `components/FolderTree.tsx`（新） | Element Plus `ElTree` 懒加载目录树；深链用 `default-expanded-keys` + `current-node-key` 自动展开高亮 |
| `pages/home/GridView.tsx`（新） | 网格 / 图墙两模式；图片走 `/d` 直链 + `loading="lazy"`；勾选框 hover 才出现；右键复用同一套菜单 |
| `pages/home/Pager.tsx`（新） | 自绘分页条（不用 `ElPagination`，原因见 21.3） |
| `pages/home/ReadmeCard.tsx`（新） | 目录 README：目录内 `readme.md`/`footer.md`/`bottom.md` 优先，meta 的 `readme` 兜底；marked → DOMPurify |
| `composables/useFileList.ts`（改） | 新增 `keyword` / `page` / `pageSize` / `kindFilter`，串成「排序 → 过滤 → 分页」一条 computed 流水线；切目录重置过滤与页码 |
| `pages/home/index.tsx`（改） | 左树右列表布局、视图切换按钮组、过滤框、分页条、README 卡片；列表/网格共用同一份选择态 |
| `styles/layout.css`（改） | 树面板 / 网格卡 / 分页 / README 样式，全部走 `--ol-*`/`--el-*` token，深浅色自动跟随 |
| `scripts/verify-v2.mjs`（新） | 18 项 Playwright 真机断言，可重复执行（`BASE` `TARGET` 环境变量） |

### 21.2 关键决策

1. **客户端分页，不用后端 `per_page`**：C# 端 `fs/list` 不接受 `sort` 参数，排序只能在前端做；
   服务端分页会让每页各自排序、跨页顺序不一致。一次取全量再切片，排序/过滤/分页三者永远自洽。
   超大目录的逃生路径是把站点 `default_page_size` 设为 0。
2. **图墙的过滤参与分页**（`kindFilter`）：否则「共 46 项 / 每页 30」会把 5 张图摊到两页里，
   分页器写着一个数、用户看到另一个数。
3. **网格单击 = 打开**，与列表点文件名一致；选择只走勾选框。不做「单击选中、双击打开」的桌面式交互，
   避免两套肌肉记忆。
4. **树不做手工 `expand()` 轮询**：把祖先链交给 `default-expanded-keys`，Element Plus 在懒加载节点
   插入时会检查这份 keys 并自动展开 —— 比「expand 后轮询 `node.loaded`」少一处竞态。
5. **README 只读不写**：先做渲染，Markdown 编辑（Monaco）留给预览器补全那一批。

### 21.3 踩到的坑：pnpm 装了两份 Vue，EP 组件静默失效

症状（极其误导）：

- 点击「网格」按钮无反应，`.el-radio-button.is-active` 仍是「列表」；
- `ElPagination` 渲染成 `<div class="ol-pager"><!----></div>`，无报错、无警告；
- 控制台偶发 `TypeError: e.scope.on is not a function`，栈全在 `runtime-core` 里，指不到业务代码。

根因：`package.json` 里 Vue 是 `3.6.0-rc.12`（prerelease），而 `element-plus` 的 peer 是 `vue: ^3.3.7` ——
**prerelease 不满足该 semver 区间**，pnpm 于是给 EP 单独装了一份 `vue@3.5.44`。
一个页面里跑着两份 `runtime-core`：EP 内部的响应式与渲染器不认识应用侧创建的 vnode，
表现为「组件渲染成空注释 + 状态改了不刷新」，且不报可定位的错误。

修复：`pnpm-workspace.yaml` 增加 `overrides`，把 `vue` 及 `@vue/*` 全锁到 **3.5.22**（稳定版）。
连带结论：

- **Vapor 实验推迟**：`vue.runtime-with-vapor` 只在 3.6 构建里，本项目的 VDOM 优先策略不受影响，
  等 3.6 正式版发布后再一次性升级 + 全量回归（届时 `overrides` 一并改）。
- **凡是「组件渲染成空 + 无报错 + 更新失效」，先查 `ls node_modules/.pnpm | grep vue@`**，
  再谈组件兼容性。

### 21.4 验证（真实后端 + Edge/Playwright，18 项全绿）

测试数据：在 `/Shared/ol-v2-test` 临时造 40 个 txt + 2 个子目录 + 3 张图 + 1 个 `README.md`（46 项），
跑完即删（`G:\Share\ol-v2-test` 已清理）。

| 用例 | 结果 |
|---|---|
| 目录树渲染（懒加载第一层 = 挂载点） | 2 节点 → 展开后 12 节点 |
| 深链 `?path=/Shared/ol-v2-test` 自动展开并高亮 | ✅ `current="ol-v2-test"` |
| 点击树节点导航 | ✅ `?path=/Pic` |
| 列表按 30 条/页分页 | ✅ 30 行；第 2 页 16 行 |
| 分页条总数 | ✅ 「共 46 项」 |
| 网格视图 | ✅ 30 张卡，目录排在前面（`子目录A` 居首） |
| 图墙只显示目录 + 图片 | ✅ 5 格、3 张 `<img>` |
| 目录内点击进入 | ✅ URL 变化 + 面包屑更新 |
| 过滤命中 `file-1*` | ✅ 10 条（file-10..19） |
| 过滤无命中 | ✅ 「没有匹配「zzz-not-exist」的条目」 |
| 过滤/图墙时数量标签切换口径 | ✅ 「显示 10 / 46 项」 |
| 目录树折叠/展开 | ✅ |
| 控制台 | ✅ 零错误 |

工程检查：`pnpm typecheck` 0 错误、`pnpm test` **54 用例全绿**（新增 `utils/list.spec.ts` 15 项：
过滤 4 + 分页 5 + 页码序列 4 + 边界…）、`pnpm build` 通过，`home` 分包 **199 KB → gzip 78 KB**（比上轮 229 KB 略降，
因为去掉了 `ElPagination`/`ElRadioGroup` 的引入）。

代码规模：`frontend-vue/src` **85 个 ts/tsx、11,719 行**。

### 21.5 下一步（M1 剩余 + 后续）

1. ~~**工具栏 7 个操作**~~ ✅ 已完成（第 22 节），顺带修了 5 个后端/测试缺陷；剩余：批量重命名 / 正则重命名；
2. ~~**预览器补 8 项**~~ ✅ 已完成 7 项（第 23 节：archive/url/plist/ipa/doc/xls/ppt/asciinema），Monaco 文本编辑留待独立成片；
3. **递归搜索**（`fs/search` 已就绪）+ 浏览历史；
4. M4 媒体域、M5 切换发布。

参考仓库的逐项对照与 Peach 的结构借鉴清单见 [`docs/frontend-reference-analysis.md`](./frontend-reference-analysis.md)。

## 22. M1 操作集第二批：工具栏 7 个操作（2026-10-26）

第 21.5 节的第 1 项。至此验收表 B（文件管理）里「操作集」一栏只剩「批量重命名 / 正则重命名」两个纯前端小项。

### 22.1 交付清单

| 文件 | 作用 |
|---|---|
| `api/fs.ts`（改） | 新增 `fsRemoveEmptyDirectory` / `fsRecursiveMove` / `fsArchiveDecompress` / `fsArchiveList` / `fsAddOfflineDownload` / `fetchOfflineDownloadTools` + `DeletePolicy` 枚举 |
| `utils/zip.ts`（新） | 前端打包：`collectEntries`（递归收集，保留空目录）、`writeZip`（fflate 流式 STORE）、`canStreamSave`/`pickSaveTarget`（File System Access）、`saveAsBlob`（回退） |
| `pages/home/toolbar/NewFileDialog.tsx` | 新建空文件（`PUT /fs/put` 零字节写）+ 覆盖开关 |
| `pages/home/toolbar/RemoveEmptyDirDialog.tsx` | 递归清理空目录 |
| `pages/home/toolbar/RecursiveMoveDialog.tsx` | 整树搬移 + 冲突策略（跳过/覆盖）+ 目录选择器 |
| `pages/home/toolbar/DecompressDialog.tsx` | 解压（目标目录/归档密码/解压到同名新目录/覆盖） |
| `pages/home/toolbar/ShareDialog.tsx` | 创建分享（分享码/密码/过期/次数/备注），成功后展示 `#/s/{code}` 并可复制 |
| `pages/home/toolbar/OfflineDownloadDialog.tsx` | 离线下载（多行 URL/工具/删除策略；无工具时前置拦截） |
| `pages/home/toolbar/PackageDownloadDialog.tsx` | 打包下载（三段式进度、可取消、内存上限保护） |
| `pages/home/toolbar/ToolbarDialogs.tsx` | **7 个对话框的唯一渲染点**：`active` 键 + `targets` 快照 |
| `pages/home/index.tsx`（改） | 工具栏「更多」下拉、批量栏（打包/分享，只读也可用）、行/空白右键菜单接入 |
| `utils/zip.spec.ts`（新） | 6 项单测：用 fflate 的 `unzipSync` 反解产出的 zip（含空目录、404 改名报错、取消） |
| `scripts/verify-ops.mjs`（新） | 24 项 Playwright 真机断言（含递归移动真实执行、解压产物、分享码匿名访问、zip 可解） |

### 22.2 关键决策

1. **打包在前端做**：后端没有「打包」端点（只有归档的 list/decompress）。新增服务端流式打包要处理驱动抽象、临时文件、断连清理，成本远高于浏览器里拼；上游 OpenList-Frontend 也是同一策略。
2. **`fflate` + STORE（不压缩）**：网盘内容多是图片/视频/已压缩包，deflate 只烧 CPU 换 1–2% 体积。单文件流式读、流式写，峰值内存只有 fetch 缓冲窗口。
3. **落盘两条路**：`showSaveFilePicker`（Chromium/WebView2）边压边写不占内存；其余浏览器累积成 Blob，**超过 1 GiB 直接拒绝并提示分批** —— 宁可让用户分批，也不要把标签页搞崩。
4. **对话框收敛**：7 个 `xxxVisible` ref 会让页面 setup 长出 7 个互不相干的状态；改成一个 `active` 键 + `targets` 快照，谁显示由 `ToolbarDialogs` 决定。
5. **只读也能下载/分享**：批量栏不再要求 `writable`，写操作按钮各自按权限显隐 —— 访客选中文件后「打包下载」是合理诉求。

### 22.3 顺手修掉的五个后端/测试缺陷

都是**接上前端才暴露的**，不修的话这些按钮是坏的（或者测试一直是红的）：

| 位置 | 问题 | 修法 |
|---|---|---|
| `/fs/remove_empty_directory`（语义） | 实现是「path 自身为空才删 path」，与 Go/上游文案（*delete all its empty subfolders*）相反；当前目录非空时直接 400；path 为挂载点根时 `DeleteDirectory` 不生效却回 200（**假成功**） | 改为自底向上递归清理空子目录、**保留 path 本身**；单个目录删除失败跳过而非中断 |
| 同上（契约） | Go 用 `src_dir`，本仓库用 `path`；按上游契约调用的客户端会拿到默认 `/`（等于扫整站根） | 新增 `RemoveEmptyDirectoryReq` 同时接受 `path` 与 `src_dir` |
| `/fs/archive/decompress` | `put_into_new_dir` 用**完整文件名**当目录名（`bundle.zip/`），与归档文件同名 → 驱动 `CreateDirectory` 冲突，恒返回 500 `already exists` | `Path.GetFileNameWithoutExtension`（`bundle.zip` → `bundle/`），对齐 Go |
| `DbInitializer.BackfillBuiltInAdminAsync` | 被 `MigrateSchemaAsync` 调在**建表之前**，全新库直接 `SQLite Error 1: no such table: x_users`；`BuiltInAdminTests` **22 个用例全红** | 查询包进 `catch (DbException)`：那个时间点不可能有「历史管理员」需要回填，跳过即可（旧库此时表一定存在，回填不受影响） |
| `BuiltInAdminTests.BuildContext` | 用 `:memory:` + `EnsureCreated()`：SQLite 的 `Exists()` 对内存库**恒为 true**（能连即存在），`EnsureCreated` 直接跳过建表 → 后面照样 `no such table` | 改为一次性临时文件库（每个用例一个 GUID），`Exists()` 才会正确返回 false |

另外 `FileSystemServiceTests` 的两个用例断言的是旧语义（删 path 本身 / 非空 400），按新语义重写为
「清空空子目录、保留传入目录、非空不报错」，并补了一个 404 用例。

### 22.4 验证

**环境**：隔离后端（临时 SQLite + `AdminPassword` + 独立端口 5001 + `DistDir=frontend/dist`），
不触碰仓库现有数据；测试挂载 `/e2e` → `G:\Share\ol-v2-ops`（44 项固定装置，脚本自建自删）。

| 用例（verify-ops，24 项全绿） | 结果 |
|---|---|
| 新建文件 `e2e-new.txt` | ✅ 列表出现 |
| 删除空目录 | ✅ `空目录B` 消失、非空的 `子目录A` 保留 |
| 解压 `bundle.zip` | ✅ 产出 `bundle/`，可读且含 `hello.txt`（解出 2 项） |
| 分享 `子目录A` | ✅ 生成 `#/s/{code}`；用该码匿名列目录拿到 `inner.txt` |
| 打包下载 `子目录A` | ✅ 触发下载，`unzipSync` 解出 `子目录A/`、`子目录A/inner.txt`（275 B） |
| 递归移动 | ✅ 未选目标时拦截；选目标后**源 404 / 目标 200** |
| 离线下载 | ✅ 未配置工具时展示原因并禁用提交 |

**回归**：`verify-v2.mjs`（浏览体验）18/18 全绿；`pnpm test` **60 用例**全绿；
`pnpm typecheck` 0 错误；`pnpm build` 通过（home 分包 230 KB / gzip 78 KB）；
`dotnet test` **462 项：461 通过 / 0 失败 / 1 跳过**（改了清理逻辑与 DbInitializer，必须回跑 ——
这一跑把此前一直红的 22 个 `BuiltInAdminTests` 也带绿了，见 22.3）。

代码规模：`frontend-vue/src` **96 个 ts/tsx、约 13.2k 行**。

### 22.5 两个脚本的用法（顺序有讲究）

```bash
# 隔离后端：临时库 + 独立端口（不碰现有数据）
cd .e2e && AdminPassword=e2e12345 ASPNETCORE_URLS=http://127.0.0.1:5001 \
  DistDir=$PWD/../frontend/dist dotnet ../src/OpenList.Server/bin/Debug/net10.0/OpenList.Server.dll

# 首次需要建挂载：POST /api/admin/storage/create {mount_path:"/e2e", driver:"Local", addition:{root_folder_path}}
node scripts/verify-v2.mjs      # 先跑（要求干净装置 44 项）
node scripts/verify-ops.mjs     # 后跑（会重建装置并改数据）
```

### 22.6 遗留

1. **离线下载不支持拖入 `.torrent`**：上游会把 torrent 转成磁力链接（bencode + SHA1）。当前只接受 URL/磁力文本；
   要补需引入 `bencode`（或自写 ~80 行解析 + Web Crypto SHA-1）。
2. **解压不能选归档内部条目**：`/fs/archive/list` 已封装，但「只解其中几个文件」的树形选择 UI 成本高于收益，暂缓。
3. **`delete_policy` 后端未实现**：`OfflineDownloadService` 收下该字段但不做删除动作，UI 仍展示以对齐上游。
4. ~~**重命名批次**~~ ✅ 已交付（第 24 节）。

## 23. M1 预览器补齐第二批：归档 / Office / 安装类（2026-10-26）

### 23.1 交付清单

| 预览器 | 实现要点 | 新依赖 |
|---|---|---|
| `archive`（zip/tar/gz/tgz/bz2/7z/rar） | 一次拉全量条目 → 前端建树（含隐式目录）→ 面包屑导航；点文件走 `/ae?inner=` 内联预览 | 0 |
| `url_open`（`.url`） | 自写 INI 解析，取 `InternetShortcut.URL`；解析失败回退展示原文 | 0 |
| `plist` | `itms-services://` 安装按钮 + 「仅 iOS 生效」提示 | 0 |
| `ipa` | `/i/{base64}.plist` OTA 安装 + TrollStore 两条路径 | 0 |
| `doc`（docx/docm/dotx） | `docx-preview` 分页渲染，样式表挂在组件容器内（不污染全局 `@page`） | `docx-preview` |
| `xls`（xls/xlsx/xlsm/xlsb） | SheetJS 解析 → 自绘表格，多表切换，行列截断 500×40 | `xlsx` |
| `ppt`（pptx/pptm/ppsx） | `pptx-preview` 平铺模式（`mode: "list"`） | `pptx-preview` |
| `asciinema`（`.cast`） | `asciinema-player` v2 录制回放，组件卸载 `dispose()` | `asciinema-player` |

全部走 `defineAsyncComponent`，**每个预览器一个独立 chunk**：入口 `home` 仍是 234 KB（gzip 80 KB），
只有真的打开对应文件才下载（pptx 最重，1.2 MB / gzip 390 KB，可接受）。

### 23.2 后端：补上「归档内单文件」这一环

旧前端的归档预览靠 `/ae/{path}?inner=/a/b.txt` 取单个条目，本仓库的 `/ae` 原本只会「解压到父目录」，
所以按钮接上去也只能看列表。这次补齐：

1. `IFileSystemArchiveReader.OpenArchiveEntryAsync(archive, entry, pass)` —— 返回**可 Seek** 的流。
   压缩流的条目是前向只读的，而内联预览要支持 Range（视频拖动、文本分段），
   因此实现把条目落到**临时文件**（`FileOptions.DeleteOnClose`，句柄关闭即删）再交出 `FileStream`。
2. `LocalDriver` 委托 `SharpCompressArchiveSupport.OpenEntry`，拒绝 ZipSlip 路径。
3. `/ae/{path}?inner=&pass=`：带 `inner` 就流式返回（按扩展名给 MIME + `enableRangeProcessing`），
   不带才解压。404/405 语义分清：条目不存在 → 404。

### 23.3 顺手修掉的三处契约问题

| 位置 | 问题 | 修法 |
|---|---|---|
| `/fs/archive/list` | 与 `/list`、`/get` 不同，它**没有 `AllowAnonymous`** → 公开目录里点开压缩包直接被踢去登录页（401） | 加 `AllowAnonymous` + `AccessGateAsync`（目录密码/白名单/隐藏条目与 `/list` 完全一致） |
| 同端点密码 | 把 `archive_pass ?? password` 当成压缩包密码，目录密码被误用 | 拆开：`password` 走 meta 门禁，`archive_pass` 才是压缩包密码 |
| `ArchiveEntry.IsDir` | 全局 camelCase 序列化成 `isDir`，而其余对象都是 `is_dir`，前端读不到 → 归档里全是「文件」 | 加 `[JsonPropertyName("is_dir")]`，与 `Obj` 对齐 |

### 23.4 关键决策

1. **一次拿全量条目，前端建树**：后端 `inner_path` 未实现；逐目录请求要为隐式目录（zip 里常有
   `a/b.txt` 却没有 `a/` 条目）额外兜底，反而更慢。压缩包本来就得扫中央目录，一次给完更划算。
2. **原生预览优先于 iframe**：`docx/xlsx/pptx` 有原生渲染器后，管理员配的在线预览（`iframe_previews`）
   退到下拉里的第二项 —— 默认不该把用户文件发到第三方站点。相应的注册表单测断言一并调整。
3. **老格式不硬撑**：`.doc` / `.ppt`（OLE 容器）不做前端解析，直接落到「下载」，比渲染出错好。
4. **`Extract here` 只做「解到同名新目录」**：与 `DecompressDialog` 同一份请求（`put_into_new_dir: true`），
   不在这里再做一套目标目录选择器。

### 23.5 验证

隔离后端（临时库 + 5001 端口 + `DistDir=frontend/dist`，挂载 `/e2e` → `G:\Share\ol-v2-prev`）：

- `scripts/verify-preview.mjs` **18/18**：归档根层 4 项且目录优先、隐式目录逐层展开、
  归档内 PNG 经 `/ae?inner` 渲染成功（`naturalWidth > 0`）、面包屑回根、
  `.url` 解析出 `https://example.com/landing?x=1`、xlsx 表格内容与多表切换、docx 正文渲染、cast 播放器挂载、全程零控制台错误。
- 接口层手测：`/ae` 单条目 `200 text/plain`、Range 请求 `206`（`0-4` 返回 5 字节）、PNG `image/png`、缺失条目 `404`。
- `pnpm test` **79 用例**；`pnpm typecheck` 0 错误；`pnpm build` 通过。
- `dotnet test` **465 项：464 通过 / 0 失败 / 1 跳过**（新增 `ArchiveEntryStreamTests` 3 项：
  可 Seek、缺失条目、路径穿越）。
- 回归：`verify-v2.mjs` **18/18**、`verify-ops.mjs` **24/24** —— 归档端点改成匿名后，
  公开浏览与写操作都没被带坏（注意两脚本有先后顺序，见 §22.5）。

### 23.6 遗留

1. ~~**文本编辑器（Monaco）未做**~~ → 已用 **CodeMirror 6** 交付（第 25 节）：体积 1/4、无需 worker；
   并发覆盖与编码转换仍是遗留。
2. **归档内视频不做流式转码**：单条目先落临时文件再给 Range，等于每次打开都解压一遍；
   几十 MB 以内没问题，超过就该走「解压后播放」。
3. **`inner_path` 仍未实现**：前端不用，但第三方客户端按 Go 契约带这个参数时会被忽略（返回全量）。
4. **`.wps` / `.et` / `.dps`（WPS 自有格式）** 不支持，与 `.doc/.ppt` 同源问题。

## 24. M1 收尾：批量重命名（2026-10-26）

### 24.1 交付清单

| 文件 | 内容 |
|---|---|
| `utils/rename.ts` | 三种规则的**纯函数**实现：正则替换、序号命名、查找替换；统一收尾做非法名校验与撞名检测 |
| `utils/rename.spec.ts` | 18 个用例（反向引用、时间变量、`{ext}`、目录不拆扩展名、撞名、路径字符……） |
| `pages/home/toolbar/BatchRenameDialog.tsx` | 规则表单 + **实时预览**在同一对话框；问题以红色行/告警条标出，`应用` 直接禁用 |
| `scripts/verify-rename.mjs` | 真机验收 12 项 |

入口：批量操作栏「重命名」（写权限 + 有选中时出现）。

### 24.2 三个刻意的差异

1. **不再是「表单 → 点确定 → 再弹预览」两步**：上游是两个 Modal 串联，改一个字符就得「上一步」重来。
   这里输入即算，预览行区分「会改 / 不变 / 非法」，提交前就知道结果。
2. **撞名与非法名在前端拦下**：上游只校验空值与正则语法，撞名要等后端逐条改名时才炸，
   而且炸到一半会留下「改了一半」的目录。现在 `a→b` 而 `b` 同在选中集合里也会被判定为冲突
   （后端逐条改名，交换必然失败）。
3. **序号模式支持 `{ext}`，且不吞扩展名**：模板末尾自带扩展名（`clip{number}.webm`）时不再拼原扩展名；
   目录不参与扩展名拆分（`v1.2` 这种目录名不会被切成 `.2`）。

### 24.3 行为约定（写进单测，避免以后漂）

- 正则：全局替换，支持 `$1` 与 `{created_year}` / `{modified_month}` 等 12 个时间变量（本地时区）。
- 序号：`{number}` 可放任意位置（默认追加在主名后）；起始序号与补零位数可调；**目录也占号**。
- 查找替换：字面量、区分大小写、替换**全部**出现位置（上游只替换第一处）。
- 全部模式都会 `trim()` 结果，且只提交真正变化的条目。

### 24.4 验证

- `scripts/verify-rename.mjs` **12/12**：正则改名落到服务端、撞名告警 + `应用` 禁用、非法字符拦截、
  序号补零（目录占号 `ep01`、文件 `ep02…ep07`）、查找替换、零控制台错误。
- `pnpm test` **97 用例**（新增 18）· `typecheck` 0 错误 · `build` 通过。
- 回归：`verify-v2.mjs` **18/18**、`verify-ops.mjs` **24/24**（批量栏新增了一个按钮）。

### 24.5 遗留

1. **单条重命名仍是 prompt 输入框**（`promptRename`）：批量对话框其实也能覆盖单条，但「选中一行 →
   批量重命名」比双击改名多两步，暂不合并。
2. **`/fs/regex_rename` 后端端点没被前端使用**：规则在前端算完走 `/fs/batch_rename`（与上游前端一致），
   少一次往返，也便于预览。
3. **改名后不做跨目录冲突预检**：只校验本次选中集合内部的撞名；与目录里未选中的文件重名仍由后端拒绝。

## 25. 文本编辑器：CodeMirror 6（2026-10-26）

### 25.1 为什么不是 Monaco

上游用 Monaco（`text-editor.tsx`，530 行，含语言选择/缩略图/行列统计）。这里换成 **CodeMirror 6**：

| | Monaco | CodeMirror 6（本次） |
|---|---|---|
| 体积 | 核心 + 4 个 worker ≈ 800 KB gzip 起 | 编辑器 chunk **335 KB 原始 / ~110 KB gzip** |
| 接入成本 | 要配 `MonacoEnvironment.getWorker` + 4 条 `?worker` 导入，Vite 8 下还要处理 `editor.api` 的 ESM 路径 | 纯 ESM，`new EditorView()` 即可 |
| 能力 | 完整 IDE（TS 类型推导、跨文件跳转） | 行号/折叠/多光标/搜索/撤销栈/语法高亮，够改一个文本文件 |

我们缺的正是「打开、改、存回去」，不需要语言服务。少 700 KB 换掉一个打包雷区，值得。
**代价**：没有 TS 智能提示与错误下划线 —— 这类需求该交给本地编辑器，而不是网盘网页。

### 25.2 三个硬约束（都写进代码注释）

1. **只按 UTF-8 读写**。文件不是合法 UTF-8 时用 `TextDecoder("utf-8", { fatal: true })` 捕获错误，
   降级为**只读**并说明原因 —— 用 UTF-8 硬保存会把 GBK 文件整份写坏，比不能编辑糟得多。
2. **保存是整份覆盖**（`PUT /fs/put` + `Overwrite: true`），没有版本号/ETag，
   因此有未保存改动时挂 `beforeunload` 拦截。
3. **403 之后连编辑器一起冻住**：`readOnly` 放进 `Compartment`，收到 403 时 `reconfigure`，
   而不是只把保存按钮变灰（否则用户还能继续敲字然后再次撞墙）。

### 25.3 交互

- 预览器下拉里出现 **Text Editor**（`order: 40`），默认仍是只读的 **Text View**（`order: 30`）——
  「看一眼」和「要改」是两种意图，不该默认把所有人扔进编辑器。
- 工具栏：语言标记（按扩展名懒加载 json / js / ts / md / py，各自独立 chunk）、
  行列、字符数、「未保存」标记、保存按钮。
- `Ctrl/Cmd+S` 保存（keymap 排在 `defaultKeymap` 之前，否则被浏览器默认行为吞掉）；
  `Ctrl+Z` 撤销回原样后 dirty 自动消失（与原始内容比较，而不是「一次改动即脏」）。

### 25.4 验证

`scripts/verify-editor.mjs` **11/11**（隔离后端 + 真实写盘）：

- CodeMirror 挂载、内容载入 → 输入后出现「未保存」→ `Ctrl+S` 后**磁盘文件真的变了**
  （脚本直接 `readFileSync` 校验 `hello editor\n\nsecond line`）→ 未保存标记消失；
- `Ctrl+Z` 撤销后重新变脏；
- 打开 GBK 文件：出现「非 UTF-8，编辑已禁用」提示、保存按钮禁用、内容仍可查看；
- 全程零控制台错误。

`pnpm test` **97 用例** · `typecheck` 0 错误 · `build` 通过（`home` 242 KB / gzip ~82 KB，
编辑器 chunk 335 KB 原始、只在切到该预览器时下载）。

### 25.5 遗留

1. **没有并发保护**：A 保存后 B 再保存会覆盖 A 的改动（后端无版本号）。要补得先给 `/fs/put` 加 If-Match。
2. **不能改编码**：GBK 文件只能下载后用本地编辑器；上游的「编码下拉 + 另存为」没做。
3. **不做语法诊断**：只高亮不报错，写坏的 JSON 不会提示。

## 26. 种子预览 + 拖入 .torrent（2026-10-26）

### 26.1 交付清单

| 文件 | 内容 |
|---|---|
| `utils/bencode.ts` | 只读 bencode 解码。**每个节点带原始字节区间** —— info_hash 必须对 info 字典的 bencoded 原文做 SHA-1，解析后重新编码会因键序/整数格式差异算出不同的哈希 |
| `utils/sha1.ts` | 纯 TS SHA-1（~60 行）。不用 `crypto.subtle`：Web Crypto 只在安全上下文可用，而自建网盘最常见的就是 `http://192.168.x.x` 访问，那里 `crypto.subtle` 是 `undefined` |
| `utils/torrent.ts` | 种子模型（name/size/pieces/files/trackers/CAS/private）+ 磁力链接生成；只做结构校验，不重算分片哈希 |
| `pages/home/previews/TorrentPreview.tsx` | 元信息 + 可折叠文件树（复用归档的 `buildArchiveTree`）+ 复制磁力 + 离线下载按钮 |
| `utils/bus.ts` | 20 行类型化事件总线。预览器嵌在 `PreviewHost` 里而离线下载对话框挂在页面级，穿三层传回调不如一个事件 |
| `scripts/verify-torrent.mjs` | 真机验收 15 项 |

单/多文件的判定看**键是否存在**而不是 `files` 是否非空：空 `files` 数组是坏种子，
当成单文件会凭空造出一个 0 字节条目（单测锁定）。

### 26.2 三个决策

1. **解析全在浏览器**，不调 `/api/fs/torrent/parse`：那个端点要登录，公开目录/分享页的种子
   同样要预览；文件字节本来就在手上（`props.src`），再让服务端读一遍纯属往返。
2. **秒传按钮没做**：`/fs/torrent/rapid_upload` 后端就绪，但**没有任何驱动实现**对应接口，
   按钮放上去只会 501。CAS 信息照常展示（种子自带的 `x-cas`），等驱动跟上再接。
3. **拖入 .torrent 直接开离线下载**，不走「先上传再预览」：种子的用途就是喂给下载器，
   存进网盘反而是垃圾。混入非种子文件时**整批拒绝**并提示，不悄悄吞掉一部分 ——
   用户拖一堆文件通常是想上传，明确拒绝好过静默丢弃。拖入其它文件也因此不再触发
   浏览器的「打开文件」跳页（旧版页面不接 drop，拖错位置直接丢页面状态）。

### 26.3 验证

- `scripts/verify-torrent.mjs` **15/15**：info_hash 与装置侧独立计算的 SHA-1 一致
  （避免「前端自己算自己验」的自证循环）、元信息齐全、文件树目录优先且可折叠、
  点离线下载后磁力链接（xt/dn/tr）预填进对话框、单文件种子、拖入 `.torrent` 直接预填、
  拖入 `notes.txt` 出提示且不开对话框、零控制台错误。
- `pnpm test` **114 用例**（新增 17：SHA-1 用 Node crypto 按 0..130 每个长度交叉验证，
  补齐 55/56/64/120/128 的补齐边界）；`typecheck` 0 错误；`build` 通过，
  `TorrentPreview` chunk 仅 5.8 KB，入口 `home` 247 KB / gzip ~83 KB。
- 本轮零后端改动，`dotnet test` 维持上轮 465 项（464 通过 / 1 跳过）。

### 26.4 遗留

1. **超大种子（数千文件）没有虚拟滚动**：折叠状态不渲染子树，展开一个 5000 文件的目录会一次出 5000 行。
2. **磁力链接没做 `xl`（显示名）与 `so`（DHT 节点）字段**：主流下载器都够用。
3. **拖入多个种子时全部塞进一个对话框**：链接框多行磁力，后端本来就支持一次多 URL，行为正确但预览不直观。
4. `verify-torrent.mjs` 依赖 `G:\Share\ol-v2-torrent` 装置，装置生成脚本在文档里没有单列
   （与 §23 的装置同风格，bencode 编码器 30 行内联在生成脚本中）。

## 27. M4 第一片：媒体库浏览 / 详情 / 播放 / 进度（2026-10-26）

### 27.1 交付清单

| 文件 | 内容 |
|---|---|
| `api/media.ts` | 媒体域类型（对齐**线上字段名**：实体的 `CreatedAt` 没有 `[JsonPropertyName]`，线上是 `createdAt`）与全部浏览/状态端点封装 |
| `pages/media/index.tsx` | 库页签（含跨库「全部」）+ 继续观看横滑条 + 海报网格 + 关键词防抖过滤 + 排序 + 复用自绘分页条 |
| `pages/media/ItemDetail.tsx` | 详情页：backdrop 头图、NFO 元信息、收藏/看过/稍后/刮削（按 `manage_media` 权限显隐）、文件清单 |
| `pages/media/MediaPlayer.tsx` | 内嵌播放器：原生 `<video>`、断点续播、倍速、播完自动下一文件 |
| `ItemEndpoints.cs` | 新增 `GET /api/media/items/{id}/image?kind=poster\|backdrop` |
| `MediaLibraryService.cs` | `library_id=0` 跨库浏览（原来直接 404 "library not found"） |
| `Program.cs` | JWT `OnMessageReceived` 补 `?access_token=` 回退 |
| `scripts/verify-media.mjs` | 真机验收 20 项 |

### 27.2 三个后端补口（都是前端接上才暴露的）

| 问题 | 修法 |
|---|---|
| 海报网格不能逐条 `fs/get` 取签名链接（一页 30 次）也无法自带 Authorization 头（`<img>` 的限制） | ① 专用图片端点：`GetItemAsync` 的 BasePath 过滤兜底可见性，VFS 路径整读（8MB 上限）、TMDB 外链 302；② JWT 回退读 `?access_token=`（header 优先，标准 ASP.NET Core 模式） |
| 跨库列表 `library_id=0` 直接 404 | 先解析用户可见库集合，再按 id 集合过滤 |
| 排序值口径 | 后端 `ApplySort` 认 `created/updated/title/rating/year`，不是想当然的 `added_at` |

### 27.3 关键决策

1. **详情页内嵌自己的播放器，不复用首页 `VideoPreview`**：后者把「同目录兄弟视频做剧集列表」绑在
   `?path=&preview=` 路由语义上；媒体页自己就掌握条目全部文件，切集 = 点下一行。
   进度上报的**节流（10s）与写回**也在详情页做 —— 播放器只报「现在第几秒」。
2. **进度是条目级的**（不区分到文件）：多文件条目（多集）只在播第一个文件时续播，
   切集从 0 开始，否则每集都从第一集的位置开始看。Peach 同样是条目级。
3. **播完自动下一集 + watched**：`ended` 时标记已看并清零进度（避免「看完了还从 47 分钟续播」）。
4. **海报加载失败降级为占位块**（无 sidecar 的条目很常见），而不是裂图。

### 27.4 验证

- `scripts/verify-media.mjs` **20/20**：库页签、网格 2 条、**海报经图片端点真实渲染**
  （`naturalWidth > 0`）、Pending 标签、关键词过滤、星标乐观更新落库、
  NFO 标题/年份/评分/原名/题材/简介、fanart 背景、图片端点 200/404/401 三态、
  播放器挂载、**真实 seek 到 15s 后进度落库（progress=15）**、详情页显示 `0:15`、
  继续观看条、看完标记 + 进度清零、零控制台错误。
- 装置：`G:\Share\ol-v2-media`，ffmpeg 生成真 MP4（20s testsrc）+ Kodi NFO + poster/fanart；
  **NFO 字段名用 Kodi 约定的 `<originaltitle>`**（首版写成 `original_title`，刮不出来）。
- `pnpm test` 114 用例 · `typecheck` 0 错误 · `build` 通过；`dotnet test` 回跑见 §27.5。

### 27.5 遗留

1. **库里改 NFO 不会刷新已 Matched 的 local 条目**（扫描只处理新增与 Pending 存量）——
   这是设计使然（local 即最终态），但「NFO 改名后想重扫」需要先人工重置条目状态。
2. **播放列表 / 口味画像 / 人物页 / 字幕搜索** 端点全就绪，前端未接。
3. **媒体库管理台**（建库/删库/扫描日志）未做，e2e 里建库走 API。
4. 进度按条目不按文件；TMDB 在线刮削依赖外网，离线装置走 local NFO。

## 28. M4 第二片：媒体库管理台（2026-10-26）

第 27.5 节第 3 项。至此媒体库从「只能 API 建库」变成可在 UI 里完整走通建库 → 扫描 → 浏览的全流程。

### 28.1 交付清单

| 文件 | 内容 |
|---|---|
| `pages/manage/media/MediaLibraries.tsx` | 管理台页面：库表格（名称/路径/类型/扫描状态徽标/最近扫描）+ 建/编对话框 + 扫描启停 + 扫描历史对话框 + 删除确认 |
| `api/media.ts` | 补 `MediaScanProgress` / `MediaScanLog` / `MediaScanTask` 类型与 stop/progress/logs/tasks 四个封装 |
| `utils/mediaManage.ts` + spec | 纯函数层：状态归一化（未知值兜底 idle）、Tag 颜色映射、`+a ~u -r` 计数摘要、日志摘要（错误截 80 字符）、跨库任务按库 id 检索 |
| `router.ts` / `ManageLayout.tsx` | `/@manage/media` 路由 + 侧边菜单项（admin 守卫沿用） |
| `lang/en/media.json` | `manage.*` 键组；`manage.json` 侧边菜单加 `media` |
| `scripts/verify-media-manage.mjs` | 真机验收 20 项（详见 28.4） |

**无后端改动** —— 管理面端点在 M4 后端建设时已全部就位（libraries CRUD + scan/stop/logs + 跨库 tasks）。

### 28.2 两个设计决策

1. **行内扫描状态走跨库 `GET /media/tasks`，不逐行轮 `/libraries/{id}/scan`**：
   一次请求覆盖全部库的运行进度与最近结果；页面驻留期间 2.5s 轮询。
   轮询里做 **running→结束的边沿检测**——结束后行内 `last_scan_at` 已过期，补一次库列表刷新
   （首轮验收就栽在这：状态徽标变 Completed 了，最近扫描列还是 `-`）。
2. **编辑态锁定路径输入框**：后端 `UpdateMediaLibraryReq` 根本没有 path 字段，
   前端不做「假装能改」的输入框，置灰 + 提示「创建后不可修改」。

### 28.3 验收踩的三个坑（都修在脚本里）

| 坑 | 教训 |
|---|---|
| `POST /api/admin/storage/create` 传 `addition` 为嵌套对象 → **400 空响应体** | `CreateStorageReq.Addition` 是**字符串化的 JSON**，脚本必须 `JSON.stringify` 后再发；空响应体 `r.json()` 直接抛 SyntaxError，报错点离真因很远 |
| 1 个 6s 视频的库 100ms 内扫完，500ms UI 轮询抓不到 running 态 | 装置加 **2000 个空 `.mp4` 占位文件**（扫描只按扩展名发现），触发后 25ms 高频轮 API 抓 running；UI 侧 2.5s 轮询断言最终 Completed 即可 |
| `getByTitle("Scan")` 命中「Scan history」；隐藏 ElDialog 里的表格行被计入空表断言 | 标题选择器一律 `{ exact: true }`；断言范围限定 `.ol-manage-page .el-card`（EP 对话框关闭后 DOM 仍在） |

另一个确认了的**扫描语义**：扫描按**目录聚合条目**（Mixed 类型下根目录视频 1 条 + 子目录 2000 个视频聚 1 条 = 2 条目 / 2001 文件），不是一文件一条目。

### 28.4 验证

- `scripts/verify-media-manage.mjs` **20/20**：管理台入口与空库提示、建库（表格行 + 服务端落库）、
  扫描 running（API 高频轮询）→ completed（API + UI 徽标双确认）、按目录聚合 2 条目、
  最近扫描时间列经边沿刷新有值、状态列结果摘要 `+2 ~2001`、扫描历史对话框 1 行带摘要、
  编辑回填 + 路径锁定 + 改名落库、删除确认后表格与服务端均清空、零控制台错误。
- 装置：`G:\Share\ol-v2-media\E2E Movie Night\`（ffmpeg testsrc 6s 真视频 + `fillers/` 2000 个空 mp4）。
- `pnpm test` 122 用例（新增 8）· `typecheck` 0 错误 · `build` 通过；后端零改动，`dotnet test` 不回跑。
- 浏览页 `/#/media` 在空库下渲染正常零报错（spot-check）。

### 28.5 遗留

1. 播放列表 / 口味画像 / 人物页 / 字幕搜索仍未接（承 27.5）。
2. 管理台暂无「整库刮削」按钮（端点 `POST /libraries/{id}/scrape` 已有），等详情页刮削体验稳定后一起上。
3. 扫描中行内只有 found 计数，没有完成度百分比（后端进度无总量估计，做不了诚实进度条）。

## 29. 媒体域模块化收口（2026-10-26）

把 §27–§28 期间长在 `Core` / `Infrastructure` / `Server` 里的媒体代码整块拆成独立模块，
并把同期解锁的后端能力一次补齐。至此**媒体域不再需要碰宿主代码**。

### 29.1 项目边界

| 项目 | 内容 | 依赖 |
|---|---|---|
| `src/OpenList.Media.Domain` | 实体、服务接口（`IMedia*.cs`）、DTO | 只引用 Core（`Resp`/`User`），**不依赖 EF** |
| `src/OpenList.Media` | 服务、provider、端点、扫描宿主服务 | Media.Domain + Infrastructure + Core |
| `Infrastructure` | 仅 `MediaModelConfiguration.cs` 做 EF 映射 | **只引用 Media.Domain**，不反向引用 Media |

宿主 `OpenList.Server/Program.cs` 只剩两行：`AddMediaModule()` / `MapMediaModule()`。
`MediaEndpoints.cs` 与 `MediaAutoScanHostedService.cs` 随之从 Server 移入 Media（原件删除）。

一个连带改动：`ClaimsPrincipal → User` 的映射原先散在各端点里，模块化后 Media 若要用就得
反向引用宿主程序集，因此上提到 `Core.Security.AuthClaimsExtensions`，`AuthEndpoints` 顺带瘦身 52 行。

### 29.2 新增端点（后端就绪，前端未接）

| 端点 | 语义 |
|---|---|
| `GET /media/facets` | 题材 / 年份 / 评分分段分布（供筛选侧栏） |
| `GET /media/stats` | 口味画像：类型占比、年代分布、评分均值、总时长 |
| `GET /media/home` | 首页聚合：继续观看 + 最近入库 + 高分推荐 |
| `GET /media/persons`、`/persons/{id}`、`/persons/{id}/items` | 人物列表 / 详情 / 作品 |
| `GET /media/studios`、`/studios/{id}/items` | 片商维度（番号域主要聚合轴） |
| `GET /media/series`、`/series/{id}/items` | 系列维度（多部曲 / 番号系列） |
| `/media/playlists` 全家桶 | 建 / 改 / 删 / 列表 / 详情 / 加条目 / 移除 / 重排 / `playlists/add` 一键加入 |
| `GET /media/subtitles/search`、`POST /media/items/{id}/subtitles` | OpenSubtitles 搜索与挂载 |

**统计口径**（`MediaCatalogService`）：固定三步 —— 先按库可见性筛、再按条目路径筛、
最后才分组统计。反过来做的话，普通用户能在题材计数里看到自己无权访问的条目。
片商与系列列表复用同一口径，不各写一套。

### 29.3 番号域（第二个元数据 provider）

`CodeMetadataProvider`（Name=`code`）与 TMDB 并列注册。番号没有公益统一数据源，
因此**不内置任何站点**，把「去哪儿查」交给设置项 `media_code_api_url`：
填一个含 `{code}` 占位符的 JSON API 模板（自建聚合服务或任意兼容端点）。
未配置时该 provider 不可用，条目照常走 TMDB。

响应体按宽松映射解析，字段缺失即留空：
`title / original_title / year(数字或日期串) / overview / rating / studio / series / genres[] / poster / backdrop / actors[]`。

### 29.4 扫描规则抽成纯函数

| 文件 | 职责 |
|---|---|
| `MediaNaming` | 哪些扩展名算视频、视频归属哪个条目目录、类型判定、标题与年份解析 |
| `MediaNfoReader` | Kodi NFO 解析（字段名按 Kodi 约定，如 `<originaltitle>`） |
| `MediaPersonSync` / `MediaTaxonomySync` | 人物、片商、系列聚合落库 |

抽出来的理由不是洁癖：这部分是全域最容易被追问、也最需要回归保护的地方
（`Blade Runner 2049 (2017)` 这类片名带数字就踩过坑），无 IO 无 DB 才能被单测直接覆盖。
**后续新规则也加在这里，不要散回扫描流程。**

### 29.5 迁移

每库新增三支：`AddMediaCollections` / `AddMediaMetadataSource` / `AddMediaCodeDomain`
（Sqlite 与 Postgres 各一套）。同时修掉一处会咬人的旧库问题：
`MigrateSchemaAsync` 原先只在 `EnsureCreated` 路径执行，而迁移文件是历史模型快照、
模型后加的列未必有对应迁移 —— 缺列时 EF 的 SELECT/INSERT 直接报
`no such column` / `NOT NULL constraint failed`。现改为两条路径都跑（函数内部逐列判存在，幂等）。

### 29.6 验证

- `dotnet build` 0 错误（仅 CLI 三处既有 null 警告）；`dotnet test` **464 通过 / 1 跳过**
  （WebDAV DELETE 挂死，既有问题）。
- 新增单测 `MediaNamingTests` / `MediaNfoTests` / `MediaCodeDomainTests` 共 737 行。
- 前端 `pnpm typecheck` 0 错误、`pnpm test` 122 用例全通过。
- 本轮无前端改动，`verify-media*.mjs` 不回跑（媒体浏览/管理台行为未变，端点只增不改）。

### 29.7 下一步

1. ~~**M4 第三片**：前端接 §29.2 的端点~~ ✅ 已交付（第 30 节）。
   播放列表（CRUD + 详情页「加入播放列表」+ 重排）、人物页、片商/系列页、口味画像（stats）、
   facets 筛选侧栏、字幕搜索全部接上。
2. **M3 收尾**：video360 / flash 两个预览器（27 项目标里只剩这两个）。
3. 管理台「整库刮削」按钮（端点 `POST /libraries/{id}/scrape` 已有，见 28.5）。
4. **M5**：`frontend-vue` 切换发布 + A–H 全量回归 + 桌面打包验证。切换前置条件是 1–3 收口。

## 30. M4 第三片：人物 / 片商 / 系列 / 口味 / 播放列表 / 字幕（2026-10-26）

§29.7 第 1 项。至此 §29.2 列出的 8 组后端端点全部接上，M4 只差「整库刮削」按钮。

### 30.1 交付清单

| 文件 | 内容 |
|---|---|
| `api/media.ts` | facets / stats / taste / persons(+items) / studios / series / playlists 全家桶 / subtitles 的类型与封装（+230 行） |
| `components/MediaGrid.tsx` | 条目海报网格（复用）；与首页 `GridView` 的区别是前者渲染**条目**（海报/评分/收藏/状态），后者渲染**文件**（图标/大小/时间/右键） |
| `pages/media/PersonList.tsx` | 人物列表：关键词防抖 + 角色筛选 + 分页；后端固定「关联条目数降序」 |
| `pages/media/PersonDetail.tsx` | 人物详情：聚合接口取头部（角色标签/作品数），网格走分页接口 |
| `pages/media/TaxonomyList.tsx` | 片商 / 系列列表（**共用**，靠路由 `meta.kind` 区分） |
| `pages/media/TaxonomyDetail.tsx` | 片商 / 系列作品页（共用） |
| `pages/media/Playlists.tsx` | 播放列表：建 / 改名改描述 / 删除 / 四宫格封面 |
| `pages/media/PlaylistDetail.tsx` | 队列：上移 / 下移 / 移除 / 跳详情 |
| `pages/media/Taste.tsx` | 口味画像：库级统计（可切库）+ 个人口味（跨库） |
| `pages/media/ItemDetail.tsx` | ＋「加入播放列表」对话框、「字幕」搜索对话框 |
| `pages/media/index.tsx` | ＋ 5 个维度入口、facets 筛选条（题材 / 年份 / 评分≥） |
| `utils/mediaCatalog.ts` + spec | 纯函数：`moveInArray` / `moveIdBy` / `percentOf` / `hoursOf` / `minutesOf` / `nameInitial` / `topBuckets`（13 用例） |
| `styles/media-extra.css` | 本片全部新样式（独立文件，不往 2200 行的 layout.css 里继续堆） |
| `scripts/verify-media-dimensions.mjs` | 真机验收 **34 项**（装置自生成） |
| `IMediaPlaylistService.cs` + `MediaPlaylistService.cs` | 摘要新增 `preview_items`（见 30.2） |

### 30.2 唯一一处后端改动：播放列表摘要带封面信息

`MediaPlaylistSummary` 原来只给 `preview_item_ids`。前端拼四宫格只能盲发
`/items/{id}/image`，**没有海报的条目必然 404** —— 画面靠 onError 降级成空块，
但浏览器会为每次 404 记一条控制台错误，`verify` 的「零错误」断言过不了。

改法是让摘要多带一组 `preview_items: [{id, title, poster_path}]`（列表接口填充）。
前端据此**只给真有海报的条目发请求**，`preview_item_ids` 保留给只认 id 的客户端。
这比在验收脚本里过滤 404 更诚实：请求根本不该发。

### 30.3 五个设计决策

1. **片商与系列共用一个组件**：两者数据同构（`{id, name, item_count}` + 同名 items 端点），
   差别只有文案。复制两份只改 i18n 键的页面 = 两处要同步修的 bug。
   维度由路由 `meta.kind` 决定，切换维度时组件实例会被复用，所以 `watch(kind)` 必须重新取数。
2. **人物详情分两次请求**：聚合接口 `GET /persons/{id}` 不分页，作品上千的演员一次全返回不划算；
   只拿它算头部（角色标签、作品数），网格走 `GET /persons/{id}/items` 分页。两者口径一致。
3. **重排传完整顺序 + 乐观更新**：`reorder` 接口吃完整 id 数组（后端把未列出的成员按相对顺序追加到末尾），
   前端只需「挪一格」→ `moveIdBy` 算出新数组。首尾边界由纯函数收敛成「原样返回」，
   调用方不用先判断是否真的移动。连点用 `busy` 串行化，否则后到的请求会用旧顺序覆盖。
4. **换库清筛选**：facets 跟着 `library_id` 变，上一个库才有的题材留在 URL 里会得到空结果，
   所以在 `watch([libraryId, sort])` 里一并清 `genre/year`。
5. **口味页两套数据别混**：`GET /stats` 是**库的客观统计**（全体用户同一库看到相同数字），
   `GET /taste` 是**本人的主观偏好**（收藏/看过/评分加权）。因此只有 stats 支持切库，
   taste 不支持 —— 偏好是跨库累积的，按库切就变成另一个问题。

### 30.4 验收

`scripts/verify-media-dimensions.mjs` **34/34**，装置由脚本自生成到 `.e2e/dim-fixture`：
三个条目（Blade Runner 1982 / Blade Runner 2049 / Arrival），每目录一个占位 `.mp4` + Kodi NFO，
刻意构成 系列×2、片商×2（Warner）、导演×2（Denis Villeneuve，跨片商）、演员×2（Harrison Ford）。
**用 NFO 而不是 TMDB**：人物/片商/系列只有刮削后才有数据，离线装置走 local 才能稳定复现。

覆盖：建库→扫描→按目录聚合 3 条目、facets 聚合计数、persons/studios/series 计数、
stats 数字、首页 facets 条与 5 个维度入口、年份筛选后网格从 3 条变 1 条、
人物列表 5 卡 / 详情标题+2 作品+Director 标签、片商页与详情 2 部、系列页、
口味页 6 张统计卡 + 11 行条形图、播放列表建卡→详情页加入 2 条→上移重排→移除、
摘要 `preview_items` 契约、字幕对话框打开、**零控制台错误**。

用法：

```bash
# 隔离后端（临时库 + 独立端口 + 指向新构建的 dist）
cd .e2e && rm -rf data && AdminPassword=e2e12345 ASPNETCORE_URLS=http://127.0.0.1:5011 \
  DistDir=$PWD/../frontend/dist dotnet ../src/OpenList.Server/bin/Debug/net10.0/OpenList.Server.dll
# 另一个终端
cd frontend && BASE=http://127.0.0.1:5011 ADMIN_PASS=e2e12345 node scripts/verify-media-dimensions.mjs
```

`pnpm typecheck` 0 错误 · `pnpm test` 135 用例（新增 13）· `pnpm build` 通过 ·
`dotnet test` 464 通过 / 1 跳过（无回退）。

### 30.5 遗留

1. ~~**人物头像显示不了**：后端只给条目图做了「可见性校验 + 读取」的端点，人物没有。
   `profile_path` 是 VFS 路径，网格里逐张取签名不可接受，目前统一用姓名首字占位。
   要补需要 `GET /media/persons/{id}/image`（与条目图同款实现即可）。~~ → 已由 §36 完成
   （端点 + NFO `<thumb>` 解析 + 前端回退）。
2. ~~**片商/系列没有单条详情接口**：只有 `/studios/{id}/items`，拿不到名称。
   跳转时把列表里的 `name` 塞进 query 顶着，直接输 URL 进来会退化成 `Studio #id`。~~ → 已由 §36 完成
3. **字幕只能在详情页按标题搜**：provider 未配置时如实报错，但 UI 里没有配置入口
   （设置在管理台 settings 的 `media_opensubtitles_*` 项）。
4. **播放列表不能整单连播**：只能逐条进详情页播。要做需要播放器维护队列与「下一集」语义。
5. **口味页无时间维度**：stats 是全量聚合，没有「最近 30 天」这类滑动窗口。
6. **整库刮削按钮仍未做**（承 §28.5）：端点 `POST /libraries/{id}/scrape` 一直就绪。

### 30.6 下一步

1. ~~**M3 收尾**：video360 / flash 两个预览器~~ ✅ 已交付（第 31 节）。
2. 管理台「整库刮削」按钮。
3. **M5**：`frontend-vue` 切换发布 + A–H 全量回归 + 桌面打包验证。

## 31. M3 收尾：video360 / flash（2026-10-26）

§30.6 第 1 项。registry 里最后两个空缺补上，至此 **22 个注册预览器**全部可用。

### 31.1 交付清单

| 文件 | 内容 |
|---|---|
| `previews/Video360Preview.tsx` | 360°/VR 全景视频：three.js 球面 + `VideoTexture`，拖动转视角、滚轮缩放、重置视角；WebGL 不可用时退回普通播放器 |
| `previews/FlashPreview.tsx` | `.swf` 用 Ruffle（WASM）播放，按需加载 `ruffle.js`，失败给出可操作的排查提示 |
| `scripts/copy-ruffle.mjs` | 从 `node_modules/@ruffle-rs/ruffle` 复制 5 个运行时文件到 `public/ruffle/`（幂等） |
| `package.json` | `dev` / `build` 前置执行复制；另留 `prepare:ruffle` 手动入口 |
| `styles/preview-extra.css` | 两个预览器的样式（独立文件，不挤进 2700 行的 layout.css） |
| `registry.ts` + spec | 新增两条定义 + 4 个匹配用例（`video360` 只对带全景标记的视频出现；`swf` 压过 image） |
| `scripts/verify-preview-extra.mjs` | 真机验收 **15 项**（装置自生成） |

### 31.2 四个坑（都表现为「黑屏」，原因各不相同）

| 现象 | 真因 | 修法 |
|---|---|---|
| `.swf` 默认打开的是「图片查看器」 | 后端扩展名表沿袭 Go 的 `GetObjType`，**swf 归 IMAGE**，image 预览器同样命中且 order 相同 | flash 的 `order: 19` 压过 image(20) |
| Ruffle 挂上了但黑屏、停在「点击取消静音」遮罩后面 | `RufflePlayer.config` 的赋值**必须在脚本执行前**。脚本跑完再改属性，`createPlayer()` 早读过默认值 | 先把配置挂到 `window.RufflePlayer`，再插 script |
| 同上，且元素上没有 `src` 属性 | selfhosted 版的 `src` 是「反射属性」，元素尚未被 `customElements` 升级时赋值会变成普通 JS 属性，**既不加载也不报错** | 改用官方方法 `player.load(url)` |
| 360 canvas 有尺寸但全黑 | 相机在球心，能看到的只有球体**内表面**；`MeshBasicMaterial` 默认 `FrontSide` 把内表面全剔除了 | `side: THREE.BackSide`（`scale.x = -1` 只负责不镜像） |

后两条都值得写进验收断言：前者靠「元素存在」看不出来，后者靠「canvas 尺寸」也看不出来。
脚本因此额外断言 **视频源 `currentTime` 在前进**（纹理侧真拿到帧），并断言切走后 canvas 已销毁
（three 的渲染循环不外泄）。

### 31.3 Ruffle 资源为什么自托管、为什么不进 git

`@ruffle-rs/ruffle` 是 **selfhosted** 包：`ruffle.js` 是个 IIFE，运行时按
`document.currentScript` 的目录去加载 `core.ruffle.<hash>.js` 与 ~14 MB 的 wasm。
交给打包器 import 会被改写路径，所以只能当静态资源放在 web 根下。

- 5 个文件合计 **27.9 MB**，由 `scripts/copy-ruffle.mjs` 从 node_modules 复制，
  `dev`/`build` 前置执行（pnpm 默认不跑 `preDev`/`preBuild` 钩子，所以显式串在命令里）；
- `public/ruffle/` 已 gitignore —— 二进制不该进版本库，文件名带 hash，随依赖版本重建；
- **桌面打包注意**：`dotnet run build/build.cs -- --app=desktop` 会带 `frontend/dist`，
  `public/ruffle` 在构建时已进 dist，无需额外步骤；反过来只拷源码不给 dist 就会缺资源
  （此时页面会显示可操作的报错文案，而不是白屏）。

体积影响：`three` 是懒加载 chunk（720 KB / gzip ~185 KB），只在打开 360 视频时下载；
Ruffle 的 14 MB wasm 只在打开 `.swf` 时请求，首屏零负担。

### 31.4 验收

`scripts/verify-preview-extra.mjs` **15/15**。装置由脚本自生成到 `.e2e/preview-extra`：

- `pano_360.mp4`：ffmpeg 生成的 8 秒 **1280×640（2:1）** 视频 —— 等距柱状投影的典型宽高比。
  内容是不是真全景不重要，验证的是「纹理链路上有没有画面」；
- `legacy.swf`：脚本里**手工拼的最小合法 SWF**（FWS 头 + RECT + 12fps + 单帧 ShowFrame，30 字节）。
  为什么不用无效字节：Ruffle 会直接弹解析错误，那样只验证了「错误 UI 能显示」，验证不到播放链路。

覆盖：默认选中普通播放器 → 下拉出现 Video360 → canvas 尺寸 → 拖动后仍在 → 未走降级 →
`currentTime` 前进（4.7s/1280×640/未暂停）→ 切走后 canvas 销毁 → swf 默认选中 Flash →
`ruffle-player` 挂载 → 穿透两层 shadow root 找到 canvas(1136×630) → 无失败提示 → 零控制台错误。

无头浏览器必须显式开 SwiftShader（`--use-gl=angle --use-angle=swiftshader
--enable-unsafe-swiftshader`），否则拿不到 WebGL 上下文，测到的是降级分支而不是全景渲染。

```bash
cd .e2e/pv && AdminPassword=e2e12345 ASPNETCORE_URLS=http://127.0.0.1:5012 \
  DistDir=$PWD/../../frontend/dist dotnet ../../src/OpenList.Server/bin/Debug/net10.0/OpenList.Server.dll
# 另一个终端
cd frontend && BASE=http://127.0.0.1:5012 node scripts/verify-preview-extra.mjs
```

`pnpm typecheck` 0 错误 · `pnpm test` 139 用例（+4）· `pnpm build` 通过。

### 31.5 遗留

1. **360 只能靠文件名识别**：`360 / VR / pano / equirect` 这类标记命中才提供入口。
   视频容器里的投影信息（SV3D / equirect 元数据）目前没人解析，误判率取决于命名习惯。
2. **无热键与陀螺仪**：桌面端拖动 + 滚轮够用；移动端没有 deviceorientation 支持。
3. **Flash 的播放控制交给 Ruffle 自带右键菜单**：音量/画质/存档都在里面，我们没做外层控件。
4. `aliyun_video`（需阿里云盘播放器 SDK + provider 身份）与 `video_box`（artplayer + ASS 字幕）
   按 §397/§398 的结论仍未做 —— 它们需要专用运行时或线上环境才能验证，不是工作量问题。

### 31.6 下一步

1. 管理台「整库刮削」按钮（承 §28.5）。
2. **M5**：`frontend-vue` 切换发布 + A–H 全量回归 + 桌面打包验证。

## 32. M5：切换发布 + A–H 全量回归 + 桌面打包（2026-10-27）

第 31.6 节的第 2 项。`frontend-vue/` 转正为 `frontend/`，构建链路从「拉上游 dist」改为
「从本仓库源码构建」，A–H 逐类跑真机验收，两个发布形态都实际打出来跑过。

### 32.1 切换动作

```bash
git mv frontend frontend-legacy      # 旧 SolidJS 子模块（.gitmodules 同步改名）
git mv frontend-vue frontend         # 新前端转正
```

**输出路径仍是 `frontend/dist`**，所以 Dockerfile 的 `COPY --from=frontend-build /fe/dist`、
`build.cs` 的 `../frontend/dist`、桌面端注入的 `OpenList__DistDir` 三处**路径零改动** ——
这正是第 2 节当初把「输出路径必须保持 `frontend/dist`」写成硬约束的原因。

`frontend-legacy/` 保留为 submodule（`OpenListTeam/Openlist-Frontend`，含此前在 SolidJS 上
做媒体页留下的未提交改动）。**不参与任何构建**，CI 也不 checkout 它：它的唯一用途是
「某个功能要移植时，能直接 diff 上游怎么写的」（见 32.8 第 2 项）。

### 32.2 构建链路（切换真正的成本）

| 位置 | 之前 | 现在 |
|---|---|---|
| `Dockerfile` | stage1 从 GitHub Releases 下上游 dist tarball（`FRONTEND_VERSION` 参数） | stage1 用 `node:24-alpine` + `pnpm@12.15.0` 源码构建；`--frozen-lockfile`；`COPY --from=frontend-build /fe/dist` |
| `.dockerignore` | 只排除 `bin/obj/data/.git/tests/*.md` | 追加 `frontend/node_modules`、`frontend/dist`、`frontend-legacy`、`artifacts` |
| `build.cs` | 默认去上游 API 查最新 tag 并下载 tarball | 默认用仓库内 `frontend/dist`，缺失**直接报错**并打印 `pnpm -C frontend install && pnpm -C frontend build`；上游 tarball 降级为 `--frontend-version=<tag>` 显式开关 |
| `ci.yml` | `resolve-frontend` 查上游 tag；FDD bundle 下 tarball | 删 `resolve-frontend`；FDD bundle 自己 `pnpm install && build` |
| `build-bundles.yml` | 每个 bundle job 各自 `gh release download` | 新增 `frontend` job（install → typecheck → test → build → 上传 artifact），8+6 个 bundle `needs: frontend` 后下载 artifact |
| `release.yml` | notes 里写「Upstream non-lite OpenList-Frontend v4.2.x」 | 「Built from this repository」，与 API 同一 commit |

为什么在 `build-bundles.yml` 里单独起一个 `frontend` job、而不是每个矩阵腿各建一次：
14 个腿各跑一遍 `pnpm install` 既慢，更糟的是**任何一条腿都可能悄悄用上一份旧 dist**。
一个 job 出产物、其余腿只消费，版本就不可能分叉。

顺带补了 CI 的一个缺口：**此前没有任何 job 跑前端单测**。现在 `frontend` job 里
`pnpm typecheck && pnpm test` 会挡住「.NET 测试全绿但 SPA 已经编译不过」这种情况。

**同时修掉一个一直在的 bug**：`build/build.cs` 的 `Publish` 在 `--app=desktop` 下只发布
GUI（`publish-desktop`），而 `Bundle` 的桌面分支要拷 `publish/`（服务端核心）—— 于是
`--app=desktop --target=Bundle` 必然在 `DirectoryNotFoundException: .../publish` 上失败。
CI 不走 Cake 这条路（workflow 里分别 `dotnet publish` 两次），所以这个洞一直没暴露。
现在 `Publish` 在桌面形态下两个都发。

### 32.3 A–H 全量回归

每轮独立数据库 + 独立后端进程 + 独立装置，`frontend/scripts/run-regression.sh` 一把跑完：

| 类 | 脚本 | 项数 | 结果 |
|---|---|---|---|
| A 认证与初始化 | `verify-common.mjs`（**新增**）：游客守卫 / 登录 / 刷新 / 退出 / 零控制台错误 | 9 | ✅ |
| B 文件管理 | `verify-v2.mjs` 18 + `verify-ops.mjs` 24 + `verify-rename.mjs` 12 | 54 | ✅ |
| C 上传管线 | `e2e-upload.mjs`（改造为可打外部后端） | 22 | ✅ |
| D 预览器 | `verify-preview.mjs` 18 + `verify-preview-extra.mjs` 15 + `verify-editor.mjs` 11 + `verify-torrent.mjs` 15 | 59 | ✅ |
| E 管理台 | `verify-common.mjs` 8 页 + `verify-media-manage.mjs` 20 | 28 | ✅ |
| F 媒体域 | `verify-media.mjs` 20 + `verify-media-dimensions.mjs` 34 | 54 | ✅ |
| G 通用 | `verify-common.mjs`：暗色主题 3 + 语言控件 2 | 5 | ✅ |
| **脚本小计** | **11 轮** | **231** | **全绿** |
| H 桌面端 | bundle 布局 + 服务端核心托管 SPA（见 32.4，人工/curl 验证，不进脚本计数） | 4 | ✅ |

`verify-common.mjs` 是这轮补的：此前 B/C/D/F 都有专门脚本，而「游客能不能进管理台」
「管理台 8 个页签是否都能打开」「暗色主题刷新后还在不在」只能靠人手点，A–H 就只能算口头承诺。
它同时纠正了一个**一开始写错的假设**：首页没有 `requiresAuth`，游客浏览是既定语义
（与 Go 版一致），所以断言不该是「未登录被踢去登录页」，而是「游客拿不到管理台 + 退出后回游客态」。

### 32.4 打包验证

| 形态 | 产物 | 实测 |
|---|---|---|
| `--app=server --rid=win-x64` | `app/` + `cli/` + `frontend/dist/` + `run.bat`（288 MB） | 起 `app/OpenList.Server.exe` 并注入 `OpenList__DistDir=../frontend/dist`：`/ping` → pong、`/` → 200（1517 B）、主 JS 200（204 KB）、`/ruffle/core.ruffle.*.js` 200（108 KB） |
| `--app=desktop --rid=win-x64` | GUI 在根 + `server/` + `cli/` + `frontend/dist/` | `OpenList.Desktop.exe` / `server/OpenList.Server.exe` / `cli/openlist.exe` 三个入口齐全；起 `server/` 里的核心同样托管出 SPA |

两份 README 里的 `frontend/dist/   OpenList SPA (Vue 3, built from this repo)` 是构建时生成的，
不是手改的 —— 顺带把「upstream non-lite release」这类会误导人的描述清掉了。

`src/OpenList.Desktop` **零改动**（`git status` 干净）：M5 只换了它旁边那份 dist 的来源。

### 32.5 装置脚本入库，编排器成型

此前每个 `verify-*.mjs` 的装置都是「某台机器上的 `G:\Share\ol-v2-*`」，文档里只有一句
「装置由生成脚本创建」—— 脚本本身没入库（§26 遗留 4 记过这笔账）。这轮补上：

- `frontend/scripts/fixtures/gen_fixtures.py`：`preview`（zip/xlsx/docx/cast/.url）、
  `torrent`（bencode 编码器 + 独立算 info_hash + expected.json）、`media`（NFO/海报/30s mp4）、
  `media-manage`、`ops` 五套装置，一条命令重建。
- `frontend/scripts/fixtures/setup.mjs`：幂等建挂载 / 建媒体库并等扫描结束。
- `frontend/scripts/run-regression.sh`：11 轮编排，失败只影响对应轮次，末尾打汇总。

编排器踩到的五个环境坑（都表现为「莫名挂住」或「明明 200 却说失败」，记下来省下次的时间）：

1. **每轮不能 `rm -rf` 装置目录**：受保护环境对「一次删除 >50 项」会 fail-closed 拦下，
   于是「跑第二遍」会静悄悄死在建装置那一步。改成**每轮一个时间戳目录**，不删只建。
2. **`pwd` 给 `/f/...`，node/dotnet 要 `F:/...`**：混用会拼出 `F:\f\Code\...` 这种鬼路径。
   统一 `abspath()` = `cd && pwd -W`。
3. **`MSYS_NO_PATHCONV` 不能全局导出**：它会让 `tasklist //FI ...` 的 `//FI` 不再转成 `/FI`，
   端口占用检测直接失效，表现为「端口被非 dotnet 进程占用，拒绝处理」。只在跑 node 时用 `env` 前缀给。
4. **`/e2e` 这类值会被 MSYS 当路径改写**（变成 `E:/e2e`），服务端回
   `mount path must start with '/'`。同样只在 node 调用处关转换。
5. **`( cd dir && cmd & echo $! > pid )` 的 pid 文件写在父目录**：`&` 把 `cd && cmd` 整段
   放进后台，`echo` 仍在调用者的 cwd 里。结果 stop_server 拿不到 PID，服务一轮轮泄漏，
   下一轮只剩「端口被旧进程占着」这一种症状。改成 `pushd` + `$!` + `popd`。

另外 `setup.mjs` 里所有请求都带 `Connection: close`：Windows 上 undici 的 keep-alive 句柄
若在进程退出时还挂着，会撞 `Assertion failed: !(handle->flags & UV_HANDLE_CLOSING)`，
进程带非 0 退出码结束 —— 调用方会以为「建挂载失败」，而实际上一切正常。

### 32.6 回归暴露的问题：两个脚本 bug、一个产品 bug

1. `verify-torrent.mjs` 的体积断言写死 `total/1024 KB`，装置一小就永远对不上
   （80 B 会渲染成 `80 B`）。改成按 UI 的 `humanSize` 口径算 —— 这是脚本的 bug，不是产品的。
2. 同一脚本假设「折叠任一目录只少 1 行」，而原装置每个目录放 2 个文件。装置调整为
   `pics/`（1 个）+ `sub/`（2 个），断言不动 —— 断言本身是对的。
3. `build.cs` 桌面发布缺服务端（见 32.2 末）。

**产品代码（`src/` 下的 C#、`frontend/src/` 下的 TS/TSX）本轮零改动**：A–H 全绿是
「Vue 前端达到对齐」的证据，不是靠改产品凑出来的。

### 32.7 遗留

1. **桌面 GUI 没有自动化**：Avalonia 窗口喂不进 Playwright，H 只验证到「包布局 + 服务端核心
   托管 SPA + 桌面端零改动」。设置页/日志页需要人手点一次。
2. **语言只有 `en`**：G 的「语言切换」只验证到控件存在且显示当前语言，没有第二份字典可切。
3. **管理台 12 模块里只自动化了 8 个**：`plugins` / `messenger` / `backup-restore` /
   `dashboard` / `about` 后端没有接口，UI 是 `(soon)` 禁用态（承 §16–19、§28）。
4. `aliyun_video` / `video_box` 仍未做（承 §31.5，需要专用 SDK 或线上环境才能验）。
5. C 的 **direct 模式**未覆盖：需要配置 `direct_upload_tools` 才能走，脚本覆盖了
   stream / form / chunked（含跳片续传）三条主干。
6. 装置目录会累积（`G:/Share/ol-v2-*-<时间戳>`，含一份 2000 filler 的目录，约几 MB），
   需要定期手动清；没有自动清理是刻意为之 —— 排查失败时现场比干净更有价值。

### 32.8 下一步（M0–M5 之后）

1. ~~管理台「整库刮削」按钮（承 §28.5，端点一直就绪）~~ → 已由 §33 完成。
2. **上游功能移植清单**：`frontend-legacy/` 里的模块逐个补。**messenger 已完成**（§34，
   后端本来就齐，还顺带修了一个越权）；~~`dashboard`（可纯前端聚合同类端点，无需后端）~~
   → 已由 §35 完成；~~`backup-restore`（需要后端先做库导出/导入）~~ → 已由 §37 完成；
   ~~`about`（版本/许可/链接即可）~~ → 已由 §38 完成（另加了 `/admin/system/info`）。
   **移植清单到此清空**，管理台 12 个模块全部就绪。
   ~~`plugins` 无 .NET 对应物，建议从导航移除而非实现~~ → 已由 §35 从导航移除。
3. 媒体域剩余项按 §30.5 / §31.5 的遗留清单推进（人物头像端点、片商/系列详情接口、
   播放列表整单连播、口味页时间维度）。

## 33. M4 收尾：整库刮削按钮（2026-10-27）

承 §28.5 / §32.8 第 1 项。端点 `POST /media/libraries/{id}/scrape` 早在 §28 就有，
所以这轮一开始的判断是「前端接一下就行」。实际接的时候发现**光有端点不够用**，
它有三个会让按钮变成摆设的缺陷，必须连同后端一起补。

### 33.1 端点的三个缺陷

| 缺陷 | 后果 | 修法 |
|---|---|---|
| 没有幂等守卫 | 双击会起两个后台任务同时搜同一批条目；每条 sleep 250ms，2000 条跑 8 分钟，两个任务重叠就是双倍 provider 配额消耗 + 撞限流 | `MediaScrapeCoordinator` 单例 + `TryBegin` 原子登记，已在跑返回 **409**（与扫描的 `_running` 守卫口径一致） |
| 失败被逐条静默吞掉 | API key 失效时每条都抛异常，整批在 8 分钟里「正常跑完」却什么都没写，界面看不出任何异常 | 连续失败 5 条即熔断；`MediaScrapeRun` 记 `Failed` / `Error`，结束后仍保留供管理台读取 |
| 没有任何进度可查 | POST 立即返回，前端只能干等 ——「点完没反应」和「早就刮完了」在界面上长得一模一样 | 新增 `GET /media/scrape/tasks`：按库返回 `pending / unmatched / matched / running / batch_size / batch_done / failed / error` |

### 33.2 为什么状态必须放单例

`MediaMetadataService` 是 **Scoped**，批量刮削又是 fire-and-forget。守卫写进它的实例字段，
每个请求都会拿到一把新锁 —— 代码看着没错，双击照样并进，且没有任何测试会失败。

因此把运行态抽成 `MediaScrapeCoordinator`（`AddSingleton`），职责只有两个：

- **守卫**：`TryBegin` 用 CAS 循环（`TryGetValue` + `TryAdd`/`TryUpdate`）。不能用单纯
  `TryAdd` —— 结束的一轮会留在字典里（见下），`TryAdd` 会永远失败；也不能「先查后写」，
  两个并发请求会同时通过检查。
- **进度换算**：`DoneOf(pendingNow) = clamp(StartPending − pendingNow, 0, BatchSize)`。
  待刮削数随刮削单调减少，本身就是进度事实，不需要后台任务额外累加计数。

`End()` 只把 `Running` 置 false，**不删记录**：连记录一起扔掉的话，熔断中止后管理台
只看到「没在跑、pending 一条没少」，完全看不出发生过什么。

进度分母是 `BatchSize`（单轮上限 2000）而不是库里总数 —— 否则一个 5000 条的库永远显示 3%。

### 33.3 三个计数不能合成一个

`pending` / `unmatched` / `matched` 回答的是三个不同问题，合并成「总数」就失去了信息量：

- `pending` 会被批量任务消费（按钮是否可用的唯一判据）；
- `unmatched` 是 provider **正常响应但没匹配到**，批量任务**不会**再碰它们
  —— 重试只能逐条走详情页。这一条最容易误解成「刮削失败」；
- `matched` 才是真正完成的。

同理，批量循环里「搜索返回空」不算失败（否则冷门库连撞 5 条不匹配就会被误伤熔断），
只有 provider 调用抛异常 / 返回空详情才计入 `Failed`。

### 33.4 前端：新增列，并顺手修掉一个一直存在的布局问题

管理台表格加「元数据」列（运行中显示进度条 + 剩余条数；否则显示 pending / unmatched /
matched 标签），加刮削按钮（空批次与运行中置灰，`title` 写明原因）。

加完发现 **7 列在 1440 宽窗口里放不下**：内容区约 900px，列宽合计 1016px，
于是「操作」列被推到横向滚动区外——按钮看不见等于没有。实测四档视口：

| 视口 | 表格可用宽 | 列宽合计 | 操作列 |
|---|---|---|---|
| 1280 | 738 | 1016 | 溢出 |
| 1440 | 898 | 1016 | 溢出 |
| 1600 | 1058 | 1058 | 可见 |
| 1920 | 1238 | 1238 | 可见 |

补一句：**改之前就有这个问题**（918px 同样 > 898px，只是溢出 20px 不明显）。
抠像素让它「刚好塞进 1440」是脆的——1280 永远塞不下。所以改成
`fixed="right"` 把操作列钉在右侧：无论多窄，操作永远可达，中间列正常横向滚动。
同时把各列宽度收窄了一轮（路径 170 / 类型 80 / 状态 180 / 元数据 150 / 时间 120）。

计数标签也做了取舍：空间只够一到两个标签，所以**不同时**展示三个 ——
还有待刮削时只看 `pending` + `unmatched`，刮完了才用 `matched` 表达「这个库齐了」。

### 33.5 验证

单测 `MediaScrapeCoordinatorTests` **9 项**（并发抢锁、结束后保留失败原因、
新一轮从干净状态开始、批量上限截断、进度两端夹取、连续失败熔断与成功重置）。

真机 `scripts/verify-scrape.mjs` **39 项**，装置自建到 `G:/Share/ol-v2-scrape`
（3 个无 NFO 的视频目录 → 条目落 Pending；`Empty Lib/` → 空库用来验证置灰）。
覆盖状态契约（8 项字段逐个断言）、未配置 provider 时 400 并透出 `media_tmdb_api_key`、
启动失败不留运行态、列内容、按钮三态与 title、确认框（库名 + 条数）、
报错 toast 原文透传、失败后无幽灵进度条、零控制台错误。

**运行态分支用路由拦截构造**（`page.route` 回一份 running 载荷），因为真实刮削需要
TMDB key + 公网，离线装置造不出来。这不算「假装测过后端」——后端那半边由单测覆盖，
脚本只验证界面按契约渲染：进度 33%、`2 left`、按钮置灰 + loading、解开拦截后恢复。

一个测试细节：`el-button` 的 `title` 在外层 `span` 上（沿用本文件其它图标按钮的写法），
不是按钮原生属性；同理 `.el-tag` 要按单元格统计，整行统计会把「类型」列的 tag 算进来。

**全量连跑还揪出一个测试自身的脆弱点**：`verify-media.mjs` 的「内嵌播放器挂载」用的是
`waitForTimeout(2000)` + `count()`。12 轮连跑时机器有负载，路由切换 + 建 `video` 元素
偶尔超过 2 秒，`count()` 当场返回 0 → 假红；随后 `evaluate` 又干等 30 秒才抛。
改成 `waitFor({ state: "visible", timeout: 15000 })`：断言内容一个字没改（播放器仍必须真的挂上），
只是不再赌时序。单独跑该轮 20/20、全量跑也 20/20，确认与产品代码无关。

### 33.6 遗留

1. **真实刮削结果未做端到端验证**：TMDB 基址硬编码 `https://api.themoviedb.org/3`。
   要覆盖「匹配成功 → 写入 NFO/海报 → matched 增长」需要可配置基址（顺带支持镜像/代理）
   或一个 provider 替身。目前靠单测 + 契约测试兜底。
2. **批量任务只吃 Pending**：`Unmatched` 条目不会重试，只能逐条点。要不要提供
   「重试全部未匹配」是产品决策（会反复消耗配额去搜注定搜不到的内容），暂不做。
3. **单轮上限 2000 且不能在界面调**：超过的库需要再点一次。按钮会一直可用直到 pending 归零。
4. **没有取消**：扫描有 `/scan/stop`，刮削没有 —— 中途想停只能重启进程。
5. **失败计数不落库**：`Failed`/`Error` 只活在进程内存里，重启即失。

## 34. 上游移植第一项：站内信（2026-10-27）

承 §32.8 第 2 项（上游功能移植清单）。选 messenger 打头，理由很直接：
它是那份清单里**唯一后端已经完整、前端却一行都没有**的模块 —— 等于一段白写的服务端代码。

### 34.1 先盘点：后端其实有两套「消息」

| | 持久化消息 | 内存广播 |
|---|---|---|
| 接口 | `/admin/message/{list,create,delete,update}` + `/me/message/{list,unread,read}` | `/admin/message/send` + `/admin/message/get` |
| 服务 | `IMessageService`（落库 `x_messages`） | `IBroadcastService`（只在内存留最新一条） |
| 语义 | 管理员 → **指定用户**，有已读状态 | 管理员 → 所有客户端，无已读、无历史 |
| 前端 | **两代都没做** | 旧前端有个调试回显页 |

旧前端的 `pages/manage/messenger/Messenger.tsx` 只有 82 行：每秒轮询 `/admin/message/get`、
把拿到的追加进列表，再发一个字符串 —— 那是 Go 版的自测工具，不是功能（用户侧没有任何消费端）。
真正有产品价值的是持久化那一套，所以这轮做的是它，广播面板**刻意不做**（见 34.6 第 3 条）。

顺带一个发现：`POST /admin/message/update?id=` 的名字骗人 —— 实现是 `MarkReadAsync`。
它是 Go 的兼容别名，前端不要用它表达「更新」，本次也没用。

### 34.2 顺带修掉的越权（本轮最该记的一条）

做 UI 之前先用两个账号探了一遍接口，发现：

```
bob 尝试把 admin 的消息标为已读 → 200 OK
>>> admin 的消息 read 变成: True (应为 False)
```

`POST /me/message/read` 直接把请求体的 `id` 丢给 `MarkReadAsync(id)`，**没有任何归属校验**。
消息 id 是自增的，任何登录用户从 1 开始试就能把所有人的消息标成已读。

危害不是「泄露内容」（那个接口不返回正文），而是**「未读」这个信号彻底不可信**：
别人可以静默清掉你的未读徽标，你就再也不会注意到「有消息来了」。这类缺陷不会报错、
不会异响，只是让一个功能悄悄失效 —— 如果只按「把页面画出来」的标准验收，它会一直躺在那里。

修法是给服务层加一个可选归属参数，两个入口各取所需：

```csharp
Task<Resp> MarkReadAsync(long messageId, long? ownerUserId = null, CancellationToken ct = default);
// null  → 管理端路径（/admin/message/update），保持跨用户能力
// 非空  → 用户端路径（/me/message/read），只允许动自己的
```

越权时返回 **404 而不是 403**：403 等于确认「这个 id 确实存在」，等于把枚举结果告诉对方。

单测 2 项钉住（`MessageServiceTests`）：他人越权返回 404 且消息仍为未读、不传 owner 的管理端路径照常可用。

### 34.3 契约陷阱：这个域是 camelCase

媒体域的 DTO 逐个写了 `[JsonPropertyName("library_id")]`，而 `Message` 一个都没写，
走的是 `Program.cs` 里的全局 `PropertyNamingPolicy = JsonNamingPolicy.CamelCase`。
照抄媒体那边的 `user_id` / `created_at` 会拿到一堆 `undefined`，而且**不会报错**——
列表能渲染出来，只是每一格都是空。`api/messages.ts` 顶部为此专门留了提示。

顺带记一笔环境坑：用 Git Bash 的 `curl -d` 发含中文的 JSON 会得到**空响应体**，
一度以为后端 400 了；换 Python 的 `urllib` 同一个请求 200 且落库正常。
以后验中文入参别用 curl 直接拼。

### 34.4 三处 UI 取舍

1. **未读数单独轮询（60s），不拉全量列表**：`/me/message/unread` 只回一个整数，
   徽标挂在顶栏、每个页面都在，代价必须压到最低；完整列表只在打开收件箱时取。
   拉不到（401/断网）时**保持上一次的值**、不弹错 —— 顶栏挂个错误提示比没有徽标更烦人。
2. **标为已读不重拉列表**：就地改本地对象 + 徽标减一。列表按 id 倒序，已读状态变化
   不会改变顺序，重拉纯属浪费，还会让弹窗闪一下。
3. **发信后清正文、留标题**：管理员常见操作是同一主题连发几条，清空标题反而要重打。

管理台是「左选人 / 右写信 + 历史」两栏：`GET /admin/user/list` 不分页（一次返回全部用户），
直接当选择器用，配一个本地过滤框就够。

### 34.5 验证

真机 `scripts/verify-messenger.mjs` **33 项**（第 13 轮，已接入编排器）：

- 管理台：导航可达且高亮、用户选择器列出目标用户、内置账号带 built-in 标记、
  过滤生效、空标题被前端挡下且不落库、发送成功后列表出现未读条目、清空正文保留标题；
- 收件人侧（**独立账号 carol**，不能只用 admin 自测）：未读数 = 1、顶栏徽标显示 1、
  收件箱内容与发件一致、标记已读后服务端未读归零 + 徽标消失；
- 越权：carol 对 admin 的消息调 `/me/message/read` 得 404、admin 侧仍为未读、
  carol 的列表里看不到别人的消息；
- 删除：确认框带标题、删除后该用户列表为空、**admin 自己的那条不受影响**；
- 零控制台错误。

装置自建（幂等建 `carol`、清空双方历史消息），不需要挂载。越权那一步用
`page.request` 直接打接口而非点 UI —— 走页面会让浏览器为 404 记一条 console error，
把最后的「零错误」断言搅浑。

`dotnet test` **476 通过 / 1 跳过**（+2）· `pnpm typecheck` 0 错误 · `pnpm test` 143 ·
A–H 全量 **13 轮全绿**（`verify-common` 的管理台页签检查同步扩到 9 个模块）。

### 34.6 遗留

1. **没有推送**：未读数靠 60s 轮询，最坏情况延迟一分钟才看到新消息。要实时得加 SSE/WebSocket
   —— 后端目前没有任何推送通道。轮询也没做「标签页隐藏时暂停」（`visibilitychange`）。
2. **没有「全部标为已读」**：后端没有批量接口，N 条消息就是 N 个请求，暂不做。
3. **广播面板没做**：`/admin/message/{send,get}` 只在内存留最新一条、用户侧无消费端，
   是 Go 的调试工具。要做「全站公告」得先给它落库语义（谁发的、什么时候、能不能撤回）。
4. **消息不可编辑**：`POST /admin/message/update` 实际是「标为已读」，名字有误导性；
   真要做编辑得改后端接口语义（会影响 Go 兼容性），本次绕开。
5. **列表不分页**：`/admin/message/list` 与 `/me/message/list` 都返回该用户全部消息。
   单用户消息上千时会明显变慢，也没有按标题搜。
6. **`plugins` 建议直接从导航删掉**：Go 版靠插件加载 `.so`，.NET 端没有对应物。
   留着 `(soon)` 等于承诺一个永远不会来的功能。

## 35. 管理台仪表盘 + 移除 plugins 导航（2026-10-27）

承 §32.8 第 2 项的最后两块：`dashboard`（可纯前端聚合）与 `plugins`（无 .NET 对应物，直接删）。

### 35.1 为什么仪表盘不新增后端端点

上游和本仓库都没有统计端点。做法是**并发拉 7 个既有端点，在浏览器里聚合**：

| 端点 | 图表里的位置 | 用到的字段 |
|---|---|---|
| `GET /admin/user/list` | 用户卡 | 总数为长度；`role == ADMIN` 数、`disabled` 数 |
| `GET /admin/storage/list` | 挂载卡 + 驱动分布 | 总数、`disabled`、`driver_name` 分组 |
| `GET /share/list` | 分享卡 | 总数、`disabled`、`accessed` 求和 |
| `GET /media/stats` | 媒体库卡 + 容量卡 + 题材分布 | `library_count` / `item_count` / `file_count` / `total_bytes` / `total_duration_seconds` / `by_genre` |
| `GET /media/scrape/tasks` | 元数据健康表 + 系统「刮削」行 | 每库 `matched` / `pending` / `unmatched` / `running` / `batch_*` / `failed` / `error` |
| `GET /admin/index/progress` | 索引卡 + 系统「索引」行 | `obj_count` / `is_done` / `last_done_time` / `error` |
| `GET /admin/scan/progress` | 系统「扫描」行 | `obj_count` / `is_done` |

代价写在明处：**用户与分享是整表拉回本地数**。管理台是低频页面、这两个表在可预见的规模内
（几十~几百行），加一个聚合接口换不来什么；真到上千行再补（见遗留 1）。

不引图表库：全页只有计数和水平条，为一张分布图拉 echarts（300 KB+）还得单独适配暗色变量。
条形用 `div` + 百分比宽度即可，`aria`/暗色/主题变量全都自动继承。

### 35.2 口径写进纯函数，界面只负责取值

所有计数逻辑集中在 `frontend/src/utils/dashboard.ts`（14 项单测）。四个容易想当然的点：

1. **停用的管理员仍计入「管理员数」**：停用是「能不能登录」，不是「是不是管理员」，
   两个数字各说各事。
2. **驱动分布只算启用的挂载**：停用的挂载不产生流量，混进分布会让图与真实负载不符。
3. **匹配率的分母是「已匹配 + 待刮削 + 未匹配」**，空库**不进分母**，且空库返回 0 而不是 100
   —— 否则「没有任何条目」会被渲染成「全部完成」。
4. **状态优先级 `running > failed > pending > unmatched > clean > empty`**：
   正在跑时下面的计数每轮都在变，先说「在跑」；`failed` 记录会保留到下一轮开始
   （§33.1 刻意不清），所以它排在 `pending` 前面 —— 否则「上次 5 条全失败」会被
   一条无关的黄色「待刮削」掩盖掉。

### 35.3 数据源失败不整体爆掉

委派管理员可能没有 `manage_media`，那 `/media/scrape/tasks` 直接 403。这里的做法是
每个请求单独 `settle`（业务失败与网络异常都只记来源名），再 `Promise.all` 一起等：

- 顶部一条 warning 列出失败的来源（用户 / 挂载 / 媒体统计 / 刮削…），可诊断；
- 其余面板照常显示 —— 比 `Promise.all` 在第一个失败处短路、整页空白强得多；
- 这也顺便避开了「未捕获的 Promise 拒绝」，控制台仍然是零 error。

### 35.4 两个只有真机才暴露的问题

1. **「用户数 = 2」是错的**：初始化时后端会建内置 `guest` 账号（默认停用），
   加上 admin 和脚本新建的账号是 3。**修法不是把 2 改成 3**，而是让期望值从接口算出来
   （`users.length`、`role === 2` 数、`disabled` 数）再比对 UI —— 断言「界面 == 接口」，
   这样内置账号增减都不会再假失败。
2. **指标卡排成 5+1**：`minmax(170px, 1fr)` 在内容区约 940px 下只能放 5 列，第 6 张孤零零掉到
   第二行。改成 `minmax(140px, 1fr)` 后 6 张一行（6×140 + 5×12 = 900 ≤ 940）。
   1280 视口下退化成 5+1，可接受；`main` 的 `scrollWidth == clientWidth`，无横向溢出。

主题与宽度用截图核过：1440 六卡一行、暗色下卡片/表头/标签全部可读、1280 表格列不截断。

### 35.5 plugins：不是「没做完」，是「不该做」

Go 版插件是进程内加载的 `.so`，.NET 侧没有对应宿主，也没有移植计划。留一个永远 `(soon)`
的入口等于承诺一个不会来的功能，所以这一轮直接删干净：

- `ManageLayout` 的 `MODULES` 去掉 `plugins` 项；
- `lang/en/manage.json` 去掉 `sidemenu.plugins`；
- 删除 `lang/en/plugins.json`（页面没了，整个命名空间就是死数据）；
- `lang/en/entry.ts` 去掉对它的 import —— 这个文件是早期 SolidJS 移植的遗留、**已无人 import**
  （i18n 走 `import.meta.glob`），但 `tsc` 仍会检查它，留着会报「模块不存在」。

导航现在 12 项，`(soon)` 只剩 `backup-restore` / `about`。

### 35.6 验证

真机 `scripts/verify-dashboard.mjs` **40 项**（第 14 轮，已接入编排器）。装置复用
`gen_fixtures.py media`（`Budapest/` 带 Kodi NFO、`NoSidecar/` 只有 mp4），扫描后库状态是
**2 条目 / 1 已匹配 / 1 待刮削 / 匹配率 50% / 题材 Comedy+Drama**，于是每个数字都可区分：

- 六张卡：用户卡 == 接口算出的用户数、挂载 1、分享 1、媒体库 1、容量为真实体积、索引 0；
- 健康表：库名、匹配率 50%、已匹配 1、待刮削 1、未匹配 0、状态 `Pending` 逐个单元格比对；
- 驱动分布含 `Local`；题材分布含 `Comedy` + `Drama`；
- 系统面板三行（索引 / 扫描 / 刮削）齐全；
- 点刷新会重新打 `/api/media/stats`（不靠时间戳：格式化到分钟，同分钟内看不出变化）；
- 导航 12 项、**无 Plugins**、首项 Dashboard、未标 `(soon)`、仍禁用 2 项；
- 零控制台错误。

`pnpm typecheck` 0 错误 · `pnpm test` **157**（+14）· A–H 全量 **14 轮全绿**
（`verify-common` 的管理台页签检查同步扩到 10 个模块，仪表盘在空库下也必须渲染得出来）。

### 35.7 遗留

1. **没有后端聚合**：用户/分享整表拉回本地数，也没有分页/搜索。上千行时页面会明显变慢，
   那时应该加 `GET /admin/dashboard`（一次 SQL 出全部计数），而不是继续堆前端请求。
2. **不自动刷新**：只在进页面与点「刷新」时取数；刷新是 7 个端点全量重拉。
   仪表盘是「看一眼现状」的页面，自动轮询留给媒体库页与索引页（它们本来就有）。
3. **任务数没进仪表盘**：`/task/{type}/{done}` 是 15 个类型 × 2 种状态，
   要算「运行中任务数」得打 15 个请求。要么等后端聚合端点，要么算了。
4. **索引/扫描没有百分比**：后端只给 `obj_count` + `is_done`，没有 total，
   只能显示二态徽标（承 §14 的口径，别在前端编分母）。
5. **没有趋势**：历史数据没落库，画不出「最近 7 天新增条目」这类曲线。
6. **`backup-restore` / `about` 仍是 `(soon)`**：前者要后端先做库导出/导入（风险最高），
   后者便宜，可随时补。

## 36. 媒体域遗留清理：维度详情接口 + 人物头像（2026-10-27）

承 §30.5 遗留 1、2。两条都不是"没做功能"，而是**界面上在编数据**：片商页直接输地址会显示
「Studio #2」，人物卡片永远只有姓名首字（后端连头像读端点都没有）。

### 36.1 后端：三个端点 + 一个共用图片助手

| 端点 | 作用 | 关键取舍 |
|---|---|---|
| `GET /media/studios/{id}` | 单条片商 | 复用列表的 `MediaTaxonomySummary`，`item_count` 同口径（只数可见条目）；行不存在 404 |
| `GET /media/series/{id}` | 单条系列 | 同上 |
| `GET /media/persons/{id}/image` | 人物头像 | 空→404、外链→302、VFS→直读；越权同样 404 |

「行存在但名下没有可见条目」返回 **200 + count 0**，而「行不存在」返回 404。
这与列表接口「无关联行不返回」的取舍**刻意不同**：列表是推荐位（没内容就别占坑），
详情是直接访问（页面上得说清"这条存在但空了"还是"地址错了"）。

新增 `Endpoints/MediaImages.cs` 收口图片响应，条目海报/背景图也换成它。顺手修掉一个不一致：
条目图在「条目不存在 / 越权」时原先是 `200 + {code:404}` 信封，浏览器拿到 JSON 当图片解码，
表现为**破图而不是 404**；现在三种失败一律 404（`<img>` 的 error 分支才靠得住）。

### 36.2 NFO `<thumb>` 全链路

1. **读**：`MediaNfoReader` 解析 `<actor>` 里的 `<thumb>`（Kodi/Jellyfin/Emby 都这么挂头像），
   值可能是同目录文件名，也可能是 TMDB 绝对 URL。
2. **归一**：新增 `MediaPersonImage`（纯函数，8 项单测）。相对值按条目目录解析成 VFS 绝对路径；
   **`..` 一律拒绝** —— NFO 是用户可写内容，一条 `<thumb>../../../etc/passwd</thumb>`
   不能让图片端点把读取引到条目目录之外。
3. **合并**：`MediaPersonSync` 对已存在的人物**只补空、不覆盖**。同一个人会出现在多部片子里，
   先入库那条未必带 `<thumb>`；本轮的 e2e 特意把头像写在**第二部**片子的 NFO 里来钉住这条，
   否则"第一次没头像就永远没头像"这类问题在单部片子的装置上根本暴露不出来。
4. **写**：NFO 写回带 `<thumb>`，本地图**只写文件名**。写绝对 VFS 路径会把 NFO 绑死在这台
   服务器的挂载布局上（换台机器、换挂载点就失效），所以读和写是两个方向、两套口径。

### 36.3 前端

- **详情页标题两级来源**：接口是真源，URL 上的 `?name=` 只为首屏立刻有字，
  接口一回来就覆盖；404 时显示 `Not found`，不再编一个「Studio #999」出来。
- **`PersonAvatar` 组件**两条硬约束：
  1. **有 `profile_path` 才发请求**。没有头像的人物非常多，盲请求 = 一页 30 条 404，
     而 Chromium 会把资源 404 记进控制台，等于自己制造"有报错"的假象（零错误断言当场变红）。
  2. **失败只降级、不重试**：图被删/路径失效立刻退回姓名首字，翻页复用组件实例时按
     `personId` 重置。

### 36.4 验证

`verify-media-dimensions.mjs` **48 项**（原 34 + 14，仍是一轮）：

- 契约：`studios/{id}`、`series/{id}` 的 名称 + 条目数；`studios/999 → 404`；
  头像 `200 + image/png`；无头像人物 `404`；匿名 `401`；
  **头像路径以 `/e2e/` 开头**（证明相对 `<thumb>` 被按条目目录解析，而不是原样落库）。
- 合并：Harrison Ford 的头像只写在第二部片子的 NFO 里，断言它最终落库 ——
  直接钉住「已存在人物补写空头像」。
- 表现：人物列表里他的头像是真图（`naturalWidth > 0`，装置写的是 1×1 合法 PNG，
  用空文件的话浏览器给 0，就验不出"渲染成功"）；Ryan Gosling 卡片**没有 img** 且显示 `R`；
  人物详情大号头像；片商/系列深链（**不带** `?name=`）标题等于真实名称；
  `#/media/studios/999` 显示 `Not found`。
- 零控制台错误：脚本故意打了两次 404，过滤器**只放行那两条 URL**，其余照旧算缺陷 ——
  零错误断言不能退化成"什么都放行"。

`dotnet test` **486 通过 / 1 跳过**（+10：8 项头像路径换算 + 1 项 thumb 解析 +
1 项详情接口）· `pnpm typecheck` 0 错误 · `pnpm test` 157 · `pnpm build` ok ·
A–H 全量 **14 轮全绿**。

### 36.5 遗留

1. **外链头像的 302 分支没有真机覆盖**：离线装置里没有外网图。它和条目海报的外链分支
   是同一段代码（`MediaImages`），目前只靠代码复用兜底；要验得给测试后端加一个假 provider。
2. **条目详情的演职员 chips 仍无头像**：一页 N 个 chip 就是 N 个请求，等做「人物浮层」
   （点 chip 出简介 + 作品）时一次性解决更划算。
3. **片商/系列没有封面与简介**：DTO 只有 `id/name/item_count`。要封面得先定义
   「用名下哪部片的海报」（最新入库？评分最高？），这是产品决策不是接口缺口。
4. **人物排序仍固定为条目数降序**：姓名字典序、最近合作等维度没做（承 §30.5）。
5. **`/media/persons` 无「有头像」筛选**：想看"哪些人还缺头像"得自己翻页。

## 37. 上游移植收尾：管理台备份 / 恢复（2026-10-27）

承 §32.8：`messenger`（§34）、`dashboard` + 移除 `plugins`（§35）、本节 `backup-restore`。
管理台的 12 个模块到此只剩 `about` 一个 `(soon)`。

### 37.1 没有后端端点：备份 = 拉五个域，恢复 = 逐条 CRUD

| 域 | 端点 | 自然键（对账用） |
|---|---|---|
| 设置 | `GET/POST /admin/setting/{list,save}` | `key` |
| 用户 | `GET /admin/user/list` + `create/update` | `username` |
| 挂载 | `GET /admin/storage/list` + `create/update` | `mount_path` |
| 路径元数据 | `GET /admin/meta/list` + `save` | `path` |
| 分享 | `GET /share/list` + `create/update` | `id`（分享码） |

因此它有两条明确性质，**写进页面提示里**，不让人误解：

1. **恢复只做新增 + 更新，永不删除**。删除是另一个语义（还得单独确认），
   混进「恢复」里，用户一个手滑就会丢数据；
2. 所以它是「把误删的东西找回来」，**不是**「把实例还原成备份那一刻」——
   后者要停服换库（见遗留 2），那是运维动作，不该做成管理台按钮。

### 37.2 与上游文件格式逐字节互通

加密沿用上游（旧 SolidJS / Go 版）的 **CryptoJS OpenSSL 口令格式**：
`AES.encrypt(JSON.stringify(value), password)` 之后再把这个 base64 **字符串**做一次 base64。
标记字段 `encrypted` 解密后必须正好等于 `JSON.stringify("encrypted")`，用来在解密全量字段前
就判出口令对错。

保持一致的收益是迁移期真实可用的：旧前端导出的备份能在这边恢复，这边导出的也能拿回去。
代价是依赖一个已停止维护的库，所以 `crypto-js` **固定 4.1.1**（与旧前端 4.2.0 输出一致），
并且只用于读写这个历史格式，不用于任何新协议。

### 37.3 三处刻意与上游不同（本轮的主要判断）

1. **用户凭据不随备份迁移**。本仓库 `/admin/user/list` 返回 `UserPublic`，**没有密码哈希**；
   旧文件里带着 Go 的哈希，拿它当明文再散列一次只会造出一批登不进去的账号。
   所以新建账号用随机口令 + **默认停用**，由管理员重置后再启用（日志里逐条写明）。
2. **设置项永远合并写入**，不受「覆盖已有项」开关影响。设置是 upsert，没有「跳过」语义；
   真按开关处理，在新实例上恢复会出现「几乎什么都没恢复」的假象。开关只作用于
   用户 / 挂载 / 元数据 / 分享这四类**对象**。
3. **自锁保护**：文件里「当前登录管理员」若被降权或停用，整份恢复直接拒绝，一个字节都不写。
   上游没这道检查，恢复完下一次请求就 401，只能走 CLI 救场。

三条都有真机断言钉住（§37.5）。

### 37.4 收口时修掉的三件事（本节真正的工作量）

这份页面代码是半成品：逻辑写完了，但**编译不过**，而且有一半根本不在 git 视野里。

1. **SFC 写法混进 TSX**：4 处 `<template #header>`。改成 `v-slots` 后又撞上
   `TS2710: 'v-slots' are specified twice` —— 因为 `ElCard` 自己有个 `header` prop，
   vue-jsx 判成重复属性（`ElDropdown` 上同样的写法没事）。最终改成**卡片内第一个 div + 下边框**，
   和 `indexes` 页的写法一致，也不再依赖插槽的类型体操。
2. **`sso_id` 不在 `UpdateUserReq` 里**：顺手确认了它本来也不该恢复 ——
   SSO 绑定是对端身份系统给的实例级凭据，跨实例抄过来只会把账号绑到一个不存在的身份上。
3. **`.gitignore` 的 `Backup*/` 把整页吃掉了**。这条来自 GitHub 的 VisualStudio 模板
   （本意忽略 VS 生成的 `Backup1/`、`Backup2/`），但 Windows 上 `core.ignorecase=true`，
   `backup/` 也被匹配 —— 于是 `frontend/src/pages/manage/backup/` **一直没出现在 `git status` 里**，
   将来提交会静默漏掉整页。收窄为 `Backup[0-9]*/`。

第 3 条不是「顺手清理」：它意味着任何 `backup*` 目录（不止这一页）都在悄悄被忽略，
对一个以 `backup` 命名的功能更是必踩。

### 37.5 验证

真机 `scripts/verify-backup.mjs` **39 项**（第 15 轮，已接入编排器）：

- **明文导出**：文件名 `openlist_backup_<站点>_<YYYYMMDD-HHmmss>.json`、`format/version` 标记、
  五个数组齐全、121 条设置、用户行**不含 password 字段**、日志给出明文提示；
- **加密导出**：逐字段变成字符串；脚本用 `crypto-js` **真的解一次**（先 base64、再 AES、
  再 JSON.parse），能还原出探针用户名 —— 只断言 `encrypted` 非空等于没验加密；
- **错口令**：先删掉探针用户再拿错口令恢复，用户数不变、不弹确认框、日志给出明确原因；
- **往返**：删除探针用户与挂载 → 用明文备份恢复到同一个实例 → 用户与挂载都回来，
  且用户是 `disabled=true`（证明「口令不迁移 + 默认停用」这条设计真的生效）；
- **幂等**：同一份文件再导入一次，Users 新增 0 / 跳过 2，不产生重复实体；
- **自锁保护**：把文件里 `admin` 的 `disabled` 改成 `true`、开覆盖模式导入 →
  整份被拒（"would demote or disable the account you are logged in as"），
  管理员仍是启用状态、当前会话仍有效；
- 零控制台错误。

`pnpm test` **188**（+31，`utils/backup.spec.ts` 覆盖加密往返/对账/自锁判定/文件名）
· `pnpm typecheck` 0 错误（收口前是 8 个）· `dotnet test` 486 通过 / 1 跳过（本轮无后端改动）
· A–H 全量 **15 轮全绿**（`verify-common` 的管理台页签扩到 11 个，
`verify-dashboard` 的「未就绪项」断言从 2 收到 1）。

### 37.6 遗留

1. **逻辑备份不是物理备份**：121 条设置 + N 个用户逐个 POST，数据量大了会慢；
   没有进度条，只有逐行日志。
2. **不能「还原到那一刻」**：不删任何东西。真要回滚得停服替换 `data.db`（SQLite）
   或 `pg_restore`（Postgres），属运维动作。
3. **不含媒体域**：媒体库、条目、观看/收藏状态都不在备份里。库与条目能从文件系统重扫回来，
   但**观看进度与收藏不会**——要保就得把 `UserMediaStates` 也纳入，尚未做。
4. **口令强度不校验**：AES 口令加密用的是 OpenSSL EVP_BytesToKey，弱口令风险自负；
   页面上给了「未加密备份含口令与分享密码，请妥善保存」的告警。
5. **大文件一次性读入内存**（`file.text()`）：几十 MB 以上会卡主线程，目前没有流式处理。
6. **`about` 仍是 `(soon)`**：管理台最后一个，版本/许可/运行环境，成本最低。

## 38. 管理台收尾：关于页（2026-10-28）

承 §32.8：`about` 是管理台最后一个 `(soon)`。做完这一节，12 个模块全部就绪，
导航里不再有「等你做完」的占位。

### 38.1 上游那一页不能照搬

`frontend-legacy/src/pages/manage/about.tsx` 干的事是：

```ts
const fetchReadme = async () => await (await fetch(
  "https://raw.githubusercontent.com/OpenListTeam/OpenList/main/README.md")).text()
```

也就是说，它把「关于」实现成**服务器进程去 GitHub 拉 README 再渲染 markdown**。三个问题：

1. 离线 / 内网部署直接是一片空白（而且是在管理台里空白，最容易被当成前端坏了）；
2. 服务器主动出网，在很多部署里是被策略禁掉的；
3. 它回答不了运维真正会问的两个问题 ——「这台机器跑的是哪个版本」和
   「这版前端配的是哪个后端」。

所以这一页重写成「读本机状态」，README 换成 4 条外链（点开才出网，且是浏览器出网）。

### 38.2 为什么这里必须新增后端端点

§35 的仪表盘刻意**没有**新增接口，因为它要的数字都在既有端点里。这一页不一样：
版本号、运行时、操作系统、进程启动时间、数据库文件位置、前端产物目录 —— 一个都没暴露过，
前端编不出来，也不该编。

`GET /api/admin/system/info` 放在 admin 组（`RequireAuthorization("admin")`）而不是公开端点：

- 里面有数据库文件、日志、临时目录的**绝对路径**，对排障有用，对匿名访客只是免费情报；
- 因此它**不返回** `cfg.Database.Password`（占位符都没有，字段压根不存在）。

| 分组 | 字段 | 说明 |
|---|---|---|
| 版本 | `version` / `tag` / `commit` / `configuration` / `built_at` | 版本与提交取自 `AssemblyInformationalVersion`；`built_at` 是程序集文件写入时间 |
| 运行时 | `runtime` / `os` / `architecture` / `processor_count` | `RuntimeInformation` + `Environment` |
| 进程 | `started_at` / `uptime_seconds` / `server_time` | 取不到启动时间时 `started_at=""`、`uptime_seconds=-1`（前端显示 `-`） |
| 数据库 | `database.{type,file,host,port,name}` | SQLite 给文件绝对路径，Postgres 给 `host:port/name`；**没有 password** |
| 路径 | `paths.{data_dir,dist_dir,dist_built_at,temp_dir,log_file,log_enabled}` | `dist_built_at` 是 `dist/index.html` 的 mtime |
| 能力 | `features.{s3,ftp,sftp,ldap,mcp,search_index,max_connections}` | 只报「开没开」，不回配置细节 |

字段名用 snake_case：与紧邻的 `/admin/index/progress`、`/admin/scan/progress` 同一族。
（全局 `JsonNamingPolicy.CamelCase` 只处理首字母大写的情形，对已是小写开头的键是空操作，
所以 `obj_count` 这类键能原样出去 —— 这条不是巧合，是有意在同一个文件里复用的约定。）

路径一律过一层 `TryFullPath`：`Path.GetFullPath` 会对非法字符/超长路径抛异常，
「关于」页只是展示信息，不该因为一个配错的 `TempDir` 整页 500。

### 38.3 三条展示口径

1. **取不到就显示 `-`，绝不编**：单文件发布时程序集在包内、`Assembly.Location` 为空；
   Postgres 没有「数据目录」；未启用文件日志时没有日志路径。这些在页面上都是 `-`，
   而不是 `1970-01-01` 或 `NaN`。
2. **`max_connections = 0` 显示 `∞`**：0 的语义是「不限制」，显示成 `0` 会被读成
   「一个连接都不许」，正好反了。
3. **时钟偏差是页面现算的**：`server_time` 减本机 `Date.now()`，只做一次性提示，
   不轮询 —— 它回答的是「两台机器的钟差多少」，不是实时监控。

### 38.4 只靠真机才看得出来的两处

1. **顶部横幅已经给了版本与提交，Runtime 块里再列一遍是错的**。
   第一版两处都列，页面看起来「版本号出现两次」，人会以为有两套版本（比如前端版本与后端版本）。
   现在 Runtime 块从 Framework 开始，提交号只在横幅出现一次（`title` 挂完整 40 位）。
2. **路径块必须整行**。第一版和 Runtime 并排放在两列（各 340px）里，Windows 绝对路径
   只露出 `F:\Code\Dotnet\OpenList.Net...` —— 等于没给信息。完整的值虽然在 `title` 里，
   但排障时不该要求用户先学会悬停。改成整行后 1440 下能看到完整路径，1280 下也能看到
   `...\.e2e\runs\...` 这种足以定位的片段。

`main` 的 `scrollWidth == clientWidth`（1440 与 1280 都验过），暗色下标签/表头/等宽路径都可读。

### 38.5 关于页不单独开一轮 e2e

它只有 16 行字段、零写操作，为一个纯读页面单独起一遍后端 + 数据库不划算。
断言挂在 `verify-common.mjs`（本来就是「管理台每个页签都打开」的那一轮）里，新增 F 段：

- 横幅的 tag / 短 commit 与 `GET /api/admin/system/info` 逐个比对（**不写死版本号** ——
  版本随构建变、路径随部署变，写死必然假失败，这条和 §35.4 是同一个教训）；
- 键值行摊平成字典后比对 Framework / OS / Architecture / Database / Data directory /
  Frontend assets / Log file；
- Uptime 与 Clock skew 只验**格式**（`22s`、`0s`），数值每次刷新都在变；
- 内置服务 5 个标签（**关闭的也要列出** —— 「什么没开」同样是信息）、`max_connections=0 → ∞`；
- 4 条外链都指向 GitHub 且 `target="_blank"` + `rel` 含 `noopener`（管理台页面别把站内导航带走）；
- 导航 12 项、**没有 `(soon)`、没有禁用项**；
- 点刷新会重新打一次 `system/info`（不是只改本地状态）。

顺带把 `verify-dashboard` / `verify-backup` 里「还剩 N 个未就绪」的旧断言改成「0 个」——
这类断言每到收尾阶段就会假失败一次，是设计如此（它想说「现在还有哪些没做完」）。

### 38.6 验证

`verify-common.mjs` **48 项**（+25，仍是 15 轮里的同一轮）
· `dotnet test` **493 通过 / 1 跳过**（+6：版本解析的三种形态、`+` 后缀为空、tag 与版本同源）
· `pnpm test` **204**（+16：`utils/about.spec.ts`）
· `pnpm typecheck` 0 错误 · `pnpm build` ok · A–H 全量 **15 轮全绿**。

### 38.7 遗留

1. **开发态版本号是 `1.0.0`**：只有走 cake / CI 注入 `--semver` 才会是真实版本；
   同时 `commit` 反映的是**上次构建时的源码**，不是当前工作区（改完代码没重编译时会对不上）。
2. **没有「检查更新」**：桌面端有（`AppInfo.ReleasesApi`），服务端故意不做 ——
   服务器进程主动出网在不少部署里被禁，而且集群里每台都去查一次是浪费。
3. **`dist_built_at` 只能当参考**：CI 打包/解压、`docker cp` 都可能改 mtime。
   它足够回答「大概是哪次构建的前端」，不足以当证据。
4. **不显示数据库体积 / 磁盘占用**：要跨库做（SQLite 是文件、Postgres 要查 pg_database_size），
   收益不如直接 `du` 一下。
5. **本仓库根目录没有 LICENSE 文件**（上游 OpenList 是 AGPL-3.0）。页脚只陈述
   「上游以 AGPL-3.0 发布」，不代本仓库声明许可 —— 这是法务决定，不是前端能补的。
6. **语言只有 en**：`about.json` 只建了英文，补中文需要先有第二份字典（承 §35 的 i18n 现状）。
