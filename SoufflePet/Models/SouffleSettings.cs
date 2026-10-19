// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using Serilog;

namespace SoufflePet.Models;

/// <summary>
/// 应用设置（SoufflePet.json）：唯一事实源。属性变化触发 PropertyChanged，
/// 持久化与运行时副作用由订阅者统一处理（接线见 App 组合根），写入方（托盘菜单/
/// 设置界面等）只需给属性赋值，不自行 Save、不直接操作服务或窗口。
/// </summary>
public class SouffleSettings : INotifyPropertyChanged
{
    private static readonly string SettingsFilePath = Path.Combine(AppContext.BaseDirectory, "SoufflePet.json");
    private static readonly JsonSerializerOptions JsonSerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>标准属性变更：值变化时更新字段并触发 PropertyChanged。</summary>
    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    [Description("是否启用自动表情（检测到游戏/音乐应用自动切换表情，进程白名单见 autoexpression.json）")]
    public bool AutoExpressionEnabled
    {
        get;
        set => Set(ref field, value);
    } = true;

    /// <summary>
    /// 自动表情轮询间隔（秒）
    /// </summary>
    public float AutoExpressionPollSeconds { get; init; } = 3f;

    public string AutoExpressionGameExpression { get; init; } = "手柄";

    public string AutoExpressionMusicExpression { get; init; } = "麦克风";

    [Description("自动表情应用列表")]
    public List<AppEntry> AutoExpressionApps { get; init; } = AppEntry.BuildDefaults();

    [Description("是否启用口型律动（自动表情处于 music 状态时，系统播放声音驱动嘴部开合）")]
    public bool LipSyncEnabled
    {
        get;
        set => Set(ref field, value);
    } = true;

    [Description("是否启用目光跟随（视线跟随鼠标移动）")]
    public bool GazeFollowEnabled
    {
        get;
        set => Set(ref field, value);
    } = true;

    [Description("是否启用游戏输入联动")]
    public bool GamepadEnabled
    {
        get;
        set => Set(ref field, value);
    } = true;

    [Description("是否启用常显手柄（不等待游戏检测，直接显示手柄并驱动）")]
    public bool GamepadManual
    {
        get;
        set => Set(ref field, value);
    }

    [Description("是否启用常显音乐模式（不等待音乐检测，直接显示音乐表情并驱动口型律动）")]
    public bool MusicManual
    {
        get;
        set => Set(ref field, value);
    }

    [Description("是否显示帧率")]
    public bool IsShowFps
    {
        get;
        set => Set(ref field, value);
    }

    [Description("是否开机自启动（写入当前用户 HKCU/.../Run 注册表项）")]
    public bool StartWithWindows
    {
        get;
        set => Set(ref field, value);
    }

    [Description("模型根目录（应用目录下 wwwroot 内的相对路径，默认 models）")]
    public string ModelsDirectory { get; init; } = "models";

    [Description("模型名称（ModelsDirectory 下的子目录名，需与 model3.json 主文件名一致；修改后重启生效）")]
    public string ModelName { get; init; } = "舒芙蕾";

    public static SouffleSettings Load()
    {
        if (!File.Exists(SettingsFilePath)) return FallBackLoad();

        try
        {
            var settings = JsonSerializer.Deserialize<SouffleSettings>(File.ReadAllText(SettingsFilePath)) ?? new SouffleSettings();
            return settings;
        }
        catch (Exception e)
        {
            Log.ForContext<SouffleSettings>().Warning(e, "Failed to load settings");
            return FallBackLoad();
        }
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(this, JsonSerializerOptions);
        File.WriteAllText(SettingsFilePath, json);
    }

    private static SouffleSettings FallBackLoad()
    {
        var defaultSettings = new SouffleSettings();
        defaultSettings.Save();
        return defaultSettings;
    }
}
