# AGENTS.md

面向在本仓库工作的 AI 编码代理的指引。人读文档见 [README.md](README.md)，插件开发见 [docs/plugin-development.md](docs/plugin-development.md)。

## 项目概览

SoufflePet 是基于 [Avalonia](https://avaloniaui.net/) 的 Windows Live2D 桌面桌宠与 Agent 框架。主程序为 `SoufflePet/`（`net10.0`，`WinExe`），内嵌 Live2D 渲染 SDK，并通过插件系统扩展工具 / 正文后处理 / 消息平台。

## 常用命令

```powershell
# 构建整个解决方案
dotnet build SoufflePet.sln

# 原生版（默认，Live2DCSharpSDK + OpenGL ES2）
dotnet run --project SoufflePet

# WebView 版（WebView2 + WebGL）
dotnet run --project SoufflePet -- --webview
```

- 运行依赖 Windows 10+、.NET 10 SDK。WebView 版需 WebView2 Runtime，原生版需支持 OpenGL ES2 的显卡驱动。
- 仓库**没有单元测试项目**。改动后以 `dotnet build SoufflePet.sln` 验证编译（`-warnaserror` 未启用，但请勿引入新警告），必要时手动运行应用验证行为。
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
│  ├─ Views/                       # 窗口与视图（桌宠、对话、MCP / 技能 / 插件设置）
│  ├─ ViewModels/                  # MVVM 视图模型（CommunityToolkit.Mvvm）
│  ├─ Services/                    # LLM 对话、工具、MCP、技能、记忆、表情、平台等
│  │  └─ Plugins/                  # 插件发现、加载、响应管线
│  ├─ Models/                      # 设置、消息、模型、MCP 等数据模型
│  ├─ Utils/                       # 截图、开机自启、全局光标等
│  ├─ Styles/                      # 深色玻璃面板等 axaml 样式
│  ├─ native/                      # 原生渲染控件与 Avalonia→OpenGL 桥接
│  ├─ wwwroot/                     # 前端页面与 Live2D 模型资源（gitignore，仅 index.html 入库）
│  └─ Assets/                      # 图标、Live2DCubismCore.dll
├─ SoufflePet.Plugin.Sdk/          # 插件契约（PluginBase、清单、配置表单、IMessagePlatform）
├─ Plugins/                        # 内置插件（独立构建，不在 sln 编译项）
│  ├─ SoufflePet.Plugin.InputKit/
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
- 命名空间与目录、文件名对齐；服务放 `Services/`，数据模型放 `Models/`，视图模型放 `ViewModels/`。
- 依赖注入优先：`Services/` 中面向窗口消费的服务常用 `IXxxService` 接口 + 实现，在 `App.axaml.cs` 注册（如 `IAutoExpressionService`）。新增服务遵循同一模式。
- 日志统一用 Serilog：`private static readonly ILogger Logger = Log.ForContext<T>();`（`Services` 中普遍如此）。
- UI 线程：涉及 UI 的更新经 `Avalonia.Threading.Dispatcher` 切换；后台线程（平台回调、检测轮询）不要长时间同步阻塞。
- 现有代码注释与提交均为**中文**，保持一致。
- 不引入新依赖除非确有必要；跟随已有的版本（Avalonia 12.1.1、Microsoft.Extensions.AI 10.11.0 等）。

## 关键约束与陷阱

- **Live2D SDK 子项目为本地改造版**：来自上游 [Live2DCSharpSDK](https://github.com/Coloryr/Live2DCSharpSDK)（MIT），本仓库有大量适配桌宠的修改与 Bug 修复。改动前先读 README 的「对 Live2DCSharpSDK 的本地修改」一节，避免覆盖已有本地改动。源文件行尾为 CRLF，与上游 diff 需 `--strip-trailing-cr`。
- **wwwroot 不入库**：`SoufflePet/wwwroot/` 除 `index.html` 外全部 gitignore；**不附带、不分发任何 Live2D 模型资源**，不要提交模型文件或把它当作可用资源断言。
- **插件与宿主分开构建**：插件工程不在 `SoufflePet.sln` 中。

  ```powershell
  # 本地调试：Debug 构建后自动部署到 SoufflePet/bin/Debug/net10.0/plugins/<id>/
  dotnet build Plugins\SoufflePet.Plugin.InputKit

  # 发行：生成可导入的 zip 到 Plugins\<项目>\artifacts\
  dotnet publish Plugins\SoufflePet.Plugin.InputKit -c Release
  ```

  插件契约 `SoufflePet.Plugin.Sdk.dll` 与 `Microsoft.Extensions.AI.Abstractions.dll` 由宿主共享，插件**不要**自带副本。
- **配置落盘位置**：应用配置 `SoufflePet.json`（可执行文件同级，首次运行生成）；MCP 配置 `~/.souffle/mcp.json`；长期记忆 `%AppData%\SoufflePet`；插件 `~/.souffle/plugins/<id>`；技能 `~/.souffle/skills`。修改配置相关逻辑时注意这些路径，不要把 MCP / 记忆写进 `SoufflePet.json`。
- 模型在**启动时**加载，`ModelName` / `ModelsDirectory` 改后需重启生效。

## 快速定位

| 需求 | 入口文件 |
| --- | --- |
| 应用启动 / DI 注册 | [Program.cs](SoufflePet/Program.cs)、[App.axaml.cs](SoufflePet/App.axaml.cs) |
| 对话与工具调用 | [LlmChatService.cs](SoufflePet/Services/LlmChatService.cs)、[ChatTools.cs](SoufflePet/Services/ChatTools.cs) |
| 桌宠渲染窗口 | [NativePetWindow.cs](SoufflePet/Views/NativePetWindow.cs)、[WebViewPetWindow.cs](SoufflePet/Views/WebViewPetWindow.cs) |
| 插件系统 | [PluginService.cs](SoufflePet/Services/Plugins/PluginService.cs)、[PluginBase.cs](SoufflePet.Plugin.Sdk/PluginBase.cs) |
| MCP 集成 | [McpToolService.cs](SoufflePet/Services/McpToolService.cs) |
| 自动表情 / 口型 | [AutoExpressionService.cs](SoufflePet/Services/AutoExpressionService.cs)、[LipSyncService.cs](SoufflePet/Services/LipSyncService.cs) |