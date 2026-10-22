// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Text.Json;
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

    /// <summary>发给模型的对话窗口：保留最近多少轮用户提问（含其后的回复与工具调用记录）。</summary>
    private const int HistoryWindowTurns = 10;

    private readonly SouffleSettings _settings;
    private readonly ILlmChatService _llm;
    private readonly ChatToolAccess _toolAccess;
    private readonly ChatMemory _memory;
    private readonly IMcpSettingsWindowService _mcpSettings;
    private readonly ISkillsWindowService _skillsWindow;

    /// <summary>构造期间同步初始选中项时不触发持久化。</summary>
    private bool _initialized;

    /// <summary>当前流式请求的取消源；生成结束后置空。</summary>
    private CancellationTokenSource? _cts;

    /// <summary>本轮回复的占位气泡：审批气泡与工具记录都要插在它之前。</summary>
    private Models.ChatMessage? _reply;

    /// <summary>
    /// 对话历史：统一用 Microsoft.Extensions.AI.ChatMessage 记录，只包含真实对话与工具调用记录。
    /// 工具记录按标准结构保存（助手消息携带 FunctionCallContent，紧跟一条 ChatRole.Tool 的结果消息），
    /// 启动时从长期记忆恢复，下次请求拿到的上下文与本次一致。
    /// </summary>
    private readonly List<ChatMessage> _history = [];

    public ChatViewModel(
        SouffleSettings settings,
        ILlmChatService llm,
        ChatToolAccess toolAccess,
        ChatMemory memory,
        IMcpSettingsWindowService mcpSettings,
        ISkillsWindowService skillsWindow)
    {
        _settings = settings;
        _llm = llm;
        _toolAccess = toolAccess;
        _memory = memory;
        _mcpSettings = mcpSettings;
        _skillsWindow = skillsWindow;
        ApprovalMode = toolAccess.ApprovalMode;
        toolAccess.ApprovalRequested += OnApprovalRequested;

        // 首次启动列表为空且没有已保存的模型名，此时保持未选中状态。
        SelectedModel = settings.ChatModelName is { Length: > 0 } name
            ? Models.FirstOrDefault(m => m.Name == name)
            : null;
        _initialized = true;

        LoadRecentHistory();
    }

    /// <summary>消息列表（首次打开即包含一条助手问候）。</summary>
    public ObservableCollection<Models.ChatMessage> Messages { get; } =
    [
        new("今天想聊点什么呀？", isUser: false)
    ];

    /// <summary>可选的对话模型（与设置共享同一集合，添加后直接持久化）。</summary>
    public ObservableCollection<ChatModelConfig> Models => _settings.ChatModels;

    /// <summary>宠物称呼（设置项）。</summary>
    public string PetName => _settings.PetName;

    /// <summary>标题栏头像上的首字，称呼为空时回退默认称呼。</summary>
    public string PetInitial =>
        (PetName.Length > 0 ? PetName : SouffleSettings.DefaultPetName)[..1];

    /// <summary>输入框占位提示。</summary>
    public string InputPlaceholder => $"和{PetName}说点什么…";

    /// <summary>窗口标题。</summary>
    public string WindowTitle => $"{PetName} · 对话";

    /// <summary>输入区下方的常驻统计：有史以来的 token 总量与缓存命中率。</summary>
    public string UsageSummaryText
    {
        get
        {
            var totals = _settings.ChatUsage;
            var total = totals.InputTokens + totals.OutputTokens;
            var cacheRate = totals.InputTokens > 0
                ? $"{(double)totals.CachedInputTokens / totals.InputTokens:P0}"
                : "—";
            return $"累计 {total:N0} tokens · 缓存命中 {cacheRate}";
        }
    }

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
    public string ModelButtonText =>
        SelectedModel is null ? "请选择模型" : $"{SelectedModel.Provider} · {SelectedModel.Name}";

    partial void OnSelectedModelChanged(ChatModelConfig? value)
    {
        OnPropertyChanged(nameof(ModelButtonText));
        if (!_initialized || value is null) return;
        _settings.ChatModelName = value.Name;
        IsModelPickerOpen = false;
    }

    /// <summary>命令执行审批规则（修改后写入设置文件）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ApprovalButtonText))]
    [NotifyPropertyChangedFor(nameof(IsManualApproval))]
    [NotifyPropertyChangedFor(nameof(IsFullAccess))]
    private partial ToolApprovalMode ApprovalMode { get; set; }

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

    [ObservableProperty] public partial string NewModelProvider { get; set; } = string.Empty;

    [ObservableProperty] public partial string NewModelName { get; set; } = string.Empty;

    [ObservableProperty] public partial string NewModelModelId { get; set; } = string.Empty;

    [ObservableProperty] public partial string NewModelBaseUrl { get; set; } = string.Empty;

    [ObservableProperty] public partial string NewModelApiKey { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAddModelError))]
    public partial string? AddModelError { get; set; }

    public bool HasAddModelError => !string.IsNullOrEmpty(AddModelError);

    partial void OnNewModelNameChanged(string value) => AddModelError = null;

    [RelayCommand]
    private void ToggleModelPicker() => IsModelPickerOpen = !IsModelPickerOpen;

    /// <summary>「更多操作」浮层是否展开。</summary>
    [ObservableProperty]
    public partial bool IsMoreOpen { get; set; }

    [RelayCommand]
    private void ToggleMore() => IsMoreOpen = !IsMoreOpen;

    /// <summary>打开 MCP 服务器设置窗口（保存后下一次对话按新配置重连）。</summary>
    [RelayCommand]
    private void OpenMcpSettings()
    {
        IsMoreOpen = false;
        _mcpSettings.Show();
    }

    /// <summary>打开技能管理窗口（每次打开前重新扫描技能目录）。</summary>
    [RelayCommand]
    private void OpenSkills()
    {
        IsMoreOpen = false;
        _skillsWindow.Show();
    }

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
            ApiKey = NewModelApiKey.Trim()
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

        // 用户这轮提问先落历史；占位与错误提示不进入历史
        _history.Add(new ChatMessage(ChatRole.User, text));
        _memory.AddTurn("user", text);
        TrimHistory();

        // 占位气泡：收到第一段内容时整体替换，避免出现空气泡
        var reply = new Models.ChatMessage(ThinkingText, isUser: false);
        _reply = reply;
        Messages.Add(reply);

        using var cts = new CancellationTokenSource();
        _cts = cts;
        IsGenerating = true;

        var segment = string.Empty;  // 当前气泡里的文本；工具调用时截断重开
        var roundReasoning = new StringBuilder(); // 当前轮的思考内容，随本轮最后一条助手消息落库
        Models.ChatMessage? thinking = null; // 当前轮次的思考气泡；工具调用后另起一个
        Models.ChatMessage? answered = null; // 本轮最后一段正文所在的气泡，用量挂在它下方
        LlmUsageEvent? usage = null;         // 本轮 token 用量（流结束时产出）
        var toolBubbles = new Dictionary<string, Models.ChatMessage>(StringComparer.Ordinal); // CallId → 进行中的工具气泡

        // 思考阶段结束（开始输出正文或开始调用工具）时自动折叠思考气泡
        void CollapseThinking()
        {
            if (thinking is not { IsReasoningDone: false } bubble) return;
            bubble.IsReasoningDone = true;
            bubble.IsExpanded = false;
        }

        // 把当前气泡里的正文作为独立的一轮落库：工具调用会截断气泡，
        // 只有在这里立刻保存，「回答 → 工具调用 → 回答」的先后顺序才能完整保留。
        // withReasoning 仅在本轮没有工具调用（即最终回复）时为 true，思考内容随这条消息一起保存。
        void CommitSegment(bool withReasoning)
        {
            if (segment.Length == 0) return;

            List<AIContent> contents = [];
            string? reasoning = null;
            if (withReasoning && roundReasoning.Length > 0)
            {
                reasoning = roundReasoning.ToString();
                contents.Add(new TextReasoningContent(reasoning));
                roundReasoning.Clear();
            }

            contents.Add(new TextContent(segment));
            _history.Add(new ChatMessage(ChatRole.Assistant, contents));
            _memory.AddTurn(ChatMemory.AssistantRole, segment, reasoning: reasoning);
            TrimHistory();
            segment = string.Empty;
        }

        // 插入一条工具记录：没有正文时直接插在回复气泡前；已有正文则截断并为后续回复新开气泡
        void AddToolBubble(Models.ChatMessage message)
        {
            CollapseThinking();
            thinking = null;
            var index = Messages.IndexOf(reply);
            if (segment.Length == 0)
            {
                if (index >= 0) Messages.Insert(index, message);
                else Messages.Add(message);
                return;
            }

            CommitSegment(withReasoning: false);
            Messages.Insert(index + 1, message);
            reply = new Models.ChatMessage(ThinkingText, isUser: false);
            _reply = reply;
            Messages.Insert(index + 2, reply);
        }

        try
        {
            await foreach (var evt in _llm.StreamAsync(model, [.. _history], cts.Token))
            {
                switch (evt)
                {
                    case LlmReasoningDelta reasoning:
                        roundReasoning.Append(reasoning.Text);
                        if (thinking is null)
                        {
                            thinking = new Models.ChatMessage(string.Empty, isUser: false, isReasoning: true);
                            Messages.Insert(Messages.IndexOf(reply), thinking);
                        }

                        thinking.Text += reasoning.Text;
                        break;

                    case LlmTextDelta delta:
                        // 正文开始输出，思考阶段结束：思考气泡自动收起
                        CollapseThinking();
                        segment += delta.Text;
                        reply.Text = segment;
                        answered = reply;
                        break;

                    case LlmUsageEvent roundUsage:
                        usage = roundUsage;
                        break;

                    case LlmToolCallStartedEvent started:
                        // 工具一开始执行就先显示「进行中」，长耗时工具也有反馈；面板默认收起
                        var bubble = new Models.ChatMessage(FormatToolStarted(started), isUser: false, isTool: true,
                            isExpanded: false)
                        {
                            ToolName = started.Name,
                            ToolArguments = started.Arguments,
                            ToolResult = "执行中…"
                        };
                        toolBubbles[started.CallId] = bubble;
                        AddToolBubble(bubble);
                        break;

                    case LlmToolCallEvent call:
                        // 气泡正文用截断文本避免撑爆，展开面板里放参数原文与完整结果
                        var display = FormatToolCall(call);

                        // 结果回来后就地更新同一条气泡；找不到时（例如重连后）补一条完整记录
                        if (!toolBubbles.Remove(call.CallId, out var completed))
                        {
                            completed = new Models.ChatMessage(display, isUser: false, isTool: true, isExpanded: false)
                            {
                                ToolName = call.Name,
                                ToolArguments = call.Arguments,
                                ToolResult = call.Result
                            };
                            AddToolBubble(completed);
                        }
                        else
                        {
                            completed.Text = display;
                            completed.ToolArguments = call.Arguments;
                            completed.ToolResult = call.Result;
                        }

                        // 工具调用记录既写入长期记忆（重启后恢复），也按标准结构进入请求历史；
                        // 本轮思考内容随第一条工具记录保存，推理模型在工具循环里要求回传 reasoning_content
                        var toolReasoning = roundReasoning.Length > 0 ? roundReasoning.ToString() : null;
                        roundReasoning.Clear();
                        _memory.AddTurn(ChatMemory.ToolRole, display, call.CallId, call.Name, call.Arguments, call.Result,
                            toolReasoning);
                        AddToolHistory(call.CallId, call.Name, call.Arguments, call.Result, display, toolReasoning);
                        TrimHistory();
                        break;
                }
            }

            if (segment.Length == 0)
            {
                if (answered is null) reply.Text = "（没有收到回复）";
                else Messages.Remove(reply); // 最后一轮只有工具调用，没有正文可显示
            }
        }
        catch (OperationCanceledException)
        {
            if (segment.Length == 0) reply.Text = "（已停止）";
        }
        catch (Exception ex)
        {
            var error = $"请求失败：{ex.Message}";
            reply.Text = segment.Length == 0 ? error : $"{segment}\n\n[{error}]";
        }
        finally
        {
            // 思考阶段结束（包括中途停止），思考气泡收起
            CollapseThinking();

            // 停止或出错时仍在「执行中」的工具记录标记为中断，避免看起来一直没结束
            foreach (var pending in toolBubbles.Values)
            {
                pending.Text += "\n（已中断）";
                pending.ToolResult = pending.ToolResult.Length > 0
                    ? $"{pending.ToolResult}\n（已中断）"
                    : "（已中断）";
            }

            // 只有真正生成出的内容才写回历史；停止时保留已收到的最后一段
            CommitSegment(withReasoning: true);

            // 用量统计：气泡底部显示本轮明细，同时累加进设置里的历史总额
            if (usage is { } roundUsage) RecordUsage(roundUsage, answered);

            _cts = null;
            _reply = null;
            IsGenerating = false;
        }
    }

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    /// <summary>记录一轮用量：气泡底部显示明细，累计值写回设置并落盘。</summary>
    private void RecordUsage(LlmUsageEvent usage, Models.ChatMessage? bubble)
    {
        if (bubble is not null) bubble.UsageText = FormatUsage(usage);

        var totals = _settings.ChatUsage;
        totals.InputTokens += usage.InputTokens;
        totals.OutputTokens += usage.OutputTokens;
        totals.CachedInputTokens += usage.CachedInputTokens;
        _settings.Save();
        OnPropertyChanged(nameof(UsageSummaryText));
    }

    /// <summary>本轮用量文案：输入/输出 token，有缓存命中时补上命中数量。</summary>
    private static string FormatUsage(LlmUsageEvent usage)
    {
        var text = $"输入 {usage.InputTokens:N0} · 输出 {usage.OutputTokens:N0} tokens";
        return usage.CachedInputTokens > 0
            ? $"{text} · 缓存命中 {usage.CachedInputTokens:N0}"
            : text;
    }

    /// <summary>工具开始执行时的展示文本：先给出反馈，结果回来后原地替换成完整记录。</summary>
    private static string FormatToolStarted(LlmToolCallStartedEvent call)
    {
        var arguments = call.Arguments.Length == 0 ? string.Empty : $" {call.Arguments}";
        return $"调用工具 {call.Name}{arguments}\n执行中…";
    }

    /// <summary>工具调用记录的展示文本：工具名（参数）换行跟结果，结果超出展示上限即截断。</summary>
    private static string FormatToolCall(LlmToolCallEvent call)
    {
        var arguments = call.Arguments.Length == 0 ? string.Empty : $" {call.Arguments}";
        var result = call.Result.Length > ToolResultMaxLength
            ? call.Result[..ToolResultMaxLength] + "…"
            : call.Result;
        return $"调用工具 {call.Name}{arguments}\n结果：{result}";
    }

    /// <summary>
    /// 按标准结构把一次工具调用写进请求历史：助手消息携带 FunctionCallContent，紧跟 ChatRole.Tool 的结果消息。
    /// 旧记录缺少 CallId / 工具名时无法配对，只能退化成助手文本，避免出现没有对应调用的 tool 消息。
    /// </summary>
    private void AddToolHistory(
        string? callId,
        string? name,
        string? arguments,
        string? result,
        string fallbackText,
        string? reasoning = null)
    {
        if (string.IsNullOrEmpty(callId) || string.IsNullOrEmpty(name) || result is null)
        {
            _history.Add(new ChatMessage(ChatRole.Assistant, fallbackText));
            return;
        }

        List<AIContent> contents = [];
        if (!string.IsNullOrEmpty(reasoning)) contents.Add(new TextReasoningContent(reasoning));
        contents.Add(new FunctionCallContent(callId, name, ParseArguments(arguments)));
        _history.Add(new ChatMessage(ChatRole.Assistant, contents));
        _history.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(callId, result)]));
    }

    /// <summary>把落盘的参数 JSON 还原成工具调用参数；为空或解析失败时视为无参数。</summary>
    private static Dictionary<string, object?>? ParseArguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>恢复历史时截断工具记录的结果部分：落盘的是完整文本，界面气泡不必全量显示。</summary>
    private static string TruncateToolText(string text)
    {
        const string marker = "\n结果：";
        var index = text.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0) return text;

        var head = text[..(index + marker.Length)];
        var result = text[(index + marker.Length)..];
        return result.Length <= ToolResultMaxLength ? text : head + result[..ToolResultMaxLength] + "…";
    }

    /// <summary>启动时把最近几轮对话（含工具调用记录）恢复到界面与请求历史里（有历史时替换掉问候语）。</summary>
    private void LoadRecentHistory()
    {
        var recent = _memory.RecentTurns(HistoryWindowTurns, includeTools: true);
        if (recent.Count == 0) return;

        Messages.Clear();

        // 思考气泡：与生成时一致，排在本条正文（或工具记录）气泡之前
        void AddReasoningBubble(string? reasoning)
        {
            if (string.IsNullOrEmpty(reasoning)) return;
            Messages.Add(new Models.ChatMessage(reasoning, isUser: false, isReasoning: true, isExpanded: false));
        }

        foreach (var turn in recent)
        {
            if (string.Equals(turn.Role, ChatMemory.ToolRole, StringComparison.OrdinalIgnoreCase))
            {
                // 工具记录按标准结构重建进请求历史，界面上按工具气泡展示（展示时截断）
                AddToolHistory(turn.CallId, turn.ToolName, turn.Arguments, turn.Result, turn.Text, turn.Reasoning);
                AddReasoningBubble(turn.Reasoning);
                Messages.Add(new Models.ChatMessage(TruncateToolText(turn.Text), isUser: false, isTool: true, isExpanded: false)
                {
                    ToolName = turn.ToolName ?? string.Empty,
                    ToolArguments = turn.Arguments ?? string.Empty,
                    ToolResult = turn.Result ?? string.Empty
                });
                continue;
            }

            var isUser = string.Equals(turn.Role, ChatMemory.UserRole, StringComparison.OrdinalIgnoreCase);
            if (!isUser && !string.IsNullOrEmpty(turn.Reasoning))
            {
                _history.Add(new ChatMessage(ChatRole.Assistant,
                    [new TextReasoningContent(turn.Reasoning), new TextContent(turn.Text)]));
                AddReasoningBubble(turn.Reasoning);
            }
            else
            {
                _history.Add(new ChatMessage(isUser ? ChatRole.User : ChatRole.Assistant, turn.Text));
            }

            Messages.Add(new Models.ChatMessage(turn.Text, isUser));
        }
    }

    /// <summary>
    /// 把发给模型的历史裁到最近 <see cref="HistoryWindowTurns"/> 轮。以用户提问作为轮的边界，
    /// 每轮包含该提问及其后的助手回复、全部工具调用记录；窗口内不会再出现半截工具链。
    /// </summary>
    private void TrimHistory()
    {
        var before = _history.Count;

        // 从后往前数到第 N 个用户发言，窗口就从这里开始；不足 N 轮时从头保留
        var keepFrom = 0;
        var userTurns = 0;
        for (var i = _history.Count - 1; i >= 0; i--)
        {
            if (_history[i].Role != ChatRole.User) continue;
            if (++userTurns == HistoryWindowTurns)
            {
                keepFrom = i;
                break;
            }
        }

        if (keepFrom > 0) _history.RemoveRange(0, keepFrom);

        if (before != _history.Count)
            Logger.Debug("history trimmed before={Before} after={After} turns={Turns}", before, _history.Count, userTurns);
    }

    private bool CanSend() => !IsGenerating && !string.IsNullOrWhiteSpace(Draft);
}