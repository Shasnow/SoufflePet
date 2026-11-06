// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Linq;
using SoufflePet.Core.Chat;
using Xunit;

namespace SoufflePet.Core.Tests;

/// <summary>行级 diff：hunk 位置、上下文合并、CRLF 与超限退化的行为。</summary>
public sealed class LineDiffTests
{
    [Fact]
    public void ReplacesMiddleLineWithContext()
    {
        var diff = LineDiff.Compute("a.txt", "A\nB\nC", "A\nX\nC");

        Assert.Equal(1, diff.Added);
        Assert.Equal(1, diff.Removed);
        Assert.False(diff.Truncated);

        var hunk = Assert.Single(diff.Hunks);
        Assert.Equal(1, hunk.OldStart);
        Assert.Equal(3, hunk.OldLines);
        Assert.Equal(1, hunk.NewStart);
        Assert.Equal(3, hunk.NewLines);
        Assert.Collection(hunk.Lines,
            line => Assert.Equal(("ctx", "A"), (line.Kind, line.Text)),
            line => Assert.Equal(("del", "B"), (line.Kind, line.Text)),
            line => Assert.Equal(("add", "X"), (line.Kind, line.Text)),
            line => Assert.Equal(("ctx", "C"), (line.Kind, line.Text)));
    }

    [Fact]
    public void AppendsLineAtEnd()
    {
        var diff = LineDiff.Compute("a.txt", "A\nB", "A\nB\nC");

        Assert.Equal(1, diff.Added);
        Assert.Equal(0, diff.Removed);
        var hunk = Assert.Single(diff.Hunks);
        Assert.Equal(1, hunk.OldStart);
        Assert.Equal(2, hunk.OldLines);
        Assert.Equal(1, hunk.NewStart);
        Assert.Equal(3, hunk.NewLines);
        Assert.Equal(("add", "C"), (hunk.Lines[^1].Kind, hunk.Lines[^1].Text));
    }

    [Fact]
    public void NewFileIsAllAdditions()
    {
        var diff = LineDiff.Compute("new.txt", string.Empty, "A\nB");

        Assert.Equal(2, diff.Added);
        Assert.Equal(0, diff.Removed);
        var hunk = Assert.Single(diff.Hunks);
        Assert.Equal(1, hunk.OldStart);
        Assert.Equal(0, hunk.OldLines);
        Assert.Equal(1, hunk.NewStart);
        Assert.Equal(2, hunk.NewLines);
        Assert.All(hunk.Lines, line => Assert.Equal("add", line.Kind));
    }

    [Fact]
    public void DeletedFileIsAllRemovals()
    {
        var diff = LineDiff.Compute("gone.txt", "A\nB", string.Empty);

        Assert.Equal(0, diff.Added);
        Assert.Equal(2, diff.Removed);
        var hunk = Assert.Single(diff.Hunks);
        Assert.All(hunk.Lines, line => Assert.Equal("del", line.Kind));
    }

    [Fact]
    public void IdenticalTextProducesNoHunks()
    {
        var diff = LineDiff.Compute("a.txt", "A\r\nB\r\n", "A\nB");

        Assert.Empty(diff.Hunks);
        Assert.False(diff.Truncated);
        Assert.Equal(0, diff.Added);
        Assert.Equal(0, diff.Removed);
    }

    [Fact]
    public void CrLfAndLfAreComparedByLineContent()
    {
        var diff = LineDiff.Compute("a.txt", "A\r\nB\r\nC", "A\nX\nC");

        Assert.Equal(1, diff.Added);
        Assert.Equal(1, diff.Removed);
        var hunk = Assert.Single(diff.Hunks);
        Assert.Equal(("del", "B"), (hunk.Lines[1].Kind, hunk.Lines[1].Text));
        Assert.Equal(("add", "X"), (hunk.Lines[2].Kind, hunk.Lines[2].Text));
    }

    [Fact]
    public void SeparateChangesProduceSeparateHunksWhenFarApart()
    {
        var oldText = string.Join('\n', Enumerable.Range(1, 30).Select(i => $"line{i}"));
        var newLines = Enumerable.Range(1, 30).Select(i => $"line{i}").ToList();
        newLines[1] = "changed2";
        newLines[27] = "changed28";

        var diff = LineDiff.Compute("a.txt", oldText, string.Join('\n', newLines));

        Assert.Equal(2, diff.Hunks.Count);
    }

    [Fact]
    public void OversizedFileIsMarkedTruncatedWithoutHunks()
    {
        var text = string.Join('\n', Enumerable.Range(1, 3001).Select(i => $"line{i}"));
        var diff = LineDiff.Compute("big.txt", text, text + "\nlast");

        Assert.True(diff.Truncated);
        Assert.Empty(diff.Hunks);
    }

    [Fact]
    public void WidelyDifferentFilesFallBackToCoarseReplacement()
    {
        // 600 行全改：编辑距离超过 Myers 上限，退化为旧行全删 + 新行全加
        var oldText = string.Join('\n', Enumerable.Range(1, 600).Select(i => $"old{i}"));
        var newText = string.Join('\n', Enumerable.Range(1, 600).Select(i => $"new{i}"));

        var diff = LineDiff.Compute("a.txt", oldText, newText);

        Assert.Equal(600, diff.Added);
        Assert.Equal(600, diff.Removed);
        Assert.False(diff.Truncated);
        var hunk = Assert.Single(diff.Hunks);
        Assert.Equal(1200, hunk.Lines.Count);
    }

    [Fact]
    public void RandomSmallInputsProduceOptimalAndReconstructableDiffs()
    {
        var random = new Random(20261106);
        for (var round = 0; round < 120; round++)
        {
            var oldLines = RandomLines(random);
            var newLines = RandomLines(random);
            var oldText = string.Join('\n', oldLines);
            var newText = string.Join('\n', newLines);

            var diff = LineDiff.Compute("x.txt", oldText, newText, context: 100);
            Assert.False(diff.Truncated);

            // 最优性：added/removed 必须与 LCS 推出的编辑距离一致
            var lcs = Lcs(oldLines, newLines);
            Assert.Equal(newLines.Count - lcs, diff.Added);
            Assert.Equal(oldLines.Count - lcs, diff.Removed);

            // 上下文足够大时，hunk 应能重建完整的新/旧文本
            var lines = diff.Hunks.SelectMany(hunk => hunk.Lines).ToList();
            Assert.Equal(newLines, lines.Where(line => line.Kind != "del").Select(line => line.Text));
            Assert.Equal(oldLines, lines.Where(line => line.Kind != "add").Select(line => line.Text));
        }
    }

    private static List<string> RandomLines(Random random)
    {
        var count = random.Next(0, 8);
        var lines = new List<string>(count);
        for (var i = 0; i < count; i++) lines.Add(((char)('A' + random.Next(0, 5))).ToString());
        return lines;
    }

    private static int Lcs(List<string> a, List<string> b)
    {
        var dp = new int[a.Count + 1, b.Count + 1];
        for (var i = 1; i <= a.Count; i++)
        {
            for (var j = 1; j <= b.Count; j++)
            {
                dp[i, j] = a[i - 1] == b[j - 1]
                    ? dp[i - 1, j - 1] + 1
                    : Math.Max(dp[i - 1, j], dp[i, j - 1]);
            }
        }

        return dp[a.Count, b.Count];
    }
}