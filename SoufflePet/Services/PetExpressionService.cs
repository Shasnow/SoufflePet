// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Threading;
using Serilog;
using SoufflePet.Models;
using SoufflePet.Views;

namespace SoufflePet.Services;

/// <summary>
/// 把 Live2D 表情能力包装成可供对话工具调用的服务：表情名单读自模型 model3.json，
/// 切换动作交给桌宠窗口执行。窗口在组合根解析后 Attach——ChatTools 的解析早于窗口，
/// 构造函数注入 IPetWindow 会与对话服务形成 DI 循环。
/// </summary>
public sealed class PetExpressionService
{
    private static readonly ILogger Logger = Log.ForContext<PetExpressionService>();

    private readonly SouffleSettings _settings;
    private IPetWindow? _petWindow;

    public PetExpressionService(SouffleSettings settings)
    {
        _settings = settings;
    }

    /// <summary>组合根解析出桌宠窗口后调用一次。</summary>
    public void Attach(IPetWindow petWindow)
    {
        _petWindow = petWindow;
    }

    /// <summary>当前模型声明的表情名（model3.json 的 FileReferences.Expressions，未配置时为空）。</summary>
    public IReadOnlyList<string> GetExpressionNames()
    {
        var model = _settings.ActiveLive2DModel;
        if (model == null) return [];

        var modelDir = Path.Combine(AppContext.BaseDirectory, "wwwroot", _settings.ModelsDirectory, model.FolderName);
        var path = Path.Combine(modelDir, $"{model.FileStem}.model3.json");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("FileReferences", out var references) ||
                !references.TryGetProperty("Expressions", out var expressions) ||
                expressions.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return
            [
                .. expressions.EnumerateArray()
                    .Select(item => item.TryGetProperty("Name", out var name) ? name.GetString() : null)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name!)
            ];
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Logger.Warning(ex, "read expressions failed path={Path}", path);
            return [];
        }
    }

    /// <summary>
    /// 当前窗口实际显示的表情名（null = 默认状态，或窗口还没准备好）。
    /// 由窗口侧跟踪，自动联动、工具切换与"戳一戳"的结果都能反映出来。
    /// </summary>
    public string? GetCurrentExpression() => _petWindow?.CurrentExpression;

    /// <summary>
    /// 切换表情（空名字表示清除、恢复默认）。名字先按模型声明校验，
    /// 避免 SDK 对未知表情抛 KeyNotFoundException；实际写入切回 UI 线程。
    /// </summary>
    public async Task<string> SetExpressionAsync(string? name)
    {
        if (_petWindow is not { } window) return "桌宠窗口还没准备好，暂时无法切换表情。";

        var wanted = name?.Trim();
        if (string.IsNullOrEmpty(wanted))
        {
            await Dispatcher.UIThread.InvokeAsync(() => window.SetExpression(null));
            return "已清除表情，恢复默认。";
        }

        var names = GetExpressionNames();
        if (names.Count == 0) return "当前 Live2D 模型没有配置任何表情。";

        var match = names.FirstOrDefault(n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase));
        if (match is null) return $"没有「{wanted}」这个表情。可用表情：{string.Join("、", names)}。";

        await Dispatcher.UIThread.InvokeAsync(() => window.SetExpression(match));
        Logger.Debug("expression set by tool name={Name}", match);
        return $"已切换到表情「{match}」。";
    }
}
