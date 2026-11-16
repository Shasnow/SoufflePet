// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace SoufflePet.Core.Tests;

/// <summary>
/// 测试进程统一把用户数据根目录指到临时目录，设置 / 会话 / 记忆等落盘不碰真实的 ~/.souffle。
/// 模块初始化早于任何测试读取该环境变量，多个测试同时落盘也互不干扰。
/// </summary>
internal static class TestEnvironment
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "soufflepet-tests", $"data-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("SOUFFLEPET_DATA_DIR", root);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(root, recursive: true); } catch { /* 退出时清理失败可忽略 */ }
        };
    }
}
