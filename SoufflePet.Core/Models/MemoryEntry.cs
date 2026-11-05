// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;

namespace SoufflePet.Models;

/// <summary>
/// 长期记忆里的一条用户档案（称呼、偏好、常用路径等）。
/// 由模型通过 remember 工具写入，按 <see cref="Key"/> 去重覆盖。
/// </summary>
public sealed class MemoryEntry
{
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}
