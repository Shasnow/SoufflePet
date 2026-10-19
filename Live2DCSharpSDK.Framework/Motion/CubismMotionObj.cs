// SPDX-License-Identifier: MIT
// Copyright (c) 2024 Coloryr
// Copyright (c) 2026 Shasnow

namespace Live2DCSharpSDK.Framework.Motion;

/// <summary>
/// motion3.json 反序列化 DTO。字段值由 JsonSerializer 填充，
/// 故声明时用 null! 初始化（消除 CS8618 警告）。
/// </summary>
public record CubismMotionObj
{
    public record MetaObj
    {
        public float Duration { get; set; }
        public bool Loop { get; set; }
        public bool AreBeziersRestricted { get; set; }
        public int CurveCount { get; set; }
        public float Fps { get; set; }
        public int TotalSegmentCount { get; set; }
        public int TotalPointCount { get; set; }
        public float? FadeInTime { get; set; }
        public float? FadeOutTime { get; set; }
        public int UserDataCount { get; set; }
        public int TotalUserDataSize { get; set; }
    }
    public record Curve
    {
        public float? FadeInTime { get; set; }
        public float? FadeOutTime { get; set; }
        public List<float> Segments { get; set; } = null!;
        public string Target { get; set; } = null!;
        public string Id { get; set; } = null!;
    }
    public record UserDataObj
    {
        public float Time { get; set; }
        public string Value { get; set; } = null!;
    }
    public MetaObj Meta { get; set; } = null!;
    public List<Curve> Curves { get; set; } = null!;
    public List<UserDataObj> UserData { get; set; } = null!;
}
