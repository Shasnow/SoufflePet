// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace SoufflePet.Plugin.Sdk;

/// <summary>
/// 插件基类，也是插件契约：入口类型必须继承本类。
/// 宿主依次调用 InitializeAsync → BuildTools → 退出时 ShutdownAsync；
/// 对话过程中还会按轮触发响应钩子。插件按需重写 OnInitializedAsync、OnToolsBuilding、
/// OnResponseStarting / OnResponseDelta / OnResponseCompleted、ShutdownAsync。
/// </summary>
public abstract class PluginBase
{
    /// <summary>初始化时由宿主注入的上下文；在初始化及之后的钩子中可用。</summary>
    protected PluginContext Context { get; private set; } = null!;

    /// <summary>宿主调用：初始化插件。抛异常时宿主把该插件标记为「加载失败」，不影响其它插件。</summary>
    public Task InitializeAsync(PluginContext context, CancellationToken cancellationToken)
    {
        Context = context;
        return OnInitializedAsync(cancellationToken);
    }

    /// <summary>初始化钩子：读取配置、创建后台资源等；不需要初始化时保持默认实现。</summary>
    protected virtual Task OnInitializedAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <summary>宿主调用：收集本插件提供的工具；不提供工具时集合保持为空。</summary>
    public void BuildTools(ICollection<AIFunction> tools) => OnToolsBuilding(tools);

    /// <summary>向 <paramref name="tools" /> 添加本插件提供的工具；不提供工具时保持默认实现。</summary>
    protected virtual void OnToolsBuilding(ICollection<AIFunction> tools)
    {
    }

    /// <summary>宿主调用：一轮模型回复开始前触发，返回值决定本轮是否参与正文后处理。</summary>
    public bool BeginResponse(string modelId) => OnResponseStarting(modelId);

    /// <summary>响应开始钩子：判断是否参与本轮（可按模型 id 过滤）；参与前在这里重置自己的状态。</summary>
    protected virtual bool OnResponseStarting(string modelId) => false;

    /// <summary>宿主调用：每收到一段正文增量触发一次。</summary>
    public string ProcessResponse(string text) => OnResponseDelta(text);

    /// <summary>处理一段流式正文，返回可以展示给用户的部分；需要跨增量判断时可返回空串暂存。</summary>
    protected virtual string OnResponseDelta(string text) => text;

    /// <summary>宿主调用：本轮正文结束时触发，用于释放暂存内容。</summary>
    public string EndResponse() => OnResponseCompleted();

    /// <summary>收尾钩子：返回仍被暂存的文本；没有暂存内容时返回空串。</summary>
    protected virtual string OnResponseCompleted() => string.Empty;

    /// <summary>禁用或退出时调用：停止后台任务、释放资源。这里抛出的异常只记录日志。</summary>
    public virtual Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
