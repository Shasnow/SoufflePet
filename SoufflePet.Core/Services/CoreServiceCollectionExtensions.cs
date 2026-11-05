// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using Microsoft.Extensions.DependencyInjection;
using SoufflePet.Services.Plugins;

namespace SoufflePet.Services;

/// <summary>
/// Core 组合根：注册与 UI 无关的模型/服务。宿主必须先注册
/// <c>SouffleSettings</c>、<c>IHttpClientFactory</c>、<c>IUiDispatcher</c>、
/// <c>IPetController</c>、<c>IScreenCapture</c>、<c>IBrowserService</c> 这些平台实现。
/// </summary>
public static class CoreServiceCollectionExtensions
{
    public static IServiceCollection AddSouffleCore(this IServiceCollection services)
    {
        // 记忆/工具/平台
        services.AddSingleton<ChatMemory>();
        services.AddSingleton<ChatToolAccess>();
        services.AddSingleton<UserQuestionService>();
        services.AddSingleton<ChatFileService>();
        services.AddSingleton<IStatisticsService, StatisticsService>();
        services.AddSingleton<PlatformService>();

        // MCP / 技能 / 插件
        services.AddSingleton<IMcpConfigStore, McpConfigStore>();
        services.AddSingleton<IMcpToolService, McpToolService>();
        services.AddSingleton(McpToolCache.Load());
        services.AddSingleton<ISkillService, SkillService>();
        services.AddSingleton<IPluginService, PluginService>();

        // LLM / 工具 / 调度 / Live2D 文件操作
        services.AddSingleton<ChatTools>();
        services.AddSingleton<ILlmChatService, LlmChatService>();
        services.AddSingleton<ILlmModelCatalog, LlmModelCatalog>();
        services.AddSingleton<IScheduledTaskService, ScheduledTaskService>();
        services.AddSingleton<ILive2DModelService, Live2DModelService>();

        return services;
    }
}