// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using SoufflePet.Models;
using SoufflePet.Utils;

namespace SoufflePet.Services;

/// <summary>
/// 带图片的工具结果：<see cref="Text"/> 回传给模型与界面用于展示，
/// <see cref="Data"/> 由对话服务作为独立的多模态内容附加到下一轮请求。
/// </summary>
public sealed record ToolImageResult(string Text, byte[] Data, string MediaType);

/// <summary>
/// 对话模型可调用的本地工具集（含切换表情、执行命令这两个有副作用的工具）：
/// 由 AIFunctionFactory 依据方法签名与 <see cref="DescriptionAttribute"/> 生成工具声明，
/// 模型按需发起调用，本地执行后再把结果回传给模型。
/// </summary>
public sealed class ChatTools
{
    /// <summary>命令执行的默认超时（秒）：模型没指定 timeout_seconds 时使用。</summary>
    private const int DefaultShellTimeoutSeconds = 30;

    /// <summary>命令执行允许的最大超时（秒）：模型传得更大时按此上限处理。</summary>
    private const int MaxShellTimeoutSeconds = 600;

    /// <summary>命令输出回传给模型的最大长度，超出截断。</summary>
    private const int ShellOutputMaxLength = 4000;

    /// <summary>
    /// 主进程退出后等待管道 EOF 的宽限时间：命令拉起后台进程（如浏览器 daemon）时，
    /// 管道写端仍被其持有，EOF 永远不会到来，只能收完已缓冲的输出就返回。
    /// </summary>
    private static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromMilliseconds(300);

    /// <summary>技能正文回传给模型的最大长度，超出截断。</summary>
    private const int SkillContentMaxLength = 8000;

    private readonly SouffleSettings _settings;
    private readonly PetExpressionService _expressions;
    private readonly ChatToolAccess _access;
    private readonly ChatMemory _memory;
    private readonly IMcpToolService _mcp;
    private readonly ISkillService _skills;

    /// <summary>
    /// 截图工具暂存的图片：工具返回值会被序列化成 JSON，类型判断识别不出图片，
    /// 改由这里暂存，对话服务在工具调用完后取走并作为多模态内容附加。
    /// </summary>
    private ToolImageResult? _pendingImage;

    public ChatTools(
        SouffleSettings settings,
        PetExpressionService expressions,
        ChatToolAccess access,
        ChatMemory memory,
        IMcpToolService mcp,
        ISkillService skills)
    {
        _settings = settings;
        _expressions = expressions;
        _access = access;
        _memory = memory;
        _mcp = mcp;
        _skills = skills;
        Tools =
        [
            AIFunctionFactory.Create(
                GetCurrentTime,
                "get_current_time",
                "获取当前的本地日期、时间、星期和时区。用户问到现在几点、今天几号、星期几时先调用它，不要凭空推测。"),
            AIFunctionFactory.Create(
                GetExpressions,
                "get_expressions",
                "列出当前 Live2D 模型可用的表情名称。"),
            AIFunctionFactory.Create(
                SetExpression,
                "set_expression",
                "切换桌宠的 Live2D 表情。表情名必须是 get_expressions 返回的之一；"
                + "传空字符串表示清除表情、恢复默认。"),
            AIFunctionFactory.Create(
                RunShellCommand,
                "run_shell_command",
                "在用户的 Windows 电脑上执行 PowerShell 命令，返回退出码、标准输出和错误输出。"
                + "仅在用户明确要求执行命令/脚本，或必须读取本机信息才能回答时调用；"
                + "不要执行格式化、修改系统设置等破坏性命令，也不要做与用户请求无关的操作。"),
            AIFunctionFactory.Create(
                TakeScreenshot,
                "take_screenshot",
                "截取用户当前显示器（光标所在屏幕）的画面，图片会直接返回给你查看，同时保存为 PNG 文件。"
                + "仅在用户要求看屏幕、或必须看到屏幕内容才能回答时调用。"),
            AIFunctionFactory.Create(
                Remember,
                "remember",
                "把值得长期记住的用户信息写入长期记忆（跨会话保留），例如称呼、喜好、常用路径、约定。"
                + "key 用简短中文词，value 用一句话；同一个 key 再写会覆盖旧值。"),
            AIFunctionFactory.Create(
                Forget,
                "forget",
                "删除长期记忆里的一条记录。用户说「忘掉…」「别再记得…」时调用。"),
            AIFunctionFactory.Create(
                Recall,
                "recall",
                "按关键词检索过去的对话记录。用户提到「之前」「上次」「还记得吗」，或需要旧信息才能回答时调用。"),
            AIFunctionFactory.Create(
                ReadSkill,
                "read_skill",
                "读取某个技能的完整说明。")
        ];
    }

    /// <summary>可传给 ChatOptions.Tools 的工具列表。</summary>
    private IReadOnlyList<AITool> Tools { get; }

    /// <summary>
    /// 本次请求可用的全部工具：本地工具 + 已连接 MCP 服务器的工具（首次调用时连接）。
    /// </summary>
    public async Task<IReadOnlyList<AITool>> GetToolsAsync(CancellationToken cancellationToken)
    {
        var mcpTools = await _mcp.GetToolsAsync(cancellationToken).ConfigureAwait(false);
        return mcpTools.Count == 0 ? Tools : [.. Tools, .. mcpTools];
    }

    private static string GetCurrentTime()
    {
        var now = DateTimeOffset.Now;
        var culture = CultureInfo.GetCultureInfo("zh-CN");
        return $"{now.ToString("yyyy-MM-dd HH:mm:ss dddd", culture)}，UTC{now:zzz}，时区 {TimeZoneInfo.Local.Id}";
    }

    private string GetExpressions()
    {
        var names = _expressions.GetExpressionNames();
        return names.Count == 0
            ? $"当前模型 {_settings.ModelName} 没有配置表情。"
            : $"当前模型 {_settings.ModelName} 的可用表情：{string.Join("、", names)}。";
    }

    private Task<string> SetExpression(
        [Description("表情名称，必须是 get_expressions 列出的名字；空字符串表示清除表情、恢复默认")]
        string? name)
        => _expressions.SetExpressionAsync(name);

    private string Remember(
        [Description("记忆的键，简短中文词，例如「称呼」「常用项目路径」")]
        string key,
        [Description("记忆的内容，一句话，例如「主人」")] string value)
        => _memory.Remember(key, value);

    private string Forget(
        [Description("要删除的记忆键")] string key)
        => _memory.Forget(key);

    private string Recall(
        [Description("检索关键词，可以是用户问题里的关键词")] string query)
        => _memory.Recall(query);

    private string ReadSkill(
        [Description("技能名称，必须是系统提示词中技能清单里的名字")] string name)
    {
        if (_skills.DiscoverEnabled().FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) is not { } skill)
            return $"没有名为「{name}」的可用技能，请从系统提示词的技能清单中选择。";

        var content = _skills.ReadContent(skill);
        if (content.Length == 0) return $"技能「{skill.Name}」没有正文内容。";

        var builder = new StringBuilder($"技能「{skill.Name}」（{skill.Source}）：\n");
        return content.Length <= SkillContentMaxLength
            ? builder.Append(content).ToString()
            : builder.Append(content[..SkillContentMaxLength]).Append("\n…（已截断）").ToString();
    }

    private string TakeScreenshot()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("截图工具仅支持 Windows。");

        var shot = ScreenCapture.CaptureCurrentMonitor();
        // 图片暂存起来（返回值会被序列化成 JSON，塞不下 base64），只把说明文本作为工具结果。
        _pendingImage = new ToolImageResult(
            $"已截取当前显示器画面：{shot.Width}×{shot.Height} 像素，图片已保存到 {shot.FilePath}",
            shot.Image,
            shot.MediaType);
        return _pendingImage.Text;
    }

    /// <summary>
    /// 取走并清空暂存的截图图片：对话服务每次工具调用后取一次，
    /// 避免旧图串到下一次调用；没有暂存图片时返回 null。
    /// </summary>
    internal ToolImageResult? TakePendingImage()
    {
        var image = _pendingImage;
        _pendingImage = null;
        return image;
    }

    private async Task<string> RunShellCommand(
        [Description(
            "要执行的 PowerShell 命令，例如 Get-ChildItem C:\\ 或 Get-Process | Sort-Object CPU -Descending | Select-Object -First 5")]
        string command,
        [Description("命令超时时间（秒），默认 30，最大 600，超出按上限处理")]
        int timeoutSeconds = DefaultShellTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        var timeoutSecondsValue = Math.Clamp(timeoutSeconds, 1, MaxShellTimeoutSeconds);

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
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        // 强制 UTF-8，避免中文输出按控制台代码页解码成乱码
        startInfo.ArgumentList.Add("$OutputEncoding=[Console]::OutputEncoding=[Text.Encoding]::UTF8; " + command);

        using var process = new Process();
        process.StartInfo = startInfo;

        // 按行异步收集输出：不能用 ReadToEndAsync，它要等管道 EOF，
        // 而命令拉起的后台进程会继承写端句柄，导致永远等不到 EOF、看起来每次都超时。
        var stdoutLines = new ConcurrentQueue<string>();
        var stderrLines = new ConcurrentQueue<string>();
        var stdoutClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) stdoutClosed.TrySetResult();
            else stdoutLines.Enqueue(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) stderrClosed.TrySetResult();
            else stderrLines.Enqueue(e.Data);
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSecondsValue));

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"命令启动失败：{ex.Message}";
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(timeout.Token);

            // 主进程已退出：能收到 EOF 就说明输出读完，收不到也不再多等（避免被后台进程拖住）。
            await Task.WhenAny(
                    Task.WhenAll(stdoutClosed.Task, stderrClosed.Task),
                    Task.Delay(OutputDrainTimeout, CancellationToken.None))
                .ConfigureAwait(false);

            var stdout = TruncateOutput(JoinOutput(stdoutLines));
            var stderr = TruncateOutput(JoinOutput(stderrLines));

            var result = new StringBuilder($"退出码 {process.ExitCode}");
            if (stdout.Length > 0) result.Append("\n输出：\n").Append(stdout);
            if (stderr.Length > 0) result.Append("\n错误：\n").Append(stderr);
            if (stdout.Length == 0 && stderr.Length == 0) result.Append("\n（无输出）");
            return result.ToString();
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                /* 已退出则忽略 */
            }

            // 只有「停止对话」才把取消继续往上抛；命令自身超时按普通结果回给模型，
            // 不能连带取消本次 LLM 请求。
            cancellationToken.ThrowIfCancellationRequested();
            return $"命令超过 {timeoutSecondsValue} 秒未结束，已终止。";
        }
    }

    private static string JoinOutput(ConcurrentQueue<string> lines) =>
        lines.IsEmpty ? string.Empty : string.Join(Environment.NewLine, lines).Trim();

    private static string TruncateOutput(string text) =>
        text.Length <= ShellOutputMaxLength ? text : text[..ShellOutputMaxLength] + "…（已截断）";
}