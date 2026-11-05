// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

namespace SoufflePet.Services;

/// <summary>
/// 启动期 UI 模式：WebDev 为 true 时让 AppWebWindow 指向 Vite dev server（仅开发用），
/// 否则加载内置的 wwwroot/app 产物。React SPA 是唯一界面，不再有渲染器切换。
/// </summary>
public sealed record UiMode(bool WebDev);
