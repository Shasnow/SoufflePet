// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenAI;
using OpenAI.Models;
using Serilog;

namespace SoufflePet.Services;

/// <summary>按提供商配置拉取可用模型列表（OpenAI 兼容的 <c>GET {base}/models</c>）。</summary>
public interface ILlmModelCatalog
{
    /// <summary>
    /// 请求提供商的模型列表，返回去重并按标识排序后的模型标识。
    /// 网络/鉴权/接口错误以异常抛出，取消通过 cancellationToken 传递。
    /// </summary>
    Task<IReadOnlyList<string>> ListModelsAsync(string baseUrl, string apiKey, CancellationToken cancellationToken);
}

/// <summary>用 OpenAI SDK 的模型客户端获取列表：地址与密钥的规范化规则与对话请求一致（见 <see cref="OpenAiEndpoint" />）。</summary>
public sealed class LlmModelCatalog : ILlmModelCatalog
{
    private static readonly ILogger Logger = Log.ForContext<LlmModelCatalog>();

    public async Task<IReadOnlyList<string>> ListModelsAsync(
        string baseUrl,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var endpoint = OpenAiEndpoint.BaseUrl(baseUrl);
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("接口地址不是合法的绝对地址");

        var options = new OpenAIClientOptions
        {
            Endpoint = uri,
            NetworkTimeout = TimeSpan.FromSeconds(30)
        };

        var client = new OpenAIModelClient(new System.ClientModel.ApiKeyCredential(OpenAiEndpoint.ApiKey(apiKey)), options);
        var result = await client.GetModelsAsync(cancellationToken).ConfigureAwait(false);

        var models = result.Value
            .Select(model => model.Id?.Trim())
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Logger.Debug("listed {Count} models from {Endpoint}", models.Count, endpoint);
        return models;
    }
}
