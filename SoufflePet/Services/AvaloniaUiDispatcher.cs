// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using Avalonia.Threading;

namespace SoufflePet.Services;

/// <summary>Avalonia Dispatcher 的 IUiDispatcher 实现（宿主专用）。</summary>
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public bool IsUiThread => Dispatcher.UIThread.CheckAccess();

    public void Post(Action action) => Dispatcher.UIThread.Post(action);

    public void Invoke(Action action)
    {
        if (IsUiThread) action();
        else Dispatcher.UIThread.Invoke(action);
    }
}