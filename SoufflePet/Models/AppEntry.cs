// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SoufflePet.Models;

/// <summary>单个应用的检测条目（逐应用配置检测策略）。</summary>
public class AppEntry
{
    /// <summary>目标进程名（不含 .exe，忽略大小写）。</summary>
    public string Process { get; set; } = "";

    /// <summary>类别：game（游戏）或 music（音乐），决定命中后切换的表情。</summary>
    public string Category { get; set; } = "game";

    /// <summary>检测方式：process（进程检测）或 foreground（前台窗口检测），缺省 process。</summary>
    public string Method { get; set; } = "process";

    /// <summary>
    /// 前台窗口标题匹配模式（仅 method=foreground 时生效）：/…/ 形式为正则，否则忽略大小写子串。
    /// foreground 模式只检测窗口标题，不做进程反查。
    /// </summary>
    public string TitlePattern { get; set; } = "";

    /// <summary>条目在 Apps 列表中的索引（运行时由服务赋值，供检测器缓存等按条目定位）。</summary>
    [JsonIgnore]
    internal int Index;
    
    public static List<AppEntry> BuildDefaults() =>
    [
        new() { Process = "steam", Category = "game", Method = "foreground", TitlePattern = "Steam" },
        new() { Process = "LeagueClient", Category = "game" },
        new() { Process = "LeagueClientUx", Category = "game" },
        new() { Process = "RiotClientServices", Category = "game" },
        new() { Process = "GenshinImpact", Category = "game" },
        new() { Process = "YuanShen", Category = "game" },
        new() { Process = "StarRail", Category = "game" },
        new() { Process = "ZenlessZoneZero", Category = "game" },
        new() { Process = "cs2", Category = "game" },
        new() { Process = "dota2", Category = "game" },
        new() { Process = "Warframe", Category = "game" },
        new() { Process = "Minecraft", Category = "game" },
        new() { Process = "QQMusic", Category = "music" },
        new() { Process = "CloudMusic", Category = "music" },
        new() { Process = "Kugou", Category = "music" },
        new() { Process = "KwMusic", Category = "music" },
        new() { Process = "spotify", Category = "music" },
        new() { Process = "foobar2000", Category = "music" },
        new() { Process = "AIMP", Category = "music" },
        new() { Process = "MusicBee", Category = "music" },
        new() { Process = "yesplaymusic", Category = "music" },
    ];
}
