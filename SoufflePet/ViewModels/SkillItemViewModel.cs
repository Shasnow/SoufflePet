// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using CommunityToolkit.Mvvm.ComponentModel;
using SoufflePet.Services;

namespace SoufflePet.ViewModels;

/// <summary>
/// 技能列表项：技能信息与正文在刷新时一次性读取，界面只负责展示与启用切换。
/// </summary>
public partial class SkillItemViewModel : ObservableObject
{
    public SkillItemViewModel(SkillInfo skill, string content, bool enabled)
    {
        Skill = skill;
        Content = content;
        Enabled = enabled;
    }

    /// <summary>启用状态被用户切换（需要立即写回设置）。</summary>
    public event EventHandler? EnabledChanged;

    public SkillInfo Skill { get; }

    public string Name => Skill.Name;

    public string Description => Skill.Description;

    public bool HasDescription => Skill.HasDescription;

    /// <summary>技能来源目录（应用目录或家目录下的 .agents/skills、.souffle/skills）。</summary>
    public string Source => Skill.Source;

    public string FilePath => Skill.FilePath;

    /// <summary>技能正文（已去掉 YAML frontmatter）。</summary>
    public string Content { get; }

    public bool HasContent => Content.Length > 0;

    [ObservableProperty]
    public partial bool Enabled { get; set; }

    partial void OnEnabledChanged(bool value) => EnabledChanged?.Invoke(this, EventArgs.Empty);
}
