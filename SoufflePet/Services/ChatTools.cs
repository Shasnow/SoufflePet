// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
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

    /// <summary>技能正文回传给模型的最大长度，超出截断。</summary>
    private const int SkillContentMaxLength = 8000;

    private readonly SouffleSettings _settings;
    private readonly PetExpressionService _expressions;
    private readonly ChatToolAccess _access;
    private readonly ChatMemory _memory;
    private readonly IMcpToolService _mcp;
    private readonly ISkillService _skills;

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
                "在用户的 Windows 电脑上执行 PowerShell 命令，返回退出码、标准输出和错误输出（30 秒超时）。"
                + "仅在用户明确要求执行命令/脚本，或必须读取本机信息才能回答时调用；"
                + "不要执行删除、格式化、修改系统设置等破坏性命令，也不要做与用户请求无关的操作。"),
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
                GetSkills,
                "get_skills",
                "列出当前可用的技能（名称与用途）。技能是用户放在工作目录 .agents/skills 或 .souffle/skills 下的操作说明，"
                + "任务与某个技能相关时，先用 read_skill 读取该技能内容，再按说明执行。"),
            AIFunctionFactory.Create(
                ReadSkill,
                "read_skill",
                "读取指定技能的完整说明。技能名必须来自 get_skills 的返回值。")
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

    /// <summary>已启用的技能（DisabledSkills 里的名字忽略大小写）。</summary>
    private IReadOnlyList<SkillInfo> EnabledSkills() =>
        [.. _skills.Discover().Where(s => !_settings.DisabledSkills.Any(d => string.Equals(d, s.Name, StringComparison.OrdinalIgnoreCase)))];

    private string GetSkills()
    {
        var skills = EnabledSkills();
        if (skills.Count == 0) return "当前没有可用的技能。";

        var builder = new StringBuilder($"当前可用技能共 {skills.Count} 个：");
        foreach (var skill in skills)
        {
            builder.Append("\n- ").Append(skill.Name);
            if (skill.HasDescription) builder.Append("：").Append(skill.Description);
        }

        return builder.ToString();
    }

    private string ReadSkill(
        [Description("技能名称，必须是 get_skills 返回的名字")] string name)
    {
        if (EnabledSkills().FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) is not { } skill)
            return $"没有名为「{name}」的可用技能，请先调用 get_skills 查看可用技能。";

        var content = _skills.ReadContent(skill);
        if (content.Length == 0) return $"技能「{skill.Name}」没有正文内容。";

        var builder = new StringBuilder($"技能「{skill.Name}」（{skill.Source}）：\n");
        return content.Length <= SkillContentMaxLength
            ? builder.Append(content).ToString()
            : builder.Append(content[..SkillContentMaxLength]).Append("\n…（已截断）").ToString();
    }

    private async Task<string> RunShellCommand(
        [Description(
            "要执行的 PowerShell 命令，例如 Get-ChildItem C:\\ 或 Get-Process | Sort-Object CPU -Descending | Select-Object -First 5")]
        string command,
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
            StandardErrorEncoding = Encoding.UTF8
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
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                /* 已退出则忽略 */
            }

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