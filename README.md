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
- **交互**（两种模式通用）：
  - 左键拖动 → 移动窗口
  - 中键拖动 → 移动模型
  - 左键单击 → 呼出 / 收起对话窗口
  - 右键 → 交互菜单：「戳一戳」，先播放被戳反应动作再发起一轮对话唤醒桌宠
  - 滚轮 → 缩放模型
- **托盘图标**：显示 / 隐藏、重置模型、对话窗口、退出；关闭窗口默认隐藏到托盘
- **自动表情**：根据当前系统状态，自动切换游戏/音乐表情
- **口型同步**：在音乐模式（自动检测或手动开启）下，根据当前播放的音乐，自动同步模型的口型
- **Agent**：`Ctrl+Alt+Space`、左键单击桌宠或托盘菜单「对话窗口」呼出对话窗口，支持 OpenAI 兼容接口、流式回复、思考过程展示、工具调用、MCP 集成、技能、长期记忆与插件（详见下节）
- **运行统计**：陪伴时长（累计程序运行秒数，跨重启累计）与 token 累计用量，常驻对话窗口底部信息栏并实时增长，桌宠也可用 `get_statistics` 工具查询
- **插件系统**：独立程序集 + `plugin.json` 清单，可扩展工具、处理模型正文、接入新的消息平台；支持界面启停、配置表单与 zip 导入

## Agent

`Ctrl+Alt+Space`、左键单击桌宠或托盘菜单「对话窗口」呼出对话窗口（再次触发收起）。窗口为单例，隐藏后内容保留；停止按钮可中断当前一轮生成。

### 对话模型

走 OpenAI 兼容接口。首次启动 `ChatModels` 为空，在对话窗口的模型菜单里点「添加模型」填写即可：

| 字段 | 说明 |
| --- | --- |
| `Name` | 显示名称（模型列表里看到的名字） |
| `ModelId` | 请求中的 `model` 字段，例如 `deepseek-chat` |
| `BaseUrl` | 兼容接口地址，例如 `https://api.example.com/v1` |
| `ApiKey` | API 密钥，可留空 |
| `Provider` | 可选的提供商标注，仅用于展示 |

以上写入 `SoufflePet.json` 的 `ChatModels` 数组，`ChatModelName` 记录当前选中的模型。人设由 `ChatSystemPrompt` 决定，称呼 `PetName` 会同步到窗口标题与默认人设。

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

当前时间不由工具提供，而是随消息下发：`TimeAwarenessEnabled` 打开时（默认），每轮把当前本地时间（含星期、UTC 偏移与时区）附加到本轮用户消息末尾，模型无需调用工具即可感知时间；关闭后模型看不到任何时间信息。时间戳只存在于当轮请求，不写入界面历史与 `conversations.jsonl`。

### MCP 服务器

对话窗口「更多操作 → MCP 服务器…」里配置，配置单独保存在用户家目录的 `~/.souffle/mcp.json`（不写进 `SoufflePet.json`）。文件用通行的 MCP 配置格式，可直接照抄 VS Code / Claude 等客户端的配置：

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

插件是独立程序集，通过 `plugin.json` 声明，无需修改主程序即可为模型增加工具、对回复做流式后处理，或接入新的消息平台（如 QQ）。对话窗口「更多操作 → 插件…」里可以查看、启用 / 禁用、编辑配置、重新扫描与导入 zip 包。

插件从应用目录的 `plugins/` 与用户家目录的 `.souffle/plugins` 读取；导入的插件装在后者的 `<id>` 子目录下，成功后自动启用。启用状态记录在 `SoufflePet.json` 的 `DisabledPlugins`。

内置插件：

| 插件 id | 作用 |
| --- | --- |
| `input-kit` | 鼠标点击 / 拖拽 / 滚轮，键盘按键、组合键与文本输入 |
| `dsml-cleaner` | 清理模型正文中泄漏的 DSML 工具调用标记 |
| `napcat` | 通过 NapCat 的 OneBot 11 正向 WebSocket 接入 QQ，被 @ 时提醒桌宠并按会话回复 |

清单字段、最小示例、生命周期钩子、配置表单、消息平台接入与打包规则见[插件开发指南](docs/plugin-development.md)。

### 技能

技能是模型按需读取的说明文档，放在 `.agents/skills/<技能名>/SKILL.md` 或 `.agents/skills/<技能名>.md`。扫描目录：

- 当前工作区（对话窗口底部可切换）下的 `.agents/skills`、`.souffle/skills`
- 用户家目录下的 `.agents/skills`、`.souffle/skills`

已启用的技能会以清单形式注入系统提示词，模型需要时用 `read_skill` 取正文；`DisabledSkills` 记录被停用的技能。设置界面支持从 zip 压缩包安装技能，安装位置固定为用户家目录下的 `.souffle/skills`，不随工作区变化；「卸载」会删除选中技能所在的目录（直接放在技能根目录下的单个 `.md` 则只删该文件），仅限上述扫描目录内的技能，操作前有二次确认。

### 长期记忆

`ChatMemoryEnabled` 打开时对话落盘到 `%AppData%\SoufflePet`：

- `conversations.jsonl`：每轮问答与工具调用记录（启动时据此重建请求上下文）
- `profile.json`：模型通过 `remember` 写入的事实（称呼、喜好、常用路径等）

检索用中文 2-gram + BM25，不依赖分词库或向量服务。

### 统计信息

`IStatisticsService` 统一统计桌宠的运行信息，落盘在 `%AppData%\SoufflePet\statistics.json`，目前包含：

- **陪伴时长**：有史以来程序运行的总时长（关闭即停、下次启动接着累加），每 60 秒与退出时落盘
- **token 用量**：有史以来累计输入 / 输出 token 与缓存命中率，每轮结束即时落盘

二者常驻对话窗口底部信息栏并实时增长，模型可通过 `get_statistics` 工具查询全部或单项；新增统计项时在 `IStatisticsService` 与实现里各补一处即可。每轮结束还会在助手气泡底部显示本轮 token 用量。

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
dotnet build SoufflePet.sln

# 原生版（默认）
dotnet run --project SoufflePet

# WebView 版
dotnet run --project SoufflePet -- --webview
```

## 解决方案结构

```
SoufflePet.sln
├─ SoufflePet/                  # 主程序（Avalonia 桌面应用）
│  ├─ Views/                     # 窗口与视图层（桌宠窗口、对话窗口、MCP / 技能设置）
│  │  ├─ WebViewPetWindow.cs    #   WebView 版窗口（纯代码，+ 本地静态服务器）
│  │  └─ NativePetWindow.cs      #   原生版窗口（拖拽 / 穿透 / 全局光标）
│  ├─ ViewModels/                # 对话、MCP、技能与提问面板的视图模型
│  ├─ Services/                  # LLM 对话、工具调用、MCP、技能、记忆、表情与检测
│  ├─ Models/                    # 设置、消息、模型与 MCP 配置等数据模型
│  ├─ Utils/                     # 截图等辅助代码
│  ├─ Styles/                    # 深色玻璃面板等外观样式
│  ├─ native/                    # 原生渲染控件
│  │  ├─ NativePetControl.cs     #   OpenGL 渲染循环、逐像素命中检测
│  │  └─ AvaloniaApi.cs          #   Avalonia GlInterface → OpenGLApi 桥接
│  ├─ wwwroot/                   # 前端页面与 Live2D 模型资源（已 gitignore）
│  └─ Assets/                    # 应用图标等资源
├─ SoufflePet.Plugin.Sdk/        # 插件契约（清单、基类、配置表单、消息平台接口）
├─ Plugins/                      # 随主程序分发的内置插件
│  ├─ SoufflePet.Plugin.InputKit/      # 键鼠输入工具
│  ├─ SoufflePet.Plugin.DsmlCleaner/   # DSML 标记清理
│  └─ SoufflePet.Plugin.NapCat/        # NapCat QQ 消息平台
├─ Live2DCSharpSDK.Framework/    # Cubism 框架层（数学、Motion、模型解析）
├─ Live2DCSharpSDK.App/          # 应用封装（LAppDelegate、LAppModel、Live2dManager）
└─ Live2DCSharpSDK.OpenGL/       # OpenGL ES2 渲染后端（CubismRenderer_OpenGLES2）
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
- `LAppModel.LipSyncValue` / `LipSyncWideValue`：口型音量外部输入（第 1 个口型参数写嘴开闭、其余写水平宽窄），替换官方示例里写死 `0` 的实现
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

1. 把获得授权的模型放入 `wwwroot/models/目录名/`（`ModelsDirectory` 默认为 `models`）。
2. 确认该目录下存在 `主文件名.model3.json`（目录名与主文件名相同或不同都可以，见下）。
3. 在 `SoufflePet.json` 中把 `ModelName` 设为 `目录名/主文件名`；两者一致时只写一段即可。
4. 重启应用（模型仅在启动时加载）。

未配置模型时应用**仍可正常启动**，只是窗口不渲染模型；WebView 版会在左上角显示缺少模型的提示。

### 配置模型

修改 `SoufflePet.json`（首次运行自动生成，位于可执行文件同级目录）两个键即可：

```json
{
  "ModelsDirectory": "models",
  "ModelName": "你的模型名"
}
```

- `ModelsDirectory`：`wwwroot/` 内的模型根目录（相对路径）
- `ModelName`：模型目录名与 `model3.json` 主文件名。两者一致时写一段（`你的模型名`，对应 `wwwroot/models/你的模型名/你的模型名.model3.json`）；两者不一致时写成 `目录名/主文件名`（对应 `wwwroot/models/目录名/主文件名.model3.json`，分隔符用 `/` 或 `\` 均可）
- VTube Studio 导出的模型（目录内有 `.vtube.json`）：原生版会读取其中的 `ParameterSettings`，把面捕角度（`FaceAngleX/Y/Z`）按作者配置的范围映射到 `AngleX/AngleY/AngleZ`、`PositionX/Y/Z` 等自定义参数，保证这类模型的物理链有输入；普通模型不受影响
- 两个键都在启动时读取，**修改后需重启生效**

### 模型相关的可配置项

`SoufflePet.json` 中还有两个表情名配置：

```json
{
  "AutoExpressionGameExpression": "游戏表情名",
  "AutoExpressionMusicExpression": "音乐表情名"
}
```

取值须为模型 `model3.json` 中 `FileReferences.Expressions` 列出的表情文件名（去掉 `.exp3.json`）。默认值是「手柄」「麦克风」——表情名因模型而异，换模型后请按实际表情文件填写，否则自动表情不会生效；留空表示不切换表情。

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