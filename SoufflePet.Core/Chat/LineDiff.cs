// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Linq;

namespace SoufflePet.Core.Chat;

/// <summary>
/// 行级差异计算（Myers）：给编辑工具与聊天界面提供结构化 hunk，而不是整份文件文本。
/// 差异过大时退化为「整份替换 / 只给摘要」，避免把超大对象塞进会话快照。
/// </summary>
public static class LineDiff
{
    /// <summary>参与 diff 的文本字符上限；超过时只标记 truncated、不生成 hunk。</summary>
    private const int MaxDiffChars = 200_000;

    /// <summary>参与 diff 的行数上限（新旧各自）。</summary>
    private const int MaxDiffLines = 3000;

    /// <summary>展示的 hunk 行数上限；超过时截断并标记 truncated。</summary>
    private const int MaxDisplayLines = 1200;

    /// <summary>Myers 最大编辑距离；改动过于分散时退化为整份替换。</summary>
    private const int MaxEditDistance = 1000;

    /// <summary>
    /// 计算两份文本的行级差异。文件过大时返回空 hunk + <c>Truncated = true</c>，
    /// 由界面提示「文件过大，未生成 diff」。
    /// </summary>
    public static ChatFileDiffState Compute(string path, string oldText, string newText, int context = 3)
    {
        var oldLines = SplitLines(oldText);
        var newLines = SplitLines(newText);

        if (oldText.Length > MaxDiffChars
            || newText.Length > MaxDiffChars
            || oldLines.Count > MaxDiffLines
            || newLines.Count > MaxDiffLines)
            return new ChatFileDiffState(path, 0, 0, true, []);

        var ops = ComputeOps(oldLines, newLines);
        if (ops is null) return CoarseDiff(path, oldLines, newLines);

        var added = ops.Count(op => op.Kind == OpKind.Insert);
        var removed = ops.Count(op => op.Kind == OpKind.Delete);
        if (added == 0 && removed == 0)
            return new ChatFileDiffState(path, 0, 0, false, []);

        var truncated = false;
        var hunks = BuildHunks(ops, oldLines, newLines, Math.Max(context, 0), ref truncated);
        return new ChatFileDiffState(path, added, removed, truncated, hunks);
    }

    /// <summary>按 \r\n / \r / \n 切行；末尾换行不额外产生空行。</summary>
    internal static List<string> SplitLines(string text)
    {
        if (text.Length == 0) return [];

        var normalized = text.ReplaceLineEndings("\n");
        var parts = normalized.Split('\n');
        var count = parts.Length;
        if (count > 0 && parts[^1].Length == 0) count--;

        var lines = new List<string>(count);
        for (var i = 0; i < count; i++) lines.Add(parts[i]);
        return lines;
    }

    /// <summary>改动过于分散（超过 Myers 上限）时的退化方案：旧行全删、新行全加。</summary>
    private static ChatFileDiffState CoarseDiff(string path, List<string> oldLines, List<string> newLines)
    {
        var lines = new List<ChatDiffLineState>();
        var truncated = false;
        foreach (var line in oldLines)
        {
            if (!TryAdd(lines, "del", line, ref truncated)) break;
        }

        if (!truncated)
        {
            foreach (var line in newLines)
            {
                if (!TryAdd(lines, "add", line, ref truncated)) break;
            }
        }

        var hunks = lines.Count == 0
            ? Array.Empty<ChatDiffHunkState>()
            : [new ChatDiffHunkState(1, oldLines.Count, 1, newLines.Count, lines)];
        return new ChatFileDiffState(path, newLines.Count, oldLines.Count, truncated, hunks);
    }

    private static List<ChatDiffHunkState> BuildHunks(
        List<Op> ops,
        List<string> oldLines,
        List<string> newLines,
        int context,
        ref bool truncated)
    {
        var changeIndexes = new List<int>();
        for (var i = 0; i < ops.Count; i++)
        {
            if (ops[i].Kind != OpKind.Equal) changeIndexes.Add(i);
        }

        var groups = new List<(int Start, int End)>();
        foreach (var index in changeIndexes)
        {
            var start = Math.Max(0, index - context);
            var end = Math.Min(ops.Count - 1, index + context);
            if (groups.Count > 0 && start <= groups[^1].End + 1)
                groups[^1] = (groups[^1].Start, Math.Max(groups[^1].End, end));
            else
                groups.Add((start, end));
        }

        var oldPrefix = new int[ops.Count + 1];
        var newPrefix = new int[ops.Count + 1];
        for (var i = 0; i < ops.Count; i++)
        {
            oldPrefix[i + 1] = oldPrefix[i] + (ops[i].OldIndex >= 0 ? 1 : 0);
            newPrefix[i + 1] = newPrefix[i] + (ops[i].NewIndex >= 0 ? 1 : 0);
        }

        var hunks = new List<ChatDiffHunkState>(groups.Count);
        var displayed = 0;
        foreach (var (start, end) in groups)
        {
            var lines = new List<ChatDiffLineState>(end - start + 1);
            int? firstOld = null;
            int? firstNew = null;
            for (var i = start; i <= end; i++)
            {
                var op = ops[i];
                var (kind, text) = op.Kind switch
                {
                    OpKind.Equal => ("ctx", oldLines[op.OldIndex]),
                    OpKind.Delete => ("del", oldLines[op.OldIndex]),
                    _ => ("add", newLines[op.NewIndex]),
                };

                if (op.OldIndex >= 0) firstOld ??= op.OldIndex;
                if (op.NewIndex >= 0) firstNew ??= op.NewIndex;

                if (displayed + lines.Count >= MaxDisplayLines)
                {
                    truncated = true;
                    break;
                }

                lines.Add(new ChatDiffLineState(kind, text));
            }

            if (lines.Count == 0) break;
            displayed += lines.Count;

            hunks.Add(new ChatDiffHunkState(
                (firstOld ?? oldPrefix[start]) + 1,
                lines.Count(line => line.Kind != "add"),
                (firstNew ?? newPrefix[start]) + 1,
                lines.Count(line => line.Kind != "del"),
                lines));

            if (truncated) break;
        }

        return hunks;
    }

    private static bool TryAdd(List<ChatDiffLineState> lines, string kind, string text, ref bool truncated)
    {
        if (lines.Count >= MaxDisplayLines)
        {
            truncated = true;
            return false;
        }

        lines.Add(new ChatDiffLineState(kind, text));
        return true;
    }

    /// <summary>Myers 编辑脚本；超出最大编辑距离时返回 null。</summary>
    private static List<Op>? ComputeOps(List<string> oldLines, List<string> newLines)
    {
        var n = oldLines.Count;
        var m = newLines.Count;
        var max = n + m;
        if (max == 0) return [];

        var v = new int[2 * max + 1];
        var offset = max;
        var trace = new List<int[]>(Math.Min(max, MaxEditDistance) + 1);
        var limit = Math.Min(max, MaxEditDistance);

        for (var d = 0; d <= limit; d++)
        {
            // 记录进入本层前的整段 v：[k-d, k+d]，回溯时 prevK 落在上一层，不能只存同奇偶的 k
            var snapshot = new int[2 * d + 1];
            Array.Copy(v, offset - d, snapshot, 0, snapshot.Length);
            trace.Add(snapshot);

            for (var k = -d; k <= d; k += 2)
            {
                var x = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1])
                    ? v[offset + k + 1]
                    : v[offset + k - 1] + 1;
                var y = x - k;
                while (x < n && y < m && oldLines[x] == newLines[y])
                {
                    x++;
                    y++;
                }

                v[offset + k] = x;
                if (x >= n && y >= m) return Backtrack(trace, d, n, m);
            }
        }

        return null;
    }

    private static List<Op> Backtrack(List<int[]> trace, int depth, int oldCount, int newCount)
    {
        // 从终点反向走回 (0,0)：每层先补回共同后缀（equal），再判断本步是删除还是插入
        var result = new List<Op>();
        var x = oldCount;
        var y = newCount;
        for (var d = depth; d >= 0; d--)
        {
            var snapshot = trace[d];
            var k = x - y;

            if (d == 0)
            {
                while (x > 0 && y > 0)
                {
                    result.Add(Op.Equal(x - 1, y - 1));
                    x--;
                    y--;
                }

                break;
            }

            var prevK = k == -d || (k != d && snapshot[k - 1 + d] < snapshot[k + 1 + d])
                ? k + 1
                : k - 1;
            var prevX = snapshot[prevK + d];
            var prevY = prevX - prevK;

            while (x > prevX && y > prevY)
            {
                result.Add(Op.Equal(x - 1, y - 1));
                x--;
                y--;
            }

            if (x == prevX)
            {
                result.Add(Op.Insert(y - 1));
                y--;
            }
            else
            {
                result.Add(Op.Delete(x - 1));
                x--;
            }
        }

        result.Reverse();
        return result;
    }

    private enum OpKind
    {
        Equal,
        Delete,
        Insert,
    }

    private readonly record struct Op(OpKind Kind, int OldIndex, int NewIndex)
    {
        public static Op Equal(int oldIndex, int newIndex) => new(OpKind.Equal, oldIndex, newIndex);

        public static Op Delete(int oldIndex) => new(OpKind.Delete, oldIndex, -1);

        public static Op Insert(int newIndex) => new(OpKind.Insert, -1, newIndex);
    }
}