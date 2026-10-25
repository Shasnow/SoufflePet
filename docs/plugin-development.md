# 插件开发指南

SoufflePet 的插件是独立的 .NET 程序集，通过 `plugin.json` 声明后由宿主发现、加载，可以向对话模型提供工具（`AIFunction`），也可以对模型回复做流式后处理（响应钩子）。新增能力不需要修改主程序代码。

## 1. 架构与运行方式

```
SoufflePet（宿主）
└─ PluginService
   ├─ 扫描 plugins 目录 → 读取 plugin.json
   ├─ 每个插件独占一个可卸载的 AssemblyLoadContext
   ├─ 实例化 PluginBase → InitializeAsync → BuildTools（触发 OnToolsBuilding 收集工具）→ 每轮响应钩子
   ├─ 每轮对话：本地工具 + 插件工具 + MCP 工具 → ChatOptions.Tools
   └─ 每轮正文：按插件顺序串联响应钩子 → 界面
```

- 契约程序集 `SoufflePet.Plugin.Sdk.dll` 与 `Microsoft.Extensions.AI.Abstractions.dll` 由宿主共享，插件不要自带这两个 dll 的副本。
- 插件入口类型必须继承 `PluginBase`；宿主按它发现、实例化插件，并调用 `InitializeAsync` / `BuildTools` / `ShutdownAsync`。
- 插件工具与主程序工具走同一条 `AIFunction` 调用通道：模型发起调用 → 宿主执行 → 结果回传模型。
- 插件工具由宿主套上守卫：单次调用超时（`timeoutSeconds`）与异常捕获，失败只影响该次调用。
- 插件响应钩子也有异常守卫：处理失败时原样放行文本，插件失败次数 +1，不影响本次回复。

## 2. 依赖要求

| 项 | 要求 |
| --- | --- |
| 目标框架 | `net10.0` |
| 契约包 | 项目引用 `SoufflePet.Plugin.Sdk` |
| AI 抽象 | `Microsoft.Extensions.AI`（`AIFunctionFactory` 与 `AIFunction` 位于 `Microsoft.Extensions.AI.Abstractions`） |
| 版本 | 与宿主使用的版本保持一致（当前 10.11.0） |

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\SoufflePet.Plugin.Sdk\SoufflePet.Plugin.Sdk.csproj" />
    <PackageReference Include="Microsoft.Extensions.AI" Version="10.11.0" />
  </ItemGroup>

  <ItemGroup>
    <None Update="plugin.json">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
  </ItemGroup>
</Project>
```

## 3. 最小插件示例

### plugin.json

```json
{
  "id": "hello-kit",
  "name": "示例插件",
  "version": "1.0.0",
  "author": "your-name",
  "description": "提供一个示例工具",
  "assembly": "HelloKit.dll",
  "entryType": "HelloKit.HelloPlugin",
  "timeoutSeconds": 30,
  "config": {
    "greeting": "你好"
  }
}
```

字段说明：

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `id` | 是 | 唯一标识，只能包含字母、数字、`-`、`_`；同时用于设置持久化与工具名冲突时的前缀 |
| `assembly` | 是 | 入口程序集相对插件目录的路径 |
| `name` | 否 | 展示名称，缺省用 `id` |
| `version` / `author` / `description` | 否 | 界面展示信息 |
| `entryType` | 否 | 入口类型全名；省略时取程序集内唯一的 `PluginBase` 子类 |
| `timeoutSeconds` | 否 | 单个工具调用超时秒数，宿主限制 1–300，默认 30 |
| `config` | 否 | 任意 JSON，原样交给 `PluginContext.Configuration` |
| `configSchema` | 否 | 配置表单描述；声明后插件窗口会生成可视化配置，见第 6 节 |

### HelloPlugin.cs

```csharp
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using SoufflePet.Plugin.Sdk;

namespace HelloKit;

public sealed class HelloPlugin : PluginBase
{
    protected override void OnToolsBuilding(ICollection<AIFunction> tools)
    {
        // 配置是 plugin.json 的 config 段，未声明时为 null
        var greeting = Context.Configuration?.TryGetProperty("greeting", out var value) == true
            ? value.GetString()
            : "你好";

        tools.Add(AIFunctionFactory.Create(
            () => Greet(greeting!),
            "hello_greet",
            "向用户打招呼。"));

        Context.Logger.LogInformation("已就绪，共 {Count} 个工具", tools.Count);
    }

    private static string Greet(string greeting) => $"{greeting}，我是插件！";
}
```

带参数的工具：

```csharp
private static string Echo(
    [Description("要回显的文本")]
    string text,
    CancellationToken cancellationToken = default) => text;
```

参数上的 `[Description]` 会进入工具的 JSON Schema，模型据此填写参数，务必写清楚。

## 4. 响应后处理钩子（可选）

除了工具，插件还可以对模型正文做流式后处理，重写三个钩子即可：

```csharp
public sealed class MyPlugin : PluginBase
{
    private readonly MyFilter _filter = new();

    // 每轮回复开始：返回 false 表示本轮不参与；参与前重置状态
    protected override bool OnResponseStarting(string modelId)
    {
        if (!modelId.Contains("deepseek", StringComparison.OrdinalIgnoreCase)) return false;
        _filter.Reset();
        return true;
    }

    // 每收到一段正文增量
    protected override string OnResponseDelta(string text) => _filter.Process(text);

    // 本轮正文结束：返回仍暂存的内容
    protected override string OnResponseCompleted() => _filter.Flush();
}

internal sealed class MyFilter
{
    public void Reset() { }
    public string Process(string text) => text;   // 可返回空串暂存
    public string Flush() => string.Empty;
}
```

不提供工具的插件不需要重写任何工具钩子；初始化逻辑（读取配置、打日志等）放在 `OnInitializedAsync` 里即可。

- `OnResponseStarting(modelId)` 每轮调用一次，默认返回 `false`，必须重写并返回 `true` 才会参与。
- 插件实例跨轮复用，务必在 `OnResponseStarting` 里清空缓冲；`OnResponseDelta` 可能收到被拆开的半个标记，返回空串可暂存。
- 多个插件按启用顺序串联：`OnResponseDelta` 依次处理每个增量；收尾时某个插件的 `OnResponseCompleted` 返回值会继续经过后面的插件。
- 钩子抛异常时，宿主原样放行文本并把插件失败次数 +1，本轮后续不再调用该插件。
- 典型用途：清理模型泄漏的 DSML 工具调用标记，可按 `modelId` 只对特定模型生效。

内置的 `dsml-cleaner` 插件就是该机制的示例，`config.models` 为空表示全部模型：

```json
"config": {
  "models": ["deepseek-chat", "deepseek-reasoner"]
}
```

## 5. 消息平台插件（可选）

插件除了扩展工具与正文，还可以把外部聊天平台接进来。让入口类同时实现 `IMessagePlatform` 即可：插件启用时宿主自动把实例注册到 `PlatformService`，停用或卸载时自动注销并退订 `OnMentioned`，插件不需要自己向宿主登记。

### 接口

```csharp
public interface IMessagePlatform
{
    string Id { get; }
    string? RolePrompt { get; }
    Task<IReadOnlyList<PlatformConversation>> GetConversationsAsync();
    Task<string> GetMessagesAsync(string conversationId, int limit = 20);
    Task SendMessageAsync(string conversationId, string message);
    Task SendAttachmentAsync(string conversationId, PlatformAttachment attachment);
    event EventHandler<PlatformIncomingMessage>? OnMentioned;
}
```

| 成员 | 说明 |
| --- | --- |
| `Id` | 平台标识，模型工具里的 `platform` 参数用它；与 `plugin.json` 的 `id` 无关 |
| `RolePrompt` | 平台角色说明，注入系统提示词（如「这是你自己的 QQ」「你正在托管主人的 QQ」）；null 或空白时不注入 |
| `GetConversationsAsync` | 返回已知会话，按最近活跃排序；只返回本次运行期间收到过消息的会话也可以 |
| `GetMessagesAsync` | 返回该会话最近消息的文本，格式自定，但应带上会话 id 与发送者 |
| `SendMessageAsync` | 向指定会话发送文本 |
| `SendAttachmentAsync` | 向指定会话发送本地图片或文件 |
| `OnMentioned` | 插件认为需要唤醒桌宠时触发，可能在任意后台线程 |

### 数据类型

| 类型 | 字段 | 用途 |
| --- | --- | --- |
| `PlatformConversation` | `ConversationId`、`DisplayName` | 会话；id 建议用 `kind:id` 形式，如 `group:12345`、`private:777` |
| `PlatformIncomingMessage` | `PlatformId`、`Content`、`Sender`、`MessageId` | 唤醒通知；宿主按 `MessageId` 去重，没有 id 时传 null |
| `PlatformAttachment` | `FilePath`、`Kind`、`DisplayName` | 要发送的附件，只传本地绝对路径 |
| `PlatformAttachmentKind` | `Image`、`File` | 附件类型 |

### 约定

- 「是否新消息、是否值得提醒、是否是自己发的」都由插件判定，宿主不关心平台细节；只有确实需要桌宠处理时才抛 `OnMentioned`（例如被 @、被引用）。
- 会话缓存要设上限，否则长时间挂机会持续增长（NapCat 插件按最近活跃淘汰，最多保留 200 个会话）。
- 附件只传路径，不要自己读成 base64 塞进模型上下文。宿主会先校验文件存在、不是目录、图片不超过 5 MB、其他文件不超过 50 MB，并按扩展名决定 `Kind`。
- `OnMentioned` 可能在后台线程触发，处理函数里不要长时间同步阻塞；耗时工作放到后台任务。

### 唤醒之后

宿主收到 `OnMentioned` 后，会把消息以「平台提醒」气泡插入对话并生成一轮回复（不会自动弹出窗口）。模型可用下列工具按会话操作：

| 工具 | 参数 | 作用 |
| --- | --- | --- |
| `get_messages` | `platform`、`conversation`、`limit` | 读取该会话最近消息 |
| `send_message` | `platform`、`conversation`、`message` | 向该会话发送文本 |
| `send_attachment` | `platform`、`conversation`、`path`、`display_name` | 发送本地图片或文件，按 `ToolApproval` 规则向用户确认 |

### 最小结构

```csharp
public sealed class DemoPlatformPlugin : PluginBase, IMessagePlatform
{
    public string Id => "demo";

    public string? RolePrompt => null;

    public event EventHandler<PlatformIncomingMessage>? OnMentioned;

    public Task<IReadOnlyList<PlatformConversation>> GetConversationsAsync() =>
        Task.FromResult<IReadOnlyList<PlatformConversation>>([]);

    public Task<string> GetMessagesAsync(string conversationId, int limit = 20) =>
        Task.FromResult(string.Empty);

    public Task SendMessageAsync(string conversationId, string message) => Task.CompletedTask;

    public Task SendAttachmentAsync(string conversationId, PlatformAttachment attachment) =>
        Task.CompletedTask;

    public override Task ShutdownAsync(CancellationToken cancellationToken)
    {
        // 断开 WebSocket / 停止轮询线程
        return Task.CompletedTask;
    }
}
```

参考实现见 `Plugins/SoufflePet.Plugin.NapCat`：OneBot 11 正向 WebSocket，含群聊 / 私聊、被 @ 或引用时唤醒、附件下载与发送。

## 6. 生命周期与线程约定

| 阶段 | 时机 | 约定 |
| --- | --- | --- |
| 构造 | 启用后首次加载时 | 构造函数应保持轻量，不要做耗时 I/O |
| `InitializeAsync` | 构造之后 | 宿主调用，不要重写；内部设置 `Context` 并触发 `OnInitializedAsync` |
| `OnInitializedAsync` | 初始化时 | 读取配置、准备资源；可用 `Context` 写日志 |
| `BuildTools` | 初始化之后 | 宿主调用，传入空集合并触发 `OnToolsBuilding` |
| `OnToolsBuilding` | 工具收集时 | 用 `tools.Add(...)` 添加工具；不提供工具时保持默认实现 |
| 调用 | 每轮对话中模型发起 | 可能在后台线程执行；参数里的 `CancellationToken` 会随对话取消触发 |
| `BeginResponse` → `OnResponseStarting` | 每轮回复开始 | 返回 `true` 才参与本轮；参与前重置缓冲状态 |
| `ProcessResponse` → `OnResponseDelta` | 每收到一段正文 | 返回可展示文本，空串表示暂存 |
| `EndResponse` → `OnResponseCompleted` | 本轮正文结束 | 返回暂存文本；之后不会再收到增量 |
| `ShutdownAsync` | 禁用或应用退出 | 停止后台任务、释放文件/定时器/线程；异常只记日志 |

- 初始化耗时过长会拖慢当轮对话；确有耗时工作请放到工具调用内部或后台任务。
- 工具方法不要长时间占用同步阻塞；需要等待的操作使用 `async` + `await`。

## 7. 配置与日志

插件窗口会为 `configSchema` 声明的字段生成可视化表单；保存时写回 `plugin.json` 的 `config` 段，并立即重新加载插件。

```json
"config": {
  "enabled": true,
  "retry": 3,
  "mode": "fast",
  "models": ["deepseek-chat"]
},
"configSchema": [
  { "key": "enabled", "label": "启用扩展", "type": "boolean" },
  { "key": "retry", "label": "重试次数", "type": "number" },
  { "key": "mode", "label": "模式", "type": "select", "options": ["fast", "safe"] },
  { "key": "models", "label": "适用模型", "type": "list", "description": "每行一个；留空表示全部" }
]
```

| type | 界面控件 | JSON 值 |
| --- | --- | --- |
| `text` | 单行输入框 | 字符串 |
| `multiline` | 多行输入框 | 字符串 |
| `number` | 单行输入框，保存时校验 | 数字 |
| `boolean` | 勾选框 | 布尔 |
| `select` | 下拉框 | `options` 中的字符串 |
| `list` | 多行输入框 | 字符串数组（每行一项） |

- `key` 必须在 `configSchema` 内唯一，且与 `config` 的属性名**完全一致（大小写敏感）**；`label` 省略时显示 `key`。写成 `Models` 而 `config` 里是 `models` 时，界面读不到当前值，保存还会新增一个插件不会读取的键，因此请逐字对齐。
- 未在 `configSchema` 中声明的 `config` 键仍会原样传给插件，只是界面上不可编辑。
- 保存会重写整个 `plugin.json`（统一缩进，注释不保留）；插件目录不可写时保存失败，界面会提示原因。
- 插件在 `OnInitializedAsync` 中读取 `Context.Configuration`；保存后宿主自动卸载并按新配置重新加载。

日志会带上插件 id 写入应用日志：

```csharp
context.Logger.LogInformation("初始化完成");
context.Logger.LogWarning("配置缺失，使用默认值");
context.Logger.LogError(exception, "请求失败");
```

## 8. 打包与导入

插件目录结构（zip 内可放在根目录或单层子目录）：

```
hello-kit/
├─ plugin.json
├─ HelloKit.dll
└─ 其它插件私有依赖（如有）
```

- 应用内入口：对话窗口「更多操作」→「插件…」→「导入」，选择 `.zip`。
- 安装位置：`~/.souffle/plugins/<id>`（Windows 为 `C:\Users\<用户名>\.souffle\plugins\<id>`）。
- 导入校验：必须有 `plugin.json` 且字段合法、`assembly` 指向的文件必须存在；解压路径限制在目标目录内。
- 导入成功后默认启用。若同 id 插件正在运行，新程序集在重启应用后生效。
- 随主程序发布的插件放在应用目录的 `plugins/<文件夹>/`，同 id 时内置版本优先。

### 独立构建与发布（以 input-kit 为例）

插件工程不在 `SoufflePet.sln` 中，与宿主分开构建、分开发布：

```powershell
# 本地调试：Debug 构建后自动部署到 SoufflePet/bin/Debug/net10.0/plugins/input-kit/
dotnet build Plugins\SoufflePet.Plugin.InputKit

# 不需要自动部署时关闭
dotnet build Plugins\SoufflePet.Plugin.InputKit -p:DeployToHost=false

# 发行：生成可导入的插件包
# 产物：Plugins\SoufflePet.Plugin.InputKit\artifacts\input-kit-1.0.0.zip
dotnet publish Plugins\SoufflePet.Plugin.InputKit -c Release
```

插件包通过「插件…」→「导入」安装到 `~/.souffle/plugins/<id>`；也可以解压到应用目录的 `plugins/<id>/` 作为内置插件随应用分发。宿主发布不再自动构建或打包插件。

## 9. 隔离与限制

宿主为每个插件提供独立的 `AssemblyLoadContext`，并做以下兜底：

- `InitializeAsync` 或 `OnToolsBuilding` 抛异常 → 该插件标记「加载失败」，其它插件与对话不受影响。
- 工具抛异常 → 返回错误文本给模型，插件失败次数 +1，主程序不崩溃。
- 工具超时 → 宿主放弃等待并返回超时文本（默认 30 秒）。
- 禁用插件 → `ShutdownAsync` → 卸载程序集 → 尽力回收内存。

进程内隔离无法防御以下进程级致命错误，插件必须自行避免：

- 调用 `Environment.Exit` / `Process.GetCurrentProcess().Kill` 等结束进程的操作。
- 栈溢出、访问违例（如错误的 P/Invoke 指针）、`OutOfMemoryException`。
- 阻塞宿主 UI 线程的长时间同步操作。

## 10. 兼容性与命名规则

- 工具名建议使用小写字母、数字与下划线，长度不超过 64 个字符。
- 插件工具与本地工具或其它插件重名时，宿主自动改为 `{插件id}__{工具名}`；仍冲突则追加 `_2`、`_3` 等后缀。
- 插件只能依赖宿主已加载的程序集版本；需要额外私有依赖时，把对应 dll 放进插件目录一起分发（`plugin.json` 的 `assembly` 指向入口 dll）。
- 插件可扩展对话工具、模型回复后处理与消息平台接入，不参与 UI 或主程序内部逻辑；如需新功能，做成独立工具、响应钩子或消息平台实现即可，无需修改主程序。
