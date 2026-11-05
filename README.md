<p align="center">
  <img src="./SoufflePet/Assets/pet.ico" alt="SoufflePet" width="128" height="128" style="border-radius: 10%;" />
</p>

# SoufflePet

基于 [Avalonia](https://avaloniaui.net/) 的 Windows Live2D 桌面桌宠与Agent框架。

> **许可证**：本项目使用混合许可证。
> 主程序 [GPL-3.0-or-later](LICENSE)，内嵌 Live2DCSharpSDK 子项目 [MIT](Live2DCSharpSDK.Framework/LICENSE)，详见文末[「许可证」](#许可证)章节。

## 功能

- **透明置顶窗口**：无边框、透明背景、置顶显示，默认停靠屏幕右下角
- **视线全局跟随**：宿主用 `GetCursorPos` 轮询全局光标（而非窗口内指针事件），鼠标移出窗口甚至在其他显示器上，模型视线仍持续追踪
- **交互**：
  - 左键拖动 → 移动窗口
  - 中键拖动 → 移动模型
  - 左键单击 → 呼出 / 收起主窗口
  - 右键 → 交互菜单：「戳一戳」「摸摸头」，先播放反应动作再发起一轮对话唤醒桌宠；「切换模型」换当前模型；「切换表情」「切换动作」手动切换表情（可多选同时激活，按激活顺序叠加参数，切换时淡入淡出）/ 播放指定动作（列表来自模型已登记的表情与动作组；「清除动作」把非循环动作留下的姿势/参数恢复默认）
  - 滚轮 → 缩放模型
- **托盘图标**：显示 / 隐藏、重置模型、主窗口、退出；关闭窗口默认隐藏到托盘
- **自动表情**：根据当前系统状态，自动切换游戏/音乐表情
- **口型同步**：在音乐模式（自动检测或手动开启）下，根据当前播放的音乐，自动同步模型的口型
- **Agent**：`Ctrl+Alt+Space`、左键单击桌宠或托盘菜单「主窗口」呼出 React 主窗口，支持 OpenAI 兼容接口、流式回复、思考过程展示、工具调用、MCP 集成、技能、长期记忆与插件（详见下节）
- **运行统计**：陪伴时长（累计程序运行秒数，跨重启累计）与 token 累计用量，常驻主窗口侧栏底部并实时增长，桌宠也可用 `get_statistics` 工具查询
- **插件系统**：独立程序集 + `plugin.json` 清单，可扩展工具、处理模型正文、接入新的消息平台；支持界面启停、配置表单与 zip 导入

## Agent

`Ctrl+Alt+Space`、左键单击桌宠或托盘菜单「主窗口」呼出 React 主窗口（再次触发收起）。窗口为单例，关闭只隐藏，隐藏后内容保留；输入区的停止按钮可中断当前一轮生成。对话、设置与会话管理都在同一个单页应用里，不再单独开设置窗口。

### 消息气泡

助手气泡的正文由 React 前端渲染 Markdown（GFM + 表格横滚 + KaTeX 公式 + Shiki 代码高亮）：段落、标题、有序 / 无序 / 任务列表、引用、围栏代码块、表格、分割线，以及粗体 / 斜体 / 删除线 / 行内代码 / 链接（点击用默认浏览器打开）。用户发言、平台提醒与工具审批气泡按纯文本原样展示，`*`、`#`、`@路径` 这类字符不会被当成语法改写。

消息区使用虚拟列表，只实例化视口附近的气泡，长会话不会随消息数量线性吃内存；会话状态机保留最近 300 条可见消息（更早的从界面裁剪，请求历史由上下文窗口单独维护）。思考、工具调用、审批、附件与用量都是独立气泡，按需展开。

### 对话模型

走 OpenAI 兼容接口，**以提供商为单位配置**：主窗口左侧「更多 → 对话模型」打开设置页里的对话模型面板，与 Live2D 模型管理分开。

- 左侧是提供商列表，每个提供商填一次名称、接口地址与 API 密钥（同一提供商下的模型共用）；「移除」会先弹二次确认，确认后连同模型清单与密钥一起从配置里删除。
- 名称 / 接口地址 / 密钥与模型勾选都是**待保存的编辑副本**：点「保存配置」才写回配置（保存前会校验名称非空且不重名、地址为合法 http/https），「还原」丢弃未保存的改动；切换提供商会一并放弃这些改动。
- 「获取模型列表」按接口请求 `GET {base}/models`（与对话请求同一套地址/密钥规范化规则），把返回的模型标识合并进清单；接口没实现 `/models` 时也可以在输入框里手动添加模型标识。切换提供商、还原、移除或离开设置页都会取消在途请求，不会让按钮一直卡在「获取中」。
- 勾选要用的模型后点「保存配置」，勾选的模型才进入对话输入区的模型选择列表（未勾选的不显示，也不会被请求）。
- 输入区的模型选择列表**按提供商分组**，点条目切换当前模型；提供商改名时，当前选中模型的键会同步改写，选中状态不会丢。

配置写在 `SoufflePet.json`，唯一事实源是 `ChatProviders`：

| 字段 | 说明 |
| --- | --- |
| `Name` | 提供商名称，也是输入区模型列表的分组标题 |
| `BaseUrl` | 兼容接口地址，例如 `https://api.example.com/v1` |
| `ApiKey` | API 密钥，可留空（本地 / 无鉴权的接口） |
| `Models` | 该提供商下的模型清单，每项 `ModelId`（请求中的 `model` 字段）、`Name`（显示名，留空用 `ModelId`）、`IsActive`（是否加入模型选择列表） |

`ChatModelKey`（`提供商/模型标识`）记录当前选中的模型。人设由 `ChatSystemPrompt` 决定，称呼 `PetName` 会同步到窗口标题与默认人设。

### 工具调用

模型可调用以下本地工具，声明由方法签名与 `Description` 自动生成：

| 工具 | 作用 |
| --- | --- |
| `get_expressions` / `set_expression` | 列出与切换 Live2D 表情 |
| `run_shell_command` | 执行 PowerShell 命令（超时与输出长度有上限） |
| `list_windows` | 列出当前可见窗口 |
| `take_screenshot` | 截取光标所在显示器，或按标题截取指定窗口（被遮挡也能截到） |
| `ask_user` | 向用户提问：分页面板 + 预设选项（可带说明）+ 自由输入，全部作答后结果回传模型 |
| `remember` / `forget` / `recall` | 长期记忆的写入、删除与关键词检索 |
| `read_skill` | 读取某个技能的完整说明 |
| `get_statistics` | 查询运行统计（陪伴时长、token 用量等），不传参返回全部、传名称返回单项 |

每轮最多 `MaxToolRounds` 次工具调用（默认 10）。命令执行受 `ToolApproval` 控制：`Manual` 每次在消息区弹出审批气泡等用户点「允许」，`FullAccess` 直接执行。工具调用与思考过程在消息里以折叠面板展示，展开可看参数与结果。

当前时间不由工具提供，而是随消息下发：`TimeAwarenessEnabled` 打开时（默认），每轮把当前本地时间（含星期、UTC 偏移与时区）附加到本轮用户消息末尾，模型无需调用工具即可感知时间；关闭后模型看不到任何时间信息。附加的时间（以及相关回忆）会作为该条消息的请求文本保存在当前会话的 `conversation.jsonl` 里，但不进入界面历史、也不参与长期记忆检索——这样做是为了让重启后重建的请求与上一轮逐字节一致，继续命中前缀缓存。

### 上下文窗口

每轮只把最近若干轮对话发给模型，窗口由 `SoufflePet.json` 里两项控制：

| 配置项 | 默认 | 说明 |
| --- | --- | --- |
| `HistoryWindowTurns` | 50 | 高水位：用户提问轮数超过它时，一次性裁剪 |
| `HistoryKeepTurns` | 30 | 低水位：裁剪后保留的轮数 |

- 两者差值越大，裁剪发生得越少。服务端前缀缓存按请求开头起的最长公共前缀命中，每裁剪一次就让缓存整体失效一次，所以默认从 50 直接裁到 30，而不是每轮丢一轮。
- 低水位必须小于高水位；非法值（0、负数或低水位 ≥ 高水位）会在启动时夹到合法范围（至少保留 1 轮）。
- 启动时按 `window.json` 记录的上次窗口轮数恢复，避免重启后的第一次请求重建前缀。

### MCP 服务器

主窗口左侧「更多 → MCP 服务器」里配置，配置单独保存在用户家目录的 `~/.souffle/mcp.json`（不写进 `SoufflePet.json`）。文件用通行的 MCP 配置格式，可直接照抄 VS Code / Claude 等客户端的配置：

```json
{
  "mcpServers": {
    "files": {
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-filesystem", "D:\\"]
    },
    "remote": {
      "type": "http",
      "url": "http://localhost:3000/mcp",
      "headers": { "Authorization": "Bearer ..." }
    }
  }
}
```

支持 Stdio（本地子进程，指定命令与参数）与 Http（远程服务）两种传输方式；额外支持 `enabled`、`timeout`（秒）、`cwd` 等可选键，本应用不认识的键在设置界面保存时会原样保留。启用的服务器在下次对话时连接，其工具以「服务器名\_工具名」暴露给模型。

### 插件

插件是独立程序集，通过 `plugin.json` 声明，无需修改主程序即可为模型增加工具、对回复做流式后处理，或接入新的消息平台（如 QQ）。主窗口左侧「插件」设置页可以查看、启用 / 禁用、编辑配置、重新扫描与导入 zip 包。

插件从应用目录的 `plugins/` 与用户家目录的 `.souffle/plugins` 读取；导入的插件装在后者的 `<id>` 子目录下，成功后自动启用。启用状态记录在 `SoufflePet.json` 的 `DisabledPlugins`。

内置插件：

| 插件 id | 作用 |
| --- | --- |
| `input-kit` | 鼠标点击 / 拖拽 / 滚轮，键盘按键、组合键与文本输入 |
| `dsml-cleaner` | 清理模型正文中泄漏的 DSML 工具调用标记 |
| `napcat` | 通过 NapCat 的 OneBot 11 正向 WebSocket 接入 QQ，被 @ 时提醒桌宠并按会话回复 |
| `loopback` | 本机调试用的回环消息平台：监听 `127.0.0.1:18765`，`GET /send?text=…` 把消息推给桌宠，`GET /replies` 读取桌宠的回复 |

> `loopback` 只监听回环地址，用于验证消息链路与缓存命中率；不需要时在插件窗口里停用即可。

清单字段、最小示例、生命周期钩子、配置表单、消息平台接入与打包规则见[插件开发指南](docs/plugin-development.md)。

### 技能

技能是模型按需读取的说明文档，放在 `.agents/skills/<技能名>/SKILL.md` 或 `.agents/skills/<技能名>.md`。扫描目录：

- 当前工作区（主窗口输入区上方可切换）下的 `.agents/skills`、`.souffle/skills`
- 用户家目录下的 `.agents/skills`、`.souffle/skills`

已启用的技能会以清单形式注入系统提示词，模型需要时用 `read_skill` 取正文；`DisabledSkills` 记录被停用的技能。设置界面支持从 zip 压缩包安装技能，安装位置固定为用户家目录下的 `.souffle/skills`，不随工作区变化；「卸载」会删除选中技能所在的目录（直接放在技能根目录下的单个 `.md` 则只删该文件），仅限上述扫描目录内的技能，操作前有二次确认。

### 长期记忆

`ChatMemoryEnabled` 打开时对话落盘到 `%AppData%\SoufflePet`：

- `sessions.json`：会话索引（标题、更新时间、工作区、当前会话），侧栏历史列表据此恢复
- `sessions/<id>/conversation.jsonl`：该会话每轮问答与工具调用记录（启动时据此重建请求上下文）
- `sessions/<id>/window.json`：该会话上次请求实际使用的历史窗口轮数（重启后按同一窗口恢复，第一次请求才能继续命中服务端前缀缓存）
- `profile.json`：模型通过 `remember` 写入的事实（称呼、喜好、常用路径等），所有会话共享

旧版单文件 `conversations.jsonl` 会在首次启动时迁移为默认会话，并保留只读 `.bak`；删除的会话移到 `sessions/.deleted`。检索用中文 2-gram + BM25，不依赖分词库或向量服务。

### 统计信息

`IStatisticsService` 统一统计桌宠的运行信息，落盘在 `%AppData%\SoufflePet\statistics.json`，目前包含：

- **陪伴时长**：有史以来程序运行的总时长（关闭即停、下次启动接着累加），每 60 秒与退出时落盘
- **token 用量**：有史以来累计输入 / 输出 token 与缓存命中率，每轮结束即时落盘

二者常驻主窗口侧栏底部并实时增长，模型可通过 `get_statistics` 工具查询全部或单项；新增统计项时在 `IStatisticsService` 与实现里各补一处即可。

助手气泡底部会显示本轮用量胶囊（`用量 114.1K tok` + 时间），点击弹出本轮明细：提供方 / 模型、缓存命中率（1 位小数）、未缓存输入、缓存读取、缓存写入与输出。这份用量与模型标识随最后一条助手消息写进会话的 `conversation.jsonl`，重启恢复历史后胶囊和浮窗仍能看到。侧栏底部的累计用量文案点击后展示累计明细（缓存命中率、缓存读取、未缓存输入、输入合计、输出）。

## 运行方式

桌宠窗口固定使用 Live2DCSharpSDK + OpenGL ES2 原生渲染；对话、会话管理与七个设置页统一由一个 React 单页应用承载（WebView2 窗口，关闭只隐藏）。旧 Avalonia 对话/设置窗口与 WebView 版桌宠已在 M7 删除，不再提供 `--webview` / `--legacy-ui` 回退。

## 环境要求

- Windows 10+，.NET 10 SDK
- React UI 需要 [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)
- 桌宠需要支持 OpenGL ES2 的显卡驱动
- **构建 React UI 需要 Node.js 22.12+ 与 pnpm 10+**（首次构建前执行 `pnpm --dir SoufflePet/web install`）

## 构建与运行

```powershell
# 安装前端依赖（首次）
pnpm --dir SoufflePet/web install

# 构建整个解决方案（会调用 pnpm build 生成 wwwroot/app）
dotnet build SoufflePet.sln

# 只验证 .NET、跳过前端构建（需已有 wwwroot/app 产物）
dotnet build SoufflePet.sln -p:SkipWebBuild=true

# 后端单测（会话记忆 / 调度 / token 格式）
dotnet test SoufflePet.Core.Tests\SoufflePet.Core.Tests.csproj

# 运行（桌宠窗口默认隐藏，Ctrl+Alt+Space / 托盘 / 左键单击桌宠呼出主窗口）
dotnet run --project SoufflePet
```

前端独立开发/验收：

```powershell
pnpm --dir SoufflePet/web dev          # http://127.0.0.1:5173/app/
pnpm --dir SoufflePet/web test         # Vitest
pnpm --dir SoufflePet/web screenshots  # 五档宽度截图 → .verifybuild/ui
```

> React 单页应用与宿主桥（`AppWebWindow` / JSON-RPC）按 [docs/react-ui-contract.md](docs/react-ui-contract.md) 实现并已冻结 v1 契约。

启动开关：

```powershell
# 启动即呼出主窗口（验收用；默认启动只显示桌宠）
dotnet run --project SoufflePet -- --open-ui

# 前端热更新：先起 Vite，再让窗口加载 dev server
pnpm --dir SoufflePet/web dev
dotnet run --project SoufflePet -- --web-dev
```

> React 窗口使用真实会话内核：`session.*` / `chat.*` 由 `ChatRpc` 转发到 Core 的 `ChatSession`/`ChatSessionManager`；七个设置页由 `SettingsApi` 提供真实读写。WebView2 初始化失败时记录错误并弹出系统提示，不再回退旧窗口。

## 解决方案结构

```
SoufflePet.sln
├─ SoufflePet/                  # 主程序（Avalonia 桌面应用）
│  ├─ Views/                     # 窗口层（原生桌宠、内置浏览器、React 主窗口）
│  │  ├─ NativePetWindow.cs      #   原生桌宠（拖拽 / 穿透 / 全局光标 / 右键菜单）
│  │  ├─ BrowserWindow.cs        #   内置浏览器窗口（WebView2）
│  │  └─ Web/AppWebWindow.cs     #   React SPA 主窗口（自定义标题栏 / 8 向缩放 / 关闭隐藏）
│  ├─ Services/                  # 宿主服务：窗口、托盘、热键、平台实现
│  │  ├─ AppWindowService.cs     #   主窗口单例 / 热键 / 戳一戳与摸头唤醒
│  │  └─ Web/                    #   React 桥：WebBridgeServer / ChatRpc / SettingsApi / ThemeService
│  ├─ Utils/                     # 截图、窗口几何、WebView 配置等辅助代码
│  ├─ native/                    # 原生渲染控件
│  │  ├─ NativePetControl.cs     #   OpenGL 渲染循环、逐像素命中检测
│  │  └─ AvaloniaApi.cs          #   Avalonia GlInterface → OpenGLApi 桥接
│  ├─ web/                       # React SPA 源码（Vite / React 19 / TS / Tailwind）
│  ├─ wwwroot/                   # 前端产物（app/，构建生成）与 Live2D 模型资源（models/）
│  └─ Assets/                    # 应用图标等资源
├─ SoufflePet.Core/              # 无 Avalonia 的领域层：模型、记忆、LLM/工具/MCP/技能/插件/调度
│  └─ Chat/                      #   会话状态机 / 会话索引与存储 / 平台消息触发路由
├─ SoufflePet.Core.Tests/        # xUnit（会话状态机、会话管理、记忆、调度、token 格式）
├─ SoufflePet.Plugin.Sdk/        # 插件契约（清单、基类、配置表单、消息平台接口）
├─ Plugins/                      # 随主程序分发的内置插件
│  ├─ SoufflePet.Plugin.InputKit/      # 键鼠输入工具
│  ├─ SoufflePet.Plugin.DsmlCleaner/   # DSML 标记清理
│  └─ SoufflePet.Plugin.NapCat/        # NapCat QQ 消息平台
├─ Live2DCSharpSDK.Framework/    # Cubism 框架层（数学、Motion、模型解析）
├─ Live2DCSharpSDK.App/          # 应用封装（LAppDelegate、LAppModel、Live2dManager）
├─ Live2DCSharpSDK.OpenGL/        # OpenGL ES2 渲染后端（CubismRenderer_OpenGLES2）
└─ docs/                         # 方案与冻结契约（react-ui-contract.md）
```

## 对 Live2DCSharpSDK 的本地修改

本项目内嵌的 SDK 子项目来自上游 [Live2DCSharpSDK](https://github.com/Coloryr/Live2DCSharpSDK)，其中解决方案实际编译三个：`Live2DCSharpSDK.App` / `.Framework` / `.OpenGL`。为适配桌宠场景，这三个项目有大量本地修改。

### 构建与可空性

- **目标框架**：`Live2DCSharpSDK.App` / `.Framework` / `.OpenGL` 三个项目 `net8.0` → `net10.0`
- **行尾**：所有源文件为 CRLF（上游为 LF），未改动内容的文件在 `diff` 下也会显示差异，对比时需加 `--strip-trailing-cr`
- **CS8618 清理**：DTO record（`ModelSettingObj` / `CubismMotionObj` / `CubismPhysicsObj` / `CubismModelUserDataObj`）及大量字段、委托、数组成员补 `= null!`（值由 JsonSerializer / 原生库填充），`TextureInfo.FileName` 改 `string?`

### 为桌宠开放的能力（`Live2DCSharpSDK.App`）

- `LAppModel.EnableFaceTracking`：目光跟随开关。关闭时跳过拖拽角度参数，`LoadParameters` 每帧重置为保存值，视线自然回正
- `LAppModel.GamepadValueUpdate`：手柄参数钩子，在动作/表情之后、**物理演算之前**触发（模型作者把摇杆/扳机/ABXY 设计为物理输入源，推摇杆会带出身体晃动）。单独开钩子而非复用 `ValueUpdate`——后者与拖拽参数被 `CustomValueUpdate` 二选一门控，手柄需要两种情况下都能写入
- `LAppModel.LipSyncValue` / `LipSyncWideValue`：口型音量外部输入（第 1 个口型参数写嘴开闭、其余写水平宽窄），替换官方示例里写死 `0` 的实现；模型 `Groups` 里的 `LipSync` 组缺失或 `Ids` 为空时（部分 VTube Studio 导出模型）回退到标准口型参数 `ParamMouthOpenY` / `ParamMouthForm`，否则口型律动没有任何可写参数
- `LAppModel.ResetExpression()`：清空表情队列并以 weight=0 写回基线，等价 Web SDK 的 `resetExpression()`
- `LAppLive2DManager.ModelToClip()`：模型坐标 → 裁剪坐标换算（用上一帧累积的 MVP，须在渲染后调用），供逐像素命中检测与包围盒测量使用
- `LAppView` 暴露视图矩阵操作：`AdjustScale` / `GetScale` / `AdjustTranslate` / `TranslateX/Y` / `ViewLeft/Right/Bottom/Top`。注意 `Render()` 每帧把 `_viewMatrix` 拷给 `manager.ViewMatrix`，缩放/平移必须落在 `_viewMatrix` 侧
- `LAppDefine`：缩放范围放宽，`ViewMaxScale` 2.0 → 3.0、`ViewMinScale` 0.8 → 0.4

### Bug 修复（`Live2DCSharpSDK.Framework`）

- `CubismEyeBlink.DetermineNextBlinkingTiming`：原写法 `_random.Next() / int.MaxValue` 是整数除法恒为 0，眨眼间隔固定为 0（约每 0.3s 连眨一次）。改 `(float)_random.NextDouble()`
- `CubismMotionQueueManager`：`Motions[0]` → `Motions[i]`，修复循环索引错误（原实现只处理队列第一个 motion）
- `CubismCore.GetDrawableRenderOrders`：P/Invoke EntryPoint `csmGetDrawableRenderOrders` → `csmGetRenderOrders`，修正原生导出名（原名调用即 `EntryPointNotFoundException`）
- `CubismExpressionMotion(_Manager)`：移除标记 `[Obsolete]` 的 `_fadeWeight` 成员，淡入淡出权重改由调用方经参数传入（成员状态在多实例间共享是隐患）；`StartMotion` 去掉多余的 `UserTimeSeconds` 参数

### 性能优化

- `CubismMatrix44.MultiplyByMatrix`：三重循环 → `System.Numerics.Matrix4x4`（内存布局逐元素一致，可直接 reinterpret 走 SIMD）。实测 ~60ns → ~9ns；先整体读入再相乘，天然处理 `a` 与 `_tr` 为同一数组的自乘别名
- `CubismClippingManager.CalcClippedDrawTotalBounds`：① `Initialize` 时缓存 Core 原生数组指针，循环内按索引读取，免去逐次 `LibraryImport`（约 50ns/次 × 41 遮罩组）；② 极值归约改 `Vector<float>` 无分支 min/max（交错的 [x,y] 数据无需反交织，偶/奇 lane 分别累积两轴），原标量版 4 个数据相关分支每顶点约 15 周期误预测
- `CubismRenderer_OpenGLES2`：每 drawable 独立网格缓冲——位置缓冲按固定容量分配、每帧 `BufferSubData` 原地更新；UV / 索引是静态数据，`STATIC_DRAW` 仅首次上传。替掉原实现"全局共用一对交错 AoS 的 VBO/IBO + 每帧逐顶点搬运重传"。同时去掉逐 drawable 的 `UseProgram(0)` / `BindBuffer(0)` / `BindVertexArray(0)` 后处理（下一帧绘制会自行绑定所需状态）
- 配套改动：`OpenGLApi` 新增 `GL_DYNAMIC_DRAW` 与 `BufferSubData` 抽象（Avalonia `GlInterface` 未封装该函数，具体绑定在 `ShufuleiPet/native/AvaloniaApi.cs` 走 `GetProcAddress`）；`CubismShader_OpenGLES2.SetVertexAttributes` 增加 `renderer` / `index` 参数以支持每 drawable 缓冲绑定；`CubismClippingContext_OpenGLES2` 改主构造函数并移除未使用的 `model` 参数
- `LAppModel.Update`：删掉只服务于眨眼门控的 `motionUpdated` 局部变量；待机动作是循环动作（每帧都更新参数），照搬官方示例的 `!motionUpdated` 门控会导致永远不眨眼，改为直接叠加眨眼值

## 资源说明

Live2D 模型资源位于 `wwwroot/models/`。**本项目不附带、不分发任何模型资源**：模型的美术著作权归原作者所有。使用者需自行获取**已获得合法授权**的模型并放入对应目录。

### 首次使用

把获得授权的模型压缩包（zip）直接导入即可：主窗口左侧「更多 → Live2D 模型」打开设置页的模型面板，点「导入」选择压缩包，面板会自动解压到 `wwwroot/models/`、登记动作与表情，并设为当前模型。

也可以手动放置：

1. 把模型放入 `wwwroot/models/目录名/`（`ModelsDirectory` 默认为 `models`）。
2. 确认该目录下存在 `主文件名.model3.json`（目录名与主文件名相同或不同都可以，见下）。
3. 在模型管理面板里「打开目录」后手工添加，或在 `SoufflePet.json` 的 `Live2DModels` 里加一项：`Name` 写 `目录名/主文件名`，两者一致时只写一段即可。

未配置模型时应用**仍可正常启动**，只是窗口不渲染模型。

### 配置模型

修改 `SoufflePet.json`（首次运行自动生成，位于可执行文件同级目录）里的模型列表：

```json
{
  "ModelsDirectory": "models",
  "Live2DModels": [
    { "Name": "舒芙蕾", "WatermarkPartId": "Part13", "GameExpression": "手柄", "MusicExpression": "麦克风" },
    { "Name": "黑猫/cat", "WatermarkPartId": "", "GameExpression": "手柄", "MusicExpression": "麦克风" }
  ]
}
```

- `ModelsDirectory`：`wwwroot/` 内的模型根目录（相对路径）
- `Live2DModels`：可切换的模型列表，每项四个字段
  - `Name`：模型目录名与 `model3.json` 主文件名。两者一致时写一段（`你的模型名`，对应 `wwwroot/models/你的模型名/你的模型名.model3.json`）；不一致时写成 `目录名/主文件名`（对应 `wwwroot/models/目录名/主文件名.model3.json`，分隔符用 `/` 或 `\` 均可）
  - `WatermarkPartId`：该模型的水印部件 Id（如 `Part13`），加载时把透明度压 0；留空表示不处理水印
  - `GameExpression` / `MusicExpression`：该模型在游戏 / 音乐状态下自动切换的表情名，取值须为模型 `model3.json` 中 `FileReferences.Expressions` 列出的表情文件名（去掉 `.exp3.json`）。默认「手柄」「麦克风」；表情名因模型而异，换模型后请在模型管理面板里按实际表情文件改，留空表示该模型不切换
- 启动加载上次选中的模型（首次为列表第一项）；运行中可在桌宠右键菜单「切换模型」里换，选择会**写回配置**，重启沿用
- 切换模型时表情名随之切换，并按新模型重算一次自动表情与联动（旧模型的手柄 / 口型服务不会残留）
- VTube Studio 导出的模型（目录内有 `.vtube.json`）：原生版会读取其中的 `ParameterSettings`，把面捕角度（`FaceAngleX/Y/Z`）按作者配置的范围映射到 `AngleX/AngleY/AngleZ`、`PositionX/Y/Z` 等自定义参数，保证这类模型的物理链有输入；普通模型不受影响

### 模型管理面板

主窗口左侧「更多 → Live2D 模型」：

- **导入**：选择一个 zip 压缩包，找其中层级最浅的 `*.model3.json` 作为主模型，解压到 `wwwroot/models/<目录名>/`；目录名优先取压缩包内主模型所在目录名，取不到时用压缩包文件名。导入后自动登记引用：
  - 目录内所有 `*.exp3.json` 写进 `FileReferences.Expressions`（`Name` 为去掉后缀的文件名），已有的跳过
  - 目录内所有 `*.motion3.json` 写进 `FileReferences.Motions`：文件名含 `idle` / `待机` 的归到 `Idle` 组，其余每个文件自成一组（因此都能被 `StartRandomMotion` 的随机动作抽到）
  - 从 `.cdi3.json` 的 `Parts` 里找名称含「水印」或 `watermark` 的部件，自动填入 `WatermarkPartId`
- **移除**：删除选中模型对应的目录（整个目录从磁盘删除，有二次确认），若删的是当前模型则自动切到相邻的一个
- **修改**：右侧可改模型名（`Name`）、水印部件 Id、游戏 / 音乐表情名，改完点「保存配置」；「设为当前」切换生效模型
- 导入、切换、移除都会写回 `SoufflePet.json` 并触发桌宠重载，无需重启

### 模型相关的可配置项

游戏 / 音乐表情名已改为**跟随模型**的配置（`Live2DModels` 每项的 `GameExpression` / `MusicExpression`），不再有全局配置项：检测到游戏或音乐时，按当前模型自己登记的表情名切换，因此不同模型可以各用各的表情。

## 许可证

本项目采用**双许可证**结构，两部分相互独立：

| 范围 | 许可证 | 许可证文件 |
| --- | --- | --- |
| SoufflePet 主程序（`SoufflePet/` 目录及解决方案配置） | [GPL-3.0-or-later](LICENSE) | 根目录 `LICENSE` |
| Live2DCSharpSDK 子项目（`Live2DCSharpSDK.App` / `.Framework` / `.OpenGL`） | [MIT](Live2DCSharpSDK.App/LICENSE) | 各子项目目录下的 `LICENSE` |

说明：

- 所有源文件头部均带 `SPDX-License-Identifier` 标注，与上表许可范围一一对应，便于工具自动识别
- SDK 子项目源码来自上游 [Live2DCSharpSDK](https://github.com/Coloryr/Live2DCSharpSDK)（MIT License），本项目对其所做的本地修改同样以 MIT 发布，保留上游版权署名
- 第三方 NuGet 依赖（Avalonia、NAudio、Serilog、SkiaSharp 等）各自遵循其上游许可证，不在本仓库许可证覆盖范围内
- Live2D Cubism Core 原生库（`Live2DCubismCore.dll`）的使用受 Live2D 官方许可协议约束，不在上述许可证覆盖范围内
- Live2D 模型资源的美术著作权归模型原作者所有，本仓库不附带、不分发任何模型资源（见[资源说明](#资源说明)）

## 鸣谢

[Live2D Cubism](https://www.live2d.com/zh-CHS/)

[Live2DCSharpSDK](https://github.com/Coloryr/Live2DCSharpSDK)