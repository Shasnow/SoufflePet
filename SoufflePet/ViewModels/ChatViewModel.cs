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
using Avalonia.Threading;
using Microsoft.Extensions.AI;
using Serilog;
using SoufflePet.Models;
using SoufflePet.Services;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

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

    /// <summary>工具记录里结果的最大展示长度（超出截断，避免撑爆气泡）。</summary>
    private const int ToolResultMaxLength = 300;

    private readonly SouffleSettings _settings;
    private readonly ILlmChatService _llm;
    private readonly ChatToolAccess _toolAccess;

    /// <summary>构造期间同步初始选中项时不触发持久化。</summary>
    private bool _initialized;

    /// <summary>当前流式请求的取消源；生成结束后置空。</summary>
    private CancellationTokenSource? _cts;

    /// <summary>本轮回复的占位气泡：审批气泡与工具记录都要插在它之前。</summary>
    private Models.ChatMessage? _reply;

    /// <summary>
    /// 对话历史：统一用 Microsoft.Extensions.AI.ChatMessage 记录，
    /// 只包含真实对话（不含工具调用记录、思考占位与错误提示）。
    /// </summary>
    private readonly List<ChatMessage> _history = [];

    public ChatViewModel(SouffleSettings settings, ILlmChatService llm, ChatToolAccess toolAccess)
    {
        _settings = settings;
        _llm = llm;
        _toolAccess = toolAccess;
        ApprovalMode = toolAccess.ApprovalMode;
        toolAccess.ApprovalRequested += OnApprovalRequested;

        // 首次启动列表为空且没有已保存的模型名，此时保持未选中状态。
        SelectedModel = settings.ChatModelName is { Length: > 0 } name
            ? Models.FirstOrDefault(m => m.Name == name)
            : null;
        _initialized = true;
    }

    /// <summary>消息列表（首次打开即包含一条助手问候）。</summary>
    public ObservableCollection<Models.ChatMessage> Messages { get; } =
    [
        new("今天想聊点什么呀？", isUser: false),
    ];

    /// <summary>可选的对话模型（与设置共享同一集合，添加后直接持久化）。</summary>
    public ObservableCollection<ChatModelConfig> Models => _settings.ChatModels;

    /// <summary>输入框内容（与 TextBox 双向绑定）。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial string Draft { get; set; } = string.Empty;

    /// <summary>是否正在等待/接收助手回复（生成期间发送按钮切换为停止）。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial bool IsGenerating { get; set; }

    /// <summary>当前选中的模型；改变时写回设置（由 App 组合根统一持久化）。</summary>
    [ObservableProperty]
    public partial ChatModelConfig? SelectedModel { get; set; }

    /// <summary>模型按钮上显示的文案。</summary>
    public string ModelButtonText => SelectedModel is null? "请选择模型 ▾" : $"{SelectedModel.Provider} · {SelectedModel.Name} ▾";

    partial void OnSelectedModelChanged(ChatModelConfig? value)
    {
        OnPropertyChanged(nameof(ModelButtonText));
        if (!_initialized || value is null) return;
        _settings.ChatModelName = value.Name;
        IsModelPickerOpen = false;
    }

    /// <summary>命令执行审批规则（本次运行有效，不持久化）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ApprovalButtonText))]
    [NotifyPropertyChangedFor(nameof(IsManualApproval))]
    [NotifyPropertyChangedFor(nameof(IsFullAccess))]
    public partial ToolApprovalMode ApprovalMode { get; set; } = ToolApprovalMode.Manual;

    partial void OnApprovalModeChanged(ToolApprovalMode value) => _toolAccess.ApprovalMode = value;

    /// <summary>审批按钮上显示的当前规则。</summary>
    public string ApprovalButtonText =>
        ApprovalMode == ToolApprovalMode.Manual ? "手动审批" : "完全访问";

    public bool IsManualApproval => ApprovalMode == ToolApprovalMode.Manual;

    public bool IsFullAccess => ApprovalMode == ToolApprovalMode.FullAccess;

    /// <summary>审批规则选择浮层是否展开。</summary>
    [ObservableProperty]
    public partial bool IsApprovalPickerOpen { get; set; }

    [RelayCommand]
    private void ToggleApprovalPicker() => IsApprovalPickerOpen = !IsApprovalPickerOpen;

    [RelayCommand]
    private void UseManualApproval() => SelectApprovalMode(ToolApprovalMode.Manual);

    [RelayCommand]
    private void UseFullAccess() => SelectApprovalMode(ToolApprovalMode.FullAccess);

    private void SelectApprovalMode(ToolApprovalMode mode)
    {
        ApprovalMode = mode;
        IsApprovalPickerOpen = false;
    }

    /// <summary>工具执行线程发起审批请求：回到 UI 线程插入带按钮的气泡。</summary>
    private void OnApprovalRequested(ToolApprovalRequest request)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var message = new Models.ChatMessage(
                $"模型请求执行命令，等待你的确认：\n{request.Arguments}",
                isUser: false,
                isTool: true,
                approval: request);

            // 和工具记录一样插在回复气泡之前，保持「确认 → 执行结果 → 回复」的顺序
            var index = _reply is { } reply ? Messages.IndexOf(reply) : -1;
            if (index >= 0) Messages.Insert(index, message);
            else Messages.Add(message);
        });
    }

    /// <summary>模型选择列表是否展开。</summary>
    [ObservableProperty]
    public partial bool IsModelPickerOpen { get; set; }

    /// <summary>「添加模型」表单是否显示。</summary>
    [ObservableProperty]
    public partial bool IsAddingModel { get; set; }

    [ObservableProperty]
    public partial string NewModelProvider { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewModelName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewModelModelId { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewModelBaseUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewModelApiKey { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAddModelError))]
    public partial string? AddModelError { get; set; }

    public bool HasAddModelError => !string.IsNullOrEmpty(AddModelError);

    partial void OnNewModelNameChanged(string value) => AddModelError = null;

    [RelayCommand]
    private void ToggleModelPicker() => IsModelPickerOpen = !IsModelPickerOpen;

    [RelayCommand]
    private void OpenAddModel()
    {
        IsModelPickerOpen = false;
        NewModelProvider = string.Empty;
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
            Provider = NewModelProvider.Trim(),
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
            Messages.Add(new Models.ChatMessage("请先选择模型（点击下方「模型 · 请选择模型」）", isUser: false));
            IsModelPickerOpen = true;
            return;
        }

        if (string.IsNullOrWhiteSpace(model.ModelId) || string.IsNullOrWhiteSpace(model.BaseUrl))
        {
            Messages.Add(new Models.ChatMessage("当前模型缺少模型标识或接口地址，请重新添加该模型", isUser: false));
            return;
        }

        Messages.Add(new Models.ChatMessage(text, isUser: true));
        Draft = string.Empty;

        // 用户这轮提问先落历史；工具记录、占位与错误提示都不进入历史
        _history.Add(new ChatMessage(ChatRole.User, text));

        // 占位气泡：收到第一段内容时整体替换，避免出现空气泡
        var reply = new Models.ChatMessage(ThinkingText, isUser: false);
        _reply = reply;
        Messages.Add(reply);

        using var cts = new CancellationTokenSource();
        _cts = cts;
        IsGenerating = true;

        var answer = string.Empty;
        try
        {
            await foreach (var evt in _llm.StreamAsync(model, [.. _history], cts.Token))
            {
                switch (evt)
                {
                    case LlmTextDelta delta:
                        answer += delta.Text;
                        reply.Text = answer;
                        break;

                    // 工具记录插在占位气泡之前，保证「工具调用 → 最终回复」的阅读顺序
                    case LlmToolCallEvent call:
                        var record = new Models.ChatMessage(FormatToolCall(call), isUser: false, isTool: true);
                        Messages.Insert(Messages.IndexOf(reply), record);
                        break;
                }
            }

            if (answer.Length == 0) reply.Text = "（没有收到回复）";
            Logger.Debug("LLM reply finished model={Model} chars={Length}", model.ModelId, answer.Length);
        }
        catch (OperationCanceledException)
        {
            if (answer.Length == 0) reply.Text = "（已停止）";
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "LLM request failed model={Model}", model.ModelId);
            var error = $"请求失败：{ex.Message}";
            reply.Text = answer.Length == 0 ? error : $"{answer}\n\n[{error}]";
        }
        finally
        {
            // 只有真正生成出的内容才写回历史；停止时保留已收到的部分
            if (answer.Length > 0) _history.Add(new ChatMessage(ChatRole.Assistant, answer));
            _cts = null;
            _reply = null;
            IsGenerating = false;
        }
    }

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    /// <summary>工具调用记录的展示文本：工具名（参数）换行跟结果。</summary>
    private static string FormatToolCall(LlmToolCallEvent call)
    {
        var arguments = call.Arguments.Length == 0 ? string.Empty : $" {call.Arguments}";
        var result = call.Result.Length <= ToolResultMaxLength
            ? call.Result
            : call.Result[..ToolResultMaxLength] + "…";
        return $"调用工具 {call.Name}{arguments}\n结果：{result}";
    }

    private bool CanSend() => !IsGenerating && !string.IsNullOrWhiteSpace(Draft);
}
