// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System.Collections.Generic;

namespace SoufflePet.Plugin.Sdk;

/// <summary>
/// 模型正文过滤器：按流式增量加工回复文本（去标记、替换措辞等）。
/// 宿主每轮响应都会创建新实例，实例内部状态只属于当轮响应。
/// </summary>
public interface IResponseFilter
{
    /// <summary>处理一段增量，返回可以展示给用户的文本；需要跨增量判断时可返回空串暂存。</summary>
    string Process(string text);

    /// <summary>本轮响应结束时调用，返回仍被暂存的文本。</summary>
    string Flush();
}

/// <summary>
/// 插件可选实现：向宿主提供模型正文过滤器（响应后处理）。
/// 与工具一样需要先启用插件；过滤器只影响文本，不改变工具调用流程。
/// </summary>
public interface IResponseFilterProvider
{
    /// <summary>
    /// 为指定模型创建本轮响应的过滤器；返回空集合表示不处理。
    /// 每轮响应调用一次，必须返回新实例，避免上一轮状态串到下一轮。
    /// </summary>
    IReadOnlyList<IResponseFilter> CreateResponseFilters(string modelId);
}
