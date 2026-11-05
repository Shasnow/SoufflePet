# 桌宠前端 React 重构计划（v4：单页应用 · DSH 式布局 · OKLCH 主题）

> 状态：**已完成**（M0–M8：契约/骨架、对话区、宿主窗口与桥、Core、ChatSession/会话管理、设置 API/七页、真实接线/默认 React、旧 UI 清理、性能与质量；见 §15）。
> 前置文档：[对话窗口 WebView 重写方案（探索稿，已归档）](webview-chat.md)（P0 探针、v1 协议、窗口行为、风险清单继续有参考价值）。
> 视觉基线：原 `docs/prototype/chat-mock.html` 不存在，改用 `SoufflePet/web` 的 mock 宿主 + `dev/widths.html` + `.verifybuild/ui` 五档截图。
> 版本变更：
> - v2：允许重写 `ChatViewModel` / `Services/`，引入 `SoufflePet.Core`。
> - v3：色彩系统改为 **OKLCH**，支持动态主题色。
> - v4：前端改为**单页应用**（侧边栏 + 对话区 + 响应式），取代 v3 的“对话窗口 + 独立设置窗口”双窗口模型；新增多会话/历史列表。

## 1. 摘要

- **单页应用**：一个 React SPA 承载全部业务界面——侧边栏（新会话 / 插件 / 自动化任务 / 更多 / 工作区会话列表 / 账户）+ 对话区 + 设置页；由**一个** WebView2 窗口承载，支持任意宽度下的响应式折叠。
- **只保留一个原生窗口**：Live2D 桌宠窗口（`NativePetWindow` + `NativePetControl` + OpenGL）不变；托盘、热键、文件对话框、点击穿透、窗口停靠仍归宿主。
- **后端重构**：`ChatViewModel` 重写为 `ChatSession`，新增 `ChatSessionManager` 管理多会话；`Services/` 分层迁移到 `SoufflePet.Core`（无 Avalonia）；设置面板 ViewModel 由 `SettingsApi` 取代。
- **视觉与主题**：所有颜色 token 用 **OKLCH**；强调色可在“外观”面板动态切换（预设/自定义/跟随系统），实时广播到窗口并持久化。
- **布局参考 DSH**：宽窗口显示 280 px 侧边栏；窄窗口自动收起为抽屉，对话区全宽；欢迎页/输入卡/会话列表的层级与 DSH 对齐，但保留 SoufflePet 的模型选择、审批模式、工作区、附件等业务元素。
- **代价**：重构范围扩大（单窗口壳 + 多会话存储 + 响应式）；`dotnet build` 依赖 Node/pnpm；WebView2 变为必需。
- **工作量**：单人约 **12 周**；前后端两人并行约 **7–8 周**。

## 2. 与旧方案的关系

| 旧结论 | v4 决定 | 说明 |
| --- | --- | --- |
| v3：对话窗口 + 独立设置窗口（两个 WebView） | **改为一个 SPA 窗口** | 侧边栏导航在同一个窗口内切换对话/设置，减少一个 renderer 与一套窗口生命周期 |
| v3：设置面板 7 个路由 | **保留，但作为内容区页面** | `#/chat/:sessionId`、`#/settings/:panel`；侧边栏高亮当前页 |
| 旧：单会话（`ChatViewModel` 单例） | **多会话** | 侧边栏需要历史列表；新增 `ChatSessionManager` 与会话索引 |
| v2：重写 `ChatSession`、Core 分层 | **保留** | 单页应用需要更干净的状态/事件模型 |
| v3：OKLCH + 动态主题 | **保留并扩展** | 侧边栏、内容区、欢迎页都使用同一套 token |
| P0：透明、窗口化宿主、页面侧 resize、DOM 浮层、双 WebView 共用 profile | **沿用** | 单窗口后 8 向 resize、拖拽、透明仍按原结论 |
| 旧：420×620 固定对话窗口 | **改为可缩放主窗口** | 默认 ~1120×760，最小 400×520，记忆尺寸/位置；窄宽度自动进入响应式布局 |
| D4：v1 不做拖放 | **仍不做** | 后续 COM `postMessageWithAdditionalObjects` |
| D6：不预热 | **仍不预热** | 首开骨架淡入 |

## 3. 范围与边界

### 3.1 React 负责

| 区域 | 内容 |
| --- | --- |
| 应用壳 | 自定义标题栏（拖拽区/窗口控制）、侧边栏、内容区、抽屉/遮罩、响应式断点 |
| 侧边栏 | 品牌、新会话、插件、自动化任务、更多（MCP/技能/对话模型/Live2D 模型/外观）、工作区与会话列表、搜索、账户/用量页脚 |
| 对话区 | 欢迎页（Hero + 输入卡）、消息列表、输入区、模型/审批/工作区/用量/AskUser/附件浮层 |
| 设置页 | 七个面板作为内容区页面：`#/settings/{llm,mcp,skills,plugins,tasks,models,appearance}` |
| 主题 | OKLCH token、外观面板、`host.theme` 应用、首帧注入、对比度提示 |

### 3.2 Core 重写/迁移

- `ChatViewModel`（1650 行）→ `ChatSession`（单会话状态机） + `ChatSessionManager`（列表/新建/打开/重命名/删除）。
- `Models/ChatMessage`：去掉 `Avalonia.Media.Imaging.Bitmap`，改为纯数据 + 图片 token。
- `Services/`（38 个文件、约 8.9k 行）按 §7.3 矩阵迁移；UI 强耦合的 `ScheduledTaskService`、`PetExpressionService`、窗口服务重写或留在宿主。
- 设置面板 ViewModel（约 1.5k 行）→ `SettingsApi` + React 表单；`SouffleSettings` 改为纯 POCO + `SettingsStore`（字段名兼容）。

### 3.3 保持原生 / 不动

- `NativePetWindow`、`NativePetControl`、Live2D SDK、OpenGL 渲染、口型/表情/手柄/目光/关屏停渲染、桌宠右键菜单。
- 托盘 `NativeMenu`、`Ctrl+Alt+Space`、文件/目录选择器、系统消息框、窗口停靠/点击穿透。
- 插件 SDK 契约与插件二进制接口。
- 既有数据：`window.json`、`statistics.json`、`SoufflePet.json`、`~/.souffle/*`；`conversations.jsonl` 做迁移而非丢弃。

### 3.4 明确不做

- 不引入 Electron；不把 Live2D 搬进 WebGL；不重写 Live2D SDK。
- 不做“对话窗口”与“设置窗口”分离的双窗口。
- 不为了重写而重写 LLM/工具/MCP/技能/插件加载算法；以搬迁 + 解耦为主。
- 不做云端同步/多端会话（本地 SQLite/JSONL 即可）。

## 4. 目标架构

```
┌──────────────────────────────┐        ┌────────────────────────────────────────┐
│ NativePetWindow (Avalonia)   │        │ AppWebWindow (WebView2)                │
│  Live2D / OpenGL / 托盘菜单   │        │  React SPA（单页）                      │
│  口型、表情、手柄、点击穿透    │        │  ┌──────────┬───────────────────────┐  │
└───────────────┬──────────────┘        │  │ Sidebar  │ Content               │  │
                │ 原生调用                │  │ 会话列表 │ #/chat/:id            │  │
                ▼                        │  │ 设置入口 │ #/settings/:panel     │  │
┌───────────────────────────────────────┐ │  └──────────┴───────────────────────┘  │
│ SoufflePet（宿主，net10.0-windows）     │ └───────────────────┬────────────────────┘
│  NativePet / Tray / Hotkey / 对话框     │                     │ JSON-RPC over WebMessage
│  AppWebWindow / WebBridgeServer         │                     │
│  ChatRpc / SettingsApi / ThemeService   │◄────────────────────┘
│  Gamepad / LipSync / LocalFileServer    │
│        │ 订阅 ChatSession 事件           │
│        ▼                                │
│ SoufflePet.Core（net10.0，无 Avalonia）  │
│  ChatSessionManager / ChatSession        │
│  Llm / Tools / Mcp / Skills / Plugins    │
│  Memory / Stats / Scheduling / Settings  │
└─────────────────────────────────────────┘
```

**窗口模型**

| 窗口 | 说明 |
| --- | --- |
| `NativePetWindow` | 唯一常驻原生窗口；Live2D 与系统交互 |
| `AppWebWindow` | 单例 WebView2 主窗口；首次呼出创建，关闭只隐藏；默认 1120×760（DIP），最小 400×520；记忆上次尺寸/位置；首次停靠在桌宠旁 |
| 托盘/热键/桌宠点击 | 切换 `AppWebWindow` 显隐；托盘“对话窗口”直接打开 `#/chat` |

**响应式状态**

| 容器宽度 | 侧边栏 | 内容区 | 顶栏 |
| --- | --- | --- | --- |
| ≥ 1024 px | 常驻 280 px | 最大 840 px 居中，左右留白 | 隐藏汉堡按钮 |
| 720–1023 px | 常驻 240 px（图标+短标签） | 全宽，左右 24 px | 汉堡按钮可收起侧栏 |
| < 720 px | 抽屉（默认关闭）+ 遮罩 | 全宽，左右 16 px | 汉堡按钮打开抽屉 |

- 用 `container-type: inline-size` + 容器查询驱动布局，避免依赖 `window` 媒体查询（WebView 窗口可任意缩放）。
- 侧栏开合状态持久化（`UiSidebarCollapsed`）；跨断点时自动关闭抽屉，不强制改变用户的手动折叠偏好。

## 5. 通信协议

### 5.1 封装

页面 → 宿主：

```json
{ "v": 1, "id": 42, "type": "req", "method": "chat.send", "params": { "text": "..." } }
```

宿主 → 页面：

```json
{ "v": 1, "id": 42, "ok": true, "result": null }
{ "v": 1, "type": "event", "name": "chat.ops", "payload": { "rev": 1024, "ops": [] } }
{ "v": 1, "type": "snapshot", "rev": 1024, "data": { } }
```

- 单窗口角色 `app`；不再区分 `chat` / `settings` 角色。
- 握手：`hello { protocolVersion, window: "app", route }` → `snapshot`（含会话列表、当前会话、主题）。
- 事件带 `rev`；跳号发 `resync`；窗口隐藏期间事件仍入队，显示时补发快照或增量。
- 40 ms 合帧；>256 KB 分片，仍超限走 `LocalFileServer /state?token=`。

### 5.2 方法与事件

| 域 | 方法 |
| --- | --- |
| `session.*` | `list` / `new` / `open` / `rename` / `delete` / `search` |
| `chat.*` | `send` / `stop` / `draft` / `attach.*` / `model.*` / `approval.*` / `workspace.*` / `ask.*` / `file.open` / `link.open` / `msg.expand` |
| `nav.*` | `goto { route }`（宿主/页面互相跳转，如“更多”→“外观”） |
| `window.*` | `drag` / `resize { edge }` / `minimize` / `maximize` / `close` / `state` |
| `settings.llm.*` | `list` / `saveProvider` / `deleteProvider` / `fetchModels` / `setEnabled` / `setDefault` |
| `settings.mcp.*` | `list` / `save` / `delete` / `test` / `refreshTools` |
| `settings.skills.*` | `list` / `setEnabled` / `openFolder` / `detail` |
| `settings.plugins.*` | `list` / `setEnabled` / `getConfigSchema` / `saveConfig` / `import` |
| `settings.tasks.*` | `list` / `save` / `delete` / `setEnabled` / `runNow` / `nextRun` |
| `settings.models.*` | `list` / `importFolder` / `importArchive` / `remove` / `setActive` / `validate` |
| `settings.appearance.*` | `get` / `setAccent` / `setFollowSystem` / `reset` / `preview` |

事件：

| 事件 | 载荷 |
| --- | --- |
| `chat.ops` | `{ sessionId, rev, ops }`，op 沿用 `msg:add/insert/remove/text/patch`、`turn`、`ask`、`usage`、`attachments`、`approval`、`workspace`、`models`、`toast` |
| `session.updated` | `{ sessions: SessionSummary[], activeId, rev }`（新建/重命名/删除/排序变化） |
| `host.theme` | `{ rev, mode, accent, accentH, accentL, accentC, followSystem }` |
| `host.log` | `{ level, msg }`（`?debug=1` 调试浮层） |

`panel` 开合、侧栏折叠、当前路由是纯 UI 状态，留在 React；Core 不感知。

### 5.3 安全

- 单窗口角色 `app`；方法白名单仍按域划分。
- 路径参数（附件、打开文件、工作区、模型导入）在宿主/Core 做存在性与根目录校验。
- 页面禁止导航外站；生产关闭 DevTools/默认右键菜单；CSP 仅 `'self'`。
- 本地图片只走 `/local-file?token=`。

## 6. 前端工程设计

### 6.1 选型

| 项 | 选择 |
| --- | --- |
| 框架 | React 19 + TypeScript strict |
| 构建 | Vite，`base: '/app/'`，HashRouter |
| 样式 | Tailwind CSS v4 + CSS 变量 token（OKLCH）+ 容器查询 |
| 基础组件 | Radix primitives（Popover/Dialog/Tooltip/ScrollArea）+ `lucide-react` |
| 状态 | zustand（会话/消息/设置） + 路由 hash |
| Markdown | `react-markdown` + `remark-gfm` + `rehype-katex` + `rehype-sanitize`，Shiki 懒加载 |
| 长列表 | `@tanstack/react-virtual` + 消息级选择器 + 增量文本 |
| 颜色 | OKLCH + 相对颜色语法；`colorjs.io` 做 sRGB↔OKLCH 与 gamut mapping |
| 测试 | Vitest + Testing Library；Playwright + mock host（含多宽度截图） |

### 6.2 应用壳布局

宽屏（参考 DSH）：

```
┌──────────────────────────────────────────────────────────────┐
│ [☰] SoufflePet                                   [─][□][×]  │  ← 自定义标题栏（拖拽区）
├───────────────┬──────────────────────────────────────────────┤
│ ＋ 新会话      │                                              │
│               │              ✦  SoufflePet                    │
│ 插件           │           “和舒芙蕾说点什么”                  │
│ 自动化任务      │                                              │
│ 更多 ▾         │   [工作区 ▾]  [标准模式 ▾]                     │
│               │   ┌────────────────────────────────────────┐ │
│ 工作区     ⌕ ＋│   │ 描述你想构建的内容…                     │ │
│  ▾ SoufflePet │   │ ＋   完全权限 ▾        deepseek ▾   ➤  │ │
│   新会话   8分 │   └────────────────────────────────────────┘ │
│   …           │                                              │
│               │                                              │
│ ⚙ 150****98   │                                              │
└───────────────┴──────────────────────────────────────────────┘
```

窄屏（参考 652×1002 截图）：

```
┌──────────────────────────────────────┐
│ [☰] SoufflePet           [─][□][×]   │
├──────────────────────────────────────┤
│                                      │
│            ✦  SoufflePet             │
│         “和舒芙蕾说点什么”             │
│                                      │
│   [工作区 ▾]   [标准模式 ▾]           │
│   ┌──────────────────────────────┐   │
│   │ 描述你想构建的内容…           │   │
│   │ ＋  完全权限 ▾   model ▾  ➤  │   │
│   └──────────────────────────────┘   │
└──────────────────────────────────────┘
```

- 侧边栏顺序与 DSH 对齐：品牌 → 新会话 → 插件 / 自动化任务 / 更多 → 工作区（标题 + 搜索 + 新建文件夹）→ 工作区下的会话列表（相对时间）→ 底部账户/设置。
- “更多”是一个 popover，列出 MCP、技能、对话模型、Live2D 模型、外观；选中后内容区切到对应设置页，侧栏保持可见（宽屏）。
- 会话列表项显示标题（首条用户消息截断）、相对时间、当前高亮；悬停出现“重命名/删除”菜单。
- 欢迎页仅在当前会话没有消息时显示；有消息时显示消息列表 + 底部输入卡，输入卡最大宽 760 px。

### 6.3 响应式与窗口行为

- 断点见 §4；用容器查询实现，`AppShell` 设 `container-type: inline-size`。
- 宽度 < 720：侧栏改为抽屉，遮罩点击关闭；选择会话/设置后自动关闭抽屉。
- 输入卡：宽屏 `max-width: 760px`；窄屏 `width: 100%`、减少内边距、按钮只留图标 + `title`。
- 消息气泡：宽屏 `max-width: min(76%, 640px)`；窄屏 88%。
- 标题栏：左侧汉堡 + 品牌；右侧最小化/最大化/关闭，均走 `window.*`；空白区 `pointerdown` 发 `window.drag`；双击最大化。
- 8 向缩放：页面最外圈 6 px 判定方向并设 `cursor`，`pointerdown` 发 `window.resize` 交给宿主（沿用 P0 结论）。
- 记住：窗口尺寸/位置（`window.json`）、侧栏开合（`UiSidebarCollapsed`）、上次路由与当前会话。

### 6.4 状态与数据流

```
host snapshot { sessions, activeSessionId, chat, theme } ──► zustand stores
host events   { chat.ops, session.updated, host.theme }  ──► incremental update
UI state      { sidebarOpen, activeRoute, openPopover }  ──► local store / router
page actions  ──► bridge.invoke("session.open" | "chat.send" | ...) ──► host
```

- 会话列表与消息都以宿主为准；页面只做乐观的“发送中”与本地输入草稿。
- 切换会话：`session.open` 返回快照；旧会话事件停止应用（按 `sessionId` 过滤）。
- 流式消息：`msg:text` 差量进当前会话 store；`MessageItem` 只订阅自己那条消息。
- 新建会话：`session.new` 只创建内存草稿并路由到 `#/chat/<id>`；草稿不进侧栏/搜索结果、不写 `sessions.json` 与会话目录，首条用户消息发出后才落盘转正（标题同时生成，可重命名）。连点「新会话」复用当前空白草稿，不会堆出多个隐形会话。
- 欢迎页 → 首条消息：发送后立即切到消息列表并显示“思考中”占位，滚动贴底。

### 6.5 色彩系统：OKLCH + 动态主题色

**原则**

- 所有颜色 token 用 OKLCH 定义（`oklch(L C H / A)`），不写 `rgba()`/hex；Chromium 156 原生支持 OKLCH 与相对颜色语法。
- 只存一个“强调色”，其余强调色变体、表面色、描边色全部派生；组件里禁止颜色字面量。
- 不从 Avalonia 的 `#AARRGGBB` 照抄（R15 字节序问题从制度上消除）；`tokens.css` 是唯一色值来源。

**基础 token（暗色玻璃，值示例）**

```css
:root {
  --accent-h: 35;
  --accent: oklch(0.72 0.17 var(--accent-h));
  --accent-hover: oklch(from var(--accent) calc(l + 0.06) c h);
  --accent-pressed: oklch(from var(--accent) calc(l - 0.06) c h);
  --accent-soft: oklch(from var(--accent) 0.32 0.08 h / 0.35);
  --on-accent: oklch(0.16 0.01 var(--accent-h));

  --surface-0: oklch(0.18 0.006 var(--accent-h));  /* 侧栏 */
  --surface-1: oklch(0.22 0.008 var(--accent-h));  /* 内容区 */
  --surface-2: oklch(0.27 0.010 var(--accent-h));  /* 卡片/浮层 */
  --text-1: oklch(0.94 0.005 var(--accent-h));
  --text-2: oklch(0.74 0.008 var(--accent-h));
  --text-3: oklch(0.58 0.008 var(--accent-h));
  --line: oklch(0.32 0.010 var(--accent-h) / 0.6);

  --danger: oklch(0.62 0.19 25);
  --success: oklch(0.70 0.15 150);
  --warning: oklch(0.78 0.15 85);
}
```

**派生与校验**

1. 外观面板选 sRGB 颜色 → `colorjs.io` 转 OKLCH → 写 `--accent-h/-l/-c` 与 `--accent`。
2. gamut mapping（`toGamut({ space: "oklch" })`），超 sRGB 夹回并提示。
3. `--on-accent` 按 L 阈值（≥0.62 深色字，否则白字），并做 WCAG 校验（≥4.5:1），不达标自动夹 L。
4. 正文/次要文字对 surface 对比度 ≥7:1 / 4.5:1；非文本 UI ≥3:1。
5. 语义色固定 hue，不参与强调色派生。

**动态切换与首帧**

- `SouffleSettings` 新增 `UiAccentColor`（`#RRGGBB`）、`UiAccentFollowSystem`。
- 宿主读 `UISettings.GetColorValue(UIColorType.Accent)`，失败回退 DWM `ColorizationColor`。
- 首帧：页面 URL 带 `?accent=%23RRGGBB&mode=dark`，`index.html` 内联脚本在 React 挂载前写入 token；`snapshot`/`host.theme` 再权威同步。
- 切换：`settings.appearance.setAccent` → 落盘 → `ThemeService` 广播 `host.theme` → 所有窗口更新 `:root` 变量（即时生效）。
- “跟随系统”开启时监听系统强调色变化并广播；原生桌宠窗口无 chrome，不参与主题。

**外观面板（`#/settings/appearance`）**

- 8 个 OKLCH 预设（固定 L/C，取 8 个色相）。
- 自定义：`<input type="color">` + L/C/H 滑杆；实时预览，确认后落盘。
- “跟随系统强调色”、恢复默认、对比度提示；修改即时生效、无需重启。

**无障碍与防回归**

- `prefers-reduced-transparency: reduce` 去毛玻璃；`prefers-contrast: more` 提高描边/文字对比。
- 不单靠颜色传达状态：按钮/审批/工具状态带文字与图标。
- `web/scripts/check-colors.mjs`：除 `styles/tokens.css` 外禁止 `#hex`/`rgb(`/`hsl(`/`oklch(` 字面量；构建时执行。
- 单测：颜色转换、gamut mapping、对比度阈值、`host.theme` 应用。

### 6.6 目录

```
SoufflePet/web/
├─ package.json / pnpm-lock.yaml / vite.config.ts / tsconfig.json / index.html
└─ src/
   ├─ app/{AppShell.tsx,Sidebar.tsx,TopBar.tsx,router.tsx}
   ├─ bridge/{protocol.ts,client.ts,contracts.ts,mock-host.ts}
   ├─ features/chat/{ChatPage,WelcomeHero,MessageList,MessageItem,Composer,
   │                  AskPanel,ModelPicker,ApprovalPicker,WorkspacePicker,
   │                  UsagePanel,Attachments,SessionMenu}
   ├─ features/sessions/{SessionList,WorkspaceGroup,SearchBox,NewSessionButton}
   ├─ features/settings/{SettingsLayout,LlmSettings,McpSettings,SkillsSettings,
   │                      PluginsSettings,PluginConfigForm,TasksSettings,ModelsSettings,
   │                      AppearanceSettings}
   ├─ theme/{oklch.ts,apply.ts}
   ├─ ui/{Button,Field,Dialog,Popover,Toast,Markdown,CodeBlock,VirtualList,Drawer}
   ├─ styles/{tokens.css,base.css}
   └─ mocks/*
```

## 7. 后端重构

### 7.1 项目分层

| 项目 | 目标框架 | 内容 |
| --- | --- | --- |
| `SoufflePet.Core`（新增） | `net10.0` | 模型、会话管理器/会话、LLM/工具/MCP/技能/插件/记忆/统计/调度、设置存储、平台抽象 |
| `SoufflePet`（现有） | `net10.0-windows` | Avalonia 窗口、Live2D、托盘/热键/对话框/剪贴板、WebView、RPC、平台服务实现 |
| `SoufflePet.Plugin.Sdk`（不动） | `net10.0` | 插件契约 |
| `SoufflePet.Core.Tests`（新增） | `net10.0` | 会话状态机、会话管理、序列化、调度器单测 |

### 7.2 `ChatSession` 与 `ChatSessionManager`

单会话状态机（替代 `ChatViewModel`）：

```csharp
public sealed class ChatSession : IAsyncDisposable
{
    public ChatState State { get; }                    // 消息、草稿、模型、附件、工作区、用量、AskUser
    public IAsyncEnumerable<ChatEvent> Events { get; } // 带 rev 的有序事件

    public Task SendAsync(string text, CancellationToken ct);
    public Task StopAsync();
    public Task SetDraftAsync(string text);
    public Task AddAttachmentsAsync(IReadOnlyList<string> paths);
    public Task RemoveAttachmentAsync(int index);
    public Task SelectModelAsync(string key);
    public Task SetApprovalModeAsync(ApprovalMode mode);
    public Task DecideApprovalAsync(string messageId, bool accept);
    public Task SwitchWorkspaceAsync(string path);
    public Task SubmitAskAsync(AskAction action, JsonElement payload);
}
```

会话管理（新增）：

```csharp
public sealed class ChatSessionManager : IAsyncDisposable
{
    public IReadOnlyList<SessionSummary> Sessions { get; }
    public string? ActiveSessionId { get; }

    public Task<SessionSummary> CreateAsync(string? workspacePath, CancellationToken ct);
    public Task<ChatSession> OpenAsync(string sessionId, CancellationToken ct);
    public Task RenameAsync(string sessionId, string title, CancellationToken ct);
    public Task DeleteAsync(string sessionId, CancellationToken ct);
    public Task<IReadOnlyList<SessionSummary>> SearchAsync(string query, CancellationToken ct);
}
```

- 每个会话一个 `conversation.jsonl`（与现有格式兼容），索引 `sessions.json` 保存标题/工作区/时间/消息数。
- 现有 `conversations.jsonl` 首次启动时迁移为默认会话，原文件保留只读备份。
- 标题默认取首条用户消息前 30 字；可手动重命名。
- 删除二次确认；删除后若当前会话被删，自动切到最近会话，没有剩余会话时在内存里补一个草稿（不落盘）。
- 新建会话先只存在于内存（草稿）：不写索引、不建目录、不进侧栏；首条用户消息发出后才落盘转正。这样连点「新会话」不会留下空会话。
- 内存中的长对话上限沿用现有裁剪策略；会话之间不共享消息上下文（长期记忆 `profile.json` 保持全局）。

### 7.3 服务迁移矩阵

| 服务 | 去向 | 处理 |
| --- | --- | --- |
| `LlmChatService`、`LlmModelCatalog` | Core | 机械搬迁；测试用假流 |
| `ChatTools`、`ChatToolAccess` | Core | 搬迁；`IScreenCapture`/`IBrowserAutomation` 抽接口 |
| `McpConfigStore`、`McpToolService` | Core | 搬迁 |
| `SkillService`、`PluginService`、`PlatformService` | Core | 搬迁；日志/DI 适配 |
| `ChatMemory`、`StatisticsService` | Core | 搬迁；按会话路径注入，`TimeProvider` |
| `ScheduledTaskService` | Core | **重写**：去 `ObservableCollection`/`DispatcherTimer` |
| `ChatFileService`、`UserQuestionService` | Core | 搬迁/轻改 |
| `Live2DModelService` | Core（文件操作） | 校验/导入/删除留 Core；缩略图留宿主 |
| `SouffleSettings` | Core | 改 POCO + `SettingsStore`；字段名不变；加 `UiAccent*` |
| `ChatMessage` | Core | 去 `Bitmap`/Avalonia；图片走路径 + token |
| 新增 `ChatSessionManager` | Core | 会话索引/CRUD/迁移 |
| `AutoExpressionService` | Host | 进程检测 + `DispatcherTimer`；抽 `IPetController` |
| `PetExpressionService` | Host | 依赖 `IPetWindow` |
| `GamepadService`、`LipSyncService` | Host | XInput/NAudio/轮询 |
| `PowerNotificationService` | Host | Win32 消息钩子 |
| `BrowserService`、`LocalFileServer`、窗口/托盘/热键服务 | Host | WebView/Avalonia/Win32 |
| `ThemeService`（新增） | Host | 系统强调色、`host.theme` 广播 |
| `WebChatBridge` | Host | 由 `ChatRpc` 取代 |
| 设置面板 VM | 删除 | 由 `SettingsApi` 取代 |
| `ChatWindow.axaml*`、`SettingsWindow`、六个 `*View.axaml*`、`WebViewPetWindow` | 删除 | P4 |

### 7.4 线程、生命周期与取消

- `ChatSession` 内部单队列串行；事件订阅隔离；Core 不引用 `Dispatcher`、不用 `async void`。
- 切换会话不阻塞 UI：`OpenAsync` 从磁盘加载 + 投影消息，返回快照；旧会话继续后台跑完或按规则取消（默认不取消发送中的轮次，切回时补事件）。
- 应用退出 `DisposeAsync` 取消所有会话；窗口隐藏不取消。
- 宿主负责 UI 线程与 WebView 调用。

### 7.5 设置 API

- 每个面板一个 `SettingsApi`；外观用 `AppearanceApi`，写成功后 `ThemeService` 广播 `host.theme`。
- 插件动态表单由 Core 返回 schema，React 渲染。
- 设置变更通过 `SettingsStore` 落盘；宿主订阅副作用（自启、表情、模型重载）。

### 7.6 数据兼容

- `window.json`、`statistics.json`、`SoufflePet.json` 字段名不变；新增 `UiAccentColor`、`UiAccentFollowSystem`、`UiSidebarCollapsed`。
- 旧 `ChatRenderer` 忽略；新增 `UiRenderer` 只影响宿主窗口选择（迁移期）。
- `conversations.jsonl` 迁移为默认会话；迁移前备份 `conversations.jsonl.bak`。
- 会话消息 DTO 只做展示投影，不写回历史文件。

### 7.7 组装根

- Core 提供 `AddSouffleCore()`；宿主注册平台实现（`IPlatformTools`、`IUserInteraction`、`IPetController`、`IBrowserAutomation`、`ThemeService`）。
- `App.axaml.cs` 只做注册/装配/启动；窗口服务只剩 `IAppWindowService`（替代对话/设置窗口服务）。

## 8. 分阶段实施

| 阶段 | 内容 | 交付/验收 | 估算 |
| --- | --- | --- | --- |
| **P0 契约** | v1 扩展：`session.*`、`nav.*`、`window.*`、`settings.*`、`host.theme`；DTO/事件模型；窗口角色 `app` | 契约评审通过；每个旧交互有对应方法 | 3 人日 |
| **B1 Core 骨架** | 新建 Core + Tests；搬迁 Models/纯服务；`AddSouffleCore`；特征测试基线 | 旧 UI 行为不变；Core 无 Avalonia 引用 | 7 人日 |
| **B2 ChatSession** | 重写 `ChatViewModel` → `ChatSession` + 事件；`ChatRpc`；`LegacyChatViewModel` 适配旧窗口 | 假模型流/工具/审批/Ask/取消通过；旧窗口可用 | 8 人日 |
| **B3 会话管理** | `ChatSessionManager`、`sessions.json`、旧 `conversations.jsonl` 迁移、CRUD/搜索 | 新建/切换/重命名/删除/重启恢复；无数据丢失 | 5 人日 |
| **B4 设置 API** | 七面板 API（含外观）；删面板 VM；插件 schema | 七面板 CRUD + 校验 + 持久化 parity | 5 人日 |
| **P1 应用壳** | Vite/React/TS/Tailwind；标题栏/侧栏/内容区/抽屉；容器查询；OKLCH token + 首帧注入；bridge client + mock host | 1416/1024/800/652/420 五档截图通过；拖/缩放/最小化/关闭正常 | 6 人日（可与 B1 并行） |
| **P2 对话区** | 欢迎页、消息列表、输入卡、模型/审批/工作区/用量/AskUser/附件、会话列表与切换、快捷键 | 与旧窗口 parity；500 条消息 + 流式不卡；窄屏可用 | 9 人日 |
| **P3 设置页** | 七个内容区页面；外观面板；错误/加载/空态 | 七面板逐项验收；主题即时切换并持久化 | 9 人日（可与 B4 并行） |
| **P4 清理** | 删旧视图/窗口/Markdown/WebViewPet/`LegacyChatViewModel`/面板 VM；`UiRenderer` 收口；README/AGENTS | `dotnet build` 0 新增警告；仅一个原生窗口 + 一个 Web 窗口 | 4 人日 |
| **P5 性能与质量** | 隐藏窗口 suspend、首屏 chunk、RPC 打点、CSP/导航、Core 测试补齐、响应式手工矩阵 | 隐藏 10 分钟工作集回落；安全项通过；核心测试绿灯 | 3 人日（并行） |

依赖：`P0 → {B1→B2→B3→P2, P1→P2}`；`B4→P3`；`P2/P3→P4`。

## 9. 文件级变更清单

**新增**

| 路径 | 说明 |
| --- | --- |
| `SoufflePet.Core/` | Core 项目（`Chat/`、`Sessions/`、`Llm/`、`Tools/`、`Mcp/`、`Skills/`、`Plugins/`、`Memory/`、`Scheduling/`、`Settings/`、`Abstractions/`） |
| `SoufflePet.Core.Tests/` | xUnit（会话状态机/会话管理/序列化/调度器） |
| `SoufflePet/web/**` | React 源码 |
| `SoufflePet/Views/Web/AppWebWindow.cs` | 单例主窗口（标题栏/拖拽/8 向缩放/关闭隐藏/记忆尺寸） |
| `SoufflePet/Services/Web/WebBridgeServer.cs` | 握手/路由/白名单/合帧 |
| `SoufflePet/Services/Web/ChatRpc.cs` | Core 事件 → ops；命令 → ChatSession |
| `SoufflePet/Services/Web/SessionRpc.cs` | 会话列表/CRUD/搜索 |
| `SoufflePet/Services/Web/SettingsApi/*.cs` | 七面板 API |
| `SoufflePet/Services/Web/ThemeService.cs` | 系统强调色 + `host.theme` 广播 |
| `SoufflePet/Services/AppWindowService.cs` | 替代 `ChatWindowService` 与六个设置窗口服务 |
| `SoufflePet/web/src/app/{AppShell,Sidebar,TopBar,router}` | SPA 壳与响应式布局 |
| `SoufflePet/web/src/features/sessions/*` | 会话列表/工作区分组/搜索 |
| `SoufflePet/web/src/theme/{oklch,apply}.ts` | 颜色转换/校验/应用 |
| `SoufflePet/web/src/features/settings/AppearanceSettings` | 外观面板 |
| `SoufflePet/web/scripts/check-colors.mjs` | 颜色字面量检查 |
| `docs/react-ui-contract.md` | 冻结协议与命令清单 |

**修改**

| 文件 | 改动 |
| --- | --- |
| [App.axaml.cs](../SoufflePet/App.axaml.cs) | 去 `useWebView/useChatWebView`；注册宿主 + `AddSouffleCore()`；固定 `NativePetWindow` |
| [ChatWindowService.cs](../SoufflePet/Services/ChatWindowService.cs) | 并入 `IAppWindowService`：单窗口切换/停靠/热键 |
| [SouffleSettings.cs](../SoufflePet/Models/SouffleSettings.cs) | 移 Core，POCO + `SettingsStore`；移除 `ChatRenderer`；加 `UiRenderer`/`UiAccent*`/`UiSidebarCollapsed` |
| [SoufflePet.csproj](../SoufflePet/SoufflePet.csproj) | 引用 Core；`BuildWebUi`；`wwwroot/app` |
| [LocalFileServer.cs](../SoufflePet/Services/LocalFileServer.cs) | MIME/缓存头/`/local-file` |
| [README.md](../README.md) / [AGENTS.md](../AGENTS.md) | React/Core 构建、SPA 布局、目录说明 |
| 其余 `Services/*.cs` | 按 §7.3 搬迁/改写命名空间与接口 |

**删除（P4）**

- `Views/ChatWindow.axaml(.cs)`、`Views/SettingsWindow.axaml(.cs)`、六个设置 `*View.axaml(.cs)`
- `Views/WebViewPetWindow.cs`、`Controls/MarkdownTextBlock.cs`、`Utils/MarkdownParser.cs`
- `wwwroot/index.html`、`wwwroot/chat/**`、`wwwroot/theme.css`
- `Models/ChatRenderMode.cs`、`--webview` / `--chat-webview`
- `ViewModels/ChatViewModel.cs`、`AskUserViewModel.cs` 等对话 VM；六个设置面板 VM
- 六个设置窗口服务（并入 `AppWindowService`）
- Core 迁移完成后旧位置的同名服务副本

## 10. 风险与对策

| # | 风险 | 影响 | 对策 |
| --- | --- | --- | --- |
| R1 | WebView2 Runtime 缺失 | 主窗口开不出来 | 启动检测 + 提示；迁移期 `--legacy-ui` |
| R2 | 重写 `ChatViewModel` 行为回归 | 对话错乱/丢消息 | 特征测试（假流/工具/审批/Ask/取消）；`conversations.jsonl` 回放；灰度 |
| R3 | 多会话迁移丢历史 | 用户数据丢失 | 迁移前 `.bak`；索引与文件双写校验；启动自检失败则只读挂载旧文件 |
| R4 | 侧边栏/响应式复杂度 | 窄屏不可用、布局抖动 | 五档宽度截图验收；容器查询；抽屉 + 遮罩；最小 400 px 手动测试 |
| R5 | 单窗口承载设置后返回路径丢失 | 用户找不到对话 | 侧栏常驻/抽屉；`nav.goto` 与浏览器后退（hash）可用 |
| R6 | 线程模型改变 | 竞态/UI 卡顿 | Core 单队列；事件按 `sessionId` 过滤；宿主统一 marshal |
| R7 | 双构建链（.NET + Node） | 贡献者门槛 | MSBuild target + `SkipWebBuild` + README |
| R8 | 生成产物不同步 | “改了不生效” | `pnpm dev`；构建前强制 build；版本参数 |
| R9 | 插件/脚本兼容 | 加载失败 | SDK/ABI 不变；加载器先搬不改逻辑 |
| R10 | IME/焦点/拖放 | 输入异常 | composition 标志；`Activate()+Focus()`；拖放 v1 不做 |
| R11 | 主题对比度/gamut | 文字看不清 | gamut map + WCAG 校验 + 自动夹 L |
| R12 | 首帧闪烁/窗口不同步 | 视觉跳变 | URL 注入 + snapshot 权威同步 + 显示补发 |
| R13 | 范围膨胀 | 工期失控 | 先搬后改；每阶段可停；非 UI 服务不重写算法 |
| R14 | 供应链/许可 | GPL 合规 | MIT/Apache 依赖；锁文件入库；`pnpm audit` |

## 11. 验收

- **布局**：1416 / 1024 / 800 / 652 / 420 五档宽度；侧栏常驻/收起/抽屉/遮罩；标题栏拖拽/双击最大化/三键；8 向缩放；记住尺寸/位置/开合。
- **会话**：新会话、切换、重命名、删除、重启恢复、首条消息生成标题、旧 `conversations.jsonl` 迁移；搜索过滤。
- **对话**：欢迎页/输入卡/发送/停止/流式 Markdown/表格/代码/KaTeX/Mermaid/推理/工具/审批/用量/模型/工作区/AskUser/附件/长会话滚动/复制。
- **设置**：七面板 CRUD + 校验 + 持久化；插件动态表单；MCP 测试；Cron；模型导入/移除/切换。
- **主题**：预设/自定义即时生效（<100 ms）；重启保持；“跟随系统”生效；对比度校验；首帧无闪烁；除 tokens.css 外无颜色字面量。
- **后端**：Core 单测（会话状态机/管理/序列化/调度）；`conversations.jsonl` 回放；插件示例加载。
- **回归**：Live2D 表情/动作/口型/手柄/目光/关屏/右键菜单；托盘/热键/停靠/点击穿透。
- **性能**：隐藏 10 分钟工作集回落；首开 ≤1 s（骨架）；流式 30 tok/s 不卡。

## 12. 工作量

| 轨道 | 阶段 | 估算 |
| --- | --- | --- |
| 后端 | P0 契约 3 + B1 Core 7 + B2 Session 8 + B3 会话 5 + B4 API 5 + P4 清理 4 + P5 3 | 35 人日 |
| 前端 | P1 壳 6 + P2 对话 9 + P3 设置 9 | 24 人日 |

- 单人顺序：约 **59 人日 ≈ 12 周**。
- 两人（后端/前端）并行：约 **7–8 周**（P0 后分轨，P4/P5 汇合）。
- 若砍掉多会话（侧栏只显示当前会话）：约 **54 人日**，但会与 DSH 式侧栏的信息密度不匹配。

## 13. 回退与灰度

1. **版本回滚**：后端重构不做双运行时；独立分支推进，合并前打 tag，出问题整版回退。
2. **迁移期适配器**：B2 后旧原生窗口通过 `LegacyChatViewModel` 可用，直到 P4；`--legacy-ui` 可切回旧 UI。
3. **前端灰度**：`UiRenderer = React | Legacy`，默认 React；WebView2 初始化失败提示并回退。
4. **数据级**：会话迁移前备份；旧格式只读兼容；不改 `statistics.json`。

## 14. 决策记录

| # | 决策 | 结论 |
| --- | --- | --- |
| D1 | 是否上 React | 上；单页应用 |
| D2 | 前端栈 | React 19 + Vite + TS + Tailwind v4 + zustand + Radix |
| D3 | 窗口模型 | **一个原生桌宠窗口 + 一个 React SPA 窗口**；设置不再单独开窗 |
| D4 | 布局 | 参考 DSH：侧栏 + 对话区；≥1024 常驻 280 px，<720 抽屉；容器查询 |
| D5 | 多会话 | 纳入本轮：`ChatSessionManager` + 侧栏历史列表 |
| D6 | 后端重构 | 新建 `SoufflePet.Core`；`ChatViewModel` → `ChatSession`；Services 分层迁移 |
| D7 | 状态归属 | `ChatSession` 为唯一状态源；React 只镜像；设置走 `SettingsApi` |
| D8 | 线程模型 | Core 单队列 + 事件流；宿主 marshal 到 UI 线程；Core 不依赖 Avalonia |
| D9 | 桥接 | WebMessage JSON-RPC（`v:1`）+ 快照/增量 + `rev/resync` |
| D10 | 色彩 | OKLCH；`UiAccentColor` + 派生；`settings.appearance.*` + `host.theme`；跟随系统 |
| D11 | 回退 | 分支/tag + 迁移期 `LegacyChatViewModel` + `UiRenderer` 灰度 |
| D12 | 构建 | MSBuild `BuildWebUi` + `SkipWebBuild`；发布前必须构建 Web |

## 15. 实施记录

### M0 契约 + 前端骨架（2026-11-05）

- 冻结协议：[react-ui-contract.md](react-ui-contract.md)（v1；传输/错误码/DTO/方法/事件/ops/存储/安全）。
- 新建 `SoufflePet/web`：Vite 7 + React 19 + TS strict + Tailwind 4 + zustand + Radix + lucide；`base='/app/'`，产物 `wwwroot/app`。
- `styles/tokens.css` 是唯一色值来源（OKLCH + 相对颜色）；`src/theme/oklch.ts` 做 sRGB↔OKLCH、gamut mapping、WCAG；`src/theme/boot.ts` 在 React 前按 URL 注入强调色；`scripts/check-colors.mjs` 构建时检查字面量。
- 应用壳：TopBar / Sidebar / 容器查询（≥1024 280px、720–1023 240px、<720 抽屉）/ 会话分组搜索 / 更多设置 popover / 自定义标题栏与 8 向缩放边带。
- 桥：`bridge/client.ts`（req/response、event、snapshot、rev 跳号 resync、超时、大载荷 URL）、`bridge/mock-host.ts`（全方法 mock + 富消息/500 条/流式/Ask 场景）。
- 构建：`SoufflePet.csproj` 的 `BuildWebUi`（增量 Inputs/Outputs）+ `SkipWebBuild`；`wwwroot/app` 产物复制到 bin/publish；README/AGENTS 补充 Node/pnpm 前置与命令。

### M1 对话区（mock）（2026-11-05）

- 欢迎页/输入卡（工作区、审批、模型、附件、发送/停止、IME 守卫、退格删附件）。
- 消息区：虚拟列表 + 滚动贴底/回到最新；用户/助手/思考/工具/提醒/审批/附件七类气泡；Markdown（GFM + 表格横滚 + KaTeX + Shiki 懒加载 css-variables 主题 + 净化 + 链接拦截）。
- 用量胶囊/浮窗、全局用量、模型/审批/工作区/AskUser 浮层、Toast、会话重命名/删除二次确认。
- 验证：`pnpm typecheck`、`check:colors`、`pnpm test`（7 文件 24 用例）、`pnpm build`；`pnpm smoke`（500 条长会话、流式发送、设置路由）；`pnpm screenshots` 生成 `.verifybuild/ui` 的 welcome/rich/rich-top/ask/settings × 五档宽度；`dotnet build SoufflePet.sln`（含 web）与 `-p:SkipWebBuild=true` 均 0 警告。

### M2 宿主窗口与桥（2026-11-05）

- `Views/Web/AppWebWindow.cs`：单例 WebView2 主窗口（1120×760 / 最小 400×520），关闭=隐藏、`ui-window.json` 记忆几何、首次停靠桌宠；页面消息驱动标题栏拖拽、8 向缩放、最小化/最大化/关闭。
- `Services/Web/WebBridgeServer.cs`：v1 req/response/event/snapshot、rev、256 KB 以上走 `/state`、页面就绪事件、错误映射；`BootstrapRpc.cs` 提供启动期快照、窗口命令、外观设置与空会话占位。
- `Services/Web/ThemeService.cs`：DWM/注册表系统强调色、预设/预览/跟随系统、`host.theme` 广播；sRGB→OKLCH 与页面同公式。
- `Services/LocalFileServer.cs`：补齐 CSS/JS/MJS/字体/WASM 等 MIME、`Cache-Control: no-store`、对 `/app/*` 下发 CSP、`/local-file` 与 `/state` 短期 token。
- `ChatWindowService` 按 `UiMode` 延迟创建 React 窗口；WebView2 初始化失败回退旧窗口；新增 `--react-ui`、`--legacy-ui`、`--web-dev`、`--open-ui`。
- 验证（2026-11-05）：
  - `--react-ui --open-ui` 启动后 localhost 页面加载、`bridge hello`、snapshot rev、欢迎页渲染；CDP 探针确认 `invokeCSharpAction`/`__bridge.receive` 均在。
  - 页面按钮实测：最大化 → 2560×1380 工作区、还原 → 1502×950、关闭 → 隐藏且进程存活；边缘拖拽 → 1502×950、标题栏拖拽 → 位置变化。
  - `WM_CLOSE` → `ui-window.json` 写入 1201.6×760 @ 478,296；重启恢复为 1502×950 @ 478,296；`WM_HOTKEY` 重新显示。
  - `settings.appearance.setAccent('#7A5CF0')` → 页面 `--accent` 变为 `oklch(0.5619 0.2120 287.27)`，reset 恢复琥珀；`chat.send` toast 可见；CDP 页面无 console error。
  - `--legacy-ui --open-ui` 不启动静态服务器、不加载 SPA，旧窗口路径可用。
- 已知限制：M2 的 `BootstrapRpc` 只占位，真实对话/会话/设置 API 在 M4/M5；窗口暂无 DWM 圆角/阴影，M8 再收口（`BootstrapRpc` 已在 M4 被 `ChatRpc` 取代）。

### M3 Core 骨架（2026-11-05）

- 新增 `SoufflePet.Core`（net10.0，**无 Avalonia 引用**）与 `SoufflePet.Core.Tests`（xUnit），两者已加入 sln。
- 搬迁纯模型（除旧 `ChatMessage`/`ChatDisplayAttachment`）与纯服务 33 个文件：`ChatMemory`、`ChatTools`、`LlmChatService`、`LlmModelCatalog`、MCP/技能/插件、`PlatformService`、`StatisticsService`、`Live2DModelService`、`ScheduledTaskService` 等；迁移期保留原命名空间，减少旧 UI 改动。
- 新增平台抽象：`IPetController`、`IScreenCapture`、`IBrowserService`、`IUiDispatcher`；宿主提供 `PetExpressionService`/`ScreenCaptureService`/`BrowserService`/`AvaloniaUiDispatcher`。
- `ScheduledTaskService` 重写为 `System.Threading.Timer` + `IUiDispatcher`（不再引用 Avalonia Dispatcher），日志改为 `IReadOnlyList` + `LogsChanged`；旧任务面板投影到自己的 `ObservableCollection`，行为保持。
- `CoreServiceCollectionExtensions.AddSouffleCore()` 统一注册；宿主只保留 Avalonia/窗口/托盘/热键/WebView/平台实现。
- `ChatMemory` 增加测试用内部构造（临时根目录），避免测试污染 `%AppData%`。
- 验证：
  - `dotnet build SoufflePet.Core`、`dotnet build SoufflePet.sln -p:BaseOutputPath=.verifybuild\bin\`：**0 警告 / 0 错误**。
  - `dotnet test SoufflePet.Core.Tests`：**9 用例全绿**（token 格式、记忆落盘/恢复/损坏行/档案、调度下次时间与预览）。
  - Core 源码 grep `using Avalonia`：无（仅注释提及）。
  - 运行时：`--react-ui --open-ui` 与 `--legacy-ui --open-ui` 均正常启动，记忆/统计/调度/插件/LLM 服务由 Core 解析，无 DI 错误。
- 已知限制：`ChatMessage`/`ChatDisplayAttachment` 仍依赖 Avalonia Bitmap，按计划留到 P4；`SouffleSettings` 仍为 INPC（B4 再 POCO 化）；调度器的 UI 线程依赖通过 `IUiDispatcher` 保持可测试。

### M4 ChatSession 与会话管理（2026-11-05）

- Core 新增 `Chat/`：
  - `ChatSession`：单会话状态机（消息/草稿/附件/模型/审批/工作区/用量/Ask），串行生成队列，增量 `ChatOp` 事件；工具审批/Ask/展示文件按 `AsyncLocal` 当前轮次路由，多会话不会串台。
  - `ChatSessionManager`：`sessions.json` 索引、新建/打开/重命名/删除/搜索，统一汇聚各会话事件；打开历史在线程池执行，首会话构造期预载保证首个快照非空。
  - `ChatSessionStore`：`sessions/<id>/conversation.jsonl`、`window.json`、`sessions.json`；删除移入 `sessions/.deleted`；旧 `conversations.jsonl` 首次迁移为默认会话并保留只读 `.bak`；索引损坏按目录重建。
  - `ChatTriggerRouter`：平台消息/定时任务/戳一戳/摸头路由到活动会话（仅 React UI 解析，旧窗口仍由 `ChatViewModel` 订阅）。
  - 会话内 `ChatMemory` 使用独立对话文件，`profile.json` 全局共享；保存档案时合并其他会话刚写入的条目。
- 宿主新增 `Services/Web/ChatRpc.cs` 取代 `BootstrapRpc`：`session.*` / `chat.*` 全部接入 Core；Core `ChatOp` 投影为契约 JSON（含附件 `/local-file` 图片 URL）；`IAppWindowCommands` 增加文件/目录选择与剪贴板图片读取。
- `ChatWindowService` 不再构造期解析 `ChatViewModel`：React 模式不创建旧视图模型，避免平台提醒/定时任务双消费。
- 修复 M2 潜在问题：超过 256 KB 的 snapshot/event 经 `/state` 时只登记 payload（此前登记整个 envelope，页面拿到后丢字段导致大快照白屏）；`/local-file` token 延长到 12 小时，聊天记录滚动重渲染不会 404。
- 前端：`Composer` 发送成功后清空本地草稿，与宿主清空一致。
- 数据隔离：`SOUFFLEPET_DATA_DIR` 可指定应用数据根目录（验收/自动化测试用），`ChatMemory.DefaultRoot` 统一供会话、统计、调度日志、窗口几何使用。
- 验证：
  - `dotnet build SoufflePet.sln`：**0 警告 / 0 错误**（含 web 构建）；`dotnet test SoufflePet.Core.Tests`：**20 用例全绿**（新增 11 个会话/管理器/序列化用例：流式落盘恢复、审批、多会话审批路由、Ask、停止、工具气泡、附件消费与 @ 路径、CRUD+迁移+回收站、活动会话删除回退、档案共享、op 判别字段）。
  - `pnpm --dir SoufflePet/web verify` 通过（typecheck + 颜色检查 + 24 Vitest + build）。
  - 真实 WebView2 端到端（隔离数据 + 本地假 OpenAI 流）：120 条迁移会话 → 大快照走 `/state` 正常渲染；侧栏新建、首条消息生成标题、流式 Markdown 回复、输入框清空、停止生成后部分文本保留、切回迁移会话、非活动会话删除进 `.deleted`、刷新后 `conversation.jsonl` 恢复；页面无 console error，截图见 `.verifybuild/ui/m4-*.png`。
  - `--legacy-ui --open-ui` 冒烟：旧窗口仍创建、`ChatViewModel` 正常加载记忆；React 模式不构造旧视图模型。
- 已知限制：设置面板仍是 M2 外观接口 + M5 占位；React 会话不再追写旧 `conversations.jsonl`（旧窗口回退时仍使用旧文件）；多会话共用进程工作目录，切换工作区仍以最后一次切换为准；DWM 圆角/阴影留到 M8。

### M5 设置 API 与七页（2026-11-05）

- 宿主新增 `Services/Web/SettingsApi.cs` 与 `SettingsDto.cs`：`settings.llm.*`、`settings.mcp.*`、`settings.skills.*`、`settings.plugins.*`、`settings.tasks.*`、`settings.models.*`、`settings.appearance.*` 全部走 Core/宿主服务；`ChatRpc` 按 `settings.` 前缀统一转发。
- 设置能力：
  - LLM：提供商/模型清单 CRUD、启用模型、默认模型、拉取模型列表；支持重命名（可选 `previousName`）。
  - MCP：Stdio/Http 表单、增删改、连接测试、工具列表、保存后重置连接。
  - 技能：扫描目录、启停（`DisabledSkills`）、详情内容、打开目录。
  - 插件：启停、真实 `configSchema` 动态表单（text/multiline/number/boolean/select/list）、配置保存、zip 导入。
  - 定时任务：Once/Daily/Weekly/Interval 表单、增删改、启停、立即执行、下次触发、日志/清空。
  - Live2D：列表/校验/切换/移除；目录导入（临时 zip）与压缩包导入。
  - 外观：8 个 OKLCH 预设、自定义颜色 + L/C/H 滑杆、跟随系统、恢复默认、对比度提示。
- 前端新增 `features/settings/{LlmSettings,McpSettings,SkillsSettings,PluginsSettings,PluginConfigForm,TasksSettings,ModelsSettings,AppearanceSettings,SettingsShared}`；`SettingsPage` 七页路由切换；新增设置页表单/列表/色板/日志样式。
- 旧 Avalonia 设置窗口与 ViewModel 暂保留到 M7（默认 Legacy 窗口仍需要入口），M5 只替换 React SPA 的设置页。
- 验证：
  - `dotnet build SoufflePet.sln`：**0 警告 / 0 错误**；`dotnet test SoufflePet.Core.Tests`：**21 用例全绿**。
  - `pnpm --dir SoufflePet/web verify` 通过：typecheck + `check:colors` + **28 Vitest**（新增设置 helper 4 例）+ build。
  - mock 宿主逐页：七页均渲染、无 M5 占位、LLM 编辑/模型清单、MCP 测试连接 3 工具、技能详情、插件动态表单 3 字段、任务编辑/日志、模型列表、外观预设色相切换；截图 `.verifybuild/ui/m5-*.png`。
  - 真实 WebView2 宿主（隔离数据、配置前后备份恢复）：新增/保存/删除 LLM 提供商（2→3→2）、MCP 服务器（增删）、定时任务（增删）；技能启停与详情；DSML 插件 schema 读取与配置保存；模型校验/切换；外观预设与恢复默认；页面无 console error。

### M6 真实接线与默认 React（2026-11-05）

- 默认渲染器切换：`SouffleSettings.UiRenderer` 默认 `React`；启动时不再用旧配置里的 `Legacy` 选择渲染器，只有 `--legacy-ui` 才强制旧窗口（`--react-ui` 继续兼容）。
- WebView2 初始化失败回退旧窗口时，`ChatWindowService` 同时关闭 `ChatTriggerRouter`，避免平台提醒/定时任务被 React 会话与旧 `ChatViewModel` 重复消费。
- 设置联动：`ChatRpc` 处理 `settings.llm.*` 成功后调用 `ChatSession.RefreshModels()`，对话页模型选择器立即收到 `models` op；当前模型失效时回落到 `ChatModelKey` 或空。
- 修复消息虚拟列表重叠：`MessageList` 每次消息/文本变化都调用 `virtualizer.measure()` 会清空已测高度，而挂载时的 `measureElement` 不会重新执行，导致所有项按 104px 估算定位（审批气泡与下一条回复重叠）。移除该重置，交给 `measureElement`/ResizeObserver 维护高度。
- 验证：
  - `dotnet build SoufflePet.sln`：**0 警告 / 0 错误**；`dotnet test SoufflePet.Core.Tests`：**21 用例全绿**；`pnpm --dir SoufflePet/web verify` 通过（28 Vitest）。
  - 默认启动（不带 `--react-ui`，配置里仍写 `UiRenderer=Legacy`）：日志 `reactUi=true`，SPA 加载。
  - 真实流式：本地假 OpenAI 服务返回 36 tok 用量；助手消息 Markdown 正常、输入框清空、用量胶囊显示；停止生成保留部分文本。
  - 设置新增提供商/模型后回到对话，模型选择器即时出现新模型（无需刷新/重开窗口）。
  - 真实工具审批：模型发起 `run_shell_command` → 审批气泡 → 允许执行 → 工具结果气泡 → 第二轮“审批链路正常”回复；修复后消息无重叠，刷新后会话恢复。
  - 工作区切换 `chat.workspace.switch` 后工作区 chip 即时更新；页面无 console error。
  - `--legacy-ui --open-ui`：日志 `reactUi=false`、不启动静态服务器，旧窗口路径可用。
  - 截图：`.verifybuild/ui/m6-*.png`。

### M7 清理旧 UI（2026-11-05）

- 删除旧 Avalonia UI 与迁移期适配层：`Views/ChatWindow.axaml(.cs)`、`SettingsWindow`、六个设置 `*View.axaml(.cs)`、`WebViewPetWindow`、`Controls/MarkdownTextBlock`、`Utils/MarkdownParser`、`Models/ChatMessage`、整个 `ViewModels/`（对话/提问/六个设置面板及模型选择辅助类）、六个设置窗口服务、`Styles/*.axaml`、`ViewLocator`、`wwwroot/index.html`。
- `ChatWindowService` 由 `Services/AppWindowService.cs`（`IAppWindowService`）取代：单例 `AppWebWindow`、`Ctrl+Alt+Space`、首次停靠/关闭隐藏、戳一戳/摸头唤醒。删除旧窗口回退；WebView2 初始化失败只记录错误并用系统消息框提示一次。
- `UiMode` 收口为 `WebDev` 一个字段；删除 `--legacy-ui`、`--react-ui`、`--webview` 与 `SouffleSettings.UiRenderer` / `UiRenderer` 枚举。旧 `SoufflePet.json` 里的 `UiRenderer` 键反序列化时被忽略。
- `ChatTriggerRouter` 删除 `Enabled/Disable`：React 会话是平台提醒/定时任务/戳一戳的唯一消费者。
- `NativePetWindow` 直接注入 `IAppWindowService`；托盘「对话窗口」与热键走同一服务；`App.axaml.cs` 只注册 `NativePetWindow` + `AppWindowService`，不再注册任何旧 VM/设置窗口。
- 文档同步：README/AGENTS 重写运行方式、目录与入口；契约移除 `UiRenderer` 并追加 M7 差异；`docs/webview-chat.md` 标记为历史存档。
- 验证：
  - `dotnet build SoufflePet.sln`：**0 警告 / 0 错误**；`dotnet test SoufflePet.Core.Tests`：**21 用例全绿**；`pnpm --dir SoufflePet/web verify`：typecheck + 颜色检查 + **28 Vitest** + build 通过。
  - 真实 WebView2（隔离数据）：SPA 欢迎页渲染；七个设置页逐页可达且无占位/console error；截图 `.verifybuild/ui/m7-welcome.png`、`m7-settings-*.png`。
  - 热键：窗口可见时 `WM_HOTKEY(0x5C41)` → 隐藏并写入 `ui-window.json`；再次触发 → 恢复显示。
  - 戳一戳/摸头：桌宠右键菜单「戳一戳」→ 主窗口自动显示、`statistics.json` 的 `PokeCount` +1、对话页出现「你被戳了一下」；「摸摸头」→ `HeadPatCount` +1、出现「摸摸头」提醒；截图 `.verifybuild/ui/m7-poke.png`。
  - 旧配置兼容：在测试用 `SoufflePet.json` 中加 `"UiRenderer": "Legacy"` 后仍正常启动并加载 SPA（未知键被忽略）。
  - 清理：临时模型副本、隔离数据目录与探针脚本已删除，无残留进程。
- 已知限制：WebView2 缺失时主窗口无法打开（只有系统提示与日志），这是去掉旧 UI 回退后的预期行为；DWM 圆角与隐藏窗口采样已在 M8 完成。

### M8 性能与质量（2026-11-05）

- 安全导航：`AppWebWindow` 只允许 SPA 同源顶层导航；跨源 http/https 交给系统默认浏览器，其它 scheme 直接取消；`NewWindowRequested` 不在应用内开新窗口。页面默认右键菜单在非输入/可编辑区且无文本选区时禁用，保留复制与输入。
- Windows 11 DWM 圆角：无边框主窗口打开时设置 `DWMWA_WINDOW_CORNER_PREFERENCE = ROUND`（低于 22H2 自动忽略）。
- RPC 打点：`WebBridgeServer` 对耗时 ≥16 ms 的方法记录 Debug 日志，便于定位设置保存/MCP/模型导入等慢请求。
- 响应式矩阵重跑：`.verifybuild/ui/{welcome,rich,rich-top,ask,settings}-{1416,1024,800,652,420}.png` 共 25 张，无页面错误。
- 隐藏窗口工作集：主窗口隐藏后连续采样 10 分钟（见 `.verifybuild/m8-hidden-ws.csv`）：前 6 分钟约 173–179 MB，之后升到约 206–209 MB 并稳定，未持续增长；WebView2 子进程未单独计入该数值。没有引入定时 `GC.Collect` 强制回落，避免运行期卡顿。
- 验证：`dotnet build SoufflePet.sln` 0 警告/0 错误；`dotnet test SoufflePet.Core.Tests` 21 用例；`pnpm --dir SoufflePet/web verify` 28 Vitest；真实 WebView2 导航探针确认 `about:blank` 顶层跳转被取消、`window.open` 不新增页面、源内 hash 正常、内容区右键 `defaultPrevented=true` 而输入框为 `false`；DWM 属性读取为 `2`（ROUND）；`pnpm screenshots` 五档矩阵通过。

### 与 v4 方案的差异（M0–M8 已落实）

- 计划 §5.3 的窗口几何 `window.json` 改为后续 M2 的 `ui-window.json`；`window.json` 继续存历史窗口轮数。
- 首帧注入从 `index.html` 内联脚本改为 `src/theme/boot.ts`（main 的第一个 import），满足 CSP 不外链/不内联脚本；仍保证 React 挂载前写入。
- M4 把计划里的 `SessionRpc` 合并进 `ChatRpc`：会话/对话共享同一个 `ChatSessionManager` 事件出口，单独拆文件会重复接线而不增加隔离。
- M5 的 `saveProvider` / `mcp.save` 增加可选 `previousName`，用于设置页重命名；不传时按名称新增/更新。
- M6 不再读取旧配置里的 `UiRenderer=Legacy`（迁移期默认值）；M7 已删除 `UiRenderer` 字段与 `--legacy-ui` 回退。
- M6 修复 `MessageList` 的虚拟高度重置问题；变量高消息（审批/工具/代码块）现在按实际高度排版。
- M6 设置页修改 LLM 清单后通过 `ChatSession.RefreshModels()` 主动推 `models` op，避免对话页模型选择器停留在旧清单。
- Live2D 的「导入目录」在宿主侧临时打包成 zip 后复用 `Live2DModelService.Import`，避免复制两套解压/登记逻辑。
- 旧 Avalonia 设置窗口/VM 因 Legacy 默认渲染器仍需要入口，M5 时暂保留；M7 已随旧 UI 一起删除。
- `/state` 快照/事件只登记 payload；应用数据根目录支持 `SOUFFLEPET_DATA_DIR` 覆盖，用于自动化验收隔离。
- 原型文件缺失，以 mock + 五档截图作为视觉基线。
- Mermaid 不纳入 v1；KaTeX 纳入。
- 侧栏账户区改为宠物名 + 累计用量 popover + 设置入口。
- M7 删除 WebView 版桌宠与 `--webview`：桌宠固定为原生 Live2DCSharpSDK 渲染，React 主窗口是唯一 WebView2 界面；`ChatTriggerRouter` 不再需要 `Disable`。
- M7 同时删除旧 Avalonia 样式 `Styles/*.axaml` 与 `ViewLocator`（旧视图/VM 已无消费者），`App.axaml` 只保留 `FluentTheme`。
- M8 把外部导航拦截放在宿主 `NavigationStarted/NewWindowRequested`，页面 Markdown 链接仍经 `chat.link.open` 走默认浏览器；右键菜单按“输入框/可编辑区/已有选区”放行，其余禁用，避免影响复制。
- M8 的隐藏窗口采样是“稳定不增长”，没有出现强制 GC 后的断崖式回落；因此没有引入额外的定时 `GC.Collect`，避免运行中引入卡顿。
- 侧栏收起从 v4 的 64 px 图标轨道改为**完全隐藏**（宽度 0），开合按钮固定在标题栏最左侧；<720 px 仍用抽屉，抽屉开合不写入折叠偏好。
- `.verifybuild/ui` 截图是验收产物，不入库。