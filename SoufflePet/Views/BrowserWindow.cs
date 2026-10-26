// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Serilog;
using SoufflePet.Utils;

namespace SoufflePet.Views;

/// <summary>
/// AI 可控的内置浏览器窗口：纯代码构建（与 NativePetWindow / WebViewPetWindow 风格一致），
/// 内嵌 <see cref="NativeWebView" />（Windows 上即 WebView2/Chromium）加载网页。
/// 顶部工具栏给用户手动接管的入口——AI 操作与用户点击作用在同一视图上，可随时切换。
/// 窗口标题固定为 <see cref="WindowTitle" />：截图工具按标题找窗口，标题跟着页面变化会找不到。
/// </summary>
public sealed class BrowserWindow : Window
{
    private static readonly ILogger Logger = Log.ForContext<BrowserWindow>();

    /// <summary>窗口标题（固定值）：按标题截图/找窗口时用它精确定位本窗口。</summary>
    public const string WindowTitle = "SoufflePet 浏览器";

    private readonly TextBox _address;

    private readonly TextBlock _status;

    /// <summary>关闭按钮/Alt+F4 只隐藏窗口，应用退出时由服务调 CloseForShutdown 真正关闭。</summary>
    private bool _allowClose;

    /// <summary>内嵌浏览器视图（WebView2）：AI 工具与用户操作都作用在它上面。</summary>
    public NativeWebView View { get; }

    public BrowserWindow()
    {
        Title = WindowTitle;
        Width = 1100;
        Height = 760;
        MinWidth = 480;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _address = new TextBox
        {
            Margin = new Thickness(4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            PlaceholderText = "输入网址后回车，或直接在上方网页里手动操作",
        };
        _address.KeyDown += OnAddressKeyDown;

        _status = new TextBlock
        {
            Margin = new Thickness(8, 0, 8, 6),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsVisible = false,
        };

        View = new NativeWebView();
        View.EnvironmentRequested += (_, e) => WebViewProfile.Apply(e);
        View.NavigationCompleted += OnNavigationCompleted;
        View.NewWindowRequested += OnNewWindowRequested;

        var toolbar = new Grid
        {
            Margin = new Thickness(8),
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*,Auto"),
        };
        Add(toolbar, MakeButton("后退", () => View.GoBack()), 0);
        Add(toolbar, MakeButton("前进", () => View.GoForward()), 1);
        Add(toolbar, MakeButton("刷新", () => View.Refresh()), 2);
        Grid.SetColumn(_address, 3);
        toolbar.Children.Add(_address);
        Add(toolbar, MakeButton("前往", NavigateToAddress), 4);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*") };
        Grid.SetRow(toolbar, 0);
        Grid.SetRow(_status, 1);
        Grid.SetRow(View, 2);
        root.Children.Add(toolbar);
        root.Children.Add(_status);
        root.Children.Add(View);
        Content = root;

        Closing += (_, e) =>
        {
            if (_allowClose) return;
            // 关掉只隐藏：网页登录态、历史都留在会话里，AI 下次呼出还能接着操作
            e.Cancel = true;
            Hide();
        };
    }

    /// <summary>应用退出时真正关闭窗口（平时关闭只隐藏，便于再次呼出）。</summary>
    public void CloseForShutdown()
    {
        _allowClose = true;
        Close();
    }

    private static void Add(Grid grid, Control control, int column)
    {
        Grid.SetColumn(control, column);
        grid.Children.Add(control);
    }

    private static Button MakeButton(string text, Action action)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 4, 0) };
        button.Click += (_, _) =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "browser toolbar action failed");
            }
        };
        return button;
    }

    private void NavigateToAddress()
    {
        var text = _address.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return;
        try
        {
            View.Navigate(uri);
        }
        catch (Exception ex)
        {
            ShowStatus($"打开失败：{ex.Message}");
        }
    }

    private void OnAddressKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key != Avalonia.Input.Key.Enter) return;
        e.Handled = true;
        NavigateToAddress();
    }

    private void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        _address.Text = View.Source?.ToString() ?? string.Empty;
        if (e.IsSuccess) HideStatus();
        else ShowStatus($"网页加载失败（{View.Source}），可以试试刷新或换个地址。");
    }

    /// <summary>
    /// 页面里 target="_blank" 或 window.open 的链接默认会另开一个网页窗口，
    /// 这里一律拦下（Handled = true 即不再开新窗口）改在本窗口内跳转，保证始终只有一个浏览器窗口。
    /// 非 http/https 的目标（about:blank、javascript: 等）直接吞掉，不做跳转。
    /// </summary>
    private void OnNewWindowRequested(object? sender, WebViewNewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        var target = e.Request;
        if (target is null) return;
        if (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps) return;
        View.Navigate(target);
    }

    private void ShowStatus(string text)
    {
        _status.Text = text;
        _status.IsVisible = true;
    }

    private void HideStatus() => _status.IsVisible = false;
}