// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

namespace SoufflePet.Views;

/// <summary>桌宠窗口的托盘菜单操作接口（WebView 版与原生版共用）。</summary>
public interface IPetWindow
{
    void ToggleVisible();
    void ResetModel();

    /// <summary>自动表情联动：传表情名切换，传 null 恢复默认（无表情）。</summary>
    void SetExpression(string? name);

    /// <summary>当前正在显示的表情名（null = 默认状态/尚未确定）。</summary>
    string? CurrentExpression { get; }

    /// <summary>目光跟随开关：关闭时视线回正并停止跟随光标。</summary>
    void SetGazeFollow(bool enabled);

    /// <summary>帧率显示开关：窗口右上角帧率叠加层的显示与否。</summary>
    void SetFpsVisible(bool visible);

    /// <summary>
    /// 输入联动：开启后把手柄/键鼠输入写进模型的摇杆与按键参数。
    /// 只在模型显示手柄时开启（自动表情命中游戏，或托盘手动开启手柄模式）。
    /// </summary>
    void SetGamepadEnabled(bool enabled);

    /// <summary>显示器电源状态（true=亮屏，false=屏关闭）：关屏时停止渲染，亮屏恢复。</summary>
    void SetDisplayPower(bool on);

    void ExitForShutdown();
}
