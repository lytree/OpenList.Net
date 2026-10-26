# 参考仓库分析整理 + Vue 3 落地路线

> 分析对象：`OpenListTeam/OpenList-Frontend`（下称 **OLF**）与 `peach-mitao/peach` 的 `frontend/`（下称 **Peach**）。
> 目标：把两者对「当前项目 `frontend/`（Vue 3 + TSX + Element Plus）」真正有用的东西分离出来 ——
> 哪些能抄实现、哪些只能抄结构、哪些压根不该碰。
>
> 所有数字为 2026-10-26 实测（GitHub Trees API + 本地 `wc -l`）。

## 0. 一句话结论

**OLF 是功能真源，Peach 是架构真源。**

- OLF 与本项目后端**同源**（都对齐 Go OpenList 的 API 契约），它的每个页面都能一对一翻译成 Vue 组件，是目前唯一可用的「功能清单 + 交互细节」参照；
- Peach 的 API 层与我们的后端**完全不同**（它对接自己的 Python 后端），但它的**分层方式**（应用编排层与视图解耦、播放器宿主/注册表、卡面美术抽象、骨架与岛分离）比 OLF 成熟，值得当作我们 M4 媒体域的结构模板；
- Peach 是 **AGPL-3.0**，OLF 是 **MIT**。许可证把「抄代码」这条路边界的很清楚：OLF 可直接翻译，Peach 只可借鉴结构、不可复制代码（BoardUI 单独 MIT，见 §6）。

## 1. 两个仓库的定位差异

| 维度 | OLF（OpenList-Frontend） | Peach（frontend/） |
|---|---|---|
| 版本 / 分支 | v4.2.9，`main`，2026-10-25 更新 | 无版本号，`master`，2026-10-24 更新 |
| Star / 许可证 | 105 / **MIT** | 11,653 / **AGPL-3.0** |
| 产品形态 | 多用户网盘（文件管理 + 管理台 + 媒体库 UI） | 单用户、本地优先的个人媒体系统 |
| 文件规模 | 374 blob（`src` 约 200 个 ts/tsx） | 2,317 blob，其中 `frontend/` **815**（`src` 536 + `e2e` 90） |
| 框架 | SolidJS + hope-ui（自家 fork）+ stitches | React 19 + react-aria + Tailwind 4 |
| 状态/数据 | solid store（手写）+ axios | TanStack Query/Table + 手写 `application/*.js` 编排层 |
| 与我们后端的关系 | **同源**：`/api/fs`、`/api/admin`、`/api/share` 字段级一致 | 无关系：`src/api.ts` 走自己的 Python 后端 |
| 对我们价值的定性 | 可直接翻译（工作量问题） | 只能借结构（设计问题） |

## 2. 技术栈对照（依赖清单实证）

**OLF 的 44 个运行时依赖几乎全是「预览器 + 播放器」的集成层**，这是全前端最难啃的部分：

- 播放/媒体：`artplayer`、`artplayer-plugin-danmuku`（弹幕）、`aplayer`、`hls.js`、`mpegts.js`、`mediabunny`、`@egjs/view360`（全景）、`@jellyfin/libass-wasm`（ASS 字幕）、`@mediabunny/ac3`
- 文档预览：`docx-preview`、`@embedpdf/snippet`、`monaco-editor`、`asciinema-player`、`highlight.js`、`katex`/`remark-math`/`rehype-*`、`mermaid`（devDep）
- 其它：`bencode`（torrent 解析）、`chardet`（编码嗅探）、`crypto-js`/`hash-wasm`/`sha256`（秒传哈希）、`libheif-js`（HEIC）、`lightgallery`、`streamsaver`（大文件下载）、`@viselect/vanilla`（框选）、`qrcode`、`seemly`
- 工程：Crowdin 多语言、`vite-plugin-dynamic-base`（子路径部署）、`vite-plugin-legacy`

**Peach 的 14 个运行时依赖走的是完全相反的路子**：`react-aria-components` + `tailwind-merge`/`clsx` + `motion` + `@tanstack/*` + `recharts` + `d3-sankey` + `sonner`。
它**没有**任何播放器/文档预览库 —— 播放器是自研的（`src/player/*.ts` 直接包 `video.js` 与自研 v10 player）。
这说明 Peach 的资产是「UI 体系 + 数据流」，OLF 的资产是「格式覆盖度」。

对我们意味着：

- **格式覆盖度只能从 OLF 的依赖清单抄**（并且要按需装，别一次装 44 个）；
- **UI 体系我们已经有 Element Plus**，不需要 Peach 的 react-aria；
- 唯一值得从 Peach 拿的是**组织方式**，见 §5。

## 3. 目录结构对照

### 3.1 OLF（页面即组件树，扁平）

```
src/
  app/            App / MediaShell / MustUser / i18n / theme        外壳与守卫
  components/     34 个通用件 + icons + markdown.css
  hooks/          useCDN / useDownload / useFetch / useLink / usePath / useRouter / useT / useTitle / useUtil
  pages/home/     Layout→Container→Body→{Nav, Obj, Readme, Password, Footer} + Sidebar
    folder/       List / Grid / GridItem / Images / Pager / Search / context-menu / helper + 骨架屏×2
    previews/     27 个预览器 + index 注册表
    toolbar/      20 个操作组件（Mkdir/NewFile/Rename/CopyMove/Decompress/Download/
                  PackageDownload/OfflineDownload(+Enhanced)/RecursiveMove/
                  RemoveEmptyDirectory/Share/TorrentFileList/LocalSettings…）
    uploads/      10 个文件（form/stream/direct/chunked/multipart/hash-worker/uploads）
  pages/manage/   12 模块 + routes/sidemenu_items/Header/SideMenu/common
  pages/media/    MediaBrowser / MediaItemDetail / MediaLayout + book|image|music|video
  store/          9 个 store（obj/user/settings/local_settings/archive/history/media/key-event/accent）
  types/          16 个 dto
  utils/          40 个（zip_read / zip-stream / zip_plugin / epub / epub_annotation /
                  players / plugin_engine / media_api / media_cover …）
```

### 3.2 Peach（分层与视图解耦，React 只是"渲染的一个消费者"）

```
frontend/src/
  application/   27 个纯 JS：catalog / entity / feed / follow / media / navigation /
                 search / selection / preferences / lifecycle / loading / receipts …
                 —— 应用编排，**不含 JSX**
  player/        21 个 ts：host / registry / stream / subtitles / progress / telemetry /
                 v10-player / videojs / dispose / playback
  query/         3 个：client.ts（TanStack Query client）/ media-sources
  card-art/      10 个：markup / framing / image-fallback / native-image /
                 representatives / hover / face-frame / feed-identity
  appearance/    7 个：density / glow / home-glow / layout / settings / theme
  history/       6 个：canonical-path / managed / overlay / route-meta / settings-route
  core/          8 个：entity-links / icon-geometry / media-sources / request-error / tags …
  react/         **385 个**页面岛 + boardui（vendored MIT）+ 一堆 *-skeleton.ts
  e2e/           90 个 playwright 用例（node --test 驱动）
```

两个仓库的组织哲学差别很直观：

- OLF 是**「一个页面一棵组件树」**，`pages/home/folder/List.tsx` 里既管数据也管渲染，改一处要读三处；
- Peach 是**「一层编排 + 一层视图 + 一层美术」**，`application/catalog.js` 能在没有 React 的情况下被单测，`player/registry.ts` 决定用哪个播放器实现，`card-art/*` 决定封面怎么取景 —— React 只负责最后一跳。

## 4. OLF 功能矩阵 → 我们 Vue 版的覆盖状态

（「✅」= `frontend/` 已实现并真机验证；「—」= 未做；「⌛」= 后端无接口/依赖未接）

| 域 | 子项 | Vue 版 |
|---|---|---|
| **认证** | 明文/哈希登录、guest 入口、remember 仅记用户名、401→登录页、权限位守卫 | ✅ |
| | WebAuthn / LDAP / 2FA | — |
| **初始化** | 安装向导 + EnvCheck | — |
| **文件浏览** | 列表 / 面包屑 / 排序 / 目录进文件退 | ✅ |
| | **网格 / 图墙 / 目录树 / 过滤 / 分页 / README** | ✅ **本片新增** |
| | 多选与批量（move/copy/delete）、右键菜单、快捷键、粘贴板 | ✅ |
| | meta 密码闸门 | ✅ |
| | 全局递归搜索（`fs/search`）、浏览历史、header 区 readme | — |
| **操作集** | mkdir / rename / batch_rename / move / copy / remove / 删除空目录 | ✅（前 6 项）|
| | 打包下载 / 解压 / 离线下载 / 递归移动 / 新建文件 / 分享对话框 / 本地设置 | — |
| **上传** | form / stream / direct / chunked / multipart + hash 秒传 + 取消 | ✅ |
| **预览器** | markdown / html / image / video / audio / pdf / text / info / download | ✅ 9 项 |
| | archive / torrent / doc / xls / ppt / plist / ipa / asciinema / url / flash / aliyun_video / video360 / video_box / markdown+wrap / text-editor(Monaco) | — 18 项 |
| **管理台** | storages（动态表单）/ users / metas / indexes / tasks / settings / shares | ✅ 7 模块 |
| | plugins / messenger / backup-restore / dashboard / about | ⌛ 后端无接口 |
| **分享** | 公开分享页 `#/s/:code` + 分享码鉴权 | ✅ |
| **媒体域** | 库/编目/墙/详情/播放列表 | — （M4） |

Vue 版现状：**84 个 ts/tsx、11,591 行**，`dist` 分包 `home` 229 KB（gzip 79 KB）。

## 5. Peach 值得借鉴的五个结构性决策（不抄代码）

### 5.1 编排层与视图解耦：`application/*.js` → 我们的 `composables/`

Peach 把「现在该显示什么数据、该发哪个请求、选中态怎么变」全部塞进 27 个 **纯 JS** 模块，React 组件只读状态、发意图。
我们已经有这个雏形（`useFileList` / `useFileOps` / `useViewLayout`），但**边界还不在同一处**：
本轮 `useFileList` 已经把「排序 → 过滤 → 分页」做成一条纯计算流水线，并把这套规则抽到 `utils/list.ts`（可单测），
这就是朝 Peach 方向迈的第一步。**后续准则：凡是能写成纯函数的状态推导，一律进 `utils/`，不进组件。**

### 5.2 播放器宿主 + 注册表：`player/host.ts` + `player/registry.ts`

Peach 的播放器不是「一个 VideoPlayer 组件」，而是：`registry` 决定用哪个引擎（v10/videojs/原生），
`host` 负责挂载/销毁/接管全屏，`subtitles`/`progress`/`telemetry` 各自订阅事件。
我们在预览器上已经有同构的设计（`previews/registry.ts` + `PreviewHost`），**但播放器内部还是一个 `<video>` 标签**。
M4 接入 artplayer/hls/ASS 时，必须按 `registry + host + 子能力` 的方式拆，否则 `VideoPreview.tsx` 会变成第二个 400 行怪物。

### 5.3 `card-art/`：封面不是 `<img>`，是一套取景规则

Peach 用 10 个文件处理封面：取哪张图（representatives）、怎么裁（framing）、加载失败怎么办（image-fallback）、
hover 时换哪张（hover）、脸部取景（face-frame）。这正是**播放墙的观感差距**所在。
我们的网格视图现在只有「`/d` 直链 + object-fit: cover」，M4 的墙必须补上：多图代表、失败回退、宽高比策略。

### 5.4 骨架屏与岛：`*-skeleton.ts` 与 `react/*-island.tsx`

Peach 大量存在 `board-skeleton` / `entity-skeleton` / `index-skeleton` / `catalog-filter-skeleton`，
配合 `application/loading.js` 做「先出骨架、再注水」。它的目标是**首屏 LCP**。
我们的现状是「loading 时一个转圈」—— 对文件列表够用，对媒体墙不够（墙的骨架本身就是体验）。
M4 的墙按「骨架网格 → 真实卡面」两步走，Peach 就是现成范本。

### 5.5 e2e 是交付物的一部分：90 个用例

Peach 的 `e2e/` 有 90 个 `node --test` + `playwright-core` 用例，命名直接对应体验承诺（`design-fidelity`、`sidebar-motion`、`player-subtitles`、`loading-fidelity`）。
我们的验证一直靠一次性脚本。本轮把它沉淀成 `frontend/scripts/verify-v2.mjs`（可重复跑），
**下一步：把这些断言并入 `pnpm verify`，作为每个切片完成的门禁。**

## 6. 许可证边界（必须遵守）

| 资产 | 许可 | 可用方式 |
|---|---|---|
| OLF 全部代码 | MIT | 可翻译、可改写（保留版权声明） |
| Peach `src/**`（含 `application`/`player`/`card-art`） | AGPL-3.0 | **不可复制代码**。只可读结构后自行实现；若复制，整个前端须 AGPL 化 |
| Peach `src/react/boardui/**` | MIT（`ORIGIN.md` + `UPSTREAM.sha256` 标注 vendored） | 可单独提取（但我们用 Element Plus，无需求） |
| Peach `e2e/**` | AGPL-3.0 | 不可复制；只学「测试命名与覆盖面」的取舍 |

结论：**我们的实现必须整仓保持「OLF 血统 + 自研组织」，Peach 只贡献设计判断。**
本文件与 `docs/frontend-stack-decision.md` 一并作为「零接触 Peach 代码」的书面证据。

## 7. 下一步（按投入产出排序）

已完成的边界见 `docs/unified-media-product-plan.md` 第 21 节（本片交付记录）。之后的优先级：

| 序 | 切片 | 参考源 | 预计 | 理由 |
|---|---|---|---|---|
| 1 | **操作集剩余 7 项**：打包下载、解压、删除空目录、递归移动、新建文件、分享对话框、离线下载 | OLF `toolbar/*`（逐组件可翻译） | 6–8 人日 | 全部后端已有端点，纯前端工作，收益立刻可见 |
| 2 | **预览器补 8 项高价值**：archive、torrent、doc、xls、ppt、Monaco 文本编辑、asciinema、url | OLF `previews/*` + 依赖清单 | 8–10 人日 | 格式覆盖是网盘的核心体验；依赖按需装（合计约 900 KB gzip） |
| 3 | **递归搜索 + 浏览历史** | OLF `folder/Search.tsx`、`store/history.ts` | 3 人日 | `fs/search` 已就绪；历史可复用 `?path=` 的天然可寻址性 |
| 4 | **媒体域 M4**：库/编目/墙/详情/播放 | 结构学 Peach（§5.2–5.4），接口用我们的 `/api/media` | 14 人日 | 产品差异化的主战场，也是 Peach 价值最大化的地方 |
| 5 | **M5 切换**：全量回归 A–H + `frontend/dist` 替换 + 桌面端打包 | — | 5 人日 | — |

**不建议现在做**：管理台剩余 4 模块（plugins/messenger/backup-restore/dashboard）——
后端没有接口，前端做了也是空壳；先补后端再谈。

## 8. 本片中已经吸收的具体条目

| Peach/OLF 的做法 | 我们落成了什么 |
|---|---|
| OLF `folder/Grid.tsx` + `Images.tsx` 的双视图 | `pages/home/GridView.tsx`（grid / image 两模式，悬停才出勾选框） |
| OLF `component/FolderTree` + `Sidebar.tsx` 的目录树 | `components/FolderTree.tsx`（Element Plus `ElTree` 懒加载 + 深链自动展开） |
| OLF `Readme.tsx` 的「目录内文件优先、meta 兜底」 | `pages/home/ReadmeCard.tsx`（marked + DOMPurify，与 Markdown 预览同一管线） |
| OLF `folder/Pager.tsx` 的分页 | `utils/list.ts` 的 `paginate`（**客户端**分页，避免与前端排序打架） |
| Peach `application/*` 的「推导逻辑不入视图」 | 排序→过滤→分页收敛成一条 computed 流水线，规则抽到 `utils/list.ts` 并单测 |
| Peach 的 e2e 门禁思路 | `scripts/verify-v2.mjs`（Playwright，可重复执行） |

## 9. 已知取舍（本片）

1. **客户端分页**：后端 `fs/list` 没有 `sort` 参数，服务端分页会让每页各自排序、跨页顺序不一致；
   因此一次取全量再切片。超大目录（>10k 项）会有一次较大的响应，逃生路径是把站点 `default_page_size` 设为 0。
2. **网格缩略图直接拉原图**：后端目前不下发 `thumb`（字段恒空），`/p?type=thumb` 的缩略服务留到 M4；
   现阶段靠 `loading="lazy"` 控制解码成本。
3. **网格单击 = 打开**（与列表一致），选择靠勾选框；不做「单击选中、双击打开」的桌面式交互，
   避免两套肌肉记忆。
4. **递归搜索未进本片**：它是独立结果面板（跨目录结果 + 跳转定位），和「当前目录过滤」不是同一个交互，
   硬塞进同一个输入框会让语义含糊。
5. **自绘分页条代替 `ElPagination`**：EP 的分页组件在本项目依赖树下渲染成空注释节点，排查成本高于自写一个
   （80 行）。原因与同类症状见下条。

## 10. 一个必须记住的工程结论：pnpm 装的 Vue 有两份

本轮最耗时的不是功能，而是一个**静默失效**：

- 点 `ElRadioGroup` 里的「网格」→ 状态不更新、`is-active` 不变；
- `ElPagination` 渲染成 `<div class="ol-pager"><!----></div>`，无报错；
- 偶发 `TypeError: e.scope.on is not a function`，栈全在 `runtime-core`。

根因：应用依赖 `vue@3.6.0-rc.12`（prerelease），而 `element-plus` 的 peer 是 `vue: ^3.3.7` ——
**prerelease 不在该 semver 区间内**，pnpm 于是给 EP 单独装了一份 `vue@3.5.44`。
同一个页面里两份 `runtime-core`：EP 的响应式与渲染器不认识应用侧 vnode，行为表现为「空渲染 + 不更新 + 不可定位的报错」。

处置：

- `pnpm-workspace.yaml` 的 `overrides` 把 `vue` 与 `@vue/*` 全锁 **3.5.22**（稳定版），EP 官方支持；
- 这也意味着 **Vapor 实验推迟**（只在 3.6 构建里提供），VDOM 优先策略不变；
- 排查口诀：**组件空渲染 + 无报错 + 更新失效 → 先 `ls node_modules/.pnpm | grep vue@`**。

对参考仓库的含义：OLF 用 SolidJS，天然没有这个问题；Peach 包了 React 也一样。
**这条纯粹是我们「Vue + EP + pnpm」这套组合自己的坑**，与两个参考仓库无关 —— 记录下来避免团队重复踩。
