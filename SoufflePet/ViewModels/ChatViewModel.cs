// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.AI;
using Serilog;
using SoufflePet.Models;
using SoufflePet.Services;

namespace SoufflePet.ViewModels;

/// <summary>
/// 对话窗口的视图模型：消息列表 + 输入草稿 + 发送命令 + 模型选择。
/// 发送时调用 OpenAI 兼容接口流式取回回复，逐段追加到助手气泡。
/// </summary>
public partial class ChatViewModel : ViewModelBase
{
    private static readonly ILogger Logger = Log.ForContext<ChatViewModel>();

    /// <summary>助手气泡在收到第一段内容前的占位文案。</summary>
    private const string ThinkingText = "正在思考…";

    private readonly SouffleSettings _settings;
    private readonly ILlmChatService _llm;

    /// <summary>构造期间同步初始选中项时不触发持久化。</summary>
    private bool _initialized;

    /// <summary>当前流式请求的取消源；生成结束后置空。</summary>
    private CancellationTokenSource? _cts;

    public ChatViewModel(SouffleSettings settings, ILlmChatService llm)
    {
        _settings = settings;
        _llm = llm;
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

    /// <summary>是否正在等待/接收助手回复（生成期间发送按钮切换为停止）。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial bool IsGenerating { get; set; }

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
    private async Task SendAsync()
    {
        var text = Draft.Trim();
        if (text.Length == 0 || IsGenerating) return;

        if (SelectedModel is not { } model)
        {
            Messages.Add(new ChatMessageViewModel("请先选择模型（点击下方「模型 · 请选择模型」）", isUser: false));
            IsModelPickerOpen = true;
            return;
        }

        if (string.IsNullOrWhiteSpace(model.ModelId) || string.IsNullOrWhiteSpace(model.BaseUrl))
        {
            Messages.Add(new ChatMessageViewModel("当前模型缺少模型标识或接口地址，请重新添加该模型", isUser: false));
            return;
        }

        Messages.Add(new ChatMessageViewModel(text, isUser: true));
        Draft = string.Empty;

        // 占位气泡：收到第一段内容时整体替换，避免出现空气泡
        var reply = new ChatMessageViewModel(ThinkingText, isUser: false);
        Messages.Add(reply);

        // 历史只取占位气泡之前的消息（本次提问已在其中），不把占位文案发给接口
        var history = new List<ChatMessage>(Messages.Count);
        foreach (var message in Messages)
        {
            if (ReferenceEquals(message, reply)) break;
            history.Add(new ChatMessage(message.IsUser ? ChatRole.User : ChatRole.Assistant, message.Text));
        }

        using var cts = new CancellationTokenSource();
        _cts = cts;
        IsGenerating = true;

        try
        {
            var received = false;
            await foreach (var chunk in _llm.StreamReplyAsync(model, history, cts.Token))
            {
                reply.Text = received ? reply.Text + chunk : chunk;
                received = true;
            }

            if (!received) reply.Text = "（没有收到回复）";
            Logger.Debug("LLM reply finished model={Model} chars={Length}", model.ModelId, reply.Text.Length);
        }
        catch (OperationCanceledException)
        {
            if (reply.Text == ThinkingText) reply.Text = "（已停止）";
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "LLM request failed model={Model}", model.ModelId);
            var error = $"请求失败：{ex.Message}";
            reply.Text = reply.Text == ThinkingText ? error : $"{reply.Text}\n\n[{error}]";
        }
        finally
        {
            _cts = null;
            IsGenerating = false;
        }
    }

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    private bool CanSend() => !IsGenerating && !string.IsNullOrWhiteSpace(Draft);
}
