// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
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

    // 历史窗口的高/低水位来自设置（SouffleSettings.HistoryWindowTurns / HistoryKeepTurns，见 SoufflePet.json）。
    // 注意不要改成「每轮只丢最旧一轮」：服务端的前缀缓存按「从请求开头起的最长公共前缀」命中，
    // 每轮换掉第一条历史消息，缓存就只剩系统提示词那 3k tokens（实测 12.7 万输入只命中 2%）。
    // 两个水位的差值决定裁剪频率：差值越大，裁剪越少、缓存越稳定。

    /// <summary>平台提醒合成消息的前缀。</summary>
    private const string PlatformNoticePrefix = "[平台提醒]";

    /// <summary>系统消息的前缀。</summary>
    private const string SystemPrefix = "[系统消息]";

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
    private readonly ILive2DModelsWindowService _live2DModelsWindow;
    private readonly ILlmModelsWindowService _llmModelsWindow;
    private readonly IPluginsWindowService _pluginsWindow;
    private readonly IScheduledTasksWindowService _scheduledTasksWindow;
    private readonly IStatisticsService _statistics;

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
        ILive2DModelsWindowService live2DModelsWindow,
        ILlmModelsWindowService llmModelsWindow,
        IPluginsWindowService pluginsWindow,
        IScheduledTasksWindowService scheduledTasksWindow,
        PlatformService platforms,
        IScheduledTaskService scheduledTasks,
        UserQuestionService questions,
        ChatFileService files,
        IStatisticsService statistics)
    {
        _settings = settings;
        _llm = llm;
        _toolAccess = toolAccess;
        _memory = memory;
        _mcpSettings = mcpSettings;
        _mcpTools = mcpTools;
        _mcpConfig = mcpConfig;
        _skillsWindow = skillsWindow;
        _live2DModelsWindow = live2DModelsWindow;
        _llmModelsWindow = llmModelsWindow;
        _pluginsWindow = pluginsWindow;
        _scheduledTasksWindow = scheduledTasksWindow;
        _statistics = statistics;
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

        // 模型列表由提供商配置展开，这里按保存的键恢复选中项。
        Models.CollectionChanged += OnModelListChanged;
        SelectedModel = FindModel(settings.ChatModelKey);
        RefreshModelGroups();
        _initialized = true;

        InitializeWorkspaces();
        LoadRecentHistory();
        TrimMessages();
    }

    /// <summary>消息列表（首次打开即包含一条助手问候）。</summary>
    public ObservableCollection<Models.ChatMessage> Messages { get; } =
    [
        new("今天想聊点什么呀？", isUser: false)
    ];

    /// <summary>
    /// 可选的对话模型（与设置共享同一集合）：由提供商配置里已启用的模型展开而来，
    /// 增删由对话模型管理面板负责，对话窗口只读。
    /// </summary>
    public ObservableCollection<ChatModelConfig> Models => _settings.ChatModels;

    /// <summary>模型选择浮层的内容：按提供商分组，组内只包含已启用的模型。</summary>
    public ObservableCollection<ChatModelGroupViewModel> ModelGroups { get; } = [];

    /// <summary>模型选择浮层是否有可选项（没有时显示「去管理模型」提示）。</summary>
    public bool HasModelGroups => ModelGroups.Count > 0;

    /// <summary>按提供商重建分组：同一提供商的模型排在一起，组内沿用配置里的顺序。</summary>
    private void RefreshModelGroups()
    {
        ModelGroups.Clear();
        foreach (var group in Models.GroupBy(model => model.Provider))
        {
            var viewModel = new ChatModelGroupViewModel(group.Key);
            foreach (var model in group)
                viewModel.Models.Add(new ChatModelPickerItem(model, this)
                {
                    IsActive = ReferenceEquals(model, SelectedModel)
                });

            ModelGroups.Add(viewModel);
        }

        OnPropertyChanged(nameof(HasModelGroups));
    }

    /// <summary>在模型选择浮层里点了一个模型：切换为当前模型并收起浮层（重复点当前模型也收起）。</summary>
    public void SelectModel(ChatModelConfig model)
    {
        SelectedModel = model;
        IsModelPickerOpen = false;
    }

    /// <summary>按「提供商/模型标识」键还原选中项。</summary>
    private ChatModelConfig? FindModel(string key) =>
        key.Length > 0 ? Models.FirstOrDefault(model => model.Key == key) : null;

    /// <summary>
    /// 模型是否可用于发起请求：模型标识与接口地址都要填，地址还得是合法的绝对地址。
    /// 管理面板保存时已校验过地址，这里兜住手工改配置的情况——直接交给 SDK 会在 new Uri 处抛异常。
    /// </summary>
    private static bool IsUsable(ChatModelConfig model) =>
        !string.IsNullOrWhiteSpace(model.ModelId)
        && Uri.TryCreate(model.BaseUrl.Trim(), UriKind.Absolute, out _);

    /// <summary>模型列表被管理面板重建：刷新分组，并让选中项跟上新实例（按保存的键重新定位）。</summary>
    private void OnModelListChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshModelGroups();
        if (SelectedModel is { } current && Models.Contains(current)) return;

        var restored = FindModel(_settings.ChatModelKey);
        if (!ReferenceEquals(restored, SelectedModel)) SelectedModel = restored;
    }

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
    public string UsageSummaryText => _statistics.UsageText;

    /// <summary>完整用量明细弹层是否展开（点击底部用量信息切换）。</summary>
    [ObservableProperty]
    public partial bool IsUsageDetailsOpen { get; set; }

    /// <summary>弹层标题右侧的累计总用量（输入 + 输出）。</summary>
    public string UsageTotalText
    {
        get
        {
            var usage = _statistics.Usage;
            return $"{usage.InputTokens + usage.OutputTokens:N0} tok";
        }
    }

    /// <summary>
    /// 完整用量明细：缓存命中（率，保留 1 位小数）、未缓存输入、缓存读取、输出。
    /// 数值来自 <see cref="IStatisticsService.Usage" /> 的累计快照，与底部文案同源。
    /// </summary>
    public IReadOnlyList<UsageDetail> UsageDetails
    {
        get
        {
            var usage = _statistics.Usage;
            return
            [
                new UsageDetail("缓存命中", usage.InputTokens > 0 ? $"{usage.CacheHitRate:P1}" : "—"),
                new UsageDetail("未缓存输入", $"{usage.UncachedInputTokens:N0} tok"),
                new UsageDetail("缓存读取", $"{usage.CachedInputTokens:N0} tok"),
                new UsageDetail("输出", $"{usage.OutputTokens:N0} tok")
            ];
        }
    }

    /// <summary>点击底部用量信息：展开/收起完整用量明细（打开时刷新一次数值）。</summary>
    [RelayCommand]
    private void ToggleUsageDetails()
    {
        IsUsageDetailsOpen = !IsUsageDetailsOpen;
        if (!IsUsageDetailsOpen) return;

        OnPropertyChanged(nameof(UsageTotalText));
        OnPropertyChanged(nameof(UsageDetails));
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
    /// 添加附件：统一只记录本地绝对路径（不限类型、不读取内容；文件与文件夹均可，文件夹取其目录路径），
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

            // FileInfo.Exists 对目录恒为 false，文件夹要单独用 Directory.Exists 判断、用 DirectoryInfo 取名
            var isDirectory = Directory.Exists(full);
            if (!isDirectory && !File.Exists(full))
            {
                failed.Add(Path.GetFileName(full));
                continue;
            }

            // 同一个路径重复选择时只保留一份
            if (Attachments.Any(a => string.Equals(a.FullPath, full, StringComparison.OrdinalIgnoreCase))) continue;

            var name = isDirectory ? new DirectoryInfo(full).Name : Path.GetFileName(full);
            Attachments.Add(new ChatAttachment(name, full));
        }

        AttachmentHint = failed.Count == 0 ? null : $"未能添加：{string.Join("、", failed)}";
    }

    /// <summary>添加一张内存里的图片附件（粘贴的剪贴板图片）：发送时才转储成文件。</summary>
    public void AddImageAttachment(byte[] data, string fileName)
    {
        Attachments.Add(new ChatAttachment(fileName, string.Empty, data));
        AttachmentHint = null;
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
    public string ModelButtonText => SelectedModel switch
    {
        null => "请选择模型",
        { Provider.Length: > 0 } model => $"{model.Provider} · {model.Name}",
        var model => model.Name
    };

    partial void OnSelectedModelChanged(ChatModelConfig? value)
    {
        OnPropertyChanged(nameof(ModelButtonText));
        // 选中项变了：浮层里的高亮跟着换（列表为空时 value 为 null，只清高亮）
        foreach (var group in ModelGroups)
            foreach (var item in group.Models)
                item.IsActive = ReferenceEquals(item.Config, value);

        if (!_initialized) return;
        // 键用于精确还原（同名模型可能存在于多个提供商下）；未选中时保留上一次的键，模型重新启用后还能回来
        if (value is not null) _settings.ChatModelKey = value.Key;

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
                // 附件气泡只展示卡片/图片本身，正文留空（IsPlain 对附件消息为 false，不会再叠一行说明文字）
                var message = new Models.ChatMessage(
                    string.Empty,
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

    [RelayCommand]
    private void ToggleModelPicker() => IsModelPickerOpen = !IsModelPickerOpen;

    /// <summary>模型通过 ask_user 工具发起的提问面板；null 表示没有待回答的问题。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAsking))]
    public partial AskUserViewModel? AskUser { get; set; }

    public bool IsAsking => AskUser is not null;

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

    /// <summary>打开 Live2D 模型管理窗口（导入/移除/切换桌面模型，与对话模型无关）。</summary>
    [RelayCommand]
    private void OpenLive2DModels()
    {
        IsMoreOpen = false;
        _live2DModelsWindow.Show();
    }

    /// <summary>打开对话模型管理窗口（按提供商配置接口/密钥，勾选要启用的模型）。</summary>
    [RelayCommand]
    private void OpenLlmModels()
    {
        IsMoreOpen = false;
        IsModelPickerOpen = false;
        _llmModelsWindow.Show();
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

    /// <summary>
    /// 插件推送了一条需要桌宠处理的消息（如被 @）：自动发起一轮对话；正在生成时先排队。
    /// 可从任意线程调用，内部切回 UI 线程；窗口不会自动弹出，等用户自己打开查看。
    /// </summary>
    private void NotifyPlatformMessage(object? _, PlatformNotification notification)
    {
        var sender = notification.Sender is { Length: > 0 } name ? name : "未知发送者";
        var display = $"{PlatformNoticePrefix} {notification.PlatformId} · {sender}：{PreviewNotice(notification.Content)}";
        var prompt = new StringBuilder()
            .Append(PlatformNoticePrefix)
            .Append($"「{notification.PlatformId}」收到一条新消息（发送者：")
            .Append(sender).Append("）：\n")
            .Append("<<<\n").Append(notification.Content).Append("\n>>>").ToString();
        EnqueuePendingTurn(new PendingTurn(prompt, display));
    }

    /// <summary>
    /// 戳一戳：用户从桌宠交互菜单戳了一下桌宠，发起一轮由桌宠自主发起的对话。
    /// 与平台提醒 / 定时任务共用同一条串行通道；可从任意线程调用，内部切回 UI 线程。
    /// </summary>
    public void WakeByPoke()
    {
        var display = "👉 你被戳了一下，回应一下吧。";
        var prompt = new StringBuilder()
            .Append(SystemPrefix)
            .Append("用户戳了你一下。").ToString();
        EnqueuePendingTurn(new PendingTurn(prompt, display));
    }

    /// <summary>摸摸头：用户从桌宠交互菜单摸了摸桌宠的头，发起一轮自主对话（与戳一戳共用串行通道）。</summary>
    public void WakeByHeadPat()
    {
        var display = "🖐 摸摸头";
        var prompt = new StringBuilder()
            .Append(SystemPrefix)
            .Append("用户摸了摸你的头。").ToString();
        EnqueuePendingTurn(new PendingTurn(prompt, display));
    }

    /// <summary>定时任务到点：排进与平台提醒同一条串行通道，避免多条任务同时执行导致桌宠异常。</summary>
    private void OnScheduledTaskTriggered(object? _, ScheduledTaskTrigger trigger)
    {
        var display = $"⏰ 定时任务「{trigger.Name}」：{PreviewNotice(trigger.Content)}";
        var prompt = new StringBuilder()
            .Append(SystemPrefix)
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

        if (SelectedModel is not { } model || !IsUsable(model))
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
            Messages.Add(new Models.ChatMessage("请先选择模型：点下方的模型按钮挑一个；没有可选模型时点「管理模型」添加。", isUser: false));
            IsModelPickerOpen = true;
            return;
        }

        if (!IsUsable(model))
        {
            Messages.Add(new Models.ChatMessage("当前模型的标识或接口地址无效，请在「管理模型」里检查该提供商的配置", isUser: false));
            return;
        }

        // 粘贴的图片此刻才落盘：此前的占位路径为空，转储后换成真实路径
        attachments = PersistPendingImages(attachments);
        var persistHint = AttachmentHint;

        // 气泡列出附件名；消息正文统一附加 @绝对路径，模型之后可随时按路径读取
        var attachmentLines = string.Join("\n", attachments.Select(a => $"[附件] {a.FileName}"));
        var bubbleText = text.Length == 0 ? attachmentLines
            : attachments.Count == 0 ? text
            : $"{text}\n{attachmentLines}";
        Messages.Add(new Models.ChatMessage(bubbleText, isUser: true));
        Draft = string.Empty;
        Attachments.Clear();
        AttachmentHint = persistHint;

        var pathReferences = attachments.Select(a => $"@{a.FullPath}");
        var messageText = string.Join("\n", new[] { text }.Concat(pathReferences).Where(line => line.Length > 0));

        // 计入对话次数：口径与统计项的迁移保持一致——只有用户手输发送算一次对话，
        // 平台提醒 / 戳一戳 / 定时任务唤醒走的是带提醒标记的排队通道，不计入
        _statistics.AddConversation();
        await RunTurnAsync(model, messageText);
    }

    /// <summary>粘贴图片的落盘目录：与长期记忆同属应用数据目录，历史里的 @路径 不会因临时目录清理失效。</summary>
    private static readonly string AttachmentDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SoufflePet", "attachments");

    /// <summary>
    /// 发送前把内存里的粘贴图片转储成 PNG（重名时加序号），返回换成真实路径的附件列表；
    /// 写盘失败的丢弃并留一行提示，不用无效路径去糊弄模型。
    /// </summary>
    private List<ChatAttachment> PersistPendingImages(List<ChatAttachment> attachments)
    {
        var failed = new List<string>();
        var result = new List<ChatAttachment>(attachments.Count);
        AttachmentHint = null;

        foreach (var attachment in attachments)
        {
            if (attachment.Data is not { } data)
            {
                result.Add(attachment);
                continue;
            }

            try
            {
                Directory.CreateDirectory(AttachmentDirectory);
                var path = Path.Combine(AttachmentDirectory, attachment.FileName);
                for (var index = 2; File.Exists(path); index++)
                {
                    path = Path.Combine(
                        AttachmentDirectory,
                        $"{Path.GetFileNameWithoutExtension(attachment.FileName)}-{index}.png");
                }

                File.WriteAllBytes(path, data);
                result.Add(attachment with { FullPath = path, FileName = Path.GetFileName(path), Data = null });
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "save pasted image {Name} failed", attachment.FileName);
                failed.Add(attachment.FileName);
            }
        }

        if (failed.Count > 0) AttachmentHint = $"未能保存：{string.Join("、", failed)}";
        return result;
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
        // 本轮开始时间：只以工具调用收尾时用量要补记到本轮已写入的记录上，用它把范围限定在本轮
        var turnStartedAt = DateTimeOffset.Now;

        // 这轮提问先落历史；路径随消息一起保存，后续轮次无需重复添加附件。
        // 与长期记忆同样去空白：重启后按 conversations.jsonl 重建的消息才能和这次请求逐字节一致。
        messageText = messageText.Trim();
        var requestText = BuildUserRequestText(messageText);
        _history.Add(new ChatMessage(ChatRole.User, requestText));
        _memory.AddTurn(ChatMemory.UserRole, messageText, display: displayText, isPlatformNotice: isPlatformNotice,
            requestText: requestText);
        TrimHistory();

        // 占位气泡：收到第一段内容时整体替换，避免出现空气泡
        var reply = new Models.ChatMessage(ThinkingText, isUser: false);
        _reply = reply;
        Messages.Add(reply);

        using var cts = new CancellationTokenSource();
        _cts = cts;
        IsGenerating = true;

        var segment = string.Empty;  // 当前气泡里的文本；工具调用时截断重开
        string? _pendingToolText = null; // 工具调用前刚输出、尚未写进请求历史的正文（与调用合并成一条助手消息）
        var roundReasoning = new StringBuilder(); // 当前轮的思考内容，随本轮最后一条助手消息落库
        Models.ChatMessage? thinking = null; // 当前轮次的思考气泡；工具调用后另起一个
        Models.ChatMessage? answered = null; // 本轮最后一段正文所在的气泡，用量挂在它下方
        LlmUsageEvent? usage = null;         // 本轮累计 token 用量（服务每完成一轮产出一份累计值，取消时也能拿到已完成轮次的）
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
        // pendingForToolCall 表示这段正文其实属于紧随其后的工具调用消息：先只落记忆，
        // 请求历史等工具结果回来时与调用合并成一条消息（见 AddToolHistory）。
        // 返回是否真的写了一条记录：false 表示没有正文也没有思考内容，调用方需要自行处理用量。
        bool CommitSegment(bool withReasoning, bool pendingForToolCall = false)
        {
            // 正文去空白后（纯空白视作无正文）为空但存在思考内容时同样落库：这条回复仍有思考内容，长期记忆不能丢
            var hasReasoning = withReasoning && roundReasoning.Length > 0;
            var content = segment.Trim();
            if (content.Length == 0 && !hasReasoning) return false;

            if (pendingForToolCall)
            {
                // 与真正发出去的请求保持同构：模型是在同一条助手消息里先输出正文再发起调用，
                // 历史里若拆成两条，下一轮请求从这一条起就接不上前缀，整轮工具消息都会重算。
                _pendingToolText = content;
                _memory.AddTurn(ChatMemory.AssistantRole, content);
                TrimHistory();
                segment = string.Empty;
                return true;
            }

            List<AIContent> contents = [];
            string? reasoning = null;
            if (hasReasoning)
            {
                // 与落盘一致地去掉首尾空白：conversations.jsonl 里的内容也是去空白后的，
                // 重启重建才能和上次请求完全相同（思考内容会作为 reasoning_content 回传）
                reasoning = roundReasoning.ToString().Trim();
                contents.Add(new TextReasoningContent(reasoning));
                roundReasoning.Clear();
            }

            // 请求历史与落盘记录保持同一份文本（都去空白），重启后按 conversations.jsonl
            // 重建出的消息才能和上次请求逐字节一致，第一次请求继续命中前缀缓存。
            contents.Add(new TextContent(content));
            _history.Add(new ChatMessage(ChatRole.Assistant, contents));
            // 本轮累计 token 用量随最后一条助手消息落盘（服务每完成一轮就产出一份累计值，
            // 所以中途取消时这里也拿得到已完成轮次的用量）；重启后据此恢复气泡底部的用量文案。
            _memory.AddTurn(ChatMemory.AssistantRole, content, reasoning: reasoning,
                inputTokens: usage?.InputTokens,
                outputTokens: usage?.OutputTokens,
                cachedInputTokens: usage?.CachedInputTokens,
                model: FormatModelLabel(model));
            TrimHistory();
            segment = string.Empty;
            return true;
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

            CommitSegment(withReasoning: false, pendingForToolCall: true);
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
                            // 思考气泡默认收起：只露出最后一行（随流式追加刷新），不占满消息区
                            thinking = new Models.ChatMessage(string.Empty, isUser: false, isReasoning: true,
                                isExpanded: false);
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

                    case LlmUsageEvent cumulativeUsage:
                        usage = cumulativeUsage;
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
                        // 本轮思考内容随第一条工具记录保存，推理模型在工具循环里要求回传 reasoning_content。
                        // 与落盘一致地去空白，重启重建的消息才能和上次请求逐字节相同。
                        var toolReasoning = roundReasoning.ToString().Trim() is { Length: > 0 } trimmedReasoning
                            ? trimmedReasoning
                            : null;
                        roundReasoning.Clear();
                        // 调用前输出的正文（如果有）与这条调用属于同一条助手消息，合并进请求历史
                        var assistantText = _pendingToolText;
                        _pendingToolText = null;
                        _memory.AddTurn(ChatMemory.ToolRole, display, call.CallId, call.Name, call.Arguments, call.Result,
                            toolReasoning);
                        AddToolHistory(call.CallId, call.Name, call.Arguments, call.Result, display, toolReasoning,
                            assistantText);
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

            // 工具调用被打断时，调用前的正文还没并进历史：把「思考 + 正文」作为一条普通助手消息补上，
            // 避免请求历史与落盘记录不一致（下次重建时会在这一轮多出这条消息）。
            // 必须放在 CommitSegment 之前，否则思考内容会先被它消费掉。
            if (_pendingToolText is { Length: > 0 } pendingText)
            {
                _pendingToolText = null;
                var pendingReasoning = roundReasoning.ToString().Trim();
                roundReasoning.Clear();
                List<AIContent> pendingContents = [];
                if (pendingReasoning.Length > 0) pendingContents.Add(new TextReasoningContent(pendingReasoning));
                pendingContents.Add(new TextContent(pendingText));
                _history.Add(new ChatMessage(ChatRole.Assistant, pendingContents));
                TrimHistory();
            }

            // 只有真正生成出的内容才写回历史；停止时保留已收到的最后一段
            var committed = CommitSegment(withReasoning: true);

            // 记住本次请求实际使用的窗口轮数：重启后按同一窗口恢复，第一次请求才能接上前缀缓存
            _memory.SaveWindowTurns(CountHistoryUserTurns());

            // 用量统计：气泡底部胶囊显示本轮用量，同时累加进设置里的历史总额
            if (usage != null)
            {
                // 本轮只以工具调用收尾、没有正文可落库时 CommitSegment 不会写记录，用量就挂不到任何一条记录上；
                // 补记到本轮最近一条助手记录，别让已经花掉的 token 在对话记录里留空。
                if (!committed)
                {
                    _memory.AttachUsageToLastTurn(turnStartedAt, usage.InputTokens, usage.OutputTokens,
                        usage.CachedInputTokens, FormatModelLabel(model));
                }

                RecordUsage(model, usage, answered);
            }

            _cts = null;
            _reply = null;
            IsGenerating = false;

            // 一轮结束后再裁剪界面气泡：生成过程中移除消息会和流式更新抢同一个列表
            TrimMessages();

            // 本轮生成期间排队进来的待执行对话（平台提醒 / 定时任务），等回复结束后继续处理
            StartPendingTurns();
        }
    }

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    /// <summary>记录一轮用量：气泡底部胶囊显示紧凑用量，点击后浮窗展示完整明细；累计值写进统计并落盘。</summary>
    private void RecordUsage(ChatModelConfig model, LlmUsageEvent usage, Models.ChatMessage? bubble)
    {
        bubble?.SetUsage(usage.InputTokens, usage.OutputTokens, usage.CachedInputTokens, FormatModelLabel(model));

        _statistics.AddUsage(usage.InputTokens, usage.OutputTokens, usage.CachedInputTokens);
        OnPropertyChanged(nameof(UsageSummaryText));
        // 明细弹层可能正开着：累计值变了要同步刷新
        OnPropertyChanged(nameof(UsageTotalText));
        OnPropertyChanged(nameof(UsageDetails));
    }

    /// <summary>用量浮窗里的「提供方 / 模型」文案；没有配置提供方时只显示模型标识。</summary>
    private static string FormatModelLabel(ChatModelConfig model) =>
        string.IsNullOrWhiteSpace(model.Provider)
            ? model.ModelId
            : $"{model.Provider.Trim()}/{model.ModelId}";

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
    /// assistantText 是调用前刚输出的正文，与调用、思考同属一条助手消息（与实际请求一致）。
    /// 旧记录缺少 CallId / 工具名时无法配对，只能退化成助手文本，避免出现没有对应调用的 tool 消息。
    /// </summary>
    private void AddToolHistory(
        string? callId,
        string? name,
        string? arguments,
        string? result,
        string fallbackText,
        string? reasoning = null,
        string? assistantText = null)
    {
        if (string.IsNullOrEmpty(callId) || string.IsNullOrEmpty(name) || result is null)
        {
            if (!string.IsNullOrEmpty(assistantText)) _history.Add(new ChatMessage(ChatRole.Assistant, assistantText));
            _history.Add(new ChatMessage(ChatRole.Assistant, fallbackText));
            return;
        }

        List<AIContent> contents = [];
        if (!string.IsNullOrEmpty(reasoning)) contents.Add(new TextReasoningContent(reasoning));
        if (!string.IsNullOrEmpty(assistantText)) contents.Add(new TextContent(assistantText));
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

    /// <summary>
    /// 启动时恢复上次会话最后使用的窗口轮数：窗口在两次裁剪之间只增不减，
    /// 恢复成同一轮数后第一次请求的前缀与上次会话一致，仍能命中缓存；
    /// 若固定恢复最大窗口，首轮又会裁到低水位、前缀全变（等于每次重启都白丢一次缓存）。
    /// </summary>
    private int RestoreWindowTurns()
    {
        var saved = _memory.WindowTurns;
        return saved > 0 && saved <= _settings.HistoryWindowTurns ? saved : _settings.HistoryWindowTurns;
    }

    /// <summary>启动时把最近几轮对话（含工具调用记录）恢复到界面与请求历史里（有历史时替换掉问候语）。</summary>
    private void LoadRecentHistory()
    {
        var recent = _memory.RecentTurns(RestoreWindowTurns(), includeTools: true);
        if (recent.Count == 0) return;

        Messages.Clear();

        // 思考气泡：与生成时一致（默认收起），排在本条正文（或工具记录）气泡之前；
        // 恢复出来的思考早已结束，标题不再显示「思考中」
        void AddReasoningBubble(string? reasoning)
        {
            if (string.IsNullOrEmpty(reasoning)) return;
            Messages.Add(new Models.ChatMessage(reasoning, isUser: false, isReasoning: true, isExpanded: false)
            {
                IsReasoningDone = true
            });
        }

        // 落盘时「工具调用前的正文」是单独一行；重建请求历史时要并回紧随其后的那条工具调用消息，
        // 否则重启后的第一次请求会在这一条断开，整轮工具消息都按未命中计费。
        string? pendingAssistantText = null;

        for (var i = 0; i < recent.Count; i++)
        {
            var turn = recent[i];
            if (string.Equals(turn.Role, ChatMemory.ToolRole, StringComparison.OrdinalIgnoreCase))
            {
                // 工具记录按标准结构重建进请求历史，界面上按工具气泡展示（展示时截断）
                AddToolHistory(turn.CallId, turn.ToolName, turn.Arguments, turn.Result, turn.Text, turn.Reasoning,
                    pendingAssistantText);
                pendingAssistantText = null;
                AddReasoningBubble(turn.Reasoning);
                var toolBubble = new Models.ChatMessage(TruncateToolText(turn.Text), isUser: false, isTool: true,
                    isExpanded: false, timestamp: turn.Time)
                {
                    ToolName = turn.ToolName ?? string.Empty,
                    ToolArguments = turn.Arguments ?? string.Empty,
                    ToolResult = turn.Result ?? string.Empty
                };
                // 本轮只以工具调用收尾时用量挂在工具记录上，这里同样还原胶囊（否则只体现在累计统计里）
                if (turn.InputTokens is { } toolInputTokens)
                    toolBubble.SetUsage(toolInputTokens, turn.OutputTokens ?? 0, turn.CachedInputTokens ?? 0, turn.Model);
                Messages.Add(toolBubble);
                continue;
            }

            var isUser = string.Equals(turn.Role, ChatMemory.UserRole, StringComparison.OrdinalIgnoreCase);
            // 平台提醒当初按 user 角色落库（模型需要看到），恢复时改回提醒气泡，不伪装成用户发言。
            // 是否提醒只看显式标记，展示文本存在与否不代表消息类型。
            var isPlatformNotice = isUser && turn.IsPlatformNotice;
            var bubbleText = turn.Display ?? turn.Text;

            // 这条正文后面紧跟工具调用：它和调用原本属于同一条助手消息，历史留到 tool 行合并
            var mergesIntoNextTool = !isUser
                && string.IsNullOrEmpty(turn.Reasoning)
                && i + 1 < recent.Count
                && string.Equals(recent[i + 1].Role, ChatMemory.ToolRole, StringComparison.OrdinalIgnoreCase);

            if (mergesIntoNextTool)
            {
                pendingAssistantText = turn.Text;
            }
            else if (!isUser && !string.IsNullOrEmpty(turn.Reasoning))
            {
                _history.Add(new ChatMessage(ChatRole.Assistant,
                    [new TextReasoningContent(turn.Reasoning), new TextContent(turn.Text)]));
                AddReasoningBubble(turn.Reasoning);
            }
            else
            {
                // 用户消息恢复成「实际发给模型」的版本（含时间 / 回忆），下一轮请求才能接上前缀缓存；
                // 界面气泡仍用 turn.Text / turn.Display 的干净正文。
                var historyText = isUser ? turn.RequestText ?? turn.Text : turn.Text;
                _history.Add(new ChatMessage(isUser ? ChatRole.User : ChatRole.Assistant, historyText));
            }

            var bubble = new Models.ChatMessage(
                DisplayText(bubbleText),
                isUser && !isPlatformNotice,
                isPlatformNotice: isPlatformNotice,
                timestamp: turn.Time);
            // 助手消息恢复本轮用量与时间（旧记录没有这些字段时保持不显示胶囊）
            if (!isUser && turn.InputTokens is { } inputTokens)
                bubble.SetUsage(inputTokens, turn.OutputTokens ?? 0, turn.CachedInputTokens ?? 0, turn.Model);
            Messages.Add(bubble);
        }
    }

    /// <summary>
    /// 组装真正发给模型的用户消息：正文 + 本轮的时间与相关回忆。
    /// 这些附加内容会随消息一起进入请求历史（并写入 conversations.jsonl），
    /// 下一轮请求的前缀才能和上一轮完全一致；否则上一轮（尤其是工具调用产生的大量消息）
    /// 都会因为「历史里是干净正文、请求里是多了一段上下文」而整体按未命中计费。
    /// 界面气泡展示的仍是干净正文（Text），两者分开保存。
    /// </summary>
    private string BuildUserRequestText(string messageText)
    {
        var historyTexts = _history
            .Select(message => StripTurnContext(message.Text).Trim())
            .Where(text => text.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var historyCallIds = _history
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>()
            .Select(call => call.CallId)
            .ToHashSet(StringComparer.Ordinal);

        var recall = _memory.BuildRecallBlock(messageText, historyTexts, historyCallIds);

        var context = new StringBuilder();
        if (recall.Length > 0)
        {
            context.AppendLine("[系统消息] 以下是与本轮提问相关的旧对话（来自长期记忆，仅供参考，不属于用户输入）：");
            context.AppendLine(recall);
        }

        if (_settings.TimeAwarenessEnabled)
        {
            var now = DateTimeOffset.Now;
            context.Append("[系统消息] 当前时间 ")
                .Append(now.ToString("yyyy-MM-dd HH:mm:ss dddd", CultureInfo.GetCultureInfo("zh-CN")))
                .Append(" UTC").Append(now.ToString("zzz"))
                .Append(' ').Append(TimeZoneInfo.Local.Id);
        }

        return context.Length == 0 ? messageText : $"{messageText}\n\n{context.ToString().TrimEnd()}";
    }

    /// <summary>去掉注入在用户消息末尾的上下文，用于长期记忆检索的去重比对。</summary>
    private static string StripTurnContext(string text)
    {
        var index = text.IndexOf("\n\n" + SystemPrefix, StringComparison.Ordinal);
        return index < 0 ? text : text[..index];
    }

    /// <summary>当前请求历史里的用户提问轮数（即窗口轮数，重启后按它恢复同一窗口）。</summary>
    private int CountHistoryUserTurns()
    {
        var turns = 0;
        foreach (var message in _history)
        {
            if (message.Role == ChatRole.User) turns++;
        }

        return turns;
    }

    /// <summary>
    /// 把发给模型的历史裁到最近若干轮。以用户提问作为轮的边界，每轮包含该提问及其后的助手回复、
    /// 全部工具调用记录；窗口内不会再出现半截工具链。
    /// 用户轮数超过设置里的 <see cref="SouffleSettings.HistoryWindowTurns" /> 时，
    /// 一次裁到 <see cref="SouffleSettings.HistoryKeepTurns" />：
    /// 前缀缓存按请求开头起的最长公共前缀命中，每轮只丢最旧一轮等于每轮都让缓存从头失效，
    /// 批量裁剪把这次失效分摊到之后的几十轮里。
    /// </summary>
    private void TrimHistory()
    {
        var before = _history.Count;

        var userTurns = 0;
        foreach (var message in _history)
        {
            if (message.Role == ChatRole.User) userTurns++;
        }

        var windowTurns = _settings.HistoryWindowTurns;
        var keepTurns = _settings.HistoryKeepTurns;
        if (userTurns <= windowTurns) return;

        // 从后往前数到第 keepTurns 个用户发言，窗口就从这里开始
        var keepFrom = 0;
        var kept = 0;
        for (var i = _history.Count - 1; i >= 0; i--)
        {
            if (_history[i].Role != ChatRole.User) continue;
            if (++kept != keepTurns) continue;
            keepFrom = i;
            break;
        }

        if (keepFrom > 0)
        {
            _history.RemoveRange(0, keepFrom);
            // 裁剪后立刻落盘：万一这次对话没跑完就退出，重启时也不会按裁剪前的旧轮数恢复
            _memory.SaveWindowTurns(keepTurns);
        }

        if (before != _history.Count)
            Logger.Debug("history trimmed before={Before} after={After} turns={Turns} keep={Keep}",
                before, _history.Count, userTurns, keepTurns);
    }

    /// <summary>
    /// 界面里保留的气泡上限。消息区已改成虚拟化（只实例化视口附近的几十条），
    /// 这个上限现在约束的是 ViewModel 侧的数据：每条消息的正文、以及图片附件解码后的位图。
    /// 请求历史由 <see cref="TrimHistory" /> 单独维护，这里只裁展示用列表，不影响模型上下文。
    /// </summary>
    private const int MaxVisibleMessages = 300;

    /// <summary>休眠时按最近消息保留气泡，超出部分连同图片位图一起释放。</summary>
    private void TrimMessages()
    {
        if (Messages.Count <= MaxVisibleMessages) return;

        var removed = Messages.Count - MaxVisibleMessages;
        var trimmed = 0;
        for (var i = 0; i < removed; i++)
        {
            // 正在生成的气泡不该在这里被丢掉：生成期间不裁剪，这里只是兜底
            var message = Messages[0];
            if (ReferenceEquals(message, _reply)) break;

            Messages.RemoveAt(0);
            message.ReleaseGraphics();
            trimmed++;
        }

        if (trimmed == 0) return;

        // 不手动 GC：被裁掉的气泡不再可达，交给 GC 在后续回收时处理；
        // 堆的收缩由 runtimeconfig 里的 System.GC.ConserveMemory=5 负责，
        // 这里再叠一次 GC.Collect 既不能保证回收（Optimized 允许跳过、非阻塞不等完成），
        // 强行 Forced + blocking 又会在每轮裁剪时卡住 UI，收益不划算。
        Logger.Debug("visible messages trimmed removed={Removed} left={Left}", trimmed, Messages.Count);
    }

    private bool CanSend() => !IsGenerating && (!string.IsNullOrWhiteSpace(Draft) || Attachments.Count > 0);
}

/// <summary>完整用量弹层里的一行：标签 + 已格式化的数值文本。</summary>
public sealed record UsageDetail(string Label, string Value);

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
