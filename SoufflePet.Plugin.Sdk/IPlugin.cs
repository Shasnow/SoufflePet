// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace SoufflePet.Plugin.Sdk;

/// <summary>
/// 插件契约：宿主发现并加载实现类后依次调用 Initialize → 读取 Tools → 退出时 Shutdown。
/// 插件只通过 <see cref="Tools" /> 向对话模型提供工具，不改动主程序其它功能。
/// </summary>
public interface IPlugin
{
    /// <summary>提供给对话模型的工具；<see cref="InitializeAsync" /> 返回后才可读取。</summary>
    IReadOnlyList<AIFunction> Tools { get; }

    /// <summary>初始化插件并构建工具列表。抛异常时宿主把该插件标记为「加载失败」，不影响其它插件。</summary>
    Task InitializeAsync(PluginContext context, CancellationToken cancellationToken);

    /// <summary>禁用或退出时调用：停止后台任务、释放资源。这里抛出的异常只记录日志。</summary>
    Task ShutdownAsync(CancellationToken cancellationToken);
}

/// <summary>宿主提供给插件的运行环境：由宿主在加载时构造，插件只读使用。</summary>
public sealed class PluginContext(
    string pluginId,
    string pluginDirectory,
    JsonElement? configuration,
    ILogger logger)
{
    /// <summary>插件 id（取自 plugin.json）。</summary>
    public string PluginId { get; } = pluginId;

    /// <summary>插件所在目录的绝对路径。</summary>
    public string PluginDirectory { get; } = pluginDirectory;

    /// <summary>plugin.json 里的 config 段；未声明时为 null。</summary>
    public JsonElement? Configuration { get; } = configuration;

    /// <summary>已按插件 id 分类的日志出口，转发到应用日志。</summary>
    public ILogger Logger { get; } = logger;
}

/// <summary>插件基类：托管 <see cref="Tools" /> 的生命周期，插件只需实现工具创建与可选的清理。</summary>
public abstract class PluginBase : IPlugin
{
    /// <summary>初始化时由宿主注入的上下文；在 <see cref="CreateToolsAsync" /> 及之后可用。</summary>
    protected PluginContext Context { get; private set; } = null!;

    public IReadOnlyList<AIFunction> Tools { get; private set; } = [];

    public async Task InitializeAsync(PluginContext context, CancellationToken cancellationToken)
    {
        Context = context;
        Tools = await CreateToolsAsync(context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>构建工具列表：返回的每个 <see cref="AIFunction" /> 都会由宿主套上超时与异常守卫。</summary>
    protected abstract ValueTask<IReadOnlyList<AIFunction>> CreateToolsAsync(
        PluginContext context,
        CancellationToken cancellationToken);

    public virtual Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
