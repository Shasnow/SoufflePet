// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoufflePet.Models;

namespace SoufflePet.ViewModels;

/// <summary>
/// 对话窗口的视图模型：消息列表 + 输入草稿 + 发送命令 + 模型选择。
/// 目前消息只做本地回显（把用户输入追加成气泡），尚未接入 LLM 服务。
/// </summary>
public partial class ChatViewModel : ViewModelBase
{
    private readonly SouffleSettings _settings;

    /// <summary>构造期间同步初始选中项时不触发持久化。</summary>
    private bool _initialized;

    public ChatViewModel(SouffleSettings settings)
    {
        _settings = settings;
        if (settings.ChatModels is null)
            settings.ChatModels = [];

        // 首次启动列表为空且没有已保存的模型名，此时保持未选中状态。
        SelectedModel = settings.ChatModelName is { Length: > 0 } name
            ? Models.FirstOrDefault(m => m.Name == name)
            : null;
        _initialized = true;
    }

    /// <summary>消息列表（首次打开即包含一条助手问候）。</summary>
    public ObservableCollection<ChatMessageViewModel> Messages { get; } =
    [
        new("今天想聊点什么呀？", isUser: false),
    ];

    /// <summary>可选的对话模型（与设置共享同一集合，添加后直接持久化）。</summary>
    public ObservableCollection<ChatModelConfig> Models => _settings.ChatModels;

    /// <summary>输入框内容（与 TextBox 双向绑定）。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _draft = string.Empty;

    /// <summary>当前选中的模型；改变时写回设置（由 App 组合根统一持久化）。</summary>
    [ObservableProperty]
    public partial ChatModelConfig? SelectedModel { get; set; }

    /// <summary>模型按钮上显示的文案。</summary>
    public string ModelButtonText => $"模型 · {SelectedModel?.Name ?? "请选择模型"} ▾";

    partial void OnSelectedModelChanged(ChatModelConfig? value)
    {
        OnPropertyChanged(nameof(ModelButtonText));
        if (!_initialized || value is null) return;
        _settings.ChatModelName = value.Name;
        IsModelPickerOpen = false;
    }

    /// <summary>模型选择列表是否展开。</summary>
    [ObservableProperty]
    public partial bool IsModelPickerOpen { get; set; }

    /// <summary>「添加模型」表单是否显示。</summary>
    [ObservableProperty]
    public partial bool IsAddingModel { get; set; }

    [ObservableProperty]
    public partial string NewModelName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewModelModelId { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewModelBaseUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewModelApiKey { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? AddModelError { get; set; }

    public bool HasAddModelError => !string.IsNullOrEmpty(AddModelError);

    partial void OnAddModelErrorChanged(string? value) => OnPropertyChanged(nameof(HasAddModelError));

    partial void OnNewModelNameChanged(string value) => AddModelError = null;

    [RelayCommand]
    private void ToggleModelPicker() => IsModelPickerOpen = !IsModelPickerOpen;

    [RelayCommand]
    private void OpenAddModel()
    {
        IsModelPickerOpen = false;
        NewModelName = string.Empty;
        NewModelModelId = string.Empty;
        NewModelBaseUrl = string.Empty;
        NewModelApiKey = string.Empty;
        AddModelError = null;
        IsAddingModel = true;
    }

    [RelayCommand]
    private void CancelAddModel() => IsAddingModel = false;

    [RelayCommand]
    private void ConfirmAddModel()
    {
        var name = NewModelName.Trim();
        if (name.Length == 0)
        {
            AddModelError = "请填写模型名称";
            return;
        }

        if (Models.Any(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            AddModelError = "已存在同名模型";
            return;
        }

        var model = new ChatModelConfig
        {
            Name = name,
            ModelId = NewModelModelId.Trim(),
            BaseUrl = NewModelBaseUrl.Trim(),
            ApiKey = NewModelApiKey.Trim(),
        };

        Models.Add(model);
        _settings.Save(); // 集合变化不会触发 PropertyChanged，这里直接落盘
        IsAddingModel = false;
        SelectedModel = model;
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private void Send()
    {
        var text = Draft.Trim();
        if (text.Length == 0) return;
        Messages.Add(new ChatMessageViewModel(text, isUser: true));
        Draft = string.Empty;
    }

    private bool CanSend() => !string.IsNullOrWhiteSpace(Draft);
}
