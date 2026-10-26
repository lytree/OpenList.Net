# 前端技术栈决策备忘录：SolidJS（维持） vs React（重写）

> 背景：用户指令「前端统一使用 React」，但要求先对比两条路线的**实现难易度**与**可复用性**，确认是否重写后再定稿。已锁定的约束：**硬切冻结**（SolidJS 冻结只修致命 bug）、**全量对齐**（React 首版须覆盖现有全部功能后才切换）。
>
> 本备忘录所有数字均来自 2026-10-25 实测：本地前端 236 个 TS/TSX 文件、31,869 行；上游与 Peach 仓库逐一核对。
>
> **⚖️ 决策记录（2026-10-25 定稿）**：触发第 0 节「React 仍然正确的三种情形」第二项——团队长期主栈为 React/Vue（主要），SolidJS 成为协作与招聘瓶颈。**最终决定：改用 Vue 3 + TSX（vue-jsx）+ Element Plus，渲染以 VDOM 优先**（中途曾定 React，随后按团队主栈与 UI 库选型改为 Vue；Peach 为 React，故代码复用为零，许可证收敛为 MIT 单一路径）。执行方案见 `docs/unified-media-product-plan.md` **v3**，脚手架见 `frontend/`（已通过构建与真实渲染验证；M5 前名为 `frontend-vue/`）。本备忘录保留为决策依据存档，第 0 节「建议维持 SolidJS」已失效。

## 0. 结论与建议（已被 2026-10-25 决策覆盖，见顶部决策记录）

**建议：维持 SolidJS，不重写。** 工程经济学结论明确：

| 路径 | 到达同一终点（统一产品+全量对齐） | 相对成本 |
|---|---|---|
| **S：维持 SolidJS** | **50–65 人日** | 1.0x |
| R-AGPL：React + fork Peach 前端 | 60–80 人日 + 前端转 AGPL | 1.2–1.6x |
| R-MIT：React 纯净重写 | 90–120 人日 | 1.8–2.4x |

三个叠加因素使 React 在**本仓库**不划算：

1. **重写税**：31,869 行可用产品代码中，UI 层在 React 路径下 0% 复用；已完成的 50% 媒体页（1.5k 行）也要扔掉重做。
2. **上游断供税**：本地 v4.2.7 紧跟上游 v4.2.9——上游红利正在真实流入（媒体库 UI、离线下载 UI、WebAuthn、双后端支持）。转 React 即永久断供，此后每个上游新功能都要手工再移植一遍。
3. **Peach 资产错配**：Peach 前端可复用的只有编目/播放墙/管理布局，但其 API 层是 Peach Python 后端的形状（需全部重写对接），UX 假设是单用户本地优先（onboarding、本机数据目录），与多用户网盘产品有张力。文件管理器、管理台（12 个模块）、全部预览器——Peach 一概没有，仍要从零建。

**React 仍然正确的三种情形**（满足任一即推翻本建议）：
- 产品定位转向 Peach 形态（单用户、本地优先、播放墙优先的个人媒体中心），OpenList 的文件管理/多用户退化为次要功能；
- 长期团队/协作者以 React 为唯一技术栈，SolidJS 成为招聘与协作瓶颈；
- 判断 OpenListTeam 上游将停止维护（hope-ui fork 同归冻结），需要自立长期生态。

## 1. 资产盘点（实测）

### 1.1 本地前端（SolidJS，OpenList-Frontend v4.2.7 衍生）

- 规模：**236 文件 / 31,869 行**，可构建可运行的完整产品
- `pages/manage/`：管理台 12 模块（storages/users/settings/tasks/shares/indexes/metas/plugins/messenger/backup-restore/dashboard/about）
- `pages/home/`：完整文件管理（浏览/上传/工具栏/file/folder/previews 全套预览器）
- `pages/media/`：原生媒体页 1,502 行（7 tab，对接自研 `/api/media`）
- 框架无关依赖已深度集成：artplayer+ASS 弹幕、hls.js、mpegts.js、monaco、katex、mermaid、lightgallery、docx-preview、streamsaver 等——**这是全前端最难啃的集成层，两条路径下均 100% 保留**

### 1.2 上游 OpenList-Frontend（SolidJS，MIT，活跃）

- 版本 v4.2.9 > 本地 4.2.7，**红利正在流入**（仅差两个补丁版）
- 自有媒体库 UI：MediaBrowser（33.5KB）+ MediaItemDetail + book/image/music/video 四类查看器子目录——SolidJS 路径的备选方案：若自研媒体页追不上，可实现上游媒体 API 契约直接换用其 UI
- 近期交付：离线下载 UI（PR #501）、WebAuthn、Go/TS 双后端适配（PR #660）

### 1.3 Peach 前端（React 19，AGPL-3.0）

- 12 目录 + 42 根文件：catalog/board(wall)/player/management/onboarding/history 等
- 技术栈：React 19.3 + react-aria + Tailwind 4 + TanStack Query/Table + recharts + BoardUI（**BoardUI 本身 MIT，可合法单独提取**）
- 可复用面：编目/播放墙/管理布局（须 AGPL）；API 层不可复用（对接 Peach Python 后端）；文件管理器/管理台/预览器：无

## 2. 实现难易度对比

| 维度 | SolidJS（增量） | React（重写） |
|---|---|---|
| 剩余工作量 | 原 M1–M5 方案 50–65 人日 | 全量对齐 90–120（MIT）/ 60–80（AGPL fork）人日 |
| 心智模型 | signals 细粒度响应，无 hooks 依赖数组陷阱 | hooks 规则 + 重渲染调优（React 19 compiler 有缓解） |
| 组件库 | hope-ui（OpenListTeam 维护的 fork，stitches 系） | react-aria/shadcn 生态丰富，但 236 文件的 hope-ui 用法要逐一映射 |
| 数据层 | axios + solid stores（现状可用） | TanStack Query 是**真升级**（缓存/失效/重试），但不构成推翻工作产品的理由 |
| 预览器/播放器 | 已集成，零工作 | 库本身框架无关，但包装组件全部重写（工作量中等，风险低） |
| i18n / 主题 | solid-primitives/i18n + 现有主题系统 | 迁移至 react-i18next 等，纯机械活但量大 |
| 上手/协作 | SolidJS 开发者市场小；AI 辅助对两者均已成熟 | React 人才与生态占绝对优势 |
| 风险 | 上游停滞则独扛 fork（但产品已完整可用） | 重写期 3–5 个月前端功能冻结；上游永久断供 |

**难易度结论**：React 重写没有技术上的"难"，只有量上的"大"——它是机械劳动（页面翻译 + 组件映射 + 集成重接），真正的成本是时间与机会成本，而不是可行性风险。

## 3. 可复用性对比

| 资产 | S：SolidJS | R-MIT | R-AGPL |
|---|---|---|---|
| 本地 31,869 行 UI | ✅ 100% | ❌ 0% | ❌ 0% |
| 播放器/预览器集成层（框架无关库） | ✅ 已集成 | ✅ 库可带走的包装层需重写 | ✅ 同左 |
| 上游 OpenList-Frontend 持续更新 | ✅ merge/rebase 即得 | ❌ 永久断供 | ❌ 永久断供 |
| 上游媒体库 UI（备选） | ✅ 可采纳 | ❌ | ❌ |
| Peach 编目/播放墙 UI | ❌（仅概念，已原生实现 50%） | ❌ | ✅（须 AGPL + API 层重写） |
| BoardUI 组件库 | — | ✅（MIT，带出处） | ✅ |
| 桌面端 `frontend/dist` 打包链 | ✅ 不动 | ✅ 输出同路径即零改动 | ✅ 同左 |

## 4. 决策框架

按你的首要目标选路径：

| 首要目标 | 选择 |
|---|---|
| 最快得到统一产品，持续白嫖上游演进 | **S：SolidJS**（本备忘录建议） |
| 产品本质是 Peach 形态（个人媒体中心优先），要 Peach UI 血统，接受 AGPL | R-AGPL |
| 前端长期自主生态/纯 React 团队，接受 1.8–2.4x 成本与上游断供 | R-MIT |

## 5. 若最终仍选 React：执行要点

在已锁定「硬切冻结 + 全量对齐」前提下的迁移骨架：

1. **位置与打包**：新建 `frontend-react/` 开发，达标后替换 `frontend/` 并保持输出 `frontend/dist`（桌面端零改动）；SolidJS 移入 `frontend-legacy/` 冻结。
2. **技术栈**：React 19 + TS + Vite + TanStack Query/Router/Table + react-aria + Tailwind（对齐 Peach 栈以便最大程度参考其结构）；UI 组件从 BoardUI（MIT）起步。
3. **组件映射表**（迁移的核心资产，先行建立）：hope-ui → react-aria/TanStack Board；solid-contextmenu → react 无障碍菜单；solid-primitives/i18n → react-i18next；solid stores → TanStack Query + zustand。
4. **里程碑**：M0 脚手架+鉴权+布局（≈10 人日）→ M1 文件管理全量（≈25）→ M2 管理台 12 模块（≈20）→ M3 预览器全量（≈15）→ M4 媒体域（含墙，≈15）→ M5 切换发布（≈5）。合计 ≈90 人日（MIT 基线；AGPL fork 时 M4 降至 ≈5–8，M1/M2/M3 不变）。
5. **AGPL 合规要点**（仅 R-AGPL）：前端整体以 AGPL-3.0 发布、保留 Peach 版权声明、向网络用户提供对应源码；参与者从即日起方可阅读 Peach 源码（此前项目为零接触，保持分界）。
6. **回归基线**：切换前以 SolidJS 版功能清单逐项勾验（全量对齐验收）。

## 6. 后续动作

- 若定 **S**：主方案 `docs/unified-media-product-plan.md` 维持原样（M1 第一批任务可立即开工）。
- 若定 **React（任一版）**：告知许可证姿态，我将按第 5 节重写主方案的路线图与技术栈章节。
