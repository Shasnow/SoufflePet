// SPDX-License-Identifier: MIT
// Copyright (c) 2024 Coloryr
// Copyright (c) 2026 Shasnow

using System.ComponentModel;

namespace Live2DCSharpSDK.Framework;

/// <summary>
/// model3.json 反序列化 DTO。字段值由 JsonSerializer 填充，
/// 故声明时用 null! 初始化（消除 CS8618 警告）。
/// </summary>
public record ModelSettingObj
{
    public record FileReference
    {
        public record Expression
        {
            public string Name { get; set; } = null!;
            public string File { get; set; } = null!;
        }
        public record Motion
        {
            public string File { get; set; } = null!;
            public string Sound { get; set; } = null!;
            [DefaultValue(-1f)]
            public float FadeInTime { get; set; }
            [DefaultValue(-1f)]
            public float FadeOutTime { get; set; }
        }
        public string Moc { get; set; } = null!;
        public List<string> Textures { get; set; } = null!;
        public string Physics { get; set; } = null!;
        public string Pose { get; set; } = null!;
        public string DisplayInfo { get; set; } = null!;
        public List<Expression> Expressions { get; set; } = null!;
        public Dictionary<string, List<Motion>> Motions { get; set; } = null!;
        public string UserData { get; set; } = null!;
    }

    public record HitArea
    {
        public string Id { get; set; } = null!;
        public string Name { get; set; } = null!;
    }

    public record Parameter
    {
        public string Name { get; set; } = null!;
        public List<string> Ids { get; set; } = null!;
    }

    public FileReference FileReferences { get; set; } = null!;
    public List<HitArea> HitAreas { get; set; } = null!;
    public Dictionary<string, float> Layout { get; set; } = null!;
    public List<Parameter> Groups { get; set; } = null!;
}
