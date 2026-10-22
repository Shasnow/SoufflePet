// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace SoufflePet.Models;

/// <summary>ask_user 工具的问题参数：模型给出问题正文与预设选项，界面固定再附带一个自由输入项。</summary>
public sealed class AskUserQuestion
{
    [Description("问题正文，一句话，尽量明确")]
    public string Question { get; set; } = string.Empty;

    [Description("预设选项，2-5 个；每项是 { text, description }，description 可省略，界面显示为选项下方的小字")]
    public AskUserOption[]? Options { get; set; }
}

/// <summary>ask_user 工具的一个预设选项：文本 + 可选说明。</summary>
public sealed class AskUserOption
{
    [Description("选项文本，尽量简短，用”（推荐）“表示推荐此选项")]
    public string Text { get; set; } = string.Empty;

    [Description("可选的补充说明，一句话；界面上以不加换行的小字显示在选项文本下方")]
    public string? Description { get; set; }
}

/// <summary>要展示给用户的一个问题：问题正文 + 预设选项（界面会额外补一个自由输入项）。</summary>
public sealed record UserQuestion(string Text, IReadOnlyList<UserOption> Options);

/// <summary>界面上的一个选项：文本 + 可选说明（说明超长时界面截断，悬停看全文）。</summary>
public sealed record UserOption(string Text, string? Description = null);

/// <summary>用户对一个问题的回答。</summary>
public sealed record UserAnswer(string Question, string Answer);

/// <summary>
/// 一次提问请求：界面填完答案后调用 <see cref="Submit"/>，工具执行侧 await <see cref="Answered"/> 取结果；
/// 用户取消或对话被停止时结果为 null。
/// </summary>
public sealed class UserQuestionRequest
{
    private readonly TaskCompletionSource<IReadOnlyList<UserAnswer>?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _resolved;

    public UserQuestionRequest(IReadOnlyList<UserQuestion> questions) => Questions = questions;

    public IReadOnlyList<UserQuestion> Questions { get; }

    /// <summary>用户提交的答案；null 表示取消或对话被停止。</summary>
    public Task<IReadOnlyList<UserAnswer>?> Answered => _completion.Task;

    /// <summary>请求结束（提交或取消）时触发，界面据此收起提问面板。</summary>
    public event Action? Resolved;

    /// <summary>是否已经有结果；界面显示面板前用它挡掉「刚弹出就已经被取消」的请求。</summary>
    public bool IsResolved => Volatile.Read(ref _resolved) != 0;

    public void Submit(IReadOnlyList<UserAnswer> answers) => Complete(answers);

    public void Cancel() => Complete(null);

    private void Complete(IReadOnlyList<UserAnswer>? answers)
    {
        // 提交与取消可能同时发生（例如提交的瞬间按了停止），只认第一次
        if (Interlocked.Exchange(ref _resolved, 1) != 0) return;

        _completion.TrySetResult(answers);
        Resolved?.Invoke();
    }
}
