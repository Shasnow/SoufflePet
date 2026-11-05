// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.ComponentModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SoufflePet.Models;

/// <summary>
/// 一个可切换的 Live2D 模型：模型名、水印部件 Id，以及该模型自己的游戏/音乐表情名。
/// 可观察：模型管理面板的列表与详情绑定的是同一批属性，改名后列表要立即跟着变。
/// </summary>
public partial class Live2DModelConfig : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FolderName))]
    [NotifyPropertyChangedFor(nameof(FileStem))]
    [Description("模型名称：ModelsDirectory 下的子目录名，或「目录/主文件名」（目录名与 model3.json 主文件名不一致时）")]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    [Description("水印部件 Id（该模型里独立的水印部件，加载时把透明度压 0；留空表示不处理）")]
    public partial string WatermarkPartId { get; set; } = string.Empty;

    [ObservableProperty]
    [Description("游戏状态下自动切换的表情名（跟随模型配置；须是该模型已登记的表情名，留空表示不切换）")]
    public partial string GameExpression { get; set; } = "手柄";

    [ObservableProperty]
    [Description("音乐状态下自动切换的表情名（跟随模型配置；须是该模型已登记的表情名，留空表示不切换）")]
    public partial string MusicExpression { get; set; } = "麦克风";

    /// <summary>模型所在子目录：Name 写成「目录/主文件名」时取斜杠前部分，只有一段时与主文件名相同。</summary>
    [JsonIgnore]
    public string FolderName => SplitName().Folder;

    /// <summary>model3.json 的主文件名（不含扩展名）：Name 写成「目录/主文件名」时取斜杠后部分。</summary>
    [JsonIgnore]
    public string FileStem => SplitName().File;

    /// <summary>把 Name 拆成「目录名 + 主文件名」，兼容 \ 与 / 两种分隔写法；只有一段时两者相同。</summary>
    private (string Folder, string File) SplitName()
    {
        var name = (Name ?? string.Empty).Replace('\\', '/').Trim().Trim('/');
        var index = name.LastIndexOf('/');
        return index > 0 && index < name.Length - 1
            ? (name[..index], name[(index + 1)..])
            : (name, name);
    }
}