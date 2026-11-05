// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Text;
using SoufflePet.Plugin.Sdk;

namespace SoufflePet.Services.Plugins;

/// <summary>
/// 一轮回复的插件正文管线：按启用顺序把增量交给参与本轮响应的插件。
/// 创建时已触发各插件的 BeginResponse；单个插件失败后本轮跳过它，正文原样放行。
/// </summary>
public sealed class ResponsePipeline
{
    private readonly List<Participant> _participants;

    internal ResponsePipeline(List<Participant> participants) => _participants = participants;

    /// <summary>处理一段正文增量，返回经过所有参与插件后的可见文本。</summary>
    public string Process(string text)
    {
        foreach (var participant in _participants)
        {
            if (participant.Failed) continue;

            try
            {
                text = participant.Plugin.ProcessResponse(text);
            }
            catch (Exception ex)
            {
                participant.Failed = true;
                participant.Owner.RecordFailure(participant.PluginId, $"响应处理失败：{ex.Message}");
            }
        }

        return text;
    }

    /// <summary>本轮正文结束：收尾各插件，并让暂存文本继续经过后面的插件。</summary>
    public string Flush()
    {
        if (_participants.Count == 0) return string.Empty;

        var output = new StringBuilder();
        for (var i = 0; i < _participants.Count; i++)
        {
            var current = _participants[i];
            if (current.Failed) continue;

            string tail;
            try
            {
                tail = current.Plugin.EndResponse();
            }
            catch (Exception ex)
            {
                current.Failed = true;
                current.Owner.RecordFailure(current.PluginId, $"响应收尾失败：{ex.Message}");
                continue;
            }

            if (tail.Length == 0) continue;

            for (var j = i + 1; j < _participants.Count; j++)
            {
                var next = _participants[j];
                if (next.Failed) continue;

                try
                {
                    tail = next.Plugin.ProcessResponse(tail);
                }
                catch (Exception ex)
                {
                    next.Failed = true;
                    next.Owner.RecordFailure(next.PluginId, $"响应处理失败：{ex.Message}");
                }
            }

            output.Append(tail);
        }

        return output.ToString();
    }

    /// <summary>本轮管线中的一个插件；失败后本轮不再调用它。</summary>
    internal sealed class Participant(PluginService owner, string pluginId, PluginBase plugin)
    {
        public PluginService Owner { get; } = owner;

        public string PluginId { get; } = pluginId;

        public PluginBase Plugin { get; } = plugin;

        public bool Failed { get; set; }
    }
}
