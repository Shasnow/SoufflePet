// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using SoufflePet.Models;

namespace SoufflePet.Services;

/// <summary>
/// 工具的「向用户提问」通道：工具执行侧发起请求，对话界面展示提问面板并收集答案。
/// 与 <see cref="ChatToolAccess"/> 的审批类似——没有订阅者（界面不可用）时按取消处理。
/// </summary>
public sealed class UserQuestionService
{
    private static readonly ILogger Logger = Log.ForContext<UserQuestionService>();

    /// <summary>需要用户回答时触发；界面订阅后展示提问面板。</summary>
    public event Action<UserQuestionRequest>? QuestionAsked;

    /// <summary>发起一次提问并等待用户回答；对话被停止时返回 null。</summary>
    public async Task<IReadOnlyList<UserAnswer>?> AskAsync(
        IReadOnlyList<UserQuestion> questions,
        CancellationToken cancellationToken)
    {
        var request = new UserQuestionRequest(questions);
        if (QuestionAsked is { } handler)
        {
            handler(request);
        }
        else
        {
            Logger.Debug("ask_user ignored: no UI subscriber");
            return null;
        }

        // 停止对话时同步取消等待，避免工具循环卡住
        using var registration = cancellationToken.Register(request.Cancel);
        var answers = await request.Answered.ConfigureAwait(false);
        Logger.Debug("ask_user resolved questions={Count} answered={Answered}", questions.Count, answers is not null);
        return answers;
    }
}
