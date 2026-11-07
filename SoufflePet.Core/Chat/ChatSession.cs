// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Serilog;
using SoufflePet.Models;
using SoufflePet.Services;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace SoufflePet.Core.Chat;

/// <summary>会话层可预期的业务错误；宿主映射为桥的 error.code。</summary>
public sealed class ChatSessionException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// 单会话状态机（替代旧 ChatViewModel 的状态部分）：
/// 消息/草稿/附件/模型/审批/工作区/用量/Ask 全部由本类持有，界面只镜像 <see cref="ChatOp" /> 事件。
/// 内部用锁保护状态、单一生成任务串行执行一轮对话；不引用 Avalonia/Dispatcher，也不使用 async void。
/// </summary>
public sealed class ChatSession : IAsyncDisposable
{
    private static readonly ILogger Logger = Log.ForContext<ChatSession>();

    private const string ThinkingText = "正在思考…";
    private const string BlankText = "（空白内容）";
    private const string DefaultTitle = "新会话";
    private const int ToolResultMaxLength = 300;
    private const int ToolArgumentsPreviewLength = 400;
    private const int PlatformNoticePreviewLength = 80;
    private const int MaxPendingTurns = 3;
    private const int MaxVisibleMessages = 300;
    private const int TitleLength = 30;
    private const int WorkspaceNameLength = 40;
    private const string SystemPrefix = "[系统消息]";
    private const string PlatformNoticePrefix = "[平台消息]";

    private static readonly string AttachmentDirectory = Path.Combine(
        ChatMemory.SouffleHome, "attachments");

    /// <summary>
    /// 当前正在跑一轮对话的会话。工具线程发起审批/提问/展示文件时，按它路由到正确的会话，
    /// 多会话并行时不会串到别的窗口。
    /// </summary>
    private static readonly AsyncLocal<ChatSession?> CurrentTurn = new();

    private readonly object _gate = new();
    private readonly List<MessageState> _messages = [];
    private readonly List<AttachmentEntry> _attachments = [];
    private readonly List<ChatMessage> _history = [];
    private readonly Queue<PendingTurn> _pendingTurns = new();
    /// <summary>工具执行期间产生的文件差异：先按 callId 缓冲，等工具结束事件到达时并入气泡。</summary>
    private readonly Dictionary<string, IReadOnlyList<ChatFileDiffState>> _pendingDiffs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    private readonly SouffleSettings _settings;
    private readonly ILlmChatService _llm;
    private readonly ChatToolAccess _toolAccess;
    private readonly UserQuestionService _questions;
    private readonly ChatFileService _files;
    private readonly IStatisticsService _statistics;
    private readonly ChatMemory _memory;

    private string _title;
    private string? _workspacePath;
    private string? _selectedModelKey;
    private string _draft = string.Empty;
    private string _approvalMode;
    private volatile bool _generating;
    /// <summary>最近一次活动时间（Environment.TickCount64），供管理器判断闲置会话是否可卸载。</summary>
    private long _lastActivityMilliseconds = Environment.TickCount64;
    private bool _processingPending;
    private bool _disposed;
    private ChatUsageState? _usage;
    private ChatAskState? _ask;
    private UserQuestionRequest? _askRequest;
    private ToolApprovalRequest? _approvalRequest;
    private string? _approvalMessageId;
    private CancellationTokenSource? _cts;
    private MessageState? _reply;
    private TaskCompletionSource? _idle;
    private Task? _turnTask;
    private Task? _pendingTask;
    /// <summary>会话目录解析器：按当前工作区给出落盘目录（会话按工作区分组）。</summary>
    private readonly Func<string?, string>? _directoryResolver;

    internal ChatSession(
        string id,
        string directory,
        string profilePath,
        string title,
        string? workspacePath,
        string? modelKey,
        SouffleSettings settings,
        ILlmChatService llm,
        ChatToolAccess toolAccess,
        UserQuestionService questions,
        ChatFileService files,
        IStatisticsService statistics,
        Func<string?, string>? directoryResolver = null)
    {
        Id = id;
        _directoryResolver = directoryResolver;
        _title = string.IsNullOrWhiteSpace(title) ? DefaultTitle : title;
        _workspacePath = workspacePath;
        _selectedModelKey = string.IsNullOrWhiteSpace(modelKey) ? settings.ChatModelKey : modelKey;
        _settings = settings;
        _llm = llm;
        _toolAccess = toolAccess;
        _questions = questions;
        _files = files;
        _statistics = statistics;
        _approvalMode = ApprovalModeString(settings.ToolApproval);

        _memory = new ChatMemory(settings, directory, "conversation.jsonl", profilePath);

        _toolAccess.ApprovalRequested += OnApprovalRequested;
        _questions.QuestionAsked += OnQuestionAsked;
        _files.FileShown += OnFileShown;
        _files.FileEdited += OnFileEdited;

        LoadRecentHistory();
        TrimMessages();
    }

    public string Id { get; }

    public string Title
    {
        get
        {
            lock (_gate) return _title;
        }
    }

    /// <summary>一个有序的会话增量批次；宿主给整个事件加全局 rev 后推给页面。</summary>
    public event Action<ChatOpBatch>? OpsProduced;

    /// <summary>标题/消息数/最后一条用户消息/模型/工作区变化，供会话索引落盘。</summary>
    public event Action<ChatSessionSummary>? SummaryChanged;

    public ChatSessionSnapshot Snapshot
    {
        get
        {
            lock (_gate) return BuildSnapshotLocked();
        }
    }

    /// <summary>
    /// 打开会话时校验工作区目录是否仍然存在，不存在就让会话回落到默认分组。
    /// 这里只做校验：会话的工作区是逻辑工作目录，不改变进程的物理 cwd。
    /// </summary>
    public void Activate()
    {
        if (_workspacePath is { } path && NormalizeDirectory(path) is null)
        {
            _workspacePath = null;
            Logger.Warning("workspace no longer exists, fallback to default id={Session} path={Path}", Id, path);
        }
    }

    /// <summary>把会话存储迁到当前工作区对应的分组目录（由工厂注入的解析器决定）。</summary>
    private void RebaseStorage(string? workspacePath)
    {
        if (_directoryResolver is not { } resolver) return;
        try
        {
            _memory.Rebase(resolver(workspacePath));
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "rebase session storage failed id={Session}", Id);
        }
    }

    internal void SetTitle(string title)
    {
        lock (_gate) _title = string.IsNullOrWhiteSpace(title) ? DefaultTitle : title.Trim();
    }

    /// <summary>发送一条用户消息；校验通过后立即返回，回复通过事件流式推送。</summary>
    public async Task SendAsync(string text, IReadOnlyList<string>? attachmentIds, CancellationToken cancellationToken)
    {
        text = (text ?? string.Empty).Trim();
        cancellationToken.ThrowIfCancellationRequested();

        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ChatModelConfig model;
            List<AttachmentEntry> selected;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_generating) throw new ChatSessionException("conflict", "正在生成回复，请先停止当前回复");
                if (SelectedModelLocked() is not { } found)
                    throw new ChatSessionException("invalid_argument", "请先选择模型");
                if (!IsUsable(found))
                    throw new ChatSessionException("invalid_argument", "当前模型的标识或接口地址无效，请在模型设置里检查");

                selected = SelectAttachmentsLocked(attachmentIds);
                if (text.Length == 0 && selected.Count == 0)
                    throw new ChatSessionException("invalid_argument", "消息不能为空");
                model = found;
            }

            selected = await Task.Run(() => PersistPendingImages(selected), cancellationToken).ConfigureAwait(false);

            string requestText;
            string displayText;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_generating) throw new ChatSessionException("conflict", "正在生成回复，请先停止当前回复");

                // 附件气泡列出文件名；发给模型的正文统一附加 @绝对路径
                var attachmentLines = string.Join("\n", selected.Select(a => $"[附件] {a.Name}"));
                displayText = text.Length == 0
                    ? attachmentLines
                    : selected.Count == 0
                        ? text
                        : $"{text}\n{attachmentLines}";

                var userMessage = NewMessage(ChatMessageKinds.User, displayText);
                _messages.Add(userMessage);

                // 选中的附件随消息消费；未选中的（理论上页面上没有）继续留在草稿区
                var ids = selected.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
                _attachments.RemoveAll(a => ids.Contains(a.Id));
                _draft = string.Empty;
                MaybeUpdateTitleLocked(text);

                var pathReferences = selected.Select(a => $"@{a.Path}");
                requestText = string.Join("\n", new[] { text }.Concat(pathReferences).Where(line => line.Length > 0));

                BeginTurnLocked();
                EmitLocked(new ChatOpMessageAdd(userMessage.ToState()));
                EmitLocked(new ChatOpAttachments(SnapshotAttachmentsLocked()));
            }

            _statistics.AddConversation();
            StartTurn(model, requestText, displayText, isPlatformNotice: false);
            RaiseSummary();
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>排入一轮由桌宠自主发起的对话（平台消息 / 定时任务 / 戳一戳 / 摸头）。</summary>
    public Task EnqueueSystemTurnAsync(string prompt, string display, bool isPlatformNotice)
    {
        if (_disposed) return Task.CompletedTask;

        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;
            if (_pendingTurns.Count >= MaxPendingTurns)
            {
                var oldest = _pendingTurns.Dequeue();
                prompt = $"{oldest.Prompt}\n{prompt}";
                display = $"{oldest.Display}\n{display}";
            }

            _pendingTurns.Enqueue(new PendingTurn(prompt, display, isPlatformNotice));
        }

        StartPendingTurns();
        return Task.CompletedTask;
    }

    /// <summary>戳一戳：系统消息 + 自主回应。</summary>
    public Task WakeByPokeAsync() => EnqueueSystemTurnAsync(
        $"{SystemPrefix}用户戳了你一下。", "👉 你被戳了一下，回应一下吧。", isPlatformNotice: true);

    /// <summary>摸摸头：系统消息 + 自主回应。</summary>
    public Task WakeByHeadPatAsync() => EnqueueSystemTurnAsync(
        $"{SystemPrefix}用户摸了摸你的头。", "🖐 摸摸头", isPlatformNotice: true);

    public Task StopAsync()
    {
        lock (_gate) _cts?.Cancel();
        return Task.CompletedTask;
    }

    public Task SetDraftAsync(string text)
    {
        lock (_gate) _draft = text ?? string.Empty;
        TouchActivity();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ChatAttachmentState>> AddAttachmentsAsync(IReadOnlyList<string> paths)
    {
        var failed = new List<string>();
        lock (_gate)
        {
            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                string full;
                try
                {
                    full = WorkspaceContext.Resolve(path, _workspacePath);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    failed.Add(Path.GetFileName(path));
                    continue;
                }

                var isDirectory = Directory.Exists(full);
                if (!isDirectory && !File.Exists(full))
                {
                    failed.Add(Path.GetFileName(full));
                    continue;
                }

                if (_attachments.Any(a => string.Equals(a.Path, full, StringComparison.OrdinalIgnoreCase))) continue;

                long? size = null;
                try
                {
                    if (!isDirectory) size = new FileInfo(full).Length;
                }
                catch (IOException)
                {
                    // 文件刚被移走：仍然允许添加，发送时模型自己会看到路径不存在
                }

                _attachments.Add(new AttachmentEntry(
                    NewMessageId("a"),
                    isDirectory ? Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) : Path.GetFileName(full),
                    full,
                    IsImagePath(full),
                    null,
                    size));
            }

            if (failed.Count > 0) Logger.Warning("attachments rejected names={Names}", string.Join(",", failed));
            if (_attachments.Count == 0 && failed.Count > 0)
                throw new ChatSessionException("invalid_argument", $"未能添加附件：{string.Join("、", failed)}");

            var state = SnapshotAttachmentsLocked();
            EmitLocked(new ChatOpAttachments(state));
            return Task.FromResult<IReadOnlyList<ChatAttachmentState>>(state);
        }
    }

    /// <summary>粘贴的剪贴板图片：先留在内存，发送时才落盘成 PNG。</summary>
    public Task<ChatAttachmentState> AddPastedImageAsync(byte[] data, string fileName)
    {
        if (data.Length == 0) throw new ChatSessionException("invalid_argument", "剪贴板里没有图片");
        fileName = Path.GetFileName(string.IsNullOrWhiteSpace(fileName) ? "粘贴图片.png" : fileName);
        if (Path.GetExtension(fileName).Length == 0) fileName += ".png";

        lock (_gate)
        {
            _attachments.Add(new AttachmentEntry(NewMessageId("a"), fileName, string.Empty, true, data, data.Length));
            var state = SnapshotAttachmentsLocked();
            EmitLocked(new ChatOpAttachments(state));
            return Task.FromResult(state.Last());
        }
    }

    public Task<IReadOnlyList<ChatAttachmentState>> RemoveAttachmentAsync(string id)
    {
        lock (_gate)
        {
            _attachments.RemoveAll(a => string.Equals(a.Id, id, StringComparison.Ordinal));
            var state = SnapshotAttachmentsLocked();
            EmitLocked(new ChatOpAttachments(state));
            return Task.FromResult<IReadOnlyList<ChatAttachmentState>>(state);
        }
    }

    public Task SelectModelAsync(string key)
    {
        key = key ?? string.Empty;
        lock (_gate)
        {
            if (_settings.ChatModels.All(model => model.Key != key))
                throw new ChatSessionException("not_found", "模型不存在或已被停用");
            _selectedModelKey = key;
            _settings.ChatModelKey = key;
            _settings.Save();
            EmitLocked(new ChatOpModels(SnapshotModelsLocked(), _selectedModelKey));
        }

        return Task.CompletedTask;
    }

    /// <summary>设置页改了提供商/模型后，把新清单推给对话页；选中项失效时回落到默认模型。</summary>
    public void RefreshModels()
    {
        lock (_gate)
        {
            var models = SnapshotModelsLocked();
            if (models.All(model => model.Key != _selectedModelKey))
            {
                _selectedModelKey = models.Any(model => model.Key == _settings.ChatModelKey)
                    ? _settings.ChatModelKey
                    : null;
            }

            EmitLocked(new ChatOpModels(models, _selectedModelKey));
        }
    }

    public Task SetApprovalModeAsync(string mode)
    {
        var parsed = mode == ChatApprovalModes.Full ? ToolApprovalMode.FullAccess : ToolApprovalMode.Manual;
        lock (_gate)
        {
            _settings.ToolApproval = parsed;
            _settings.Save();
            _approvalMode = ApprovalModeString(parsed);
            EmitLocked(new ChatOpApproval(_approvalMode));
        }

        return Task.CompletedTask;
    }

    /// <summary>允许/拒绝一条待审批的工具调用；消息 id 必须对应仍在等待的审批气泡。</summary>
    public Task DecideApprovalAsync(string messageId, bool approve)
    {
        ToolApprovalRequest? request = null;
        lock (_gate)
        {
            if (!string.Equals(_approvalMessageId, messageId, StringComparison.Ordinal) || _approvalRequest is null)
                throw new ChatSessionException("not_found", "这条审批已经处理过了");
            request = _approvalRequest;
        }

        if (approve) request.Approve();
        else request.Reject();
        return Task.CompletedTask;
    }

    /// <summary>切换工作区：目录必须存在；只记录为该会话的逻辑工作目录，不改变进程 cwd。</summary>
    public Task<ChatWorkspaceState?> SwitchWorkspaceAsync(string path)
    {
        string? full = NormalizeDirectory(path);
        if (!string.IsNullOrWhiteSpace(path) && full is null)
            throw new ChatSessionException("invalid_argument", "工作区目录不存在");

        ChatWorkspaceState? workspace;
        IReadOnlyList<ChatWorkspaceState> workspaces;
        string? newWorkspace;
        lock (_gate)
        {
            _workspacePath = full;
            newWorkspace = _workspacePath;
            if (full is not null && !_settings.WorkspacePaths.Any(p =>
                    string.Equals(p, full, StringComparison.OrdinalIgnoreCase)))
                _settings.WorkspacePaths = [.. _settings.WorkspacePaths, full];
            _settings.ActiveWorkspacePath = full ?? string.Empty;
            _settings.Save();
            workspace = ToWorkspace(full);
            workspaces = SnapshotWorkspacesLocked();
            EmitLocked(new ChatOpWorkspace(workspace, workspaces));
        }

        // 会话目录跟着工作区分组走：历史文件一起搬到新分组
        RebaseStorage(newWorkspace);
        RaiseSummary();
        return Task.FromResult(workspace);
    }

    /// <summary>从工作区历史里移除一项；删的是当前工作区时回落到应用目录。</summary>
    public Task<IReadOnlyList<ChatWorkspaceState>> DeleteWorkspaceAsync(string path)
    {
        string? full = NormalizeDirectory(path);
        IReadOnlyList<ChatWorkspaceState> workspaces;
        ChatWorkspaceState? active;
        var cleared = false;
        lock (_gate)
        {
            var key = full ?? path;
            _settings.WorkspacePaths = _settings.WorkspacePaths
                .Where(item => !string.Equals(item, key, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (full is not null)
            {
                var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in _settings.WorkspaceNames)
                {
                    if (string.Equals(entry.Key, full, StringComparison.OrdinalIgnoreCase)) continue;
                    names[entry.Key] = entry.Value;
                }

                _settings.WorkspaceNames = names;
            }

            cleared = full is null
                ? string.Equals(_workspacePath, path, StringComparison.OrdinalIgnoreCase)
                : string.Equals(_workspacePath, full, StringComparison.OrdinalIgnoreCase);
            if (cleared)
            {
                _workspacePath = null;
                _settings.ActiveWorkspacePath = string.Empty;
            }

            _settings.Save();
            workspaces = SnapshotWorkspacesLocked();
            active = ToWorkspace(_workspacePath);
            EmitLocked(new ChatOpWorkspace(active, workspaces));
        }

        // 工作区被移除后会话回到默认分组，历史文件一起搬走
        if (cleared) RebaseStorage(null);
        RaiseSummary();
        return Task.FromResult(workspaces);
    }

    /// <summary>重命名工作区：只改展示名称，磁盘目录与会话分组不变。</summary>
    public Task<IReadOnlyList<ChatWorkspaceState>> RenameWorkspaceAsync(string path, string? name)
    {
        string? full = NormalizeDirectory(path);
        if (full is null) throw new ChatSessionException("invalid_argument", "工作区目录不存在");

        IReadOnlyList<ChatWorkspaceState> workspaces;
        ChatWorkspaceState? active;
        lock (_gate)
        {
            var known = _settings.WorkspacePaths.Any(item =>
                            string.Equals(item, full, StringComparison.OrdinalIgnoreCase))
                        || string.Equals(_workspacePath, full, StringComparison.OrdinalIgnoreCase);
            if (!known) throw new ChatSessionException("invalid_argument", "工作区不在历史列表中");

            var label = (name ?? string.Empty).Trim();
            if (label.Length > WorkspaceNameLength) label = label[..WorkspaceNameLength];

            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in _settings.WorkspaceNames)
            {
                if (string.Equals(entry.Key, full, StringComparison.OrdinalIgnoreCase)) continue;
                names[entry.Key] = entry.Value;
            }

            if (label.Length > 0) names[full] = label;
            _settings.WorkspaceNames = names;
            _settings.Save();

            workspaces = SnapshotWorkspacesLocked();
            active = ToWorkspace(_workspacePath);
            EmitLocked(new ChatOpWorkspace(active, workspaces));
        }

        return Task.FromResult(workspaces);
    }

    /// <summary>处理提问面板的提交/取消；其余导航动作由页面本地完成。</summary>
    public Task SubmitAskAsync(string requestId, string action, JsonElement payload)
    {
        UserQuestionRequest? request;
        lock (_gate)
        {
            if (_askRequest is null || !string.Equals(_ask?.RequestId, requestId, StringComparison.Ordinal))
                throw new ChatSessionException("not_found", "这次提问已经结束");
            request = _askRequest;
        }

        switch (action)
        {
            case "cancel":
                request.Cancel();
                break;
            case "submit":
                var answers = ParseAnswers(payload);
                if (answers.Count == 0)
                    throw new ChatSessionException("invalid_argument", "请先回答全部问题");
                request.Submit(answers);
                break;
            default:
                // prev/next/select/custom 是页面本地的交互，服务端无需处理
                break;
        }

        return Task.CompletedTask;
    }

    public Task ExpandMessageAsync(string messageId, bool expanded)
    {
        MessageState? message;
        lock (_gate) message = _messages.FirstOrDefault(m => m.Id == messageId);
        if (message is null) throw new ChatSessionException("not_found", "消息不存在");
        PatchMessage(message, new ChatMessagePatch(Expanded: expanded));
        return Task.CompletedTask;
    }

    /// <summary>等待当前轮次和排队中的系统对话全部结束（测试用）。</summary>
    public Task WaitForIdleAsync()
    {
        lock (_gate) return _idle?.Task ?? Task.CompletedTask;
    }

    // ===== 会话轮次 =====

    private void StartTurn(
        ChatModelConfig model,
        string messageText,
        string displayText,
        bool isPlatformNotice)
    {
        _turnTask = Task.Run(() => RunTurnSafelyAsync(model, messageText, displayText, isPlatformNotice));
    }

    /// <summary>兜底：RunTurn 在进入自身 try/finally 之前出错时，不能让会话永远停在「生成中」。</summary>
    private async Task RunTurnSafelyAsync(
        ChatModelConfig model,
        string messageText,
        string? displayText,
        bool isPlatformNotice)
    {
        try
        {
            await RunTurnAsync(model, messageText, displayText, isPlatformNotice).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "chat turn failed before stream session={Session}", Id);
            lock (_gate)
            {
                _cts?.Dispose();
                _cts = null;
                _reply = null;
                _generating = false;
                EmitLocked(new ChatOpTurn(false));
            }

            CompleteIdleIfIdle();
        }
    }

    private void BeginTurnLocked()
    {
        _generating = true;
        _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        EmitLocked(new ChatOpTurn(true));
    }

    /// <summary>
    /// 一轮对话：写历史/长期记忆、插入占位气泡、流式消费事件。
    /// 手输发送（用户气泡已插入）与平台消息/定时任务共用这条链路。
    /// </summary>
    private async Task RunTurnAsync(
        ChatModelConfig model,
        string messageText,
        string? displayText,
        bool isPlatformNotice)
    {
        var turnStartedAt = DateTimeOffset.Now;
        var segment = string.Empty;
        string? pendingToolText = null;
        var roundReasoning = new StringBuilder();
        MessageState? thinking = null;
        MessageState? answered = null;
        LlmUsageEvent? usage = null;
        var toolBubbles = new Dictionary<string, MessageState>(StringComparer.Ordinal);
        CancellationToken token;
        string? workspacePath;

        MessageState reply;
        lock (_gate)
        {
            workspacePath = _workspacePath;
            messageText = messageText.Trim();
            var requestText = BuildUserRequestText(messageText);
            _history.Add(new ChatMessage(ChatRole.User, requestText));
            _memory.AddTurn(ChatMemory.UserRole, messageText, display: displayText,
                isPlatformNotice: isPlatformNotice, requestText: requestText);
            TrimHistory();

            reply = NewMessage(ChatMessageKinds.Assistant, string.Empty);
            _reply = reply;
            _messages.Add(reply);
            token = _cts?.Token ?? CancellationToken.None;
            EmitLocked(new ChatOpMessageAdd(reply.ToState()));
        }

        void CollapseThinking()
        {
            if (thinking is not { ReasoningDone: false } bubble) return;
            bubble.ReasoningDone = true;
            bubble.Expanded = false;
            Emit(new ChatOpMessagePatchOp(bubble.Id,
                new ChatMessagePatch(Expanded: false, ReasoningDone: true)));
        }

        bool CommitSegment(bool withReasoning, bool pendingForToolCall = false)
        {
            var hasReasoning = withReasoning && roundReasoning.Length > 0;
            var content = segment.Trim();
            if (content.Length == 0 && !hasReasoning) return false;

            if (pendingForToolCall)
            {
                pendingToolText = content;
                _memory.AddTurn(ChatMemory.AssistantRole, content);
                TrimHistory();
                segment = string.Empty;
                return true;
            }

            List<AIContent> contents = [];
            string? reasoning = null;
            if (hasReasoning)
            {
                reasoning = roundReasoning.ToString().Trim();
                contents.Add(new TextReasoningContent(reasoning));
                roundReasoning.Clear();
            }

            contents.Add(new TextContent(content));
            _history.Add(new ChatMessage(ChatRole.Assistant, contents));
            _memory.AddTurn(ChatMemory.AssistantRole, content, reasoning: reasoning,
                inputTokens: usage?.InputTokens,
                outputTokens: usage?.OutputTokens,
                cachedInputTokens: usage?.CachedInputTokens,
                model: FormatModelLabel(model));
            TrimHistory();
            segment = string.Empty;
            return true;
        }

        // 插入一条工具记录：没有正文时直接插在占位气泡前；
        // 已有正文则截断旧气泡并为后续回复新开一个占位气泡（两段正文之间夹工具记录）。
        void AddToolBubble(MessageState message)
        {
            CollapseThinking();
            thinking = null;

            int toolIndex;
            MessageState? fresh = null;
            int freshIndex = -1;
            lock (_gate)
            {
                var index = _messages.IndexOf(reply);
                if (index < 0) index = _messages.Count;

                if (segment.Trim().Length == 0)
                {
                    // 没有正文：工具记录插在占位气泡前，沿用同一个占位气泡
                    reply.Text = string.Empty;
                    _messages.Insert(index, message);
                    toolIndex = index;
                }
                else
                {
                    CommitSegment(withReasoning: false, pendingForToolCall: true);
                    _messages.Insert(index + 1, message);
                    toolIndex = index + 1;

                    fresh = NewMessage(ChatMessageKinds.Assistant, string.Empty);
                    freshIndex = index + 2;
                    _reply = fresh;
                    reply = fresh;
                    _messages.Insert(freshIndex, fresh);
                }
            }

            Emit(new ChatOpMessageInsert(toolIndex, message.ToState()));
            if (fresh is not null) Emit(new ChatOpMessageInsert(freshIndex, fresh.ToState()));
        }

        try
        {
            CurrentTurn.Value = this;
            // 本轮对话内的相对路径、AGENTS.md、技能目录、命令工作目录都按本会话的工作区解析；
            // 用异步上下文而非进程 cwd，多个会话并发生成时互不干扰
            using var workspaceScope = WorkspaceContext.Enter(workspacePath);
            // 编辑工具覆盖文件前把旧版本备份到本轮会话目录，删除会话时随目录进入 .deleted
            using var backupScope = ToolInvocationContext.EnterBackupDirectory(_memory.SessionDirectory);
            await foreach (var evt in _llm.StreamAsync(model, [.. _history], token)
                               .ConfigureAwait(false))
            {
                switch (evt)
                {
                    case LlmReasoningDelta reasoningDelta:
                        roundReasoning.Append(reasoningDelta.Text);
                        if (segment.Length == 0 && reply.Text.Length > 0)
                        {
                            reply.Text = string.Empty;
                            Emit(new ChatOpMessageText(reply.Id, "set", string.Empty));
                        }

                        if (thinking is null)
                        {
                            int index;
                            lock (_gate)
                            {
                                thinking = NewMessage(ChatMessageKinds.Reasoning, string.Empty,
                                    expanded: false, reasoningDone: false);
                                index = Math.Max(0, _messages.IndexOf(reply));
                                _messages.Insert(index, thinking);
                            }

                            Emit(new ChatOpMessageInsert(index, thinking.ToState()));
                        }

                        thinking.Text += reasoningDelta.Text;
                        Emit(new ChatOpMessageText(thinking.Id, "append", reasoningDelta.Text));
                        break;

                    case LlmTextDelta delta:
                        CollapseThinking();
                        segment += delta.Text;
                        var display = DisplayText(segment);
                        reply.Text = display;
                        answered = reply;
                        Emit(new ChatOpMessageText(reply.Id, "set", display));
                        break;

                    case LlmUsageEvent cumulative:
                        usage = cumulative;
                        break;

                    case LlmToolCallStartedEvent started:
                        var bubble = NewMessage(
                            ChatMessageKinds.Tool,
                            FormatToolStarted(started),
                            tool: new ChatToolState(started.Name, started.Arguments, "执行中…",
                                started.Label ?? ToolDisplay.Describe(started.Name, started.Arguments)));
                        bubble.Expanded = false;
                        toolBubbles[started.CallId] = bubble;
                        AddToolBubble(bubble);
                        break;

                    case LlmToolCallEvent call:
                        var toolText = FormatToolCall(call);
                        var toolLabel = call.Label ?? ToolDisplay.Describe(call.Name, call.Arguments);
                        var toolDiffs = TakePendingDiffs(call.CallId);
                        if (toolBubbles.Remove(call.CallId, out var completed))
                        {
                            completed.Text = toolText;
                            // 执行中气泡可能已经由 Started 事件算过标题，事件没带标题时优先保留它
                            completed.Tool = new ChatToolState(call.Name, call.Arguments, call.Result,
                                call.Label ?? completed.Tool?.Label ?? toolLabel, toolDiffs);
                            Emit(new ChatOpMessageText(completed.Id, "set", toolText));
                            Emit(new ChatOpMessagePatchOp(completed.Id,
                                new ChatMessagePatch(Tool: completed.Tool)));
                        }
                        else
                        {
                            var late = NewMessage(
                                ChatMessageKinds.Tool,
                                toolText,
                                tool: new ChatToolState(call.Name, call.Arguments, call.Result, toolLabel, toolDiffs));
                            late.Expanded = false;
                            AddToolBubble(late);
                        }

                        var toolReasoning = roundReasoning.ToString().Trim() is { Length: > 0 } trimmed
                            ? trimmed
                            : null;
                        roundReasoning.Clear();
                        var assistantText = pendingToolText;
                        pendingToolText = null;
                        _memory.AddTurn(ChatMemory.ToolRole, toolText, call.CallId, call.Name, call.Arguments,
                            call.Result, toolReasoning, toolLabel: toolLabel, diffs: toolDiffs);
                        AddToolHistory(call.CallId, call.Name, call.Arguments, call.Result, toolText,
                            toolReasoning, assistantText);
                        TrimHistory();
                        break;
                }
            }

            if (segment.Length == 0)
            {
                if (roundReasoning.Length > 0)
                {
                    reply.Text = BlankText;
                    Emit(new ChatOpMessageText(reply.Id, "set", BlankText));
                }
                else if (answered is null)
                {
                    reply.Text = "（没有收到回复）";
                    Emit(new ChatOpMessageText(reply.Id, "set", reply.Text));
                }
                else
                {
                    lock (_gate) _messages.Remove(reply);
                    Emit(new ChatOpMessageRemove(reply.Id));
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (segment.Length == 0 && reply.Text.Length == 0)
            {
                reply.Text = "（已停止）";
                Emit(new ChatOpMessageText(reply.Id, "set", reply.Text));
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "chat turn failed session={Session}", Id);
            var error = $"请求失败：{ex.Message}";
            reply.Text = segment.Length == 0 ? error : $"{segment}\n\n[{error}]";
            Emit(new ChatOpMessageText(reply.Id, "set", reply.Text));
        }
        finally
        {
            CurrentTurn.Value = null;
            lock (_gate) _pendingDiffs.Clear();
            CollapseThinking();

            // 停止或出错时仍在「执行中」的工具记录标记为中断
            foreach (var pending in toolBubbles.Values)
            {
                pending.Text += "\n（已中断）";
                pending.Tool = pending.Tool is { } tool
                    ? tool with { Result = tool.Result.Length > 0 ? $"{tool.Result}\n（已中断）" : "（已中断）" }
                    : new ChatToolState(string.Empty, string.Empty, "（已中断）");
                Emit(new ChatOpMessageText(pending.Id, "set", pending.Text));
                Emit(new ChatOpMessagePatchOp(pending.Id, new ChatMessagePatch(Tool: pending.Tool)));
            }

            // 工具调用被打断时，调用前的正文还没并进历史
            if (pendingToolText is { Length: > 0 } leftovers)
            {
                pendingToolText = null;
                var pendingReasoning = roundReasoning.ToString().Trim();
                roundReasoning.Clear();
                List<AIContent> contents = [];
                if (pendingReasoning.Length > 0) contents.Add(new TextReasoningContent(pendingReasoning));
                contents.Add(new TextContent(leftovers));
                _history.Add(new ChatMessage(ChatRole.Assistant, contents));
                TrimHistory();
            }

            var committed = CommitSegment(withReasoning: true);
            _memory.SaveWindowTurns(CountHistoryUserTurns());

            if (usage is not null)
            {
                if (!committed)
                {
                    _memory.AttachUsageToLastTurn(turnStartedAt, usage.InputTokens, usage.OutputTokens,
                        usage.CachedInputTokens, FormatModelLabel(model));
                }

                RecordUsage(model, usage, answered);
            }

            lock (_gate)
            {
                _cts?.Dispose();
                _cts = null;
                _reply = null;
                _generating = false;
                EmitLocked(new ChatOpTurn(false));
            }

            TrimMessages();
            RaiseSummary();
            CompleteIdleIfIdle();
            StartPendingTurns();
        }
    }

    private void RecordUsage(ChatModelConfig model, LlmUsageEvent usage, MessageState? bubble)
    {
        var label = FormatModelLabel(model);
        if (bubble is not null)
        {
            bubble.Usage = new ChatUsageState(usage.InputTokens, usage.OutputTokens, usage.CachedInputTokens,
                label, DateTimeOffset.Now);
            Emit(new ChatOpMessagePatchOp(bubble.Id, new ChatMessagePatch(Usage: bubble.Usage)));
        }

        ChatUsageState cumulative;
        lock (_gate)
        {
            var input = (_usage?.InputTokens ?? 0) + usage.InputTokens;
            var output = (_usage?.OutputTokens ?? 0) + usage.OutputTokens;
            var cached = (_usage?.CachedInputTokens ?? 0) + usage.CachedInputTokens;
            _usage = new ChatUsageState(input, output, cached, label, DateTimeOffset.Now);
            cumulative = _usage;
            EmitLocked(new ChatOpUsage(cumulative));
        }

        _statistics.AddUsage(usage.InputTokens, usage.OutputTokens, usage.CachedInputTokens);
    }

    // ===== 平台消息 / 定时任务 =====

    private void StartPendingTurns()
    {
        lock (_gate)
        {
            if (_disposed || _processingPending || _generating || _pendingTurns.Count == 0) return;
            _processingPending = true;
        }

        _pendingTask = ProcessPendingTurnsAsync();
    }

    private async Task ProcessPendingTurnsAsync()
    {
        try
        {
            while (true)
            {
                PendingTurn turn;
                lock (_gate)
                {
                    if (_disposed || _generating || _pendingTurns.Count == 0) break;
                    turn = _pendingTurns.Dequeue();
                    _generating = true;
                    _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _cts?.Dispose();
                    _cts = new CancellationTokenSource();
                    EmitLocked(new ChatOpTurn(true));

                    var notice = NewMessage(ChatMessageKinds.Notice, turn.Display);
                    _messages.Add(notice);
                    EmitLocked(new ChatOpMessageAdd(notice.ToState()));
                }

                if (SelectedModel is not { } model || !IsUsable(model))
                {
                    lock (_gate)
                    {
                        var hint = NewMessage(ChatMessageKinds.Assistant, "当前没有可用的对话模型，无法执行这条提醒。");
                        _messages.Add(hint);
                        EmitLocked(new ChatOpMessageAdd(hint.ToState()));
                        _generating = false;
                        EmitLocked(new ChatOpTurn(false));
                    }

                    CompleteIdleIfIdle();
                    continue;
                }

                await RunTurnSafelyAsync(model, turn.Prompt, turn.Display, turn.IsPlatformNotice).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_gate) _processingPending = false;
            CompleteIdleIfIdle();
            // 处理期间又来了新的系统对话：继续消费
            StartPendingTurns();
        }
    }

    private void CompleteIdleIfIdle()
    {
        lock (_gate)
        {
            if (_generating || _processingPending || _pendingTurns.Count > 0) return;
            _idle?.TrySetResult();
        }
    }

    /// <summary>提醒气泡里的单行摘要：折叠换行并截断，完整内容仍放在给模型的提示里。</summary>
    public static string PreviewNotice(string content)
    {
        var singleLine = content.ReplaceLineEndings(" ").Trim();
        return singleLine.Length <= PlatformNoticePreviewLength
            ? singleLine
            : singleLine[..PlatformNoticePreviewLength] + "…";
    }

    // ===== 审批 / 提问 / 展示文件 =====

    private void OnApprovalRequested(ToolApprovalRequest request)
    {
        // 事件是多播：非当前轮次的会话必须安静地忽略，绝不能在这里 Cancel，
        // 否则会先把真正发起审批的会话的请求取消掉。
        if (!ReferenceEquals(CurrentTurn.Value, this)) return;
        if (_disposed)
        {
            request.Cancel();
            return;
        }

        int index;
        MessageState message;
        lock (_gate)
        {
            if (!_generating || _approvalRequest is not null)
            {
                request.Cancel();
                return;
            }

            var action = ApprovalAction(request.ToolName);
            message = NewMessage(
                ChatMessageKinds.Approval,
                $"模型请求{action}，等待你的确认",
                approval: new ChatApprovalState(request.ToolName, request.Arguments, Pending: true));

            index = _reply is { } reply ? _messages.IndexOf(reply) : -1;
            if (index < 0) index = _messages.Count;
            _messages.Insert(index, message);
            _approvalRequest = request;
            _approvalMessageId = message.Id;
            EmitLocked(new ChatOpMessageInsert(index, message.ToState()));
        }

        request.Resolved += () => OnApprovalResolved(request);
        if (request.Outcome != ToolApprovalOutcome.Pending) OnApprovalResolved(request);
    }

    private void OnApprovalResolved(ToolApprovalRequest request)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_approvalRequest, request)) return;

            var message = _messages.FirstOrDefault(m => string.Equals(m.Id, _approvalMessageId, StringComparison.Ordinal));
            _approvalRequest = null;
            _approvalMessageId = null;
            if (message?.Approval is not { } approval) return;

            var outcome = request.Outcome switch
            {
                ToolApprovalOutcome.Approved => "approved",
                ToolApprovalOutcome.Rejected => "rejected",
                ToolApprovalOutcome.Canceled => "cancelled",
                _ => approval.Outcome,
            };
            message.Approval = approval with { Pending = false, Outcome = outcome };
            EmitLocked(new ChatOpMessagePatchOp(message.Id, new ChatMessagePatch(Approval: message.Approval)));
        }
    }

    private void OnQuestionAsked(UserQuestionRequest request)
    {
        // 同审批：非当前轮次的会话只忽略，不能替发起会话取消提问。
        if (!ReferenceEquals(CurrentTurn.Value, this)) return;
        if (_disposed)
        {
            request.Cancel();
            return;
        }

        ChatAskState ask;
        lock (_gate)
        {
            if (!_generating)
            {
                request.Cancel();
                return;
            }

            // 同一时间只保留一个面板：旧请求按取消处理，避免它的工具调用一直等
            _askRequest?.Cancel();

            var questions = request.Questions
                .Select(question => new ChatAskQuestionState(
                    question.Text,
                    question.Options.Select(option =>
                        new ChatAskOptionState(option.Text, option.Description)).ToList()))
                .ToList();

            _askRequest = request;
            _ask = new ChatAskState(NewMessageId("ask"), questions);
            ask = _ask;
            EmitLocked(new ChatOpAsk(ask));
        }

        request.Resolved += () => OnQuestionResolved(request);
        if (request.IsResolved) OnQuestionResolved(request);
    }

    private void OnQuestionResolved(UserQuestionRequest request)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_askRequest, request)) return;
            _askRequest = null;
            _ask = null;
            EmitLocked(new ChatOpAsk(null));
        }
    }

    private void OnFileShown(ChatFileDisplay display)
    {
        if (!ReferenceEquals(CurrentTurn.Value, this) || _disposed) return;

        try
        {
            int index;
            long? size = null;
            if (File.Exists(display.FilePath)) size = new FileInfo(display.FilePath).Length;

            var message = NewMessage(
                ChatMessageKinds.Attachment,
                string.Empty,
                attachment: new ChatAttachmentState(NewMessageId("att"), display.FileName,
                    display.FilePath, display.IsImage, null, size));

            lock (_gate)
            {
                index = _reply is { } reply ? _messages.IndexOf(reply) : -1;
                if (index < 0) index = _messages.Count;
                _messages.Insert(index, message);
                EmitLocked(new ChatOpMessageInsert(index, message.ToState()));
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "show local file {Path} failed", display.FilePath);
        }
    }

    private void OnFileEdited(ChatFileEdit edit)
    {
        if (!ReferenceEquals(CurrentTurn.Value, this) || _disposed) return;

        // 工具在返回前记录差异；这里按 callId 缓存，等 LlmToolCallEvent 到达时并入工具气泡
        lock (_gate) _pendingDiffs[edit.CallId] = edit.Diffs;
    }

    private IReadOnlyList<ChatFileDiffState>? TakePendingDiffs(string callId)
    {
        lock (_gate)
        {
            return _pendingDiffs.Remove(callId, out var diffs) && diffs.Count > 0 ? diffs : null;
        }
    }

    private static IReadOnlyList<UserAnswer> ParseAnswers(JsonElement payload)
    {
        var answers = new List<UserAnswer>();
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("answers", out var array)
            || array.ValueKind != JsonValueKind.Array)
            return answers;

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var question = item.TryGetProperty("question", out var q) ? q.GetString() ?? string.Empty : string.Empty;
            var answer = item.TryGetProperty("answer", out var a) ? a.GetString() ?? string.Empty : string.Empty;
            if (answer.Trim().Length > 0) answers.Add(new UserAnswer(question, answer.Trim()));
        }

        return answers;
    }

    // ===== 历史与请求上下文 =====

    /// <summary>启动时把最近几轮对话（含工具调用记录）恢复到界面与请求历史里。</summary>
    private void LoadRecentHistory()
    {
        var recent = _memory.RecentTurns(RestoreWindowTurns(), includeTools: true);
        if (recent.Count == 0) return;

        void AddReasoning(string? reasoning, DateTimeOffset time)
        {
            if (string.IsNullOrEmpty(reasoning)) return;
            _messages.Add(NewMessage(ChatMessageKinds.Reasoning, reasoning,
                expanded: false, reasoningDone: true, time: time));
        }

        string? pendingAssistantText = null;

        for (var i = 0; i < recent.Count; i++)
        {
            var turn = recent[i];
            if (turn.InputTokens is { } accumulatedInput)
            {
                _usage = new ChatUsageState(
                    (_usage?.InputTokens ?? 0) + accumulatedInput,
                    (_usage?.OutputTokens ?? 0) + (turn.OutputTokens ?? 0),
                    (_usage?.CachedInputTokens ?? 0) + (turn.CachedInputTokens ?? 0),
                    turn.Model,
                    turn.Time);
            }

            if (string.Equals(turn.Role, ChatMemory.ToolRole, StringComparison.OrdinalIgnoreCase))
            {
                AddToolHistory(turn.CallId, turn.ToolName, turn.Arguments, turn.Result, turn.Text, turn.Reasoning,
                    pendingAssistantText);
                pendingAssistantText = null;
                AddReasoning(turn.Reasoning, turn.Time);

                var bubble = NewMessage(
                    ChatMessageKinds.Tool,
                    TruncateToolText(turn.Text),
                    expanded: false,
                    time: turn.Time,
                    tool: new ChatToolState(turn.ToolName ?? string.Empty, turn.Arguments ?? string.Empty,
                        turn.Result ?? string.Empty,
                        turn.Label ?? ToolDisplay.Describe(turn.ToolName, turn.Arguments),
                        turn.Diffs));
                if (turn.InputTokens is { } toolInput)
                    bubble.Usage = new ChatUsageState(toolInput, turn.OutputTokens ?? 0,
                        turn.CachedInputTokens ?? 0, turn.Model, turn.Time);
                _messages.Add(bubble);
                continue;
            }

            var isUser = string.Equals(turn.Role, ChatMemory.UserRole, StringComparison.OrdinalIgnoreCase);
            var isPlatformNotice = isUser && turn.IsPlatformNotice;
            var bubbleText = turn.Display ?? turn.Text;

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
                AddReasoning(turn.Reasoning, turn.Time);
            }
            else
            {
                var historyText = isUser ? turn.RequestText ?? turn.Text : turn.Text;
                _history.Add(new ChatMessage(isUser ? ChatRole.User : ChatRole.Assistant, historyText));
            }

            var kind = isPlatformNotice ? ChatMessageKinds.Notice
                : isUser ? ChatMessageKinds.User
                : ChatMessageKinds.Assistant;
            var restored = NewMessage(kind, DisplayText(bubbleText), time: turn.Time);
            if (!isUser && turn.InputTokens is { } input)
                restored.Usage = new ChatUsageState(input, turn.OutputTokens ?? 0,
                    turn.CachedInputTokens ?? 0, turn.Model, turn.Time);
            _messages.Add(restored);
        }
    }

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

    private static string StripTurnContext(string text)
    {
        var index = text.IndexOf("\n\n" + SystemPrefix, StringComparison.Ordinal);
        return index < 0 ? text : text[..index];
    }

    private int CountHistoryUserTurns() =>
        _history.Count(message => message.Role == ChatRole.User);

    private void TrimHistory()
    {
        var before = _history.Count;
        var userTurns = CountHistoryUserTurns();
        var windowTurns = _settings.HistoryWindowTurns;
        var keepTurns = _settings.HistoryKeepTurns;
        if (userTurns <= windowTurns) return;

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
            _memory.SaveWindowTurns(keepTurns);
        }

        if (before != _history.Count)
            Logger.Debug("session history trimmed before={Before} after={After} turns={Turns} keep={Keep}",
                before, _history.Count, userTurns, keepTurns);
    }

    private int RestoreWindowTurns()
    {
        var saved = _memory.WindowTurns;
        return saved > 0 && saved <= _settings.HistoryWindowTurns ? saved : _settings.HistoryWindowTurns;
    }

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

    private static string TruncateToolText(string text)
    {
        const string marker = "\n结果：";
        var index = text.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0) return text;

        var head = text[..(index + marker.Length)];
        var result = text[(index + marker.Length)..];
        return result.Length <= ToolResultMaxLength ? text : head + result[..ToolResultMaxLength] + "…";
    }

    private static string DisplayText(string text) => text.Trim().Length == 0 ? BlankText : text;

    private void TrimMessages()
    {
        lock (_gate)
        {
            if (_messages.Count <= MaxVisibleMessages) return;

            var removed = _messages.Count - MaxVisibleMessages;
            var trimmed = 0;
            for (var i = 0; i < removed; i++)
            {
                var message = _messages[0];
                if (ReferenceEquals(message, _reply)) break;
                _messages.RemoveAt(0);
                EmitLocked(new ChatOpMessageRemove(message.Id));
                trimmed++;
            }

            if (trimmed > 0)
                Logger.Debug("session visible messages trimmed removed={Removed} left={Left}", trimmed, _messages.Count);
        }
    }

    // ===== 快照 =====

    /// <summary>是否正在生成回复（侧栏据此显示加载图标）。字段为 volatile，读取无需再加锁。</summary>
    public bool IsGenerating => _generating;

    private ChatSessionSnapshot BuildSnapshotLocked() => new(
        Id,
        _messages.Select(message => message.ToState()).ToList(),
        _draft,
        SnapshotAttachmentsLocked(),
        _generating,
        SnapshotModelsLocked(),
        _selectedModelKey,
        _approvalMode,
        ToWorkspace(_workspacePath),
        SnapshotWorkspacesLocked(),
        _usage,
        _ask);

    private IReadOnlyList<ChatModelState> SnapshotModelsLocked() =>
        [.. _settings.ChatModels.Select(model => new ChatModelState(
            model.Key, model.Provider, model.Name, model.ModelId))];

    private IReadOnlyList<ChatAttachmentState> SnapshotAttachmentsLocked() =>
        [.. _attachments.Select(entry => entry.ToState())];

    private IReadOnlyList<ChatWorkspaceState> SnapshotWorkspacesLocked()
    {
        var result = new List<ChatWorkspaceState>();
        foreach (var path in _settings.WorkspacePaths)
        {
            if (NormalizeDirectory(path) is not { } full) continue;
            if (result.Any(item => string.Equals(item.Path, full, StringComparison.OrdinalIgnoreCase))) continue;
            result.Add(new ChatWorkspaceState(full, _settings.WorkspaceDisplayName(full)));
        }

        return result;
    }

    private ChatModelConfig? SelectedModelLocked() =>
        _settings.ChatModels.FirstOrDefault(model => model.Key == _selectedModelKey);

    private ChatModelConfig? SelectedModel
    {
        get
        {
            lock (_gate) return SelectedModelLocked();
        }
    }

    private void MaybeUpdateTitleLocked(string text)
    {
        if (!string.Equals(_title, DefaultTitle, StringComparison.Ordinal) && _title.Length > 0) return;
        var line = text.ReplaceLineEndings(" ").Trim();
        if (line.Length == 0) return;
        _title = line.Length <= TitleLength ? line : line[..TitleLength] + "…";
    }

    private void RaiseSummary()
    {
        ChatSessionSummary summary;
        lock (_gate)
        {
            var lastUser = _messages.LastOrDefault(message => message.Kind == ChatMessageKinds.User);
            summary = new ChatSessionSummary(
                _title,
                _workspacePath,
                DateTimeOffset.Now,
                _messages.Count,
                lastUser?.Text,
                _selectedModelKey);
        }

        try
        {
            SummaryChanged?.Invoke(summary);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "session summary subscriber failed id={Session}", Id);
        }
    }

    // ===== 工具方法 =====

    private void Emit(ChatOp op) => Emit(new[] { op });

    private void Emit(IReadOnlyList<ChatOp> ops) => Emit(new ChatOpBatch(Id, ops));

    private void Emit(ChatOpBatch batch)
    {
        if (batch.Ops.Count == 0) return;
        TouchActivity();
        try
        {
            OpsProduced?.Invoke(batch);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "chat op subscriber failed session={Session}", Id);
        }
    }

    private void TouchActivity() => Volatile.Write(ref _lastActivityMilliseconds, Environment.TickCount64);

    /// <summary>最近一次活动时间（毫秒时间戳），供管理器卸载闲置会话。</summary>
    internal long LastActivityMilliseconds => Volatile.Read(ref _lastActivityMilliseconds);

    /// <summary>是否正在生成/处理排队消息，忙碌的会话不能卸载。</summary>
    internal bool IsBusy
    {
        get
        {
            lock (_gate)
            {
                return _generating || _processingPending || _pendingTurns.Count > 0
                    || _turnTask is { IsCompleted: false } || _pendingTask is { IsCompleted: false };
            }
        }
    }

    /// <summary>是否有未发送的草稿/附件；卸载会丢失这些输入，因此跳过。</summary>
    internal bool HasUnsavedInput
    {
        get
        {
            lock (_gate) return _draft.Length > 0 || _attachments.Count > 0;
        }
    }

    private void EmitLocked(ChatOp op) => Emit(op);

    private void PatchMessage(MessageState message, ChatMessagePatch patch)
    {
        lock (_gate) message.Apply(patch);
        Emit(new ChatOpMessagePatchOp(message.Id, patch));
    }

    private List<AttachmentEntry> SelectAttachmentsLocked(IReadOnlyList<string>? ids)
    {
        if (ids is null) return [.. _attachments];
        var set = ids.ToHashSet(StringComparer.Ordinal);
        return [.. _attachments.Where(attachment => set.Contains(attachment.Id))];
    }

    private List<AttachmentEntry> PersistPendingImages(List<AttachmentEntry> attachments)
    {
        var result = new List<AttachmentEntry>(attachments.Count);
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
                var path = Path.Combine(AttachmentDirectory, attachment.Name);
                for (var index = 2; File.Exists(path); index++)
                {
                    path = Path.Combine(AttachmentDirectory,
                        $"{Path.GetFileNameWithoutExtension(attachment.Name)}-{index}.png");
                }

                File.WriteAllBytes(path, data);
                result.Add(attachment with
                {
                    Path = path,
                    Name = Path.GetFileName(path),
                    Data = null,
                    Size = data.Length,
                });
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "save pasted image {Name} failed", attachment.Name);
            }
        }

        return result;
    }

    private static bool IsImagePath(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".bmp";

    private static string? NormalizeDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
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

    private ChatWorkspaceState? ToWorkspace(string? path) =>
        path is { Length: > 0 } ? new ChatWorkspaceState(path, _settings.WorkspaceDisplayName(path)) : null;

    private static bool IsUsable(ChatModelConfig model) =>
        !string.IsNullOrWhiteSpace(model.ModelId)
        && Uri.TryCreate(model.BaseUrl.Trim(), UriKind.Absolute, out _);

    private static string FormatModelLabel(ChatModelConfig model) =>
        string.IsNullOrWhiteSpace(model.Provider)
            ? model.ModelId
            : $"{model.Provider.Trim()}/{model.ModelId}";

    private static string FormatToolStarted(LlmToolCallStartedEvent call)
    {
        var arguments = PreviewArguments(call.Arguments);
        return $"调用工具 {call.Name}{arguments}\n执行中…";
    }

    private static string FormatToolCall(LlmToolCallEvent call)
    {
        var arguments = PreviewArguments(call.Arguments);
        var result = call.Result.Length > ToolResultMaxLength
            ? call.Result[..ToolResultMaxLength] + "…"
            : call.Result;
        return $"调用工具 {call.Name}{arguments}\n结果：{result}";
    }

    /// <summary>工具参数的展示预览：编辑类工具可能带上整份文件内容，不能原样塞进消息文本。</summary>
    private static string PreviewArguments(string arguments)
    {
        if (arguments.Length == 0) return string.Empty;
        var preview = arguments.Length <= ToolArgumentsPreviewLength
            ? arguments
            : arguments[..ToolArgumentsPreviewLength] + "…";
        return $" {preview}";
    }

    /// <summary>审批气泡里的动作文案：不同工具用不同动词，未知工具回退到通用说法。</summary>
    private static string ApprovalAction(string toolName)
    {
        // 插件工具重名时宿主会加「插件id__」前缀，判断动作时只取原工具名
        var separator = toolName.LastIndexOf("__", StringComparison.Ordinal);
        var name = separator >= 0 ? toolName[(separator + 2)..] : toolName;
        return name switch
        {
            "run_shell_command" => "执行命令",
            "write_file" => "写入文件",
            "edit_file" => "编辑文件",
            "send_attachment" => "发送文件",
            "create_scheduled_task" => "新建定时任务",
            "update_scheduled_task" => "修改定时任务",
            "delete_scheduled_task" => "删除定时任务",
            "mouse_click" or "mouse_drag" => "操作鼠标",
            "keyboard_press" or "keyboard_hotkey" or "keyboard_type" => "发送键盘输入",
            "browser_click" or "browser_type" or "browser_press_key" => "操作网页",
            _ => "调用工具",
        };
    }

    private static string ApprovalModeString(ToolApprovalMode mode) =>
        mode == ToolApprovalMode.FullAccess ? ChatApprovalModes.Full : ChatApprovalModes.Manual;

    private static string NewMessageId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static MessageState NewMessage(
        string kind,
        string text,
        bool? expanded = null,
        bool? reasoningDone = null,
        ChatToolState? tool = null,
        ChatAttachmentState? attachment = null,
        ChatApprovalState? approval = null,
        DateTimeOffset? time = null) =>
        new()
        {
            Id = NewMessageId("m"),
            Kind = kind,
            Text = text,
            Time = time ?? DateTimeOffset.Now,
            Expanded = expanded,
            ReasoningDone = reasoningDone,
            Tool = tool,
            Attachment = attachment,
            Approval = approval,
        };

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _cts?.Cancel();
        }

        _toolAccess.ApprovalRequested -= OnApprovalRequested;
        _questions.QuestionAsked -= OnQuestionAsked;
        _files.FileShown -= OnFileShown;
        _files.FileEdited -= OnFileEdited;

        if (_turnTask is { } task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "chat turn ended during dispose session={Session}", Id);
            }
        }

        if (_pendingTask is { } pending)
        {
            try
            {
                await pending.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "pending turn ended during dispose session={Session}", Id);
            }
        }
    }

    private readonly record struct PendingTurn(string Prompt, string Display, bool IsPlatformNotice);

    private sealed record AttachmentEntry(
        string Id,
        string Name,
        string Path,
        bool IsImage,
        byte[]? Data,
        long? Size)
    {
        public ChatAttachmentState ToState() => new(Id, Name, Path, IsImage, null, Size);
    }

    private sealed class MessageState
    {
        public required string Id { get; init; }

        public required string Kind { get; set; }

        public required string Text { get; set; }

        public DateTimeOffset Time { get; init; }

        public bool? Expanded { get; set; }

        public bool? Pending { get; set; }

        public bool? ReasoningDone { get; set; }

        public ChatToolState? Tool { get; set; }

        public ChatUsageState? Usage { get; set; }

        public ChatAttachmentState? Attachment { get; set; }

        public ChatApprovalState? Approval { get; set; }

        public void Apply(ChatMessagePatch patch)
        {
            if (patch.Kind is not null) Kind = patch.Kind;
            if (patch.Text is not null) Text = patch.Text;
            if (patch.Expanded is { } expanded) Expanded = expanded;
            if (patch.Pending is { } pending) Pending = pending;
            if (patch.ReasoningDone is { } reasoningDone) ReasoningDone = reasoningDone;
            if (patch.Tool is not null) Tool = patch.Tool;
            if (patch.Usage is not null) Usage = patch.Usage;
            if (patch.Attachment is not null) Attachment = patch.Attachment;
            if (patch.Approval is not null) Approval = patch.Approval;
        }

        public ChatMessageState ToState() => new(
            Id,
            Kind,
            Text,
            Time,
            Expanded,
            Pending,
            ReasoningDone,
            Tool,
            Usage,
            Attachment,
            Approval);
    }
}