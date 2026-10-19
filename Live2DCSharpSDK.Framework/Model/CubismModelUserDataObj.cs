// SPDX-License-Identifier: MIT
// Copyright (c) 2024 Coloryr
// Copyright (c) 2026 Shasnow

namespace Live2DCSharpSDK.Framework.Model;

/// <summary>
/// userdata3.json 反序列化 DTO。字段值由 JsonSerializer 填充，
/// 故声明时用 null! 初始化（消除 CS8618 警告）。
/// </summary>
public record CubismModelUserDataObj
{
    public record MetaObj
    {
        public int UserDataCount { get; set; }
        public int TotalUserDataSize { get; set; }
    }

    public record UserDataObj
    {
        public string Target { get; set; } = null!;
        public string Id { get; set; } = null!;
        public string Value { get; set; } = null!;
    }

    public MetaObj Meta { get; set; } = null!;
    public List<UserDataObj> UserData { get; set; } = null!;
}
