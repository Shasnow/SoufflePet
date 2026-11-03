// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

namespace SoufflePet.Utils;

/// <summary>token 数量的统一展示格式：底部信息栏、气泡用量胶囊与用量浮窗共用。</summary>
public static class TokenDisplay
{
    /// <summary>紧凑格式：1.2B / 98.1M / 12.3K / 980（最多 1 位小数，整数值省略小数位）。</summary>
    public static string Compact(long tokens) => tokens switch
    {
        >= 1_000_000_000 => $"{tokens / 1_000_000_000d:0.#}B",
        >= 1_000_000 => $"{tokens / 1_000_000d:0.#}M",
        >= 1_000 => $"{tokens / 1_000d:0.#}K",
        _ => tokens.ToString("N0")
    };

    /// <summary>带千位分隔与单位的明细数字：2,481,299 tok。</summary>
    public static string Tokens(long tokens) => $"{tokens:N0} tok";

    /// <summary>百分比，保留 1 位小数：99.9%。</summary>
    public static string Percent(double rate) => $"{rate:P1}";
}
