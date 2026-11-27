// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SoufflePet.Chat;

namespace SoufflePet.Core.Chat;

/// <summary>命令的参数类型：决定「/命令 」（带空格）之后的候选来源。</summary>
public enum SlashCommandArgumentKind
{
    /// <summary>没有参数，选中即执行。</summary>
    None = 0,

    /// <summary>参数是当前会话可用的对话模型。</summary>
    Model = 1,

    /// <summary>参数是工具审批模式（manual / full）。</summary>
    ApprovalMode = 2,
}

/// <summary>
/// 参数类型的线名：与 React 侧 <c>SlashCommandArgumentKind</c> 的字面量
///（<c>'none' | 'model' | 'approval'</c>）一字不差，由 <c>chat.command.list</c> 原样交给界面。
/// </summary>
public static class SlashCommandArgumentKinds
{
    /// <summary>
    /// 逐个列举，兜底分支直接抛：新增参数类型时这里会立刻炸掉（并在 <c>chat.command.list</c> 上暴露），
    /// 而不是静默降级成 <c>none</c>——那会让界面把「需要参数」的命令渲染成「选中即执行」，且没有任何报错可查。
    /// 不能只靠「少写一条分支让编译器报 CS8524」：枚举允许未命名取值，那样写会留下永久告警。
    /// </summary>
    public static string WireName(this SlashCommandArgumentKind kind) => kind switch
    {
        SlashCommandArgumentKind.None => "none",
        SlashCommandArgumentKind.Model => "model",
        SlashCommandArgumentKind.ApprovalMode => "approval",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知的命令参数类型"),
    };
}

/// <summary>
/// 一条命令的定义。<see cref="Name" /> 不含开头的斜杠；
/// <see cref="Usage" /> 是给人看的完整用法，界面预览与 /help 都用它。
/// </summary>
public sealed record SlashCommandDefinition(
    string Name,
    string Summary,
    string Description,
    SlashCommandArgumentKind Argument = SlashCommandArgumentKind.None,
    string? ArgumentLabel = null,
    IReadOnlyList<string>? Aliases = null,
    /// <summary>
    /// 是否允许从消息平台执行。改变整机安全策略的命令（如审批模式）只能在桌面端操作，
    /// 避免平台侧账号被盗后远程放开工具审批。
    /// </summary>
    bool AllowFromPlatform = true)
{
    /// <summary>别名（含命令本名之外的写法），没有别名时为空列表。</summary>
    public IReadOnlyList<string> AliasList => Aliases ?? [];

    /// <summary>用法：无参数时是 <c>/stop</c>，有参数时是 <c>/model [模型名称/模型ID]</c>。</summary>
    public string Usage => Argument == SlashCommandArgumentKind.None || string.IsNullOrEmpty(ArgumentLabel)
        ? ContractName
        : $"{ContractName} [{ArgumentLabel}]";

    /// <summary>契约里的写法：带斜杠的命令名。</summary>
    public string ContractName => SlashCommandCatalog.Trigger + Name;
}

/// <summary>命令目录：进程内唯一的事实源，前端由 <c>chat.command.list</c> 读取，平台消息在 Core 内直接匹配。</summary>
public static class SlashCommandCatalog
{
    /// <summary>触发字符：用户输入以它开头即进入命令模式。</summary>
    public const char Trigger = '/';

    /// <summary>全部命令（按界面展示顺序）。</summary>
    public static IReadOnlyList<SlashCommandDefinition> All { get; } =
    [
        new(
            "model",
            "切换当前会话使用的对话模型",
            "切换当前会话使用的对话模型，可以填模型名称、模型标识或列表里的序号；" +
            "不带参数时列出当前全部可用模型。切换立即生效，应用到当前会话后续的所有对话，" +
            "同时记为默认模型（之后新开的会话也用它）。",
            SlashCommandArgumentKind.Model,
            "模型名称/模型ID",
            ["m", "models"]),
        new(
            "approval",
            "切换工具审批模式",
            "切换工具审批模式（写入全局默认，当前会话立即生效、之后新开的会话同样生效）：" +
            "manual 每次调用工具都要确认，full 直接放行。",
            SlashCommandArgumentKind.ApprovalMode,
            "manual/full",
            ["approve"],
            // 审批模式是整机安全策略：只允许在桌面端切换，平台消息里一律拒绝
            AllowFromPlatform: false),
        new(
            "stop",
            "停止当前正在生成的回复",
            "停止当前会话正在生成的回复，已输出的内容会保留。",
            SlashCommandArgumentKind.None,
            null,
            ["cancel"]),
        new(
            "compact",
            "压缩当前会话的上下文",
            "把较早的对话总结成摘要、只保留窗口预算内的最近原文，释放上下文窗口；" +
            "通常不需要手动执行，接近窗口上限时会自动压缩。",
            SlashCommandArgumentKind.None,
            null,
            ["compress"]),
        new(
            "help",
            "列出全部命令",
            "列出全部命令与用法。",
            SlashCommandArgumentKind.None,
            null,
            ["?"]),
    ];

    /// <summary>按命令名或别名查找（忽略大小写与开头的斜杠）。</summary>
    public static SlashCommandDefinition? Find(string? name)
    {
        var key = NormalizeName(name);
        if (key.Length == 0) return null;

        foreach (var command in All)
        {
            if (string.Equals(command.Name, key, StringComparison.OrdinalIgnoreCase)) return command;
            if (command.AliasList.Any(alias => string.Equals(alias, key, StringComparison.OrdinalIgnoreCase)))
                return command;
        }

        return null;
    }

    /// <summary>去掉开头的斜杠与首尾空白，得到用于匹配的命令名。</summary>
    public static string NormalizeName(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        return trimmed.Length > 0 && trimmed[0] == Trigger ? trimmed[1..].Trim() : trimmed;
    }

    /// <summary>/help 的正文：列出全部命令与用法；从消息平台执行时只列平台可用的命令。</summary>
    public static string HelpText(bool fromPlatform = false)
    {
        var builder = new StringBuilder("可用命令：");
        foreach (var command in All)
        {
            if (fromPlatform && !command.AllowFromPlatform) continue;
            builder.Append('\n').Append(command.Usage).Append(" · ").Append(command.Summary);
        }

        if (fromPlatform) builder.Append("\n（审批模式等只能在桌面端主窗口切换。）");
        return builder.ToString();
    }
}

/// <summary>解析后的命令输入。命令名可能不属于已知命令，是否已知由调用方判断。</summary>
public readonly record struct SlashCommandInput(string Name, string Argument, bool HasArgumentSeparator);

/// <summary>命令的解析：只认「以斜杠开头 + 非空的命令名」，其余一律当普通文本。</summary>
public static class SlashCommandParser
{
    /// <summary>
    /// 把一段输入解析成命令名 + 参数。命令名取斜杠之后的第一段（到空白为止），
    /// 参数取剩余内容并去掉首尾空白；只认单行输入，不是命令时返回 false。
    /// 结尾空白不算已输入参数（`/model` 与 `/model ` 同义），但 `/model ` 里的空格
    /// 仍然算「已经进入参数阶段」，界面据此列出模型候选。
    ///
    /// 空白按 <see cref="char.IsWhiteSpace(char)" /> 判定（含全角空格 U+3000、NBSP U+00A0、NEL U+0085），
    /// 与 React 侧 slashCommands.ts 的分界规则保持一致：否则 `/model　reason` 在界面上是
    /// 「命令 + 参数」，到宿主却会拼成一个不存在的命令名。
    /// </summary>
    public static bool TryParse(string? text, out SlashCommandInput input)
    {
        input = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (text.Contains('\n') || text.Contains('\r')) return false;

        var trimmed = text.TrimStart();
        if (trimmed[0] != SlashCommandCatalog.Trigger) return false;

        var body = trimmed[1..];
        if (body.Length == 0) return false;

        var separator = IndexOfWhiteSpace(body);
        var name = separator < 0 ? body : body[..separator];
        if (name.Length == 0) return false;

        var argument = separator < 0 ? string.Empty : body[(separator + 1)..].Trim();
        input = new SlashCommandInput(name, argument, separator >= 0);
        return true;
    }

    /// <summary>第一个 Unicode 空白字符的下标；没有则返回 -1。</summary>
    private static int IndexOfWhiteSpace(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsWhiteSpace(text[index])) return index;
        }

        return -1;
    }
}

/// <summary>
/// 模糊打分：先精确、再前缀、再包含，最后允许字符跳跃（子序列）匹配，
/// 这样「dschat」也能命中「deepseek-chat」。供 <c>/model</c> 执行时把参数解析成具体模型；
/// 界面联想列表的排序由 React 侧按同样的评分规则自行实现。
/// </summary>
public static class SlashCommandFuzzy
{
    /// <summary>不匹配。</summary>
    public const int NoMatch = -1;

    /// <summary>单字段打分：越高越匹配；空查询时所有候选同分。</summary>
    public static int Score(string? candidate, string? query)
    {
        var text = (candidate ?? string.Empty).Trim();
        var pattern = (query ?? string.Empty).Trim();
        if (pattern.Length == 0) return 1;
        if (text.Length == 0) return NoMatch;

        if (string.Equals(text, pattern, StringComparison.OrdinalIgnoreCase)) return 1000;

        if (text.StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
            return 900 - Math.Min(200, text.Length - pattern.Length);

        var index = text.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
        if (index >= 0) return 700 - Math.Min(200, index);

        var gaps = SubsequenceGaps(text, pattern);
        return gaps < 0 ? NoMatch : 500 - Math.Min(400, gaps);
    }

    /// <summary>多个字段里取最高分（命令名 + 别名 + 显示名这类场景）。</summary>
    public static int ScoreAny(string? query, params string?[] candidates)
    {
        var best = NoMatch;
        foreach (var candidate in candidates)
        {
            var score = Score(candidate, query);
            if (score > best) best = score;
        }

        return best;
    }

    /// <summary>
    /// 子序列匹配的「跳跃代价」：跳过越多字符代价越大；不匹配返回 -1。
    /// 汉字按字符处理，因此按拼音缩写搜中文名称不生效，按中文片段搜仍然可用。
    /// </summary>
    private static int SubsequenceGaps(string text, string pattern)
    {
        var source = text.ToLowerInvariant();
        var target = pattern.ToLowerInvariant();
        var gaps = 0;
        var cursor = 0;
        foreach (var character in target)
        {
            var found = source.IndexOf(character, cursor);
            if (found < 0) return -1;
            gaps += found - cursor;
            cursor = found + 1;
        }

        return gaps;
    }
}

/// <summary>
/// 命令的执行：解析、模型参数匹配与会话操作都在这里，供宿主桥与平台消息共用。
/// 界面侧的联想面板由 React（slashCommands.ts）自行根据 <c>chat.command.list</c> 的目录计算，
/// 不经过本类——两侧只共享命令定义与解析规则，不存在重复的「补全」实现。
/// </summary>
public static class SlashCommands
{
    private const string ApprovalManual = ChatApprovalModes.Manual;
    private const string ApprovalFull = ChatApprovalModes.Full;

    /// <summary>
    /// 执行一条命令。失败不抛异常：返回 <see cref="SlashCommandResult.Success" /> = false 与可读原因，
    /// 界面提示、平台消息回执都用同一份文案。
    /// 执行结果会写进会话的可见消息（通知气泡）：页面刷新后仍在（会话还在内存里），
    /// 但通知气泡**不落盘**——会话关闭 / 空闲卸载或应用重启后就不再出现。
    /// <paramref name="fromPlatform" /> 为 true 时拒绝只在桌面端可用的命令（如审批模式）。
    /// <paramref name="clearDraft" /> 为 true 时，执行成功会清空会话草稿——命令来自输入框时应当清空，
    /// 且必须**无条件**清空：面板选中的候选文本和草稿并不相等（草稿 <c>/hel</c> → 执行 <c>/help</c>），
    /// 不清掉的话重新打开会话会从快照里带回旧命令、输入面板还会自动弹回。
    /// 失败时始终保留草稿，方便修改重试。
    /// </summary>
    public static async Task<SlashCommandResult> ExecuteAsync(
        ChatSession session,
        string? text,
        bool fromPlatform = false,
        bool clearDraft = false)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (!SlashCommandParser.TryParse(text, out var input))
            return new SlashCommandResult(false, string.Empty, "这不是命令。");

        var command = SlashCommandCatalog.Find(input.Name);
        if (command is null)
            return new SlashCommandResult(false, input.Name,
                $"没有 /{input.Name} 这条命令，输入 /help 查看全部命令。");

        SlashCommandResult result;
        try
        {
            if (fromPlatform && !command.AllowFromPlatform)
            {
                result = new SlashCommandResult(false, command.Name,
                    $"{command.Usage} 只能在桌面端主窗口执行，消息平台里不支持。");
            }
            else if (command.Name == "model")
                result = await SelectModelAsync(session, input.Argument).ConfigureAwait(false);
            else if (command.Name == "approval")
                result = await SetApprovalAsync(session, input.Argument).ConfigureAwait(false);
            else if (command.Name == "stop")
                result = await StopAsync(session).ConfigureAwait(false);
            else if (command.Name == "compact")
                result = await CompactAsync(session).ConfigureAwait(false);
            else if (command.Name == "help")
                result = new SlashCommandResult(true, command.Name, SlashCommandCatalog.HelpText(fromPlatform));
            else
                result = new SlashCommandResult(false, command.Name, $"没有 /{command.Name} 这条命令。");
        }
        catch (ChatSessionException ex)
        {
            result = new SlashCommandResult(false, command.Name, ex.Message);
        }

        await session.AddNoticeAsync(NoticeText(command, result)).ConfigureAwait(false);
        if ((result.Success || result.Skipped) && clearDraft)
            await session.SetDraftAsync(string.Empty).ConfigureAwait(false);
        return result;
    }

    private static string NoticeText(SlashCommandDefinition command, SlashCommandResult result) =>
        result.Success || result.Skipped
            ? $"{command.ContractName} · {result.Message}"
            : $"{command.ContractName} 未执行：{result.Message}";

    private static Task<SlashCommandResult> SelectModelAsync(ChatSession session, string argument)
    {
        var snapshot = session.Snapshot;
        var models = snapshot.Models;
        if (models.Count == 0)
            return Task.FromResult(new SlashCommandResult(false, "model",
                "当前没有可用的对话模型，请先在设置里配置提供商与模型。"));

        if (string.IsNullOrWhiteSpace(argument))
        {
            var current = models.FirstOrDefault(model =>
                string.Equals(model.Key, snapshot.SelectedModelKey, StringComparison.Ordinal));
            var listing = string.Join("\n", models.Select((model, index) =>
                $"  {index + 1}. {model.Name}（{model.Provider} · {model.ModelId}）" +
                (ReferenceEquals(model, current) ? " ← 当前" : string.Empty)));
            // 只是列出可用模型，没有做改动：按提示呈现，不算失败
            return Task.FromResult(new SlashCommandResult(false, "model",
                $"当前模型：{Describe(current)}。\n可用模型：\n{listing}\n用法：/model [模型名称/模型ID]",
                Skipped: true));
        }

        var resolution = ResolveModel(models, argument);
        if (resolution.Model is null)
            return Task.FromResult(new SlashCommandResult(false, "model", resolution.Error ?? "没有找到该模型。"));

        return SelectModelCoreAsync(session, resolution.Model);
    }

    private static async Task<SlashCommandResult> SelectModelCoreAsync(ChatSession session, ChatModelState model)
    {
        await session.SelectModelAsync(model.Key).ConfigureAwait(false);
        return new SlashCommandResult(true, "model",
            $"已切换模型：{Describe(model)}，本会话立即生效，并记为默认模型（新会话沿用）。", model.Key);
    }

    private static async Task<SlashCommandResult> SetApprovalAsync(ChatSession session, string argument)
    {
        var mode = argument.Trim().ToLowerInvariant();
        switch (mode)
        {
            case "":
            {
                var current = session.Snapshot.ApprovalMode;
                // 只是列出当前模式，没有做改动：按提示呈现，不算失败
                return new SlashCommandResult(false, "approval",
                    $"当前审批模式：{current}。用法：/approval [manual|full]", Skipped: true);
            }
            case ApprovalManual:
            case ApprovalFull:
                // 审批模式写在全局设置里（_settings.ToolApproval + Save），不是「当前会话」的局部状态
                await session.SetApprovalModeAsync(mode).ConfigureAwait(false);
                return new SlashCommandResult(true, "approval",
                    mode == ApprovalFull
                        ? "已切换为 full：工具调用直接放行（全局默认，当前及之后新开的会话都生效）。"
                        : "已切换为 manual：工具调用前需要确认（全局默认，当前及之后新开的会话都生效）。");
            default:
                return new SlashCommandResult(false, "approval", $"未知的审批模式「{argument}」，只能是 manual 或 full。");
        }
    }

    private static async Task<SlashCommandResult> StopAsync(ChatSession session)
    {
        // 压缩期间没有活动 turn：`/stop` 也要能中断摘要生成，所以先看压缩
        if (!session.Snapshot.Generating)
        {
            if (!session.Compacting) return new SlashCommandResult(false, "stop", "当前没有正在生成的回复。");

            await session.StopAsync().ConfigureAwait(false);
            return new SlashCommandResult(true, "stop", "已请求停止压缩上下文。");
        }

        await session.StopAsync().ConfigureAwait(false);
        return new SlashCommandResult(true, "stop", "已请求停止当前回复。");
    }

    /// <summary>
    /// 手动压缩上下文。压缩过程本身会插一条「正在压缩上下文…」，结果由 <see cref="ExecuteAsync" />
    /// 统一包成通知；成败按 <see cref="CompactionStatus" /> 判断，不看文案。
    /// </summary>
    private static async Task<SlashCommandResult> CompactAsync(ChatSession session)
    {
        var outcome = await session.CompactAsync().ConfigureAwait(false);
        return outcome.Status switch
        {
            CompactionStatus.Compacted => new SlashCommandResult(true, "compact", outcome.Message),
            CompactionStatus.Failed => new SlashCommandResult(false, "compact", outcome.Message),
            // 无需压缩 / 用户取消：都不算失败，按提示呈现
            _ => new SlashCommandResult(false, "compact", outcome.Message, Skipped: true),
        };
    }

    /// <summary>模型解析结果：成功时 <see cref="Model" /> 非空，失败时 <see cref="Error" /> 是可读原因。</summary>
    public readonly record struct ModelResolution(ChatModelState? Model, string? Error);

    /// <summary>
    /// 把参数解析成会话里的模型：支持序号（列表第几个）、模型名称、模型标识、
    /// <c>提供商/标识</c> 与完整键；都不匹配时按模糊分取最高的一项，并列则不猜。
    /// </summary>
    public static ModelResolution ResolveModel(IReadOnlyList<ChatModelState> models, string? argument)
    {
        var query = (argument ?? string.Empty).Trim();
        if (query.Length == 0) return new ModelResolution(null, null);

        if (int.TryParse(query, out var ordinal) && ordinal >= 1 && ordinal <= models.Count)
            return new ModelResolution(models[ordinal - 1], null);

        var exact = models.FirstOrDefault(model => string.Equals(model.Key, query, StringComparison.OrdinalIgnoreCase))
            ?? models.FirstOrDefault(model => string.Equals(model.ModelId, query, StringComparison.OrdinalIgnoreCase))
            ?? models.FirstOrDefault(model => string.Equals(model.Name, query, StringComparison.OrdinalIgnoreCase))
            ?? models.FirstOrDefault(model => string.Equals(model.Name.Trim(), query, StringComparison.OrdinalIgnoreCase))
            ?? models.FirstOrDefault(model => string.Equals($"{model.Provider}/{model.ModelId}", query,
                StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return new ModelResolution(exact, null);

        var scored = new List<(ChatModelState Model, int Score)>();
        foreach (var model in models)
        {
            var score = SlashCommandFuzzy.ScoreAny(query, model.Name, model.ModelId, $"{model.Provider}/{model.ModelId}");
            if (score != SlashCommandFuzzy.NoMatch) scored.Add((model, score));
        }

        if (scored.Count == 0)
            return new ModelResolution(null,
                $"没有匹配「{query}」的模型，输入 /model 查看当前可用模型。");

        scored.Sort((left, right) => right.Score.CompareTo(left.Score));
        if (scored.Count > 1 && scored[0].Score == scored[1].Score)
        {
            var candidates = string.Join("、", scored.Where(entry => entry.Score == scored[0].Score)
                .Select(entry => entry.Model.Name));
            return new ModelResolution(null, $"「{query}」匹配到多个模型：{candidates}，请写得更具体一些。");
        }

        return new ModelResolution(scored[0].Model, null);
    }

    private static string Describe(ChatModelState? model) =>
        model is null ? "（未选择）" : $"{model.Name}（{model.Provider} · {model.ModelId}）";
}

/// <summary>
/// 命令的执行结果。<see cref="ModelKey" /> 只在切换模型成功时有值；
/// <see cref="Skipped" /> 表示命令没有做任何改动但不是错误（列表、无需压缩、用户取消），
/// 界面按提示呈现——不要用「消息文案开头是什么」来表达这种区别。
/// </summary>
public sealed record SlashCommandResult(
    bool Success,
    string CommandName,
    string Message,
    string? ModelKey = null,
    bool Skipped = false);
