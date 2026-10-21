// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using SoufflePet.Models;

namespace SoufflePet.Services;

/// <summary>
/// 对话模型可调用的本地工具集（含切换表情、执行命令这两个有副作用的工具）：
/// 由 AIFunctionFactory 依据方法签名与 <see cref="DescriptionAttribute"/> 生成工具声明，
/// 模型按需发起调用，本地执行后再把结果回传给模型。
/// </summary>
public sealed class ChatTools
{
    /// <summary>命令执行超时：超时后连同子进程一起结束，避免挂死对话。</summary>
    private const int ShellTimeoutSeconds = 30;

    /// <summary>命令输出回传给模型的最大长度，超出截断。</summary>
    private const int ShellOutputMaxLength = 4000;

    private readonly SouffleSettings _settings;
    private readonly PetExpressionService _expressions;
    private readonly ChatToolAccess _access;

    public ChatTools(SouffleSettings settings, PetExpressionService expressions, ChatToolAccess access)
    {
        _settings = settings;
        _expressions = expressions;
        _access = access;
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
            AIFunctionFactory.Create(
                GetExpressions,
                "get_expressions",
                "列出当前 Live2D 模型可用的表情名称。想给用户换表情前先调用它，避免猜错名字。"),
            AIFunctionFactory.Create(
                SetExpression,
                "set_expression",
                "切换桌宠的 Live2D 表情，立刻显示在桌面上。表情名必须是 get_expressions 返回的之一；"
                + "传空字符串表示清除表情、恢复默认。用户让你换表情、做表情（哭、生气、脸红、比心等）时调用。"),
            AIFunctionFactory.Create(
                RunShellCommand,
                "run_shell_command",
                "在用户的 Windows 电脑上执行 PowerShell 命令，返回退出码、标准输出和错误输出（30 秒超时）。"
                + "用户处于「手动审批」规则时，执行前需要用户在对话里点击允许，请耐心等待结果。"
                + "仅在用户明确要求执行命令/脚本，或必须读取本机信息才能回答时调用；"
                + "不要执行删除、格式化、修改系统设置等破坏性命令，也不要做与用户请求无关的操作。"),
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

    private string GetExpressions()
    {
        var names = _expressions.GetExpressionNames();
        return names.Count == 0
            ? $"当前模型 {_settings.ModelName} 没有配置表情。"
            : $"当前模型 {_settings.ModelName} 的可用表情：{string.Join("、", names)}。";
    }

    private Task<string> SetExpression(
        [Description("表情名称，必须是 get_expressions 列出的名字；空字符串表示清除表情、恢复默认")] string? name)
        => _expressions.SetExpressionAsync(name);

    private async Task<string> RunShellCommand(
        [Description("要执行的 PowerShell 命令，例如 Get-ChildItem C:\\ 或 Get-Process | Sort-Object CPU -Descending | Select-Object -First 5")] string command,
        CancellationToken cancellationToken)
    {
        // 命令执行按当前审批规则处理：手动审批时阻塞等待用户点击允许
        if (!await _access.RequestShellApprovalAsync(command, cancellationToken))
        {
            return cancellationToken.IsCancellationRequested
                ? "命令已取消。"
                : "用户没有允许这次命令执行，命令未运行。";
        }

        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        // 强制 UTF-8，避免中文输出按控制台代码页解码成乱码
        startInfo.ArgumentList.Add("$OutputEncoding=[Console]::OutputEncoding=[Text.Encoding]::UTF8; " + command);

        using var process = new Process();
        process.StartInfo = startInfo;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(ShellTimeoutSeconds));

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"命令启动失败：{ex.Message}";
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* 已退出则忽略 */ }
            return cancellationToken.IsCancellationRequested
                ? "命令已取消。"
                : $"命令超过 {ShellTimeoutSeconds} 秒未结束，已终止。";
        }

        var stdout = TruncateOutput((await stdoutTask).Trim());
        var stderr = TruncateOutput((await stderrTask).Trim());

        var result = new StringBuilder($"退出码 {process.ExitCode}");
        if (stdout.Length > 0) result.Append("\n输出：\n").Append(stdout);
        if (stderr.Length > 0) result.Append("\n错误：\n").Append(stderr);
        if (stdout.Length == 0 && stderr.Length == 0) result.Append("\n（无输出）");
        return result.ToString();
    }

    private static string TruncateOutput(string text) =>
        text.Length <= ShellOutputMaxLength ? text : text[..ShellOutputMaxLength] + "…（已截断）";
}
