# 舒芙蕾桌宠（ShufuleiPet）

基于 [Avalonia](https://avaloniaui.net/) 的 Windows 桌面桌宠，展示 Live2D 模型「舒芙蕾」。提供两种渲染模式：WebView（WebGL）版与原生 OpenGL 版，共用同一套托盘、拖拽与全局鼠标追踪逻辑。

## 功能

- **透明置顶窗口**：无边框、透明背景、置顶显示，默认停靠屏幕右下角
- **视线全局跟随**：宿主用 `GetCursorPos` 轮询全局光标（而非窗口内指针事件），鼠标移出窗口甚至在其他显示器上，模型视线仍持续追踪
- **交互**（两种模式通用）：
  - 按住拖动 → 移动窗口（系统级拖拽）
  - 左键单击 → 随机切换表情
  - 右键单击 → 重播 Idle 动作
  - 滚轮 → 缩放模型
- **点击穿透**（原生版）：逐像素读取帧缓冲 alpha 判定光标是否压在模型上；不在模型上时给窗口挂 `WS_EX_TRANSPARENT`，点击落到下层窗口，压在模型上（或按住鼠标期间）恢复交互
- **托盘图标**：显示 / 隐藏、退出；关闭窗口默认隐藏到托盘

## 两种运行模式

| 模式 | 启动参数 | 渲染方式 | 说明 |
| --- | --- | --- | --- |
| WebView 版 | （默认） | WebView2 + WebGL（Live2D JS SDK） | 宿主内置 `HttpListener` 静态服务器（端口 18923 起，最多顺延 10 个），加载 `wwwroot/index.html` |
| 原生版 | `--native` | Live2DCSharpSDK + OpenGL ES2 | 不依赖 WebView，Cubism Native SDK 的 C# 移植直接渲染 |

## 环境要求

- Windows 10+，.NET 10 SDK
- WebView 版需要 [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)
- 原生版需要支持 OpenGL ES2 的显卡驱动

## 构建与运行

```powershell
# 构建整个解决方案
dotnet build ShufuleiPet.sln

# WebView 版（默认）
dotnet run --project ShufuleiPet

# 原生版
dotnet run --project ShufuleiPet -- --native
```

## 解决方案结构

```
ShufuleiPet.sln
├─ ShufuleiPet/                  # 主程序（Avalonia 桌面应用）
│  ├─ Views/                     # 窗口层
│  │  ├─ MainWindow.axaml(.cs)   #   WebView 版窗口 + 本地静态服务器
│  │  └─ NativePetWindow.cs      #   原生版窗口（拖拽 / 穿透 / 全局光标）
│  ├─ native/                    # 原生渲染控件
│  │  ├─ NativePetControl.cs     #   OpenGL 渲染循环、逐像素命中检测
│  │  └─ AvaloniaApi.cs          #   Avalonia GlInterface → OpenGLApi 桥接
│  ├─ wwwroot/                   # 前端页面与 Live2D 模型资源（已 gitignore）
│  └─ Assets/                    # 应用图标等资源
├─ Live2DCSharpSDK.Framework/    # Cubism 框架层（数学、Motion、模型解析）
├─ Live2DCSharpSDK.App/          # 应用封装（LAppDelegate、LAppModel、Live2dManager）
└─ Live2DCSharpSDK.OpenGL/       # OpenGL ES2 渲染后端（CubismRenderer_OpenGLES2）
```

> `Live2DCSharpSDK.Vulkan*` 项目未纳入解决方案，且已被 `.gitignore` 忽略。

## 日志

使用 [Serilog](https://serilog.com/) 输出，内容为英文（避免控制台乱码）：

- 文件：`logs/pet-YYYYMMDD.log`，按天滚动，保留 7 天
- 控制台：DEBUG 构建额外输出
- 级别：Debug 及以上；未处理异常记录 Fatal 并在退出前刷盘

## 资源说明

Live2D 模型资源位于 `ShufuleiPet/wwwroot/`（含 `舒芙蕾/` 模型目录），整个 `wwwroot` 已被 `.gitignore` 忽略，不随仓库分发，请自行放入合法授权的模型文件后再构建运行。
