// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
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

    /// <summary>工具执行期间占位气泡的提示文本。</summary>
    private const string ToolCallingText = "正在调用工具…";

    /// <summary>模型只输出空白字符时，气泡里显示的提示文本。</summary>
    private const string BlankText = "（空白内容）";

    /// <summary>工具记录里结果的最大展示长度（超出截断，避免撑爆气泡）。</summary>
    private const int ToolResultMaxLength = 300;

    /// <summary>发给模型的对话窗口：保留最近多少轮用户提问（含其后的回复与工具调用记录）。</summary>
    private const int HistoryWindowTurns = 10;

    /// <summary>平台提醒合成消息的前缀：恢复历史时据此还原提醒气泡，而不是当作普通用户发言。</summary>
    private const string PlatformNoticePrefix = "[平台提醒]";

    /// <summary>生成中最多排队的一轮待执行对话数，超出时与最旧一条合并。</summary>
    private const int MaxPendingTurns = 3;

    /// <summary>平台提醒气泡里展示的正文上限（完整内容仍交给模型）。</summary>
    private const int PlatformNoticePreviewLength = 80;

    private readonly SouffleSettings _settings;
    private readonly ILlmChatService _llm;
    private readonly ChatToolAccess _toolAccess;
    private readonly ChatMemory _memory;
    private readonly IMcpSettingsWindowService _mcpSettings;
    private readonly IMcpToolService _mcpTools;
    private readonly IMcpConfigStore _mcpConfig;
    private readonly ISkillsWindowService _skillsWindow;
    private readonly IPluginsWindowService _pluginsWindow;
    private readonly IScheduledTasksWindowService _scheduledTasksWindow;

    /// <summary>构造期间同步初始选中项时不触发持久化。</summary>
    private bool _initialized;

    /// <summary>当前流式请求的取消源；生成结束后置空。</summary>
    private CancellationTokenSource? _cts;

    /// <summary>本轮回复的占位气泡：审批气泡与工具记录都要插在它之前。</summary>
    private Models.ChatMessage? _reply;

    /// <summary>生成中收到的一轮待执行对话（平台提醒 / 定时任务），等本轮结束后逐条处理。</summary>
    private readonly Queue<PendingTurn> _pendingTurns = new();

    /// <summary>是否正在处理待执行对话，避免重入导致并发发起多轮对话。</summary>
    private bool _handlingPendingTurn;

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
        IMcpToolService mcpTools,
        IMcpConfigStore mcpConfig,
        ISkillsWindowService skillsWindow,
        IPluginsWindowService pluginsWindow,
        IScheduledTasksWindowService scheduledTasksWindow,
        PlatformService platforms,
        IScheduledTaskService scheduledTasks,
        UserQuestionService questions,
        ChatFileService files)
    {
        _settings = settings;
        _llm = llm;
        _toolAccess = toolAccess;
        _memory = memory;
        _mcpSettings = mcpSettings;
        _mcpTools = mcpTools;
        _mcpConfig = mcpConfig;
        _skillsWindow = skillsWindow;
        _pluginsWindow = pluginsWindow;
        _scheduledTasksWindow = scheduledTasksWindow;
        ApprovalMode = toolAccess.ApprovalMode;
        toolAccess.ApprovalRequested += OnApprovalRequested;
        questions.QuestionAsked += OnQuestionAsked;
        files.FileShown += OnFileShown;
        platforms.Notified += NotifyPlatformMessage;
        scheduledTasks.Triggered += OnScheduledTaskTriggered;

        // 附件区的显隐与发送按钮的可用性都取决于附件数量
        Attachments.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasAttachments));
            SendCommand.NotifyCanExecuteChanged();
        };

        // 首次启动列表为空且没有已保存的模型名，此时保持未选中状态。
        SelectedModel = settings.ChatModelName is { Length: > 0 } name
            ? Models.FirstOrDefault(m => m.Name == name)
            : null;
        _initialized = true;

        InitializeWorkspaces();
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

    /// <summary>待随下一条消息发送的附件：只记绝对路径，发送后清空。</summary>
    public ObservableCollection<ChatAttachment> Attachments { get; } = [];

    /// <summary>附件区提示：只用于添加失败等反馈，没有提示时不显示。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAttachmentHint))]
    public partial string? AttachmentHint { get; set; }

    public bool HasAttachmentHint => !string.IsNullOrEmpty(AttachmentHint);

    public bool HasAttachments => Attachments.Count > 0;

    /// <summary>
    /// 添加附件：统一只记录本地绝对路径（不限类型、不读取内容），
    /// 发送时以「@路径」写进消息，模型之后可随时用命令或工具读取。
    /// </summary>
    public void AddAttachments(IEnumerable<string> paths)
    {
        var failed = new List<string>();

        foreach (var path in paths)
        {
            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                failed.Add(Path.GetFileName(path));
                continue;
            }

            var info = new FileInfo(full);
            if (!info.Exists)
            {
                failed.Add(info.Name);
                continue;
            }

            // 同一个文件重复选择时只保留一份
            if (Attachments.Any(a => string.Equals(a.FullPath, full, StringComparison.OrdinalIgnoreCase))) continue;

            Attachments.Add(new ChatAttachment(info.Name, full));
        }

        AttachmentHint = failed.Count == 0 ? null : $"未能添加：{string.Join("、", failed)}";
    }

    /// <summary>移除一个待发送附件。</summary>
    public void RemoveAttachment(ChatAttachment attachment)
    {
        Attachments.Remove(attachment);
        AttachmentHint = null;
    }

    /// <summary>退格键删除最后一个附件：附件按整块移除，不会只删掉半个名字。</summary>
    public void RemoveLastAttachment()
    {
        if (Attachments.Count == 0) return;
        Attachments.RemoveAt(Attachments.Count - 1);
        AttachmentHint = null;
    }

    /// <summary>历史工作区列表：条目由设置里的路径还原，增删时同步写回设置。</summary>
    public ObservableCollection<WorkspaceItem> Workspaces { get; } = [];

    public bool HasWorkspaces => Workspaces.Count > 0;

    /// <summary>当前激活的工作区；null 表示使用应用所在目录。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WorkspaceButtonText))]
    [NotifyPropertyChangedFor(nameof(WorkspaceToolTip))]
    public partial WorkspaceItem? ActiveWorkspace { get; set; }

    /// <summary>工作区按钮文案：当前激活工作区的完整名称，没有激活项时提示选择。</summary>
    public string WorkspaceButtonText => ActiveWorkspace?.Name ?? "选择工作区";

    /// <summary>工作区按钮的悬浮提示：带上完整路径，便于区分同名目录。</summary>
    public string WorkspaceToolTip => ActiveWorkspace is { } workspace
        ? $"切换工作区\n{workspace.FullPath}"
        : "选择工作区（工具相对路径、命令执行与 AGENTS.md 以此目录为准）";

    /// <summary>工作区选择浮层是否展开。</summary>
    [ObservableProperty]
    public partial bool IsWorkspacePickerOpen { get; set; }

    /// <summary>等待二次确认删除的工作区；null 表示没有待确认的删除。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WorkspaceDeletePrompt))]
    public partial WorkspaceItem? PendingDeleteWorkspace { get; set; }

    /// <summary>删除二次确认是否显示。</summary>
    [ObservableProperty]
    public partial bool IsConfirmingWorkspaceDelete { get; set; }

    public string WorkspaceDeletePrompt => PendingDeleteWorkspace is { } workspace
        ? $"「{workspace.Name}」将从工作区历史中移除。\n{workspace.FullPath}"
        : string.Empty;

    /// <summary>浮层被点击外部关闭时，一并取消未完成的删除确认。</summary>
    partial void OnIsWorkspacePickerOpenChanged(bool value)
    {
        if (value) return;
        IsConfirmingWorkspaceDelete = false;
        PendingDeleteWorkspace = null;
    }

    [RelayCommand]
    private void ToggleWorkspacePicker() => IsWorkspacePickerOpen = !IsWorkspacePickerOpen;

    /// <summary>切换到某个历史工作区：更新高亮与设置，并关闭浮层；切换失败时保持原状态。</summary>
    public void SwitchWorkspace(WorkspaceItem workspace)
    {
        if (!SetActiveWorkspace(workspace)) return;
        IsWorkspacePickerOpen = false;
    }

    /// <summary>选择目录后加入历史并立即切换过去。</summary>
    public void UseWorkspace(string path)
    {
        if (NormalizePath(path) is not { } full) return;

        var workspace = Workspaces.FirstOrDefault(w =>
            string.Equals(w.FullPath, full, StringComparison.OrdinalIgnoreCase));

        if (workspace is null)
        {
            workspace = new WorkspaceItem(full);
            Workspaces.Add(workspace);
            OnPropertyChanged(nameof(HasWorkspaces));
            SaveWorkspaces();
        }

        SwitchWorkspace(workspace);
    }

    /// <summary>点击删除：先弹出二次确认，确认后才真正移除。</summary>
    public void RequestDeleteWorkspace(WorkspaceItem workspace)
    {
        PendingDeleteWorkspace = workspace;
        IsConfirmingWorkspaceDelete = true;
    }

    [RelayCommand]
    private void CancelDeleteWorkspace()
    {
        IsConfirmingWorkspaceDelete = false;
        PendingDeleteWorkspace = null;
    }

    [RelayCommand]
    private void ConfirmDeleteWorkspace()
    {
        if (PendingDeleteWorkspace is not { } workspace) return;

        var wasActive = ReferenceEquals(workspace, ActiveWorkspace);
        Workspaces.Remove(workspace);
        OnPropertyChanged(nameof(HasWorkspaces));
        CancelDeleteWorkspace();
        SaveWorkspaces();

        // 删掉当前激活项：回落到应用目录，按钮文案同步更新
        if (wasActive) SetActiveWorkspace(null);
    }

    /// <summary>Esc：先取消删除确认，再关闭浮层；都没有时返回 false 交给窗口处理。</summary>
    public bool CloseWorkspaceOverlay()
    {
        if (IsConfirmingWorkspaceDelete)
        {
            CancelDeleteWorkspace();
            return true;
        }

        if (!IsWorkspacePickerOpen) return false;
        IsWorkspacePickerOpen = false;
        return true;
    }

    /// <summary>还原历史工作区与激活项，并把进程工作目录切到激活工作区（没有时用应用目录）。</summary>
    private void InitializeWorkspaces()
    {
        foreach (var path in _settings.WorkspacePaths)
        {
            if (NormalizePath(path) is not { } full) continue;
            if (Workspaces.Any(w => string.Equals(w.FullPath, full, StringComparison.OrdinalIgnoreCase))) continue;
            Workspaces.Add(new WorkspaceItem(full));
        }

        var active = Workspaces.FirstOrDefault(w =>
            string.Equals(w.FullPath, _settings.ActiveWorkspacePath, StringComparison.OrdinalIgnoreCase));

        // 还原失败（目录已失效或无法进入）：清掉激活路径并回落到应用目录，避免每次启动都撞同一个错
        if (active is not null && !ApplyWorkingDirectory(active.FullPath))
        {
            _settings.ActiveWorkspacePath = string.Empty;
            active = null;
        }

        if (active is null) ApplyWorkingDirectory(null);

        foreach (var workspace in Workspaces) workspace.IsActive = ReferenceEquals(workspace, active);
        ActiveWorkspace = active;
    }

    /// <summary>设置激活工作区；进程工作目录切换失败时保持原状态并返回 false。</summary>
    private bool SetActiveWorkspace(WorkspaceItem? workspace)
    {
        if (!ApplyWorkingDirectory(workspace?.FullPath)) return false;

        ActiveWorkspace = workspace;
        foreach (var item in Workspaces) item.IsActive = ReferenceEquals(item, workspace);
        _settings.ActiveWorkspacePath = workspace?.FullPath ?? string.Empty;

        // 工作目录变了：WorkingDirectory 留空的 MCP server 还跑在旧目录里，重连一次让它们跟上
        if (HasCwdDependentMcpServer()) _ = ResetMcpConnectionsAsync();

        return true;
    }

    private void SaveWorkspaces() => _settings.WorkspacePaths = [.. Workspaces.Select(w => w.FullPath)];

    /// <summary>是否存在依赖当前工作目录的 MCP server（stdio 且未显式配置工作目录）。</summary>
    private bool HasCwdDependentMcpServer() => _mcpConfig.Load().Any(server =>
        server is { Enabled: true, Transport: McpTransportType.Stdio }
        && string.IsNullOrWhiteSpace(server.WorkingDirectory));

    /// <summary>重连 MCP server，让它们的默认工作目录跟随新的工作区；失败只记日志，不影响对话。</summary>
    private async Task ResetMcpConnectionsAsync()
    {
        try
        {
            await _mcpTools.ResetAsync();
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "reset MCP connections after workspace switch failed");
        }
    }

    /// <summary>
    /// 把进程工作目录切到工作区：工具的相对路径、命令执行与 AGENTS.md 读取都以此为准；
    /// 路径为空时回到应用目录。切换失败只记日志并返回 false，由调用方决定是否回退状态。
    /// </summary>
    private static bool ApplyWorkingDirectory(string? path)
    {
        var target = string.IsNullOrEmpty(path) ? AppContext.BaseDirectory : path;

        try
        {
            Directory.SetCurrentDirectory(target);
            Logger.Debug("working directory switched to {Path}", target);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "switch working directory to {Path} failed", target);
            return false;
        }
    }

    /// <summary>把目录转成完整路径；目录不存在或路径非法时返回 null。</summary>
    private static string? NormalizePath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            return Directory.Exists(full) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }


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
            var action = request.ToolName == "run_shell_command" ? "执行命令" : "发送文件";
            var message = new Models.ChatMessage(
                $"模型请求{action}，等待你的确认：\n{request.Arguments}",
                isUser: false,
                isTool: true,
                approval: request);

            // 和工具记录一样插在回复气泡之前，保持「确认 → 执行结果 → 回复」的顺序
            var index = _reply is { } reply ? Messages.IndexOf(reply) : -1;
            if (index >= 0) Messages.Insert(index, message);
            else Messages.Add(message);
        });
    }

    /// <summary>工具执行线程发起提问：回到 UI 线程显示提问面板，等用户提交或取消。</summary>
    private void OnQuestionAsked(UserQuestionRequest request)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // 同一时间只保留一个面板：旧请求按取消处理，避免它的工具调用一直等
            AskUser?.CancelCommand.Execute(null);

            var panel = new AskUserViewModel(request);
            request.Resolved += () => Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(AskUser, panel)) AskUser = null;
            });

            // 极端情况下请求在执行到 UI 线程前就已经结束（例如立刻按了停止），不再显示死角面板
            if (request.IsResolved) return;

            AskUser = panel;
        });
    }

    /// <summary>
    /// 工具线程要求在对话窗口展示本地文件：回到 UI 线程插入带附件的气泡，
    /// 图片在后台解码，解码完成后再挂到气泡上。
    /// </summary>
    private void OnFileShown(ChatFileDisplay display)
    {
        Dispatcher.UIThread.Post(async void () =>
        {
            try
            {
                var attachment = new ChatDisplayAttachment(display.FilePath, display.FileName, display.IsImage);
                var message = new Models.ChatMessage(
                    $"已展示文件：{display.FileName}",
                    isUser: false,
                    displayAttachment: attachment);

                // 和工具记录一样插在回复气泡之前，避免文件卡片排在「正在思考…」之后
                var index = _reply is { } reply ? Messages.IndexOf(reply) : -1;
                if (index >= 0) Messages.Insert(index, message);
                else Messages.Add(message);

                if (!display.IsImage) return;

                var image = await Task.Run(() => ChatDisplayAttachment.LoadImage(display.FilePath));
                if (image is not null) attachment.Image = image;
            }
            catch (Exception e)
            {
                Logger.Warning(e, "load local file {Path} for display failed", display.FilePath);
            }
        });
    }

    /// <summary>模型选择列表是否展开。</summary>
    [ObservableProperty]
    public partial bool IsModelPickerOpen { get; set; }

    /// <summary>模型通过 ask_user 工具发起的提问面板；null 表示没有待回答的问题。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAsking))]
    public partial AskUserViewModel? AskUser { get; set; }

    public bool IsAsking => AskUser is not null;

    /// <summary>「添加模型」表单是否显示。</summary>
    [ObservableProperty]
    public partial bool IsAddingModel { get; set; }

    [ObservableProperty] public partial string NewModelProvider { get; set; } = string.Empty;

    public string NewModelName
    {
        get;
        set
        {
            field = value;
            AddModelError = null;
            OnPropertyChanged();
        }
    } = string.Empty;

    [ObservableProperty] public partial string NewModelModelId { get; set; } = string.Empty;

    [ObservableProperty] public partial string NewModelBaseUrl { get; set; } = string.Empty;

    [ObservableProperty] public partial string NewModelApiKey { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAddModelError))]
    public partial string? AddModelError { get; set; }

    public bool HasAddModelError => !string.IsNullOrEmpty(AddModelError);

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

    /// <summary>打开插件管理窗口（显示前重新扫描插件目录）。</summary>
    [RelayCommand]
    private void OpenPlugins()
    {
        IsMoreOpen = false;
        _pluginsWindow.Show();
    }

    /// <summary>打开定时任务管理窗口（显示前重排触发计划并刷新下次触发时间）。</summary>
    [RelayCommand]
    private void OpenScheduledTasks()
    {
        IsMoreOpen = false;
        _scheduledTasksWindow.Show();
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

    /// <summary>
    /// 插件推送了一条需要桌宠处理的消息（如被 @）：自动发起一轮对话；正在生成时先排队。
    /// 可从任意线程调用，内部切回 UI 线程；窗口不会自动弹出，等用户自己打开查看。
    /// </summary>
    private void NotifyPlatformMessage(object? _, PlatformNotification notification)
    {
        var sender = notification.Sender is { Length: > 0 } name ? name : "未知发送者";
        var display = $"{PlatformNoticePrefix} {notification.PlatformId} · {sender}：{PreviewNotice(notification.Content)}";
        var prompt = new StringBuilder()
            .Append(PlatformNoticePrefix).Append(" 你在消息平台「").Append(notification.PlatformId)
            .Append("」收到一条需要处理的消息（发送者：").Append(sender).Append("）：\n")
            .Append("<<<\n").Append(notification.Content).Append("\n>>>").ToString();
        EnqueuePendingTurn(new PendingTurn(prompt, display));
    }

    /// <summary>定时任务到点：排进与平台提醒同一条串行通道，避免多条任务同时执行导致桌宠异常。</summary>
    private void OnScheduledTaskTriggered(object? _, ScheduledTaskTrigger trigger)
    {
        var display = $"⏰ 定时任务「{trigger.Name}」：{PreviewNotice(trigger.Content)}";
        var prompt = new StringBuilder()
            .Append("你被定时任务「").Append(trigger.Name).Append("」唤醒，请执行以下内容：\n")
            .Append("<<<\n").Append(trigger.Content).Append("\n>>>").ToString();
        EnqueuePendingTurn(new PendingTurn(prompt, display));
    }

    /// <summary>把一轮待执行对话排进队列；可从任意线程调用。</summary>
    private void EnqueuePendingTurn(PendingTurn turn)
    {
        if (Dispatcher.UIThread.CheckAccess()) EnqueueTurnCore(turn);
        else Dispatcher.UIThread.Post(() => EnqueueTurnCore(turn));
    }

    private void EnqueueTurnCore(PendingTurn turn)
    {
        if (_pendingTurns.Count >= MaxPendingTurns)
        {
            // 连续到点时与最旧一条合并：既不无限排队，也不丢内容
            var oldest = _pendingTurns.Dequeue();
            turn = new PendingTurn($"{oldest.Prompt}\n{turn.Prompt}", $"{oldest.Display}\n{turn.Display}");
        }

        _pendingTurns.Enqueue(turn);
        StartPendingTurns();
    }

    private void StartPendingTurns()
    {
        if (_handlingPendingTurn || IsGenerating || _pendingTurns.Count == 0) return;
        _ = ProcessPendingTurnsAsync();
    }

    private async Task ProcessPendingTurnsAsync()
    {
        _handlingPendingTurn = true;
        try
        {
            while (!IsGenerating && _pendingTurns.TryDequeue(out var turn))
                await RunPendingTurnAsync(turn);
        }
        finally
        {
            _handlingPendingTurn = false;
            // 处理期间又来了新的待执行对话：继续消费
            StartPendingTurns();
        }
    }

    /// <summary>为一条待执行对话（平台提醒 / 定时任务）发起一轮对话：界面先插提醒气泡，再按普通轮次生成回复。</summary>
    private async Task RunPendingTurnAsync(PendingTurn turn)
    {
        Messages.Add(new Models.ChatMessage(turn.Display, isUser: false, isPlatformNotice: true));

        if (SelectedModel is not { } model
            || string.IsNullOrWhiteSpace(model.ModelId)
            || string.IsNullOrWhiteSpace(model.BaseUrl))
        {
            Messages.Add(new Models.ChatMessage("当前没有可用的对话模型，无法执行这条提醒。", isUser: false));
            return;
        }

        // 落库时同时保存气泡文本与「提醒」标记：完整提示给模型，恢复历史时展示同一份摘要
        await RunTurnAsync(model, turn.Prompt, turn.Display, isPlatformNotice: true);
    }

    /// <summary>排队等待执行的一轮对话：<see cref="Prompt" /> 发给模型，<see cref="Display" /> 作为气泡文案。</summary>
    private sealed record PendingTurn(string Prompt, string Display);

    /// <summary>提醒气泡里的单行摘要：折叠换行并截断，完整内容仍放在给模型的提示里。</summary>
    private static string PreviewNotice(string content)
    {
        var singleLine = content.ReplaceLineEndings(" ").Trim();
        return singleLine.Length <= PlatformNoticePreviewLength
            ? singleLine
            : singleLine[..PlatformNoticePreviewLength] + "…";
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = Draft.Trim();
        var attachments = Attachments.ToList();
        if (IsGenerating || (text.Length == 0 && attachments.Count == 0)) return;

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

        // 气泡列出附件名；消息正文统一附加 @绝对路径，模型之后可随时按路径读取
        var attachmentLines = string.Join("\n", attachments.Select(a => $"[附件] {a.FileName}"));
        var bubbleText = text.Length == 0 ? attachmentLines
            : attachments.Count == 0 ? text
            : $"{text}\n{attachmentLines}";
        Messages.Add(new Models.ChatMessage(bubbleText, isUser: true));
        Draft = string.Empty;
        Attachments.Clear();
        AttachmentHint = null;

        var pathReferences = attachments.Select(a => $"@{a.FullPath}");
        var messageText = string.Join("\n", new[] { text }.Concat(pathReferences).Where(line => line.Length > 0));

        await RunTurnAsync(model, messageText);
    }

    /// <summary>
    /// 发起一轮对话：写入历史与长期记忆、插入占位气泡并流式接收回复。
    /// 手输发送（先插用户气泡）与平台提醒（先插提醒气泡）共用这一条链路。
    /// </summary>
    /// <param name="model">本轮使用的模型配置。</param>
    /// <param name="messageText">发给模型的消息正文。</param>
    /// <param name="displayText">气泡展示文本；只影响界面，发给模型的仍是 messageText。</param>
    /// <param name="isPlatformNotice">
    /// 该轮是否为平台提醒。与 <paramref name="displayText" /> 无关：任何消息都可以有展示文本，
    /// 但只有提醒才按提醒样式恢复。
    /// </param>
    private async Task RunTurnAsync(
        ChatModelConfig model,
        string messageText,
        string? displayText = null,
        bool isPlatformNotice = false)
    {
        // 这轮提问先落历史；路径随消息一起保存，后续轮次无需重复添加附件
        _history.Add(new ChatMessage(ChatRole.User, messageText));
        _memory.AddTurn(ChatMemory.UserRole, messageText, display: displayText, isPlatformNotice: isPlatformNotice);
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
            // 正文去空白后（纯空白视作无正文）为空但存在思考内容时同样落库：这条回复仍有思考内容，长期记忆不能丢
            var hasReasoning = withReasoning && roundReasoning.Length > 0;
            var content = segment.Trim();
            if (content.Length == 0 && !hasReasoning) return;

            List<AIContent> contents = [];
            string? reasoning = null;
            if (hasReasoning)
            {
                reasoning = roundReasoning.ToString();
                contents.Add(new TextReasoningContent(reasoning));
                roundReasoning.Clear();
            }

            contents.Add(new TextContent(content));
            _history.Add(new ChatMessage(ChatRole.Assistant, contents));
            _memory.AddTurn(ChatMemory.AssistantRole, content, reasoning: reasoning);
            TrimHistory();
            segment = string.Empty;
        }

        // 插入一条工具记录：没有正文时直接插在回复气泡前；已有正文则截断并为后续回复新开气泡
        void AddToolBubble(Models.ChatMessage message)
        {
            CollapseThinking();
            thinking = null;
            var index = Messages.IndexOf(reply);

            // 完全没有正文（含只有空白字符）：插在占位气泡前，沿用同一个占位气泡
            if (segment.Trim().Length == 0)
            {
                reply.Text = ToolCallingText; // 工具执行中，占位提示改为调用中
                if (index >= 0) Messages.Insert(index, message);
                else Messages.Add(message);
                return;
            }

            CommitSegment(withReasoning: false);
            Messages.Insert(index + 1, message);
            reply = new Models.ChatMessage(ToolCallingText, isUser: false);
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
                        // 工具已返回、开始新一轮思考：占位提示从「正在调用工具中」恢复
                        if (segment.Length == 0 && reply.Text == ToolCallingText) reply.Text = ThinkingText;
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

                        // 只输出空白字符时气泡显示空白提示，收到实际正文后再整体替换
                        reply.Text = DisplayText(segment);
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
                if (roundReasoning.Length > 0) reply.Text = BlankText; // 只有思考没有正文：显示空白提示
                else if (answered is null) reply.Text = "（没有收到回复）";
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
            if (usage != null) RecordUsage(usage, answered);

            _cts = null;
            _reply = null;
            IsGenerating = false;

            // 本轮生成期间排队进来的待执行对话（平台提醒 / 定时任务），等回复结束后继续处理
            StartPendingTurns();
        }
    }

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    /// <summary>记录一轮用量：气泡底部显示明细，累计值写回设置并落盘。</summary>
    private void RecordUsage(LlmUsageEvent usage, Models.ChatMessage? bubble)
    {
        bubble?.UsageText = FormatUsage(usage);

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

    /// <summary>气泡展示文本：落库的原始内容只有空白字符时替换为空白提示，避免出现空气泡。</summary>
    private static string DisplayText(string text) => text.Trim().Length == 0 ? BlankText : text;

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
            // 平台提醒当初按 user 角色落库（模型需要看到），恢复时改回提醒气泡，不伪装成用户发言。
            // 是否提醒只看显式标记，展示文本存在与否不代表消息类型。
            var isPlatformNotice = isUser && turn.IsPlatformNotice;
            var bubbleText = turn.Display ?? turn.Text;
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

            Messages.Add(new Models.ChatMessage(
                DisplayText(bubbleText),
                isUser && !isPlatformNotice,
                isPlatformNotice: isPlatformNotice));
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
            if (++userTurns != HistoryWindowTurns) continue;
            keepFrom = i;
            break;
        }

        if (keepFrom > 0) _history.RemoveRange(0, keepFrom);

        if (before != _history.Count)
            Logger.Debug("history trimmed before={Before} after={After} turns={Turns}", before, _history.Count, userTurns);
    }

    private bool CanSend() => !IsGenerating && (!string.IsNullOrWhiteSpace(Draft) || Attachments.Count > 0);
}

/// <summary>工作区列表项：展示目录名与完整路径，当前激活项高亮。</summary>
public sealed partial class WorkspaceItem : ObservableObject
{
    public WorkspaceItem(string fullPath)
    {
        FullPath = fullPath;

        // 目录名即工作区名称；根目录等取不到最后一段时退回完整路径
        var trimmed = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Name = Path.GetFileName(trimmed) is { Length: > 0 } name ? name : fullPath;
    }

    /// <summary>工作区完整路径。</summary>
    public string FullPath { get; }

    /// <summary>工作区完整名称（路径最后一段）。</summary>
    public string Name { get; }

    /// <summary>是否为当前激活的工作区。</summary>
    [ObservableProperty]
    public partial bool IsActive { get; set; }
}
