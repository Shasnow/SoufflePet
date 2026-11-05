# 对话窗口 WebView 重写方案（探索稿）

> **已废弃（历史存档）**：本方案最终未采用「原生 JS + 旧窗口共存」路线，实际落地的是 [React 单页 + Core 重构](react-ui-refactor-plan.md)。文中 `ChatWindow`、`ChatViewModel`、`MarkdownTextBlock`、`WebViewPetWindow` 等文件已在 M7 删除，`--chat-webview` / `ChatRenderer` 也未实现；保留本文仅用于回溯 P0 探针结论与 WebView2 行为记录。
>
> 状态：方案探索，**未改动任何生产代码**。
> 配套视觉原型：[docs/prototype/chat-mock.html](prototype/chat-mock.html)（直接用浏览器打开，模拟 420×620 窗口）。

## 1. 结论（TL;DR）

- **可行，且比预想的便宜**：仓库里已经有一套跑通的 WebView 基础设施（`NativeWebView` + `LocalFileServer` + JSON 消息桥 + 页面内菜单），桌宠 WebView 版就是活例子；对话窗口可以直接复用同一套通道。
- **不需要重写 `ChatViewModel`**：桥接层只做「观察者 + 命令转发」，`ChatViewModel.cs`（1650 行）与 `ChatMessage.cs`（367 行）一行不改，原生窗口与 WebView 窗口可以并存（用于灰度与回退）。
- **最大收益不是"好看"，而是删掉三块长期维护成本**：1142 行 XAML + 640 行窗口代码后端 + 1410 行手写 Markdown 渲染（`MarkdownTextBlock` 621 + `MarkdownParser` 789），换成浏览器原生的排版、滚动、选择、文本编辑与 `overflow-anchor`——现在那套虚拟化锚点校正（`KeepBubbleInPlace` / `KeepViewportInPlace` / `BeginAnchorCorrection`，约 200 行、12 轮布局收敛）可以整块删掉。
- **最大风险是 WebView2 的 airspace（子 HWND 遮挡）**：窗口内的 Avalonia 控件会被 WebView 盖住（探针已复现），页面必须自己画面板与浮层。**`ExperimentalOffscreen` 那条"更优解"已在 P0 排掉**：它确实能消掉 airspace 并把缩放边带还给宿主的 `WM_NCHITTEST`，但窗口会失去透明（整窗变黑），而透明是这个窗口的硬需求。
- **P0 探针已跑完（2026-11-05）**，六项风险全部有结论：① 透明成立 → **走窗口化宿主**，offscreen 因失去透明被否决；② 缩放：WebView 会盖住整个客户区，**最终选定页面侧 resize 协议（面板贴边）**，6 px 内缩边带留作回退；③ 键盘能进 WebView、双 WebView 可共用同一 profile、`InvokeScript` 往返仅 0.2 ms。详见 §6.1。
- **8 条决策已定案（§11）**：默认仍 Native、原生 JS + 单向渲染、Markdown 用 JS 重写、v1 不做拖放、不预热、灰度期双窗口共存、抽公共 `theme.css`。
- 工作量：**约 10–14 人日**（分 5 个阶段，每阶段可独立验收、随时可停），代码净变化大致「新增 ~2700 行、删除 ~2960 行」。
- 建议落地方式：新增 `--chat-webview` 开关与设置项 `ChatRenderer`（`Native` / `WebView`），**默认仍为 Native**；灰度验证通过、页面对齐像素后，再决定是否把默认值翻过来。
- **不引入 Vue/React 构建链**：逐项算下来这一页只能省约 190 行（0.5–1 人日），却要永久承担 Node/Vite/锁文件，并打破 README 里"只需 .NET 10"的构建承诺（仓库无 CI 兜底）。若确实想要声明式，退一步用**免构建**的 Preact + htm；完整推演见 §4.1。
- 附带的视觉原型已经在 Chromium（DSH 内置浏览器）里渲染验证过：玻璃面板、气泡、表格、代码块、用量胶囊与浮窗、提问面板、6 个页面内浮层都能还原，过程中还实测到一条必须记下来的迁移坑——**Avalonia 的 `#AARRGGBB` 在 CSS 里是 `#RRGGBBAA`，照抄会把"半透明白"变成"青色"**（见 R15）。

## 2. 现状盘点

### 2.1 对话窗口（要被重写的部分）

| 文件 | 行数 | 职责 | 重写后 |
| --- | --- | --- | --- |
| [SoufflePet/Views/ChatWindow.axaml](../SoufflePet/Views/ChatWindow.axaml) | 1142 | 标题栏 / 消息区 / 提问面板 / 输入区 / 5 个 Popup / 底部信息栏 | 改成 `chat.html` + `chat.css` |
| [SoufflePet/Views/ChatWindow.axaml.cs](../SoufflePet/Views/ChatWindow.axaml.cs) | 640 | 无边框拖拽、`WM_NCHITTEST` 缩放、拖放/粘贴附件、Enter 发送、自动滚动与锚点校正 | 拆成「宿主窗口行为」+「页面内交互」 |
| [SoufflePet/Controls/MarkdownTextBlock.cs](../SoufflePet/Controls/MarkdownTextBlock.cs) | 621 | 把解析结果拼成 Avalonia 控件树、流式增量更新 | 删除（Native 模式保留） |
| [SoufflePet/Utils/MarkdownParser.cs](../SoufflePet/Utils/MarkdownParser.cs) | 789 | 块级 / 行内 Markdown 解析 | 换成 JS 解析器（约 350 行） |
| [SoufflePet/ViewModels/ChatViewModel.cs](../SoufflePet/ViewModels/ChatViewModel.cs) | 1650 | 会话状态、流式、工具、记忆、统计、工作区、审批、提问 | **不动** |
| [SoufflePet/Models/ChatMessage.cs](../SoufflePet/Models/ChatMessage.cs) | 367 | 气泡数据与展示派生文案 | **不动**（加一层 DTO 序列化） |
| [SoufflePet/Services/ChatWindowService.cs](../SoufflePet/Services/ChatWindowService.cs) | 224 | 单例窗口、全局热键、停靠定位、戳一戳唤醒 | 改成面向 `IChatView`，两套窗口都接 |

### 2.2 已有的 WebView 基础设施

| 设施 | 位置 | 可复用程度 |
| --- | --- | --- |
| `NativeWebView` 控件 | NuGet `Avalonia.Controls.WebView` 12.1.1 | 直接用（`Source` / `InvokeScript` / `WebMessageReceived` / `EnvironmentRequested` / `NavigationCompleted` / `WebResourceRequested`） |
| 静态文件服务器 | [LocalFileServer.cs](../SoufflePet/Services/LocalFileServer.cs)（121 行，`HttpListener`，18923–18932 扫码） | 需要小改：MIME、缓存头、`/local-file` 图片代理 |
| 宿主→页面 | `_webView.InvokeScript("window.onHostXxx && window.onHostXxx(...)")` | 复用，但要改成"批量 + 差量" |
| 页面→宿主 | JS `invokeCSharpAction(JSON.stringify(obj))` → `WebViewReceivedEventArgs.Body` | 复用，同一套 JSON 约定 |
| 用户数据目录 | [WebViewProfile.cs](../SoufflePet/Utils/WebViewProfile.cs)（`%LocalAppData%\SoufflePet\WebView2`） | 复用（注意双 WebView 同目录的坑，见风险 R2） |
| 页面内菜单/浮层写法 | [wwwroot/index.html](../SoufflePet/wwwroot/index.html) 的 `#menu` | 直接照抄思路（airspace 规避） |
| 窗口拖拽 | [WebViewPetWindow.cs](../SoufflePet/Views/WebViewPetWindow.cs) 的 `{"type":"drag"}` → `WM_NCLBUTTONDOWN` + `HTCAPTION` | 直接复用，并扩展成 8 个缩放方向 |

## 3. 换与不换

### 3.1 换的价值

| 收益 | 说明 |
| --- | --- |
| 排版能力 | 表格、嵌套列表、代码高亮（CSS `pre` + 等宽字体）、引用、图片、`<details>` 折叠，全部由浏览器排版引擎负责；现在这些是手写控件树换来的 |
| 删除脆弱代码 | 虚拟化 + 估算高度 + 锚点校正约 200 行、5 个 `ContentControl` 载体类属性（`PlainPanel` / `ReasoningPanel` / `ToolPanel` / `UsageSection` / `UsagePanel`）只为绕过控件实例化成本而存在；浏览器里 `content-visibility: auto` 一行搞定 |
| 样式迭代成本 | 改配色/间距现在是「改 axaml + 重建 + 重启」；改成 CSS 后是「改 CSS + 刷新页面」（`EnableDevTools` + `Cache-Control: no-store`） |
| 交互一致性 | 文本选择、复制、IME、滚动惯性、Ctrl+A/C/V 全部是浏览器原生行为，不用逐个适配 Avalonia |
| 未来富内容 | 图片卡片、按钮式选项、代码折叠、表格排序这类"富消息"在 DOM 里是加法，在 Avalonia 里是乘法 |
| 玻璃质感可以更真 | Chromium 的 `backdrop-filter: blur()` 能把浮层背后的页面内容糊掉（原型里已加），浮窗从"半透明色块"变成"磨砂玻璃"；Avalonia 没有等价能力 |
| 与桌宠同源 | 桌宠已是 WebView 版路线，对话窗口跟着走，两套页面的调试、字体、主题 token 可以共享 |

### 3.2 换的代价

| 代价 | 量化 / 说明 |
| --- | --- |
| 启动与内存 | WebView2 首次初始化 ~0.3–0.8 s、常驻 ~80–150 MB；原生模式下用户第一次按 `Ctrl+Alt+Space` 会有可感知延迟（需要预热或骨架屏） |
| 桥接往返 | `InvokeScript` 是跨进程调用，流式正文必须按 ~40 ms 合并推送（桌宠页面已做过同样的脏检查/节流） |
| airspace | WebView2 是子 HWND，**窗口内**的 Avalonia 控件（气泡、标题栏、覆盖层）会被它盖住 → 页面自己画面板与浮层；**但独立顶层窗口的 Popup / ToolTip 不受影响**（探针实测能盖在 WebView 上） |
| 窗口外观 | 透明、圆角、投影要靠「窗口透明 + 页面 CSS」实现，比现在的 Avalonia 画 Border 更依赖 WebView 后端的透明支持 |
| 新技能栈 | 维护者需要懂一点 CSS/原生 JS（不加构建链、不引入前端框架） |
| 依赖运行时 | 需要 WebView2 Runtime；Win11 自带，Win10 可能缺失 → 必须有 Native 回退 |

### 3.3 什么时候不该换

如果近期重点只是「再调几个颜色、加一两种气泡」，**继续用 Avalonia 更划算**；这次重写的价值要在"富内容/排版正确性/长期维护"上才收得回来。

## 4. 方案对比

| | A. 全页重写（推荐） | B. 仅消息区混合 | C. 保持原生 |
| --- | --- | --- | --- |
| 做法 | 整个对话窗口内容换成 `chat.html`，窗口只剩 `NativeWebView` | Avalonia 保留标题栏/输入区/浮层，只把消息 `ItemsControl` 换成 WebView | 现状 |
| 删除的代码 | XAML 1142 + 代码后端 640 + Markdown 1410（全部退场） | 仅消息模板与 Markdown | 0 |
| 浮层 | 页面内 DOM，天然不受 airspace 影响 | Avalonia Popup 是独立顶层窗口，实测**能**盖住 WebView；但圆角拼接、焦点在两个键盘世界之间来回切仍然麻烦 | 无 |
| 透明/圆角 | 整窗一个 WebView，CSS 圆角 + 窗口透明 | 窗口内出现"WebView 矩形 + Avalonia 面板"拼接，圆角处必然有缝隙/直角 | 好 |
| 输入法/复制 | 浏览器原生 | 输入区仍 Avalonia（原生 OK），但焦点在 WebView 与 Avalonia 之间来回切，Esc/快捷键易丢 | 好 |
| 工作量 | 10–14 人日 | 6–8 人日（但会留一堆过渡态问题） | 0 |
| 结论 | ✅ | ❌（省下的 4 人日会在浮层与拼接上还回去） | 保底 |

另有两个**不推荐但值得记录**的选项：

- **`NativeWebDialog`**（同包）：网页在独立原生窗口里，宿主拿不到 Avalonia 的透明/置顶/停靠定位，且无法与桌宠窗口共享 `Bounds` 计算 → 不适合。
- **引入 Vue/React + 构建链**：结论见 §4.1（不建议；若只要声明式，有免构建的中间档）。

### 4.1 要不要引入 Vue / React（以及构建链）

这个问题表面是"用不用框架"，实际是**三个决定被捆在一起**，必须拆开算：

| 决定 | 内容 | 我们的判断 |
| --- | --- | --- |
| ① 声明式组件模型 | 状态变了自动更新 DOM，而不是手写 `createElement/appendChild/patch` | 有一点用，但这一页的 DOM 操作很窄 |
| ② 构建链 | Vite/SFC/TS/打包产物入库或由 MSBuild 触发 `pnpm build` | **代价主要在这里**，且会破坏"只需 .NET 10"的承诺 |
| ③ 依赖政策 | AGENTS.md：不引入新依赖除非确有必要；README 现状：`dotnet build SoufflePet.sln` 是唯一构建入口，无 CI | 引入后需长期维护 node/pnpm/锁文件 |

#### 4.1.1 量化：这一页到底能省多少

按 §9 的拆分逐项估算（页面总量约 1850 行 = html 200 + css 600 + js 700 + markdown 350）：

| 页面里的活 | 原生 JS | Vue/React | 差值 |
| --- | --- | --- | --- |
| 消息 id ↔ DOM 映射、4 种结构 op（add/insert/remove/patch） | ~120 行 | ~60 行（`v-for` + keyed diff） | −60 |
| 流式正文增量更新 | ~30 行 | ~10 行（`text +=` 自动重绘） | −20 |
| 6 个浮层的开关 + 贴边定位 | ~120 行 | ~60 行（`v-if` + style 绑定；**定位仍要手量**） | −60 |
| 提问面板（分页 / 选项 / 自由输入 / 提交态） | ~120 行 | ~70 行（响应式数组） | −50 |
| 附件 chips / 输入框自适应 / 禁用态 | ~80 行 | ~80 行（`scrollHeight` 仍是手写） | 0 |
| Markdown 解析与渲染 | ~350 行 | ~350 行（**框架不帮忙**，除非再引一个 `marked` 依赖） | 0 |
| 桥接协议、与宿主的命令分发 | ~200 行 | ~200 行（**框架不帮忙**） | 0 |
| 样式 | ~600 行 | ~600 行（SFC 的 `<style scoped>` 稍好组织，但行数不变） | 0 |

**净省约 190 行（≈0.5–1 人日）**，且这笔钱要在 P1 之外先花 0.5–1 人日搭工程、写构建说明。

#### 4.1.2 成本：构建链到底带来什么

| 成本 | 具体表现 |
| --- | --- |
| 工具链承诺被改写 | README 现在写「Windows 10+，.NET 10 SDK」；一旦页面要 `pnpm build`，每个贡献者都要装 Node + pnpm，AGENTS.md 的构建段落也要重写 |
| 产物来源二选一 | **提交 dist**：PR 里混入几百 KB 生成代码、审查困难，且本地忘记 build 就会运行旧页面（"改了不生效"是最难排查的一类 bug，而本地页面恰恰没有版本号提示）；**MSBuild 调 `pnpm`**：`dotnet build` 从此依赖 Node 与首次联网 restore，离线构建/单文件发布都要跟着改 |
| 没有 CI 兜底 | 仓库目前**没有 `.github`**，没有任何流水线能在合并前校验 dist 是否最新 |
| 供应链面变大 | 原生 JS 方案：0 个新依赖。Vite + Vue：`pnpm-lock.yaml` 几十个传递依赖，需要定期安全更新；GPL 项目里 MIT/ISC 许可都没问题，但审查面确实变大 |
| 调试红利打折 | WebView2 里 **Vue/React DevTools 默认不可用**（要开 `--remote-debugging-port` 再用独立客户端连），所以"框架 + devtools"这套 DX 收益在这条链上要大打折扣 |
| 体积 | Vue 3 运行时约 34 KB gzip、React 18（react+react-dom）约 45 KB gzip，相对数百 KB 的手写页面和 450 ms 的 WebView2 初始化可以忽略——**体积不是决策因素** |

#### 4.1.3 什么时候值得上（判据）

满足任意两条再考虑：

1. 把**≥3 个窗口**（设置 / 对话模型 / MCP / 技能 / 插件 / 定时任务）也搬到 Web —— 表单类页面的组件化收益远大于聊天页；
2. 需要**复杂富消息**（交互卡片、可排序表格、diff 视图、图片画廊）；
3. 页面代码预计 **> 5000 行**（这一页 1850，还有 1.7 倍余量）；
4. 团队里有前端同学能承担构建链，且愿意把 Node 写进贡献指南。

现在这四条一条都不满足。

#### 4.1.4 如果只要"声明式"，不要构建链

这条路值得记录：**框架的收益大头是①，而①并不必然需要②**。

| 方案 | 形态 | 体积 | 要不要构建 | 代价 |
| --- | --- | --- | --- | --- |
| **Preact + htm** | 两个 `.mjs`（`preact.mjs` + `htm.mjs`）直接 vendored 进 `wwwroot/chat/vendor/`，`htm` 提供 JSX 风格模板字符串 | ~12 KB | **不要** | 无 SFC / 无类型检查；升级要手工换文件 |
| **Lit** | Web Components + `html` 模板字符串，同样 vendored | ~5 KB | **不要** | 事件/样式约定与原生略不同 |
| Alpine.js | `x-data / x-for / x-text` 写在 HTML 里 | ~15 KB | **不要** | 300 条消息的 `x-for` 列表性能一般，流式更新要额外规避 |

这三个都能保住 `dotnet build SoufflePet.sln` 一把梭，也不需要锁文件。**但不建议现在就用**：它们是"为了声明式而声明式"，而我们这页的 DOM 操作窄到用 30 行 `Map<id, Element>` 就能覆盖。

> 注：`index.html` 里确实从 CDN 拉 Pixi/Live2D——那是模型 SDK 的历史包袱，属于"网络可用时才能显示桌宠"的既有事实，**不应该成为"对话窗口也依赖 CDN/网络"的先例**。

#### 4.1.5 让选择可逆：不管选哪条，都写成单向渲染

真正的风险不是"选错"，而是"写死成没法换"。P1 的页面按下面的形状写，将来换框架只需要替换渲染层，协议、样式、宿主全不动：

```js
// chat.js：一个 store + 一组纯渲染函数
const state = { messages: [], models: [], panels: {}, ask: null, generating: false };

function applyOps(ops) {            // 宿主推来的增量，只改 state
  for (const op of ops) reduce(state, op);
  render();                          // 唯一的重绘入口
}

function render() {                  // 声明式：state → DOM
  renderMessages(state.messages);    // 内部用 Map<id, Element> 做 keyed patch
  renderChrome(state);               // 输入区 / 浮层 / 底部
}
```

这样即使将来换 Vue，也只把 `renderMessages/renderChrome` 换成模板，`applyOps`/`reduce`/桥接协议原样保留。

#### 4.1.6 结论

- **P1–P5 用原生 JS + 单向渲染**：省下的 190 行不值得换来一条常年要维护的构建链，也不值得破坏"只需 .NET 10"的门槛；
- **把"要不要框架"变成"要不要把更多窗口搬上 Web"**：等问题 4.1.3 的判据满足时再上，届时这一页的渲染层可以平滑替换（§4.1.5）；
- 若你更看重声明式写法、且不想放弃零构建，**Preact + htm** 是唯一我认可的折中；其余三个方案都不建议。

## 5. 推荐架构

### 5.1 分层与数据流

```
        ┌──────────────────────────── 进程内（C#） ────────────────────────────┐
        │  ChatViewModel（单例，唯一状态源；本轮、历史、记忆、统计、审批、提问）  │
        │        │ INotifyPropertyChanged / CollectionChanged（只读观察）        │
        │        ▼                                                             │
        │  WebChatBridge（新增）：快照 + 增量、命令分发、附件/剪贴板/对话框代理  │
        │        │ JSON（页面→宿主：invokeCSharpAction；宿主→页面：InvokeScript） │
        └────────┼─────────────────────────────────────────────────────────────┘
                 ▼
        WebChatWindow（新增，Window + NativeWebView）
                 │
                 ├─ http://localhost:{port}/chat.html  ← LocalFileServer（扩展 MIME/缓存/图片代理）
                 └─ 页面：chat.html + chat.css + chat.js + markdown.js（全部无框架、无构建）
```

窗口只负责：`Source` 指向本机页面、无边框窗口行为（拖拽、缩放转发、关闭即隐藏）、焦点、DPI、`Closing` 拦截。

### 5.2 关键决定：桥接层是纯观察者，不改 ViewModel

现有 VM 已经把一切暴露成可观察属性与 `RelayCommand`（`SendCommand` / `StopCommand` / `SelectModel` / `AddAttachments` / `ToggleModelPicker` / `OpenLlmModels` / `SwitchWorkspace` / `RequestDeleteWorkspace` / `CloseWorkspaceOverlay` / `UsageSummaryText` / `UsageDetails` / `AskUser` ……）。因此桥接层不需要新的领域事件：

- **快照**：`ready` 时把 VM 当前状态整体序列化一次（消息、模型、附件、工作区、用量、提问面板）。
- **增量**：订阅 `Messages.CollectionChanged` + 每条 `ChatMessage.PropertyChanged` + VM 自身关键属性，翻译成 §5.4 的 op。
- **命令**：页面发来的 JSON 直接映射到已有的方法/命令，宿主侧不做业务判断。

这样**两套界面可以同时挂在同一个 VM 上**（这对灰度、对照测试、回退都至关重要）。

### 5.3 消息增量同步

- **稳定 id**：桥接层维护 `Dictionary<ChatMessage, string>`，写 `m1/m2/…`；页面所有更新按 id 定位，**不按索引**（裁剪、工具记录插到中间都会让索引漂移）。
- **正文差量**：`ChatMessage.Text` 绝大多数变化是纯追加（流式）。桥接层保留上一条已发送文本：
  - `新.StartsWith(旧)` → 发 `{op:"msg:text", id, mode:"append", text: 新增后缀}`
  - 否则（占位替换、`（已中断）`、错误追加、工具结果就地更新） → 发 `{op:"msg:text", id, mode:"set", text: 全文}`
  - 纯追加的载荷通常只有几个到几十个字节。
- **合并节流**：所有增量进一个并发队列，`DispatcherTimer`（40 ms）一次性打包成 `apply([...])` 发一次。P0 实测单次 `InvokeScript` 只要 0.15–0.2 ms，所以**合并的意义是少触发页面重绘，不是省 IPC**；流式 30 tok/s 时每秒约 25 次推送、页面侧再 60 ms 防抖重排 Markdown。
- **顺序**：一律 `msg:remove` / `msg:insert(index)` / `msg:add` 三种结构变更，页面在指定 index 前插入对应 DOM；`index` 只用于定序，定位仍靠 id。

### 5.4 协议草案（`v:1`）

宿主 → 页面（打包成 `{"v":1,"ops":[...]}`）：

| op | 载荷 | 触发 |
| --- | --- | --- |
| `hello` | `{pet:{name,initial}, title, placeholder, bubbleMax}` | 页面 `ready` 后 |
| `snapshot` | `{messages:[Msg], models, selectedModelKey, approval, workspace, usage, generating, draft, attachments}` | 同上（一次给全） |
| `msg:add` / `msg:insert` | `{index, msg:Msg}` | `Messages.Add` / `Insert` |
| `msg:remove` | `{id}` | 裁剪、`Messages.Remove` |
| `msg:text` | `{id, mode, text}` | 正文变化（差量） |
| `msg:patch` | `{id, patch:{expanded?,pending?,tool?,usage?}}` | 折叠、审批结果、工具结果、用量落盘 |
| `turn` | `{generating}` | `IsGenerating` |
| `panel` | `{name, open}` | 5 个浮层状态 |
| `ask` | `{panel:null \| {...}}` | 提问面板任意变化（整份快照） |
| `usage` | `{summary,total,details}` | 累计用量刷新 |
| `toast` | `{text, level}` | 附件添加失败等提示 |

`Msg` DTO（由 `ChatMessage` 投影而来，新增约 60 行）：

```jsonc
{ "id":"m12", "kind":"assistant|user|tool|reasoning|notice|attachment|approval",
  "text":"...", "time":"09:48", "expanded":false, "pending":false,
  "reasoning":{"done":true},
  "tool":{"name":"run_shell_command","args":"{...}","result":"..."},
  "usage":{"compact":"用量 12.3K tok","total":"12,345 tok","hit":"99.8%",
           "uncached":"50 tok","cached":"12,295 tok","output":"230 tok","model":"deepseek/deepseek-v4.1"},
  "attachment":{"path":"...","name":"...","isImage":true,"imageUrl":"/local-file?token=..."} }
```

页面 → 宿主：

| type | 载荷 | 映射到 |
| --- | --- | --- |
| `ready` | — | 发 `hello` + `snapshot` |
| `send` | `{text}` | `vm.Draft = text` → `SendCommand` |
| `stop` | — | `StopCommand` |
| `draft` | `{text}`（100 ms 防抖） | `vm.Draft`（保持 `CanSend` 一致） |
| `attach:pick` / `attach:paste` / `attach:remove` / `attach:removeLast` | `{id?}` | `StorageProvider.OpenFilePickerAsync` / `Clipboard` / `RemoveAttachment` / `RemoveLastAttachment` |
| `model:select` / `model:manage` | `{key}` | `SelectModel` / `OpenLlmModelsCommand` |
| `approval:mode` / `approval:decide` | `{mode}` / `{id,accept}` | `UseManualApproval` / `ChatMessage.Approve`·`Reject` |
| `workspace:switch` / `pick` / `delete` / `deleteConfirm` / `deleteCancel` | `{path?}` | `SwitchWorkspace` / `UseWorkspace` / `RequestDeleteWorkspace` / `Confirm…` / `Cancel…` |
| `ask` | `{action:"prev"\|"next"\|"select"\|"custom"\|"submit"\|"cancel", payload}` | `AskUserViewModel` 的命令与属性 |
| `open` | `{target:"mcp"\|"skills"\|"plugins"\|"live2d"\|"scheduled"}` | 对应 `OpenXxxCommand` |
| `file:open` / `link:open` | `{path}` / `{url}` | 系统默认程序 / 默认浏览器 |
| `window` | `{action:"drag"}` / `{action:"resize", edge:"l\|r\|t\|b\|lt\|rt\|lb\|rb"}` | `ReleaseCapture` + `WM_NCLBUTTONDOWN`：拖拽用 `HTCAPTION`，缩放用对应 `HT*` 常量；缩放时宿主自己取 `GetCursorPos` 填 `lParam`，页面不必回传坐标 |
| `window:close` | — | `Hide()`（与现在一致：关闭=隐藏） |
| `log` | `{level,msg}` | Serilog（页面异常不回灌宿主，只记日志） |

### 5.5 静态服务器改造（`LocalFileServer`）

| 事项 | 现状 | 改动 |
| --- | --- | --- |
| 路由 | 任意 `wwwroot/*` 文件 | 不变，新增 `chat.html` 等（**注意 `.gitignore` 的 `**/wwwroot/*` 会忽略它们，需补白名单**） |
| MIME | 只有 `.html/.js/.json/.png/.ico`，其余 `application/octet-stream` | 补 `.css`（**必须**，否则 Chromium 拒绝加载样式）、`.svg`、`.woff2`、`.ttf`、`.map`、`.wasm` |
| 缓存 | 无响应头（Chromium 自行启发式缓存） | 统一 `Cache-Control: no-store`（本地页面，宁可每次重取；也免掉"改了 CSS 不生效"） |
| 本地图片 | 无（页面拿不到本地路径） | 新增 `/local-file?token=xxx`：仅允许桥接层登记过的绝对路径（白名单字典 + 一次性/短期 token），`Content-Type` 按扩展名，`Cache-Control: no-store`，**不暴露任意文件读取** |
| 端口 | 18923 起扫码 | 不变，页面 URL 里带 `?port=` 也无所谓（同源） |
| 安全 | 只监听 localhost | 不变；对话内容全部走桥接，页面不提供任何"读取会话"的 HTTP 接口 |

### 5.6 窗体行为（无边框 + 透明 + 缩放）

| 能力 | 现在（Avalonia） | WebView 版做法 |
| --- | --- | --- |
| 透明 / 圆角 | `TransparencyLevelHint=Transparent` + `Border CornerRadius=16` | 窗口透明 + 页面 `body{background:transparent}` + 面板 `border-radius:16px`（**P0 实测：窗口化宿主下逐像素 alpha 正常**） |
| 拖动 | 标题栏 `PointerPressed → BeginMoveDrag` | 标题栏 `pointerdown` → 发 `{action:"drag"}` → 宿主 `ReleaseCapture + WM_NCLBUTTONDOWN/HTCAPTION`（桌宠同款） |
| 缩放 | 父窗口 `WM_NCHITTEST` 返回 8 个 HT 常量 | **面板贴边（已定方案）**：WebView 铺满窗口，页面在最外圈 6 px 检测方向 → 发 `{action:"resize", edge}` → 宿主 `ReleaseCapture()` + `SendMessage(WM_NCLBUTTONDOWN, HTxxx, 当前光标屏幕坐标)`，之后缩放手势交给 `DefWindowProc`；页面同时按方向切 `cursor`（8 种），窗体内不必留透明边带。**回退**：若 8 向下手势不稳，把 WebView `Margin` 改成 6 即可走 P0 验证过的内缩边带（`AttachResizeHitTest` 原样可用） |
| 关闭 = 隐藏 | `Closing` 拦截 | 不变（`window:close` → `Hide()`；页面标题栏只发消息） |
| Esc | 窗口 `KeyDown` | 焦点在 WebView 内、父窗口收不到 → 页面 `keydown` 发 `window:close`；工作区浮层的 Esc 由页面自己处理 |
| 焦点 | `FocusInput()` | `Activate()` + `_view.Focus()`（**P0 实测返回 `false` 但 OS 焦点已进 WebView 容器**，忽略返回值）+ `InvokeScript("document.getElementById('input').focus()")`；页面自己也要 `input.focus()` |
| DPI | Avalonia 自动 | 页面 `devicePixelRatio`；CSS 用 `px` 即可，浏览器按 DPR 光栅化 |
| 置顶 / 不进任务栏 | 窗口属性 | 不变 |
| 预热 | — | 可选：应用启动后延迟创建（`ShowInTaskbar=false`、透明、`WindowState` 隐藏）以消掉首次呼出的 0.5 s |

### 5.7 附件、剪贴板与本地图片

| 场景 | 现在 | WebView 版 |
| --- | --- | --- |
| 选择文件 | `StorageProvider.OpenFilePickerAsync`（Avalonia 对话框） | 页面按钮 → `attach:pick` → 宿主开同样的对话框 → 结果进 `vm.Attachments` → 回推 `attachments` |
| 拖入文件 | `DragDrop.Drop` 拿 `IStorageItem.Path` | **风险项 R7**：WebView2 会自己接住拖放（默认 `AllowExternalDrop=true`），HTML5 `File` 又拿不到绝对路径。v1 先不做拖放（用按钮 + 粘贴），后续用 `CoreWebView2` 的 `postMessageWithAdditionalObjects`（需 COM 互操作，见 R7）或 `AllowExternalDrop=false` + 父窗口兜底做 spike |
| 粘贴图片 | `Clipboard.TryGetBitmapAsync` → PNG → 附件 | 宿主仍从剪贴板取（页面拿不到系统剪贴板里的位图），`attach:paste` 触发 |
| 粘贴文件/文本 | `TryGetFilesAsync` / `TryGetTextAsync` | 文本粘贴交给浏览器原生（`textarea`）；文件走宿主 |
| 气泡里显示本地图片 | `Bitmap.DecodeToWidth` 直接显示 | 宿主登记路径 → 生成 token → 页面 `<img src="/local-file?token=…">`；图片按 260 px 显示、DPR 2x 足够 |
| 打开附件 | `Process.Start(UseShellExecute)` | `file:open` → 宿主同款调用 |
| 链接 | Markdown 链接（`HyperlinkButton`） | `link:open` → `Process.Start`；`NavigationStarted` 拦掉页面内跳页（防止整页被替换） |

### 5.8 Markdown

- **位置**：JS 里解析并渲染成 DOM（约 350 行），范围与现有 C# 解析器对齐：段落 / 标题 / 有序·无序·任务列表 / 引用 / 分割线 / 围栏代码块（带语言标注）/ 表格（对齐、`\|` 转义）/ 行内粗体·斜体·删除线·代码·链接。
- **为什么不复用 C# 解析器**：它在 `MarkdownBlock` 记录里带 Avalonia 语义（`MarkdownTableAlignment` 之类），要么写一个 HTML 序列化器（多一层、且每次流式都要重新序列化字符串），要么改造成与渲染无关的 AST——两者都不如直接把解析搬到消费端。C# 版本保留给 Native 模式。
- **流式增量**：浏览器侧按最后一条未闭合块做局部重绘的成本远低于 Avalonia 的控件复用逻辑；简单做法是「文本变化 → 60 ms 防抖 → 只重渲染该气泡」（气泡内 DOM 规模通常在几十个节点）。
- **XSS**：所有文本都先 `textContent` / 转义，HTML 只由解析器生成白名单标签，模型输出永远不被当成 HTML 插入。

### 5.9 滚动、虚拟化与"跳消息"问题

| 现在 | WebView 版 |
| --- | --- |
| `VirtualizingStackPanel` + 估算高度 + `KeepBubbleInPlace` / `KeepViewportInPlace` / `BeginAnchorCorrection`（约 200 行、最多 12 轮布局收敛、600 ms 兜底） | `#list{overflow-anchor:auto}`（Chromium 原生滚动锚定，默认开启）+ `content-visibility:auto` + `contain-intrinsic-size` 控制离屏渲染成本 |
| 贴底跟随靠 `Extent/Viewport/Offset` 三方比较 | 「距底 < 8 px 即贴底」的滚动事件，逻辑照抄，但不再需要估算高度补偿 |
| 每气泡一整套控件树，靠 5 个 `ContentControl` 载体按需实例化省内存 | DOM 天然惰性；再叠 300 条上限（`MaxVisibleMessages` 不变，仍由 VM 裁剪） |

**这是本方案最确定的一处收益**：`ChatWindow.axaml.cs` 里最难维护的一段（滚动锚点）可以被浏览器默认行为替代。

### 5.10 调试与可观测性

- `WindowsWebView2EnvironmentRequestedEventArgs.EnableDevTools = true`（仅 Debug 构建）：F12 / 右键"检查"打开 DevTools，实时改 CSS。
- 页面内 `?debug=1`：右下角一个桥接日志浮层（显示收到的 op 与发出的命令），排查"消息没更新"比翻 Serilog 快。
- 桥接层把每个 op 以 `Logger.Debug` 记一行（含 op 名与字节数），异常路径（页面未就绪、`InvokeScript` 抛错）降级为告警而不是崩溃。

## 6. 风险清单与验证方法

| # | 风险 | 影响 | 验证方法 | 缓解 |
| --- | --- | --- | --- | --- |
| R1 | WebView2 Runtime 缺失 | 窗口开不出来 | 探针实测：`AdapterCreated` 后 `AdapterInfo` = `WebView2 / Blink 156.0.4314.58`；`IWindowsWebView2PlatformHandle` 能拿到 `CoreWebView2` 指针 | ✅ 可用。回退：初始化异常/`AdapterInfo` 为空时直接用 `ChatWindow` 并提示一次 |
| R2 | 与桌宠 WebView 共用 `%LocalAppData%\SoufflePet\WebView2` | `--webview` 模式下第二个 WebView 初始化失败 | 探针实测：同进程两个 WebView 共用一个 UserDataFolder，**两个都正常**（第二个 75 ms 就绪，明显复用 environment） | ✅ 不需要独立子目录 |
| R3 | airspace：Avalonia 浮层被 WebView 盖住 | 窗口内的覆盖层失效 | 探针实测：同位置的粉色 Avalonia `Border` 被页面的青色块完全盖住；**但独立顶层窗口的 `Popup` 正常显示在最上层** | ✅ 页面自己画面板；浮层用 DOM（Popup 虽可用，但锚点元素在页面里，保持 DOM 更省事） |
| R3b | `ExperimentalOffscreen` / `OffscreenRenderer` 是否可用 | 若可用：airspace 消失 | 探针实测：可用——`WindowFromPoint` 全区域命中 Avalonia 顶层窗口（无子 HWND）、覆盖层与 Popup 都正常；**代价是窗口不再透明，整窗黑底** | ❌ 否决：透明是硬需求，见 §6.1 |
| R4 | 透明背景在 WebView2 里实际表现 | 面板变白底/黑底 | 探针截图：窗口透出桌面（左文件管理器、右对话窗口），页面内半透明矩形与桌面正常混色，圆角外透出桌面 | ✅ 窗口化宿主下逐像素 alpha 正常 |
| R5 | 缩放边带 | 用户拖不到窗口边缘 | 探针实测：WebView 铺满客户区时，窗口四边 3 px 处 `WindowFromPoint` 全是 `Chrome_RenderWidgetHostHWND`；把 WebView 内缩 6 px 后四边全部回到 Avalonia 顶层窗口 | **已定：页面侧 resize 协议**（面板贴边）；页面 6 px 圈判定方向 + 切 `cursor` → 宿主 `ReleaseCapture` + `WM_NCLBUTTONDOWN(HT*, GetCursorPos())`。回退：WebView `Margin=6` + `AttachResizeHitTest`（已验证） |
| R6 | 流式延迟与吞吐 | 打字机卡顿 | 探针实测：`InvokeScript` 冷启 1.51 ms，200 次空调用 avg 0.154 / p95 0.229 ms，100 次 3.7 KB 批量脚本 avg 0.203 / p95 0.345 ms；页面→宿主 4 条/秒无丢失 | ✅ 跨进程开销可忽略；40 ms 打包只为减少重绘，与性能无关 |
| R7 | 拖放文件拿不到绝对路径 | 失去拖拽加附件 | 页面里 `drop` 读 `dataTransfer.files[0]` 看有没有 `path`；试 `AllowExternalDrop` | v1 不做；后续 COM 拿 `CoreWebView2`（`IWindowsWebView2PlatformHandle` 已暴露 `IntPtr`）调 `postMessageWithAdditionalObjects` |
| R8 | 页面内嵌 `<img>` 指向本地文件被拦 | 图片气泡显示裂图 | 试 `file:///` 与代理两种 | 只走 `/local-file?token=`（同源） |
| R9 | 中文 IME 组字时 Enter 误发送 | 输入被打断 | 微软拼音组字中按 Enter | `compositionstart/end` 标志位，组字期间不发送 |
| R10 | 字体差异 | 视觉与现在不同（现在是 Inter + 系统中文） | 对比截图 | 字体栈 `Inter, "Microsoft YaHei UI", system-ui`；需要的话把 `Avalonia.Fonts.Inter` 里的 `Inter.ttf` 由静态服务器导出 |
| R11 | 首次呼出延迟 0.3–0.8 s | 体感变慢 | 冷启动计时 | 启动后预热隐藏窗口（可配置）；页面首帧骨架屏 |
| R12 | 内存 +80–150 MB | 桌宠常驻 | 任务管理器对比两种模式 | 接受（`System.GC.ConserveMemory=5` 只影响托管堆）；预热只在首次打开后保持 |
| R13 | 桥接协议漂移（页面与 VM 不同步） | 界面显示过期状态 | 每次 `ready` 强制重传 `snapshot`；页面 `visibilitychange`/意外重载时重新握手 | 快照兜底 + `?debug=1` 桥接日志 |
| R14 | `.gitignore` 忽略 `wwwroot/*` | 新页面文件不入库 | `git status` 看不到新文件 | 在 `.gitignore` 补 `!**/wwwroot/chat/**` 之类白名单 |
| R15 | **色值语法不同**：Avalonia 是 `#AARRGGBB`，CSS 是 `#RRGGBBAA` | 照抄 `Border.axaml` 的色值 → 底色/文字全变样（原型实测：`#0FFFFFFF`、`#17FFFFFF` 这类"白色"在 CSS 里变成**青色**，`#F21B1C22` 变成不透明红） | 原型首轮截图即为该现象的复现 | 迁移时把每个色值显式转成 `rgba(R,G,B,A/255)`，并在 `theme.css` 里只保留一份 token 表；加一条肉眼验收：面板底必须是深灰而不是彩底 |
| R16 | 键盘 / 焦点交接 | 打字进不去、Esc 失效 | 探针实测：`_view.Focus()` 返回 `false`、`IsFocused=false`，但 **OS 焦点已落在 WebView 容器 `Chrome_WidgetWin_1` 上**；用 `SendInput` 打 `abc` 后页面 `keydown` 与输入框都收到字符 | ✅ 键盘通路正常。宿主侧只要 `Activate()` + `_view.Focus()`（返回值忽略）+ 页面 `input.focus()`；Esc 由页面 `keydown` 自己处理 |
| R17 | 中文 IME 组字 | 候选框定位、组字期间误发送 | ⚠️ 探针只能注入 Unicode/虚拟键，无法驱动真实 IME | P3 手工验收：微软拼音组字中按 Enter 不应发送；`compositionstart/end` 标志位兜底 |

### 6.1 P0 探针结果（2026-11-05 实测）

探针是一个**仓库外的临时工程**（`%TEMP%\chatprobe`，Avalonia 12.1.1 + `Avalonia.Controls.WebView` 12.1.1，无任何生产代码改动），用 `base / ring / offscreen / dual / timing` 五个场景跑完 R1–R6，并截屏 + `WindowFromPoint` 取证。

| 场景 | 做法 | 结论 | 证据 |
| --- | --- | --- | --- |
| `base` | 420×620 透明无边框窗口 + `NativeWebView`，页面 `background: transparent`，同位置再放一个粉色 Avalonia `Border` 覆盖层与一个红色 `Popup` | **透明成立、窗口内覆盖层被遮挡、Popup 不被遮挡** | 截图里能看到文件管理器与对话窗口透过窗口；青色页面块盖住粉色覆盖层；红色 Popup 显示在最上层 |
| `ring` | 同上，但 WebView `Margin=6` | **6 px 内缩边带把四边交还给父窗口** | `WindowFromPoint`：中心 → `Chrome_RenderWidgetHostHWND`；四边 3 px → Avalonia 顶层窗口（`isSelf=True`） |
| `offscreen` | `WindowsWebView2EnvironmentRequestedEventArgs.ExperimentalOffscreen = true` | **airspace 消失，但窗口失去透明（整窗黑底）** | 同样的 `WindowFromPoint` 全部命中 Avalonia 顶层窗口；覆盖层与 Popup 都正常；截图里桌面不透光，页面半透明矩形叠在黑底上 |
| `dual` | 同一进程两个窗口、同一个 UserDataFolder | **两个 WebView 都能用** | A/B 各自 `adapter=WebView2/156.0.4314.58 ready=True`，B 从创建到页面 ready 仅 75 ms |
| `timing` | 反复 `InvokeScript` | **跨进程往返可忽略** | 冷启 1.51 ms；200 次 `window.probeEcho` avg 0.154 / p50 0.139 / p95 0.229 ms；100 次 3.7 KB 批量 op 脚本 avg 0.203 / p95 0.345 ms |
| `base`（键盘） | `Activate()` + `_view.Focus()` + `SendInput` 打 `abc` | **键盘能进 WebView** | 页面 `keydown` 与 `<input>` 都收到字符；`_view.Focus()` 返回 `false` 但 OS 焦点已在 `Chrome_WidgetWin_1` |

由此定下的四件事（已写回 §5）：

1. **走窗口化宿主，不走 offscreen。** 透明 > 顺带解决 airspace；`ExperimentalOffscreen` 留作"若哪天不要透明了"的备选，并记录在案（它同时把缩放边带还给了宿主，说明这条路本身是通的）。
2. **缩放有两条路，最终选了"面板贴边"那条。** P0 验证了 6 px 内缩边带可用（零代码、`AttachResizeHitTest` 原样能用），但面板会比窗口小一圈；**决策是走页面侧 resize 协议**（§5.6 / §5.4 的 `window:resize`），把 6 px 内缩留作 5 行代码的回退。实现要点：页面在 6 px 圈内判定方向并切 `cursor`，`pointerdown` 时发消息，宿主 `ReleaseCapture()` + `WM_NCLBUTTONDOWN(HTxxx, GetCursorPos())` 把缩放手势交回系统。
3. **浮层仍用 DOM**，但不再是因为 airspace 无解——而是因为锚点元素本来就在页面里；顺带记录一条事实：**Avalonia 的 Popup/ToolTip 能盖在 WebView 上**，以后要临时弹原生浮层是可行的。
4. **40 ms 打包降级为"可选优化"**：IPC 本身只要 0.2 ms，合并推送的真实收益是少触发页面重绘（Markdown 重解析），不是省跨进程开销。

探针没覆盖、留到后续阶段的两项：**中文 IME**（无法用注入模拟，P3 手工验收）、**真实鼠标拖拽/缩放的体感**（P4 实机验收）。

探针工程本身**不入库**，但源码、日志与三张取证截图留在了 `.verifybuild\chatprobe\`（该目录已在 `.gitignore` 里），需要复跑时 `dotnet run -- base|ring|offscreen|dual|timing` 即可；截图见 `shots\base.png`（透明 + 覆盖层被遮）、`shots\offscreen.png`（黑底）、`shots\kbd.png`（键盘键入后的输入框）。

## 7. 分阶段实施计划

| 阶段 | 内容 | 交付 | 验收 | 估算 |
| --- | --- | --- | --- | --- |
| **P0 探针** ✅ **已完成（2026-11-05）** | `WebChatWindow` 骨架 + `base/ring/offscreen/dual/timing` 五个场景，结论见 §6.1 | 透明、airspace、offscreen、缩放、双实例、往返耗时、键盘全部有结论 | 两项改写方案（窗口化 + 6 px 边带） | ~1.5 人日 |
| **P1 外观与只读渲染** | `theme.css` token + `chat.html/css/js` + `snapshot` + Markdown/表格/代码/折叠 + 用量胶囊与浮窗 + 滚动锚定；顺带做**拖放兜底探针**（`AllowExternalDrop=false` + 父窗口能否收到拖放，约 30 分钟） | 打开窗口即完整还原历史会话（不含输入） | 与原生窗口并排截图逐项对齐；300 条消息滚动不跳、展开折叠不跳 | 2.5–3 人日 |
| **P2 流式与工具** | `msg:*` 增量、流式差量、思考气泡、工具气泡（含"执行中"就地更新）、审批气泡、`turn`/`usage` | 真实模型一轮完整对话（含工具调用）在页面里跑通 | 缓存命中率仍 ≥99%（协议改动不得影响请求组装）；流式无明显卡顿 | 1.5–2 人日 |
| **P3 交互** | 输入区、附件（选择/粘贴/移除/退格）、模型菜单、审批菜单、更多菜单、工作区（含删除二次确认）、`ask_user` 面板、底部用量明细 | 全部现有功能可用 | 与原生窗口逐条对照的功能清单（§8 表） | 2.5–3 人日 |
| **P4 窗体与接入** | 拖拽 + **8 向 resize 协议**（页面边带判定 + 光标形状 + 宿主 `WM_NCLBUTTONDOWN`）、Esc/焦点/DPI、`ChatWindowService` 抽象出 `IChatView`、`--chat-webview` 与设置项、静默回退（不预热） | 热键、托盘、戳一戳、停靠全部照旧 | 关窗=隐藏、热键两次即开关、四边四角都能拖、戳一戳能唤起并显示回复 | 1.5–2 人日 |
| **P5 收尾** | 回归测试、README/AGENTS.md 更新、截图对比、默认值决策 | 文档与默认值 | `dotnet build` 0 警告；两条路径都能开 | 1–1.5 人日 |

合计 **P1–P5 约 9.5–13 人日**（P0 已消耗 ~1.5 人日并已完成）。每个阶段结束都能 `git commit` 且不破坏 Native 路径。

## 8. 功能对照清单（P3 验收用）

| 功能 | 原生 | WebView 目标 |
| --- | --- | --- |
| 消息气泡 Markdown | ✅ | ✅ |
| 表格 / 代码块 / 引用 / 任务列表 | ✅ | ✅ |
| 思考折叠（标题跟随最新行） | ✅ | ✅ |
| 工具折叠（工具名 / 参数 / 结果） | ✅ | ✅ |
| 审批气泡（允许/拒绝） | ✅ | ✅ |
| 平台提醒气泡 | ✅ | ✅ |
| 附件气泡（图片预览 / 文件卡片） | ✅ | ✅ |
| 本轮用量胶囊 + 浮窗（时间、模型、命中率 1 位小数） | ✅ | ✅ |
| 底部累计用量 + 明细 | ✅ | ✅ |
| 模型选择（按提供商分组 + 管理入口） | ✅ | ✅ |
| 审批规则切换 | ✅ | ✅ |
| 工作区切换 / 选择 / 删除二次确认 | ✅ | ✅ |
| `ask_user` 分页提问（选项/说明/自由输入/自动翻页） | ✅ | ✅ |
| 附件：选择 / 粘贴 / 移除 / 退格删 | ✅ | ✅ |
| 附件：拖入文件 | ✅ | ⚠️ v1 缺（R7） |
| 自适应气泡宽度（0.76×窗口，240–640） | ✅ | ✅ `max-width: min(76%, 640px)` |
| 关闭=隐藏 / 热键 / 停靠 / 戳一戳 | ✅ | ✅ |
| 消息可选中复制 | ✅ | ✅（浏览器原生，更好） |

## 9. 文件改动清单

**新增**

| 文件 | 说明 |
| --- | --- |
| `SoufflePet/Views/WebChatWindow.cs` | 无边框 + 透明 + `NativeWebView`，窗口行为与 `ChatWindowService` 对接（约 350 行） |
| `SoufflePet/Services/WebChat/WebChatBridge.cs` | 快照/增量/命令分发（约 450 行） |
| `SoufflePet/Services/WebChat/ChatMessageDto.cs` | `ChatMessage` → DTO 投影（约 60 行） |
| `SoufflePet/wwwroot/theme.css` | 共享主题 token（色值/圆角/字号），`index.html` 与 `chat.html` 共用（约 80 行，D8） |
| `SoufflePet/wwwroot/chat/chat.html` | 页面骨架（约 200 行） |
| `SoufflePet/wwwroot/chat/chat.css` | 布局 + 气泡 + 浮层（token 引用 `theme.css`，约 550 行） |
| `SoufflePet/wwwroot/chat/chat.js` | 桥接、`applyOps/reduce/render`、交互、8 向 resize 判定（约 750 行） |
| `SoufflePet/wwwroot/chat/markdown.js` | Markdown → DOM（约 350 行，D3） |

**修改**

| 文件 | 改动 |
| --- | --- |
| `LocalFileServer.cs` | MIME 表、`Cache-Control: no-store`、`/local-file` 图片代理（约 80 行） |
| `wwwroot/index.html` | 改引 `theme.css`（D8；纯样式抽取，不改逻辑） |
| `ChatWindowService.cs` | 抽 `IChatView`（`Show/Hide/FocusInput/CloseForShutdown/Position`），按设置选窗口实现（D1/D7） |
| `SouffleSettings.cs` | 新增 `ChatRenderer`（`Native` / `WebView`），默认 `Native` |
| `App.axaml.cs` | 注册 `WebChatWindow` 与 `WebChatBridge`；启动参数 `--chat-webview` |
| `.gitignore` | 为 `wwwroot/chat/**` 补白名单 |
| `README.md` / `AGENTS.md` | 「快速定位」表补 WebView 对话窗口条目；说明两种渲染器 |

**保留（Native 模式继续用，不删）**

`ChatWindow.axaml` / `.axaml.cs`、`MarkdownTextBlock.cs`、`MarkdownParser.cs`、`Styles/*`。等 WebView 路线灰度稳定（比如一个版本）后再决定是否整体删除。

## 10. 回退策略

1. **开关级**：`ChatRenderer` 设置项 + `--chat-webview` 参数；`WebView` 初始化失败（R1）时自动用 `Native` 并在日志与托盘提示一次。
2. **代码级**：桥接层不侵入 VM，删掉新增的 8 个文件 + 3 处注册即可完全回到今天的状态。
3. **数据级**：完全不碰 `conversations.jsonl`、`window.json`、`statistics.json` 格式——重写只影响"怎么显示"，不影响"存了什么"。

## 11. 决策记录（2026-11-05 定案）

| # | 决策 | 结论 | 落点 |
| --- | --- | --- | --- |
| D1 | 默认渲染器 | **先 Native 为默认**：新增 `--chat-webview` 与设置项 `ChatRenderer`，灰度一个阶段后再评估是否翻默认；WebView2 初始化失败静默回退 | §5.1、§7 P4、§10 |
| D2 | 前端选型 | **原生 JS + 单向渲染**（`applyOps → reduce(state) → render()`），零依赖、零构建、无锁文件 | §4.1、§4.1.5 |
| D3 | Markdown | **JS 重写**（`markdown.js`）：过渡期拿现有 C# 解析器的输出当 oracle，用样例语料逐块比对；C# 版只为 Native 服务 | §5.8、§9 |
| D4 | 拖放附件 | **v1 不做**：P1 里先花约 30 分钟探 `AllowExternalDrop=false` + 父窗口能否收到拖放，能用就顺手接上；否则只保留"选择文件 / 粘贴 / 退格删" | §5.7、R7、§7 P1 |
| D5 | 缩放 | **页面侧 resize 协议、面板贴边**：页面在最外圈 6 px 判定 8 个方向并设 `cursor`，`pointerdown` 发 `window:resize` → 宿主 `ReleaseCapture()` + `WM_NCLBUTTONDOWN(HT*, GetCursorPos())`；**6 px 内缩边带作为回退**（P0 已验证零成本） | §5.4、§5.6、R5、§7 P4 |
| D6 | 预热 | **不预热**：P4 实测首开体感后再定；需要时先上"页面骨架淡入" | §7 P4 |
| D7 | 灰度期共存 | 保留 `ChatWindow`（Native）与 `WebChatWindow`，两者挂**同一个** `ChatViewModel` 单例 | §5.2、§10 |
| D8 | 主题 token | 抽 `wwwroot/theme.css`，`index.html`（桌宠右键菜单）与 `chat.html` 共用；色值统一用 `rgba()`（见 R15） | §5.6、§9 |

**决策带来的实现约定（写代码时直接照此执行）：**

- 缩放协议下 **WebView 铺满窗口**；`ChatWindow.axaml.cs` 的 `AttachResizeHitTest` 不挂到 WebView 窗口（只留给 Native），改由消息驱动。页面必须在 `pointerdown` **之前**就设好 `cursor`——进入系统缩放循环后页面不再收到指针事件。
- 8 向 `HT*` 常量：`l/r/t/b` = 10/11/12/15，`lt/rt/lb/rb` = 13/14/16/17；`lParam` 由宿主用 `GetCursorPos()` 填屏幕坐标（传 0 会让缩放手柄跳位）。
- 拖放探针若不通，README 的对话窗口一节要写明"拖入文件加附件"在 WebView 渲染下不可用（Native 下仍可用）。
- 页面不引用任何 CDN/网络资源（三份本地文件 + 本地图片代理），断网时对话窗口照常可用。

## 12. 附录：视觉原型验证记录

原型：[docs/prototype/chat-mock.html](prototype/chat-mock.html)（单文件、无依赖、无桥接，420×620 固定窗口），在 Chromium 内核里逐项核对过：

| 检查项 | 结论 |
| --- | --- |
| 玻璃面板 / 圆角 16 px / 1 px 描边 | ✅ CSS 直接还原，窗口透明即可露出桌面 |
| 气泡五种形态（助手 / 用户 / 思考 / 工具 / 提醒 / 附件） | ✅ 色值、圆角（四角不同）、内边距、字号行高对齐 `Border.axaml` |
| 气泡自适应宽度 | ✅ `max-width: min(76%, 640px)` 一条规则替掉宿主侧 `DynamicResource` + `SizeChanged` |
| Markdown：标题 / 列表 / 任务列表 / 引用 / 分割线 / 行内样式 | ✅ |
| 表格 | ✅ 表头底色 + 1 px 内框 + `white-space: nowrap` + 容器横向滚动（列宽不再被挤成两行） |
| 代码块 | ✅ 等宽字体 + 横向滚动条 |
| 折叠面板 | ✅ 原生 `<details>`，无需自定义 Expander |
| 本轮用量胶囊 + 浮窗 | ✅ 位置在气泡**外**侧下方，点击在胶囊上方弹窗（明细六行含"提供方 / 模型"） |
| 底部累计用量浮窗 | ✅ 右对齐弹出（`data-align="right"` 对应原 `TopEdgeAlignedRight`） |
| 6 个浮层（模型 / 更多 / 审批 / 工作区 / 用量 / 本轮用量） | ✅ 全部 DOM 实现，自动夹到窗口内 |
| 提问面板（分页 / 选项说明 / 自由输入 / 取消提交） | ✅ |
| 输入区（附件 chip、多行自适应 62–132 px、禁用态发送按钮） | ✅ |
| 流式增长时的滚动行为 | ✅ 演示里"先滚到 45 % 再流式"，视口不会被扯动；贴底时会跟随到底 |
| 迁移中暴露的问题 | ⚠️ R15 色值语法（首轮截图整窗变青色）；表格列被挤（已改 nowrap + 横滚）；浮层可加 `backdrop-filter`（已加，纯加分项） |