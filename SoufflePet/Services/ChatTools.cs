// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.AI;
using SoufflePet.Models;

namespace SoufflePet.Services;

/// <summary>
/// 对话模型可调用的本地工具集（只读、无副作用）：由 AIFunctionFactory 依据方法签名与
/// <see cref="DescriptionAttribute"/> 生成工具声明，模型按需发起调用，本地执行后再把结果回传给模型。
/// </summary>
public sealed class ChatTools
{
    private readonly SouffleSettings _settings;

    public ChatTools(SouffleSettings settings)
    {
        _settings = settings;
        Tools =
        [
            AIFunctionFactory.Create(
                GetCurrentTime,
                "get_current_time",
                "获取当前的本地日期、时间、星期和时区。用户问到现在几点、今天几号、星期几时先调用它，不要凭空推测。"),
            AIFunctionFactory.Create(
                GetAppStatus,
                "get_app_status",
                "读取桌宠当前的功能开关与配置（只读）：自动表情、口型律动、目光跟随、游戏输入联动、开机自启、当前 Live2D 模型名。"),
        ];
    }

    /// <summary>可传给 ChatOptions.Tools 的工具列表。</summary>
    public IReadOnlyList<AITool> Tools { get; }

    private static string GetCurrentTime()
    {
        var now = DateTimeOffset.Now;
        var culture = CultureInfo.GetCultureInfo("zh-CN");
        return $"{now.ToString("yyyy-MM-dd HH:mm:ss dddd", culture)}，UTC{now:zzz}，时区 {TimeZoneInfo.Local.Id}";
    }

    private string GetAppStatus()
    {
        return string.Join("；",
            $"自动表情 {(_settings.AutoExpressionEnabled ? "开启" : "关闭")}",
            $"口型律动 {(_settings.LipSyncEnabled ? "开启" : "关闭")}",
            $"目光跟随 {(_settings.GazeFollowEnabled ? "开启" : "关闭")}",
            $"游戏输入联动 {(_settings.GamepadEnabled ? "开启" : "关闭")}",
            $"开机自启 {(_settings.StartWithWindows ? "开启" : "关闭")}",
            $"Live2D 模型 {_settings.ModelName}");
    }
}
