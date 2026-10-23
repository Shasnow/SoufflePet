# 插件开发指南

SoufflePet 的插件是独立的 .NET 程序集，通过 `plugin.json` 声明后由宿主发现、加载，可以向对话模型提供工具（`AIFunction`），也可以对模型回复做流式后处理（`IResponseFilter`）。新增能力不需要修改主程序代码。

## 1. 架构与运行方式

```
SoufflePet（宿主）
└─ PluginService
   ├─ 扫描 plugins 目录 → 读取 plugin.json
   ├─ 每个插件独占一个可卸载的 AssemblyLoadContext
   ├─ 实例化 IPlugin → InitializeAsync → 读取 Tools 与响应过滤器
   ├─ 每轮对话：本地工具 + 插件工具 + MCP 工具 → ChatOptions.Tools
   └─ 每轮正文：按插件顺序串联响应过滤器 → 界面
```

- 契约程序集 `SoufflePet.Plugin.Sdk.dll` 与 `Microsoft.Extensions.AI.Abstractions.dll` 由宿主共享，插件不要自带这两个 dll 的副本。
- 插件工具与主程序工具走同一条 `AIFunction` 调用通道：模型发起调用 → 宿主执行 → 结果回传模型。
- 插件工具由宿主套上守卫：单次调用超时（`timeoutSeconds`）与异常捕获，失败只影响该次调用。
- 插件响应过滤器也有异常守卫：处理失败时原样放行文本，插件失败次数 +1，不影响本次回复。

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
| `entryType` | 否 | 入口类型全名；省略时取程序集内唯一的 `IPlugin` 实现 |
| `timeoutSeconds` | 否 | 单个工具调用超时秒数，宿主限制 1–300，默认 30 |
| `config` | 否 | 任意 JSON，原样交给 `PluginContext.Configuration` |

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
    protected override ValueTask<IReadOnlyList<AIFunction>> CreateToolsAsync(
        PluginContext context,
        CancellationToken cancellationToken)
    {
        // 配置是 plugin.json 的 config 段，未声明时为 null
        var greeting = context.Configuration?.TryGetProperty("greeting", out var value) == true
            ? value.GetString()
            : "你好";

        IReadOnlyList<AIFunction> tools =
        [
            AIFunctionFactory.Create(
                () => Greet(greeting!),
                "hello_greet",
                "向用户打招呼。")
        ];

        context.Logger.LogInformation("已就绪，共 {Count} 个工具", tools.Count);
        return ValueTask.FromResult(tools);
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

## 4. 响应过滤器（可选）

除了工具，插件还可以对模型正文做流式后处理。实现 `IResponseFilterProvider` 即可：

```csharp
public sealed class MyPlugin : PluginBase, IResponseFilterProvider
{
    public IReadOnlyList<IResponseFilter> CreateResponseFilters(string modelId) =>
        modelId.Contains("deepseek", StringComparison.OrdinalIgnoreCase)
            ? [new MyFilter()]
            : [];

    // 过滤器插件也可以同时提供工具；没有工具时返回空集合
    protected override ValueTask<IReadOnlyList<AIFunction>> CreateToolsAsync(
        PluginContext context, CancellationToken cancellationToken)
        => ValueTask.FromResult<IReadOnlyList<AIFunction>>([]);
}

internal sealed class MyFilter : IResponseFilter
{
    public string Process(string text) => text;   // 处理一段流式增量，可返回空串暂存
    public string Flush() => string.Empty;        // 本轮结束时返回暂存内容
}
```

- 宿主每轮响应都会调用一次 `CreateResponseFilters(modelId)`，必须返回新实例，不能复用上一轮的对象。
- 多个插件的过滤器按启用顺序串联：`Process` 处理每个增量，`Flush` 在流结束时收尾。
- 过滤器抛异常时，宿主原样放行该段文本并把插件失败次数 +1，不影响本次回复。
- 典型用途：清理模型泄漏的 DSML 工具调用标记，可按 `modelId` 只对特定模型生效。

内置的 `dsml-cleaner` 插件就是该机制的示例，`config.models` 为空表示全部模型：

```json
"config": {
  "models": ["deepseek-chat", "deepseek-reasoner"]
}
```

## 5. 生命周期与线程约定

| 阶段 | 时机 | 约定 |
| --- | --- | --- |
| 构造 | 启用后首次需要工具时 | 构造函数应保持轻量，不要做耗时 I/O |
| `InitializeAsync` | 构造之后 | 构建并缓存工具；可用 `PluginContext` 读配置、写日志 |
| `Tools` | `InitializeAsync` 返回后 | 宿主读取一次并缓存，之后不要再替换集合 |
| 调用 | 每轮对话中模型发起 | 可能在后台线程执行；参数里的 `CancellationToken` 会随对话取消触发 |
| `ShutdownAsync` | 禁用或应用退出 | 停止后台任务、释放文件/定时器/线程；异常只记日志 |

- 初始化耗时过长会拖慢当轮对话；确有耗时工作请放到工具调用内部或后台任务。
- 工具方法不要长时间占用同步阻塞；需要等待的操作使用 `async` + `await`。

## 6. 配置与日志

```csharp
// plugin.json 的 config 段（JsonElement?）
var config = context.Configuration;

// 日志会带上插件 id 写入应用日志
context.Logger.LogInformation("初始化完成");
context.Logger.LogWarning("配置缺失，使用默认值");
context.Logger.LogError(exception, "请求失败");
```

本期不提供插件配置表单，`config` 由插件作者写在 `plugin.json` 中随包分发；用户也可以直接编辑已安装插件目录下的 `plugin.json`（修改后重启应用生效）。

## 7. 打包与导入

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

## 8. 隔离与限制

宿主为每个插件提供独立的 `AssemblyLoadContext`，并做以下兜底：

- `InitializeAsync` 抛异常 → 该插件标记「加载失败」，其它插件与对话不受影响。
- 工具抛异常 → 返回错误文本给模型，插件失败次数 +1，主程序不崩溃。
- 工具超时 → 宿主放弃等待并返回超时文本（默认 30 秒）。
- 禁用插件 → `ShutdownAsync` → 卸载程序集 → 尽力回收内存。

进程内隔离无法防御以下进程级致命错误，插件必须自行避免：

- 调用 `Environment.Exit` / `Process.GetCurrentProcess().Kill` 等结束进程的操作。
- 栈溢出、访问违例（如错误的 P/Invoke 指针）、`OutOfMemoryException`。
- 阻塞宿主 UI 线程的长时间同步操作。

## 9. 兼容性与命名规则

- 工具名建议使用小写字母、数字与下划线，长度不超过 64 个字符。
- 插件工具与本地工具或其它插件重名时，宿主自动改为 `{插件id}__{工具名}`；仍冲突则追加 `_2`、`_3` 等后缀。
- 插件只能依赖宿主已加载的程序集版本；需要额外私有依赖时，把对应 dll 放进插件目录一起分发（`plugin.json` 的 `assembly` 指向入口 dll）。
- 插件扩展对话工具与模型回复后处理，不参与 UI 或主程序内部逻辑；如需新功能，做成独立工具或过滤器即可，无需修改主程序。
