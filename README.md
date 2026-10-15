# 舒芙蕾桌宠（ShufuleiPet）

基于 [Avalonia](https://avaloniaui.net/) 的 Windows 桌面桌宠，展示 Live2D 模型「舒芙蕾」。提供两种渲染模式：WebView（WebGL）版与原生 OpenGL 版，共用同一套托盘、拖拽与全局鼠标追踪逻辑。

## 功能

- **透明置顶窗口**：无边框、透明背景、置顶显示，默认停靠屏幕右下角
- **视线全局跟随**：宿主用 `GetCursorPos` 轮询全局光标（而非窗口内指针事件），鼠标移出窗口甚至在其他显示器上，模型视线仍持续追踪
- **交互**（两种模式通用）：
  - 左键拖动 → 移动窗口
  - 中键拖动 → 移动模型
  - 左键单击 → 随机切换表情
  - 滚轮 → 缩放模型
- **托盘图标**：显示 / 隐藏、退出；关闭窗口默认隐藏到托盘
- **自动表情**：根据当前系统状态，自动切换游戏/音乐表情
- **口型同步**：在音乐模式下，根据当前播放的音乐，自动同步模型的口型

## 两种运行模式

| 模式 | 启动参数 | 渲染方式 |
| --- | --- | --- |
| 原生版（默认） | （无参数） | Live2DCSharpSDK + OpenGL ES2 |
| WebView 版 | `--webview` | WebView2 + WebGL（Live2D JS SDK） |

## 环境要求

- Windows 10+，.NET 10 SDK
- WebView 版需要 [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)
- 原生版需要支持 OpenGL ES2 的显卡驱动

## 构建与运行

```powershell
# 构建整个解决方案
dotnet build ShufuleiPet.sln

# 原生版（默认）
dotnet run --project ShufuleiPet

# WebView 版
dotnet run --project ShufuleiPet -- --webview
```

## 解决方案结构

```
ShufuleiPet.sln
├─ ShufuleiPet/                  # 主程序（Avalonia 桌面应用）
│  ├─ Views/                     # 窗口层
│  │  ├─ WebViewPetWindow.cs    #   WebView 版窗口（纯代码，+ 本地静态服务器）
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

## 资源说明

Live2D 模型资源位于 `wwwroot/models/`，模型版权归原作者所有，不随仓库分发，请自行放入合法授权的模型文件后再构建运行。

## 鸣谢

[Live2D Cubism](https://www.live2d.com/zh-CHS/)
[Live2DCSharpSDK](https://github.com/live2d/Live2DCSharpSDK)