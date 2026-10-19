// SPDX-License-Identifier: MIT
// Copyright (c) 2024 Coloryr
// Copyright (c) 2026 Shasnow

namespace Live2DCSharpSDK.Framework.Rendering;

/// <summary>
/// カラーブレンディングのモード
/// </summary>
public enum CubismBlendMode
{
    /// <summary>
    /// 通常
    /// </summary>
    Normal = 0,
    /// <summary>
    /// 加算
    /// </summary>
    Additive = 1,
    /// <summary>
    /// 乗算
    /// </summary>
    Multiplicative = 2,
    /// <summary>
    /// マスク
    /// </summary>
    Mask = 3,
};
