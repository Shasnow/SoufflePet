// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Text;

namespace SoufflePet.Plugins.DsmlCleaner;

/// <summary>
/// 过滤模型正文里泄漏的 DSML 工具调用标记（&lt;｜｜DSML｜｜tool_calls&gt; 等）。
/// 流式增量可能把标记拆成多段，未闭合的片段会暂存到下一次输入；成块的工具调用内容整段丢弃。
/// </summary>
internal sealed class DsmlResponseFilter
{
    /// <summary>迟迟等不到 '&gt;' 时不再当作标记，避免正常文本被无限期扣留。</summary>
    private const int MaxPendingTagLength = 256;

    private readonly StringBuilder _buffer = new();
    private readonly Stack<string> _openBlocks = [];

    /// <summary>每轮响应开始前清空状态，避免上一轮的暂存内容串到下一轮。</summary>
    public void Reset()
    {
        _buffer.Clear();
        _openBlocks.Clear();
    }

    /// <summary>处理一段增量，返回可以展示给用户的部分。</summary>
    public string Process(string text)
    {
        if (text.Length > 0) _buffer.Append(text);
        return Drain(final: false);
    }

    /// <summary>本轮响应结束时调用，释放被当作潜在标记而暂存的内容。</summary>
    public string Flush() => Drain(final: true);

    private string Drain(bool final)
    {
        var output = new StringBuilder();
        var text = _buffer.ToString();
        var index = 0;

        while (index < text.Length)
        {
            var tagStart = text.IndexOf('<', index);
            if (tagStart < 0)
            {
                if (_openBlocks.Count == 0) output.Append(text, index, text.Length - index);
                index = text.Length;
                break;
            }

            if (_openBlocks.Count == 0) output.Append(text, index, tagStart - index);

            var tagEnd = text.IndexOf('>', tagStart + 1);
            if (tagEnd < 0)
            {
                // 标记可能被拆在相邻增量里：留到下一次；真正的普通文本（超长或已结束）原样输出
                var pending = text.AsSpan(tagStart);
                if (!final && pending.Length <= MaxPendingTagLength)
                {
                    index = tagStart;
                    break;
                }

                if (_openBlocks.Count == 0 && !pending.Contains("DSML", StringComparison.OrdinalIgnoreCase))
                    output.Append(pending);
                index = text.Length;
                break;
            }

            var tag = text.AsSpan(tagStart, tagEnd - tagStart + 1);
            if (!TryParseDsmlTag(tag, out var closing, out var name))
            {
                if (_openBlocks.Count == 0) output.Append(tag);
                index = tagEnd + 1;
                continue;
            }

            if (closing)
            {
                if (_openBlocks.Contains(name))
                {
                    while (_openBlocks.Count > 0 && _openBlocks.Pop() != name) { }
                }
            }
            else if (IsBlockTag(name))
            {
                _openBlocks.Push(name);
            }

            index = tagEnd + 1;
        }

        _buffer.Remove(0, index);
        return output.ToString();
    }

    private static bool IsBlockTag(string name) =>
        name is "tool_calls" or "tool_call" or "function_calls" or "function" or "invoke" or "parameter";

    /// <summary>识别 &lt;｜｜DSML｜｜xxx&gt; / &lt;/｜｜DSML｜｜ xxx&gt; 这类标记，兼容半角竖线写法。</summary>
    private static bool TryParseDsmlTag(ReadOnlySpan<char> tag, out bool closing, out string name)
    {
        closing = false;
        name = string.Empty;
        if (tag.Length < 3 || tag[0] != '<') return false;

        var body = tag[1..^1];
        if (body.Length > 0 && body[0] == '/')
        {
            closing = true;
            body = body[1..];
        }

        var marker = body.IndexOf("DSML", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return false;

        var rest = body[(marker + 4)..];
        var start = 0;
        while (start < rest.Length && !char.IsAsciiLetterOrDigit(rest[start]) && rest[start] != '_') start++;

        var end = start;
        while (end < rest.Length && (char.IsAsciiLetterOrDigit(rest[end]) || rest[end] == '_')) end++;

        if (end > start) name = rest[start..end].ToString().ToLowerInvariant();
        return true;
    }
}
