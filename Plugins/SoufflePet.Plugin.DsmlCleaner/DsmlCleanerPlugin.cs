// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using SoufflePet.Plugin.Sdk;

namespace SoufflePet.Plugins.DsmlCleaner;

/// <summary>
/// 响应后处理插件：清理模型正文里泄漏的 DSML 工具调用标记。
/// config.models 为空表示所有模型；填写模型 id 时只处理匹配的模型。
/// </summary>
public sealed class DsmlCleanerPlugin : PluginBase, IResponseFilterProvider
{
    private IReadOnlyList<string> _models = [];

    protected override ValueTask<IReadOnlyList<AIFunction>> CreateToolsAsync(
        PluginContext context,
        CancellationToken cancellationToken)
    {
        _models = ReadModels(context.Configuration);
        context.Logger.LogInformation(
            "DSML 过滤器已就绪，适用模型：{Models}",
            _models.Count == 0 ? "全部" : string.Join(", ", _models));
        return ValueTask.FromResult<IReadOnlyList<AIFunction>>([]);
    }

    public IReadOnlyList<IResponseFilter> CreateResponseFilters(string modelId) =>
        AppliesTo(modelId) ? [new DsmlResponseFilter()] : [];

    private bool AppliesTo(string modelId) =>
        _models.Count == 0
        || _models.Any(model => string.Equals(model, modelId, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<string> ReadModels(JsonElement? config)
    {
        if (config?.TryGetProperty("models", out var models) != true
            || models.ValueKind != JsonValueKind.Array)
            return [];

        return
        [
            .. models.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
        ];
    }
}
