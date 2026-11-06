# AGENTS.md

面向在本仓库工作的 AI 编码代理的指引。人读文档见 [README.md](README.md)，插件开发见 [docs/plugin-development.md](docs/plugin-development.md)。

## 项目概览

SoufflePet 是基于 [Avalonia](https://avaloniaui.net/) 的 Windows Live2D 桌面桌宠与 Agent 框架。主程序为 `SoufflePet/`（`net10.0`，`WinExe`），内嵌 Live2D 渲染 SDK，并通过插件系统扩展工具 / 正文后处理 / 消息平台。

## 常用命令

```powershell
# 首次：安装前端依赖（React UI）
pnpm --dir SoufflePet/web install

# 构建整个解决方案（会调用 pnpm build，产物写入 wwwroot/app）
dotnet build SoufflePet.sln

# 只验证 .NET、跳过前端构建（需已有 wwwroot/app 产物）
dotnet build SoufflePet.sln -p:SkipWebBuild=true

# 后端单测（会话状态机 / 会话管理 / 记忆 / 调度 / token 格式）
dotnet test SoufflePet.Core.Tests\SoufflePet.Core.Tests.csproj

# 原生桌宠（Live2DCSharpSDK + OpenGL ES2）
dotnet run --project SoufflePet

# 启动即呼出 React 主窗口（默认只显示桌宠；Ctrl+Alt+Space / 托盘 / 单击桌宠也可呼出）
dotnet run --project SoufflePet -- --open-ui

# 前端热更新：先起 Vite，再让窗口加载 dev server
pnpm --dir SoufflePet/web dev
dotnet run --project SoufflePet -- --web-dev

# 前端独立开发 / 测试 / 五档截图
pnpm --dir SoufflePet/web dev
pnpm --dir SoufflePet/web test
pnpm --dir SoufflePet/web screenshots
```

- 运行依赖 Windows 10+、.NET 10 SDK。React 主窗口需 WebView2 Runtime，桌宠需支持 OpenGL ES2 的显卡驱动。
- React UI 构建依赖 **Node.js 22.12+ 与 pnpm 10+**；`dotnet build` 默认调用 `pnpm build`，缺依赖时会给出可操作错误。
- 测试：Web 前端用 Vitest（`pnpm --dir SoufflePet/web test`）；领域层用 `dotnet test SoufflePet.Core.Tests`。改动后至少跑 `dotnet build SoufflePet.sln`（`-warnaserror` 未启用，但请勿引入新警告）。
- `SoufflePet.Core` **不得引用 Avalonia**；平台能力（桌宠表情、UI 线程）通过 `IPetController` / `IUiDispatcher` 抽象注入。
- 插件工程**不在解决方案内**，需单独构建（见下）。
- **输出文件被占用**：构建时若报 MSB3026 错误（通常是运行中的 `.NET Host` 进程锁定了 dll），**改用 `.verifybuild` 作为输出目录重试**，不要终止用户进程：

  ```powershell
  dotnet build SoufflePet.sln -p:BaseOutputPath=.verifybuild\bin\
  ```

  `.verifybuild` 已在 `.gitignore` 中（不入库），仅用于编译验证。

## 解决方案结构

```
SoufflePet.sln
├─ SoufflePet/                     # 主程序（Avalonia）
│  ├─ Views/                       # 窗口（原生桌宠、React 主窗口）
│  │  └─ Web/AppWebWindow.cs       #   React SPA 主窗口（WebView2、拖拽/缩放/隐藏）
│  ├─ Services/                    # 宿主服务：窗口/托盘/热键/平台实现
│  │  ├─ AppWindowService.cs       #   主窗口单例、Ctrl+Alt+Space、戳一戳/摸头唤醒
│  │  └─ Web/                      #   WebBridgeServer / ChatRpc / SettingsApi / ThemeService
│  ├─ Utils/                       # 开机自启、全局光标、窗口几何等
│  ├─ native/                      # 原生渲染控件与 Avalonia→OpenGL 桥接
│  ├─ web/                         # React SPA 源码（Vite / React 19 / TS / Tailwind 4）
│  ├─ wwwroot/                     # 前端产物（app/）与 Live2D 模型资源（均 gitignore）
│  └─ Assets/                      # 图标、Live2DCubismCore.dll
├─ SoufflePet.Core/                # 无 Avalonia 的领域层（模型/LLM/工具/MCP/记忆/调度）
│  └─ Chat/                        #   ChatSession / ChatSessionManager / 会话存储 / 触发路由
├─ SoufflePet.Core.Tests/          # xUnit（会话状态机、会话管理、记忆、调度、token 格式）
├─ SoufflePet.Plugin.Sdk/          # 插件契约（PluginBase、清单、配置表单、IMessagePlatform）
├─ Plugins/                        # 内置插件（独立构建，不在 sln 编译项）
│  ├─ SoufflePet.Plugin.ComputerUse/
│  ├─ SoufflePet.Plugin.BrowserUse/
│  ├─ SoufflePet.Plugin.DsmlCleaner/
│  └─ SoufflePet.Plugin.NapCat/
├─ Live2DCSharpSDK.Framework/      # Cubism 框架层
├─ Live2DCSharpSDK.App/            # 应用封装
└─ Live2DCSharpSDK.OpenGL/         # OpenGL ES2 渲染后端
```

## 许可证与文件头

双许可证，**新增源文件必须带对应 SPDX 头**：

- 主程序 `SoufflePet/`、`SoufflePet.Plugin.Sdk/`、`Plugins/`、解决方案配置 → GPL-3.0-or-later

```csharp
// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow
```

- Live2D SDK 子项目 `Live2DCSharpSDK.App` / `.Framework` / `.OpenGL` → MIT

```csharp
// SPDX-License-Identifier: MIT
```

## 编码约定

- 面向可空引用类型：项目 `Nullable` 一律 `enable`，不要引入新的可空性警告。
- 命名空间与目录、文件名对齐；宿主服务放 `SoufflePet/Services/`，领域模型与服务放 `SoufflePet.Core/`，前端组件放 `SoufflePet/web/src/`。
- 依赖注入优先：`Services/` 中面向窗口消费的服务常用 `IXxxService` 接口 + 实现，在 `App.axaml.cs` 注册（如 `IAutoExpressionService`、`IAppWindowService`）。新增服务遵循同一模式。
- 日志统一用 Serilog：`private static readonly ILogger Logger = Log.ForContext<T>();`（`Services` 中普遍如此）。
- UI 线程：涉及 UI 的更新经 `Avalonia.Threading.Dispatcher` 切换；后台线程（平台回调、检测轮询）不要长时间同步阻塞。
- 现有代码注释与提交均为**中文**，保持一致。

## 关键约束与陷阱

- **Live2D SDK 子项目为本地改造版**：来自上游 [Live2DCSharpSDK](https://github.com/Coloryr/Live2DCSharpSDK)（MIT），本仓库有大量适配桌宠的修改与 Bug 修复。改动前先读 README 的「对 Live2DCSharpSDK 的本地修改」一节，避免覆盖已有本地改动。源文件行尾为 CRLF，与上游 diff 需 `--strip-trailing-cr`。
- **wwwroot 不入库**：`SoufflePet/wwwroot/` 下的 `app/`（构建产物）与 `models/`（模型资源）全部 gitignore；**不附带、不分发任何 Live2D 模型资源**，不要提交模型文件或把它当作可用资源断言。
- **插件与宿主分开构建**：插件工程不在 `SoufflePet.sln` 中。

  ```powershell
  # 本地调试：Debug 构建后自动部署到 SoufflePet/bin/Debug/net10.0/plugins/<id>/
  dotnet build Plugins\SoufflePet.Plugin.ComputerUse

  # 发行：生成可导入的 zip 到 Plugins\<项目>\artifacts\
  dotnet publish Plugins\SoufflePet.Plugin.ComputerUse -c Release
  ```

  插件契约 `SoufflePet.Plugin.Sdk.dll` 与 `Microsoft.Extensions.AI.Abstractions.dll` 由宿主共享，插件**不要**自带副本。
- **配置落盘位置**：应用配置 `SoufflePet.json`（可执行文件同级，首次运行生成）；MCP 配置 `~/.souffle/mcp.json`；会话 `~/.souffle/sessions`（`index.json` + 按工作区分组的 `<workspace-group>\<id>\conversation.jsonl`，编辑 diff 存同目录 `diffs\`，JSONL 只存引用；覆盖写入前的旧版本存 `backups\`）；长期记忆/统计 `%AppData%\SoufflePet`；插件 `~/.souffle/plugins/<id>`；技能 `~/.souffle/skills`。修改配置相关逻辑时注意这些路径，不要把 MCP / 记忆写进 `SoufflePet.json`。
- **验收/测试隔离用户数据**：设置环境变量 `SOUFFLEPET_DATA_DIR` 可把应用数据根目录（会话、记忆、统计、调度日志、窗口几何）指到临时目录；不设置时会话在 `~/.souffle/sessions`，其余数据仍为 `%AppData%\SoufflePet`。
- **UI 架构**：React SPA 是唯一对话/设置界面；桌宠只有 `NativePetWindow` 一个原生渲染窗口。WebView2 初始化失败会记录错误并弹一次系统提示，**不再回退旧 Avalonia 窗口**；`--legacy-ui`、`--react-ui`、`--webview` 等迁移期开关已删除。
- **不做向后兼容**：当前项目未正式发布，且处于快速迭代阶段，不做向后兼容。

## 快速定位

| 需求 | 入口文件 |
| --- | --- |
| 应用启动 / DI 注册 | [Program.cs](SoufflePet/Program.cs)、[App.axaml.cs](SoufflePet/App.axaml.cs) |
| React SPA 源码 | [web/src](SoufflePet/web/src)、[App.tsx](SoufflePet/web/src/app/App.tsx) |
| React 宿主窗口 / 桥 | [AppWebWindow.cs](SoufflePet/Views/Web/AppWebWindow.cs)、[WebBridgeServer.cs](SoufflePet/Services/Web/WebBridgeServer.cs)、[ChatRpc.cs](SoufflePet/Services/Web/ChatRpc.cs) |
| 设置 API / 设置页 | [SettingsApi.cs](SoufflePet/Services/Web/SettingsApi.cs)、[SettingsDto.cs](SoufflePet/Services/Web/SettingsDto.cs)、[features/settings](SoufflePet/web/src/features/settings) |
| 桥接契约 / mock 宿主 | [react-ui-contract.md](docs/react-ui-contract.md)、[client.ts](SoufflePet/web/src/bridge/client.ts)、[mock-host.ts](SoufflePet/web/src/bridge/mock-host.ts) |
| OKLCH 主题 | [tokens.css](SoufflePet/web/src/styles/tokens.css)、[oklch.ts](SoufflePet/web/src/theme/oklch.ts)、[ThemeService.cs](SoufflePet/Services/Web/ThemeService.cs) |
| Core 领域层 / 组合根 | [SoufflePet.Core](SoufflePet.Core)、[CoreServiceCollectionExtensions.cs](SoufflePet.Core/Services/CoreServiceCollectionExtensions.cs) |
| 会话状态机 / 会话管理 | [ChatSession.cs](SoufflePet.Core/Chat/ChatSession.cs)、[ChatSessionManager.cs](SoufflePet.Core/Chat/ChatSessionManager.cs)、[ChatSessionStore.cs](SoufflePet.Core/Chat/ChatSessionStore.cs) |
| Core 测试 | [CoreTests.cs](SoufflePet.Core.Tests/CoreTests.cs)、[ChatSessionTests.cs](SoufflePet.Core.Tests/ChatSessionTests.cs) |
| 对话与工具调用 | [LlmChatService.cs](SoufflePet.Core/Services/LlmChatService.cs)、[ChatTools.cs](SoufflePet.Core/Services/ChatTools.cs) |
| 主窗口呼出 / 热键 / 戳一戳 | [AppWindowService.cs](SoufflePet/Services/AppWindowService.cs) |
| 对话模型配置 / 设置页 | [ChatProviderConfig.cs](SoufflePet.Core/Models/ChatProviderConfig.cs)、[LlmSettings.tsx](SoufflePet/web/src/features/settings/LlmSettings.tsx)、[LlmModelCatalog.cs](SoufflePet.Core/Services/LlmModelCatalog.cs) |
| 消息气泡 / Markdown 渲染 | [MessageList.tsx](SoufflePet/web/src/features/chat/MessageList.tsx)、[Markdown.tsx](SoufflePet/web/src/ui/Markdown.tsx) |
| 桌宠渲染窗口 | [NativePetWindow.cs](SoufflePet/Views/NativePetWindow.cs) |
| 插件系统 | [PluginService.cs](SoufflePet.Core/Services/Plugins/PluginService.cs)、[PluginBase.cs](SoufflePet.Plugin.Sdk/PluginBase.cs) |
| MCP 集成 | [McpToolService.cs](SoufflePet.Core/Services/McpToolService.cs) |
| 定时任务 / 记忆 / 调度 | [ScheduledTaskService.cs](SoufflePet.Core/Services/ScheduledTaskService.cs)、[ChatMemory.cs](SoufflePet.Core/Services/ChatMemory.cs) |
| 自动表情 / 口型 | [AutoExpressionService.cs](SoufflePet/Services/AutoExpressionService.cs)、[LipSyncService.cs](SoufflePet/Services/LipSyncService.cs) |