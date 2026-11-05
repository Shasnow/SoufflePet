// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using SoufflePet.Core.Chat;
using SoufflePet.Models;
using SoufflePet.Services;

namespace SoufflePet.Services.Web;

/// <summary>AppWebWindow 暴露给桥的窗口命令（拖拽/缩放/三键/状态/文件与目录选择/剪贴板）。</summary>
public interface IAppWindowCommands
{
    void BeginDrag();
    void BeginResize(string edge);
    void Minimize();
    void ToggleMaximize();
    void HideWindow();
    WindowStateDto GetWindowState();

    /// <summary>打开系统文件选择器；用户取消时返回空列表。</summary>
    Task<IReadOnlyList<string>> PickFilesAsync();

    /// <summary>打开系统文件选择器（单个文件 + 扩展名过滤）；用户取消时返回 null。</summary>
    Task<string?> PickFileAsync(string title, IReadOnlyList<string> extensions);

    /// <summary>打开系统目录选择器；用户取消时返回 null。</summary>
    Task<string?> PickFolderAsync(string title);

    /// <summary>读取剪贴板里的图片（PNG 字节）；没有图片时返回 null。</summary>
    Task<ClipboardImage?> ReadClipboardImageAsync();
}

public sealed record ClipboardImage(byte[] Data, string FileName);

/// <summary>
/// 真实桥实现（M4 起）：会话/对话走 Core 的 ChatSessionManager / ChatSession，
/// 设置面板仍由 M2 的外观接口 + M5 的 SettingsApi 补齐；窗口命令与主题不变。
/// </summary>
public sealed class ChatRpc : IWebRpcHandler, IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<ChatRpc>();

    /// <summary>可直接交给 ShellExecute 的高风险扩展名：聊天附件里即使出现也拒绝打开。</summary>
    private static readonly HashSet<string> BlockedOpenExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".msi", ".msp", ".scr", ".pif", ".cpl", ".dll", ".sys",
        ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".reg", ".lnk",
        ".url", ".jar", ".application", ".appref-ms",
    };

    private readonly IAppWindowCommands _window;
    private readonly ChatSessionManager _sessions;
    private readonly SouffleSettings _settings;
    private readonly ThemeService _theme;
    private readonly IStatisticsService _statistics;
    private readonly ILocalFileServer _files;
    private readonly IMcpToolService _mcpTools;
    private readonly IMcpConfigStore _mcpConfig;
    private readonly SettingsApi _settingsApi;
    private bool _disposed;

    public ChatRpc(
        IAppWindowCommands window,
        ChatSessionManager sessions,
        SouffleSettings settings,
        ThemeService theme,
        IStatisticsService statistics,
        ILocalFileServer files,
        IMcpToolService mcpTools,
        IMcpConfigStore mcpConfig,
        SettingsApi settingsApi)
    {
        _window = window;
        _sessions = sessions;
        _settings = settings;
        _theme = theme;
        _statistics = statistics;
        _files = files;
        _mcpTools = mcpTools;
        _mcpConfig = mcpConfig;
        _settingsApi = settingsApi;

        _sessions.OpsProduced += OnSessionOps;
        _sessions.SessionsChanged += OnSessionsChanged;
    }

    public string Role => "app";

    public event Action<string, object?>? EventProduced;

    public AppSnapshotDto BuildSnapshot()
    {
        var active = _sessions.ActiveSession;
        var chat = active is null ? null : MapChat(active.Snapshot);
        var records = _sessions.Sessions;

        return new AppSnapshotDto(
            1,
            Role,
            active is null ? "#/chat" : $"#/chat/{active.Id}",
            new PetDto(_settings.PetName, InitialOf(_settings.PetName)),
            _theme.GetTheme(),
            ToGlobalUsage(_statistics.GlobalUsage),
            MapSessions(records),
            _sessions.ActiveSessionId,
            chat);
    }

    public async Task<object?> HandleAsync(
        string method,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        try
        {
            if (method.StartsWith("settings.", StringComparison.Ordinal))
            {
                var result = await _settingsApi.HandleAsync(method, parameters, cancellationToken).ConfigureAwait(false);

                // 提供商/模型清单变化后，对话页的模型选择器要立即跟上（不必等下一次快照）
                if (method.StartsWith("settings.llm.", StringComparison.Ordinal))
                    _sessions.ActiveSession?.RefreshModels();
                return result;
            }

            switch (method)
            {
                case "window.drag":
                    _window.BeginDrag();
                    return null;
                case "window.resize":
                {
                    var edge = GetString(parameters, "edge") ?? string.Empty;
                    if (edge is not ("l" or "r" or "t" or "b" or "lt" or "rt" or "lb" or "rb"))
                        throw new BridgeRpcException("invalid_argument", "未知的缩放方向");
                    _window.BeginResize(edge);
                    return null;
                }
                case "window.minimize":
                    _window.Minimize();
                    return null;
                case "window.maximize":
                    _window.ToggleMaximize();
                    return null;
                case "window.close":
                    _window.HideWindow();
                    return null;
                case "window.state":
                    return _window.GetWindowState();

                case "session.list":
                    return MapSessions(_sessions.Sessions);
                case "session.search":
                    return MapSessions(await _sessions.SearchAsync(
                        GetString(parameters, "query") ?? string.Empty, cancellationToken).ConfigureAwait(false));
                case "session.new":
                    return MapSession(await _sessions.CreateAsync(
                        GetString(parameters, "workspacePath"), cancellationToken).ConfigureAwait(false));
                case "session.open":
                {
                    var session = await _sessions.OpenAsync(
                        RequireString(parameters, "sessionId"), cancellationToken).ConfigureAwait(false);
                    return MapChat(session.Snapshot);
                }
                case "session.rename":
                    await _sessions.RenameAsync(
                        RequireString(parameters, "sessionId"),
                        RequireString(parameters, "title"),
                        cancellationToken).ConfigureAwait(false);
                    return MapSessions(_sessions.Sessions)
                        .First(record => record.Id == RequireString(parameters, "sessionId"));
                case "session.delete":
                {
                    var payload = await _sessions.DeleteAsync(
                        RequireString(parameters, "sessionId"), cancellationToken).ConfigureAwait(false);
                    return new { sessions = MapSessions(payload.Sessions), activeId = payload.ActiveId };
                }

                case "chat.send":
                {
                    var session = RequireSession();
                    var ids = GetStringArray(parameters, "attachmentIds");
                    await session.SendAsync(GetString(parameters, "text") ?? string.Empty, ids, cancellationToken)
                        .ConfigureAwait(false);
                    return null;
                }
                case "chat.stop":
                    await RequireSession().StopAsync().ConfigureAwait(false);
                    return null;
                case "chat.draft":
                    await RequireSession().SetDraftAsync(GetString(parameters, "text") ?? string.Empty)
                        .ConfigureAwait(false);
                    return null;
                case "chat.attach.pick":
                {
                    var session = RequireSession();
                    var paths = await _window.PickFilesAsync().ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (paths.Count > 0) await session.AddAttachmentsAsync(paths).ConfigureAwait(false);
                    return new { attachments = MapAttachments(session.Snapshot.Attachments) };
                }
                case "chat.attach.paste":
                {
                    var session = RequireSession();
                    var image = await _window.ReadClipboardImageAsync().ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (image is null) throw new BridgeRpcException("invalid_argument", "剪贴板里没有图片");
                    await session.AddPastedImageAsync(image.Data, image.FileName).ConfigureAwait(false);
                    return new { attachments = MapAttachments(session.Snapshot.Attachments) };
                }
                case "chat.attach.remove":
                {
                    var session = RequireSession();
                    var list = await session.RemoveAttachmentAsync(RequireString(parameters, "id"))
                        .ConfigureAwait(false);
                    return new { attachments = MapAttachments(list) };
                }
                case "chat.model.select":
                    await RequireSession().SelectModelAsync(RequireString(parameters, "key")).ConfigureAwait(false);
                    return null;
                case "chat.approval.mode":
                {
                    var mode = GetString(parameters, "mode");
                    if (mode is not ("manual" or "full"))
                        throw new BridgeRpcException("invalid_argument", "审批模式只能是 manual 或 full");
                    await RequireSession().SetApprovalModeAsync(mode).ConfigureAwait(false);
                    return null;
                }
                case "chat.approval.decide":
                    await RequireSession().DecideApprovalAsync(
                        RequireString(parameters, "messageId"),
                        GetBool(parameters, "approve")).ConfigureAwait(false);
                    return null;
                case "chat.workspace.switch":
                {
                    var session = RequireSession();
                    var workspace = await session.SwitchWorkspaceAsync(
                        GetString(parameters, "path") ?? string.Empty).ConfigureAwait(false);
                    ResetMcpIfNeeded();
                    return new
                    {
                        workspace = MapWorkspace(workspace),
                        workspaces = MapWorkspaces(session.Snapshot.Workspaces),
                    };
                }
                case "chat.workspace.pick":
                {
                    var path = await _window.PickFolderAsync("选择工作区").ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    return path is null ? null : MapWorkspace(new ChatWorkspaceState(path, WorkspaceName(path)));
                }
                case "chat.workspace.delete":
                {
                    var session = RequireSession();
                    var workspaces = await session.DeleteWorkspaceAsync(
                        GetString(parameters, "path") ?? string.Empty).ConfigureAwait(false);
                    return new { workspaces = MapWorkspaces(workspaces) };
                }
                case "chat.ask":
                {
                    var payload = parameters.ValueKind == JsonValueKind.Object
                        && parameters.TryGetProperty("payload", out var value)
                            ? value.Clone()
                            : default;
                    await RequireSession().SubmitAskAsync(
                        RequireString(parameters, "requestId"),
                        RequireString(parameters, "action"),
                        payload).ConfigureAwait(false);
                    return null;
                }
                case "chat.msg.expand":
                    await RequireSession().ExpandMessageAsync(
                        RequireString(parameters, "messageId"),
                        GetBool(parameters, "expanded")).ConfigureAwait(false);
                    return null;
                case "chat.file.open":
                    OpenFile(RequireString(parameters, "path"));
                    return null;
                case "chat.link.open":
                    OpenLink(RequireString(parameters, "url"));
                    return null;

                default:
                    throw new BridgeRpcException("unknown_method", $"未知方法 {method}");
            }
        }
        catch (ChatSessionException ex)
        {
            throw new BridgeRpcException(ex.Code, ex.Message);
        }
    }

    private ChatSession RequireSession() =>
        _sessions.ActiveSession ?? throw new BridgeRpcException("not_found", "当前没有活动会话");

    private void OnSessionOps(ChatOpBatch batch) =>
        Raise("chat.ops", new { sessionId = batch.SessionId, ops = MapOps(batch.Ops) });

    private void OnSessionsChanged(ChatSessionListPayload payload) =>
        Raise("session.updated", new { sessions = MapSessions(payload.Sessions), activeId = payload.ActiveId });

    private void Raise(string name, object? payload)
    {
        if (_disposed) return;
        try
        {
            EventProduced?.Invoke(name, payload);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "raise chat event {Name} failed", name);
        }
    }

    // ===== DTO 映射 =====

    private ChatSnapshotDto MapChat(ChatSessionSnapshot snapshot) => new(
        snapshot.SessionId,
        snapshot.Messages.Select(MapMessage).ToList(),
        snapshot.Draft,
        MapAttachments(snapshot.Attachments),
        snapshot.Generating,
        snapshot.Models.Select(model => new ChatModelDto(model.Key, model.Provider, model.Name, model.ModelId))
            .ToList(),
        snapshot.SelectedModelKey,
        snapshot.ApprovalMode,
        MapWorkspace(snapshot.Workspace),
        MapWorkspaces(snapshot.Workspaces),
        MapUsage(snapshot.Usage),
        MapAsk(snapshot.Ask));

    private ChatMessageDto MapMessage(ChatMessageState message) => new(
        message.Id,
        message.Kind,
        message.Text,
        message.Time.ToString("O"),
        message.Expanded,
        message.Pending,
        message.ReasoningDone,
        message.Tool is { } tool ? new ToolDetailDto(tool.Name, tool.Args, tool.Result) : null,
        MapUsage(message.Usage),
        MapAttachment(message.Attachment),
        message.Approval is { } approval
            ? new ApprovalDetailDto(approval.ToolName, approval.Detail, approval.Pending, approval.Outcome)
            : null);

    private IReadOnlyList<AttachmentDto> MapAttachments(IReadOnlyList<ChatAttachmentState> attachments) =>
        [.. attachments.Select(attachment => MapAttachment(attachment)!)];

    private AttachmentDto? MapAttachment(ChatAttachmentState? attachment)
    {
        if (attachment is null) return null;

        string? url = null;
        if (attachment.IsImage && attachment.Path.Length > 0)
        {
            try
            {
                if (File.Exists(attachment.Path))
                    url = $"/local-file?token={_files.RegisterFile(attachment.Path)}";
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "register image url failed path={Path}", attachment.Path);
            }
        }

        return new AttachmentDto(attachment.Id, attachment.Name, attachment.Path, attachment.IsImage, url,
            attachment.Size);
    }

    private static UsageDto? MapUsage(ChatUsageState? usage) => usage is null
        ? null
        : new UsageDto(usage.InputTokens, usage.OutputTokens, usage.CachedInputTokens, usage.Model,
            usage.Time?.ToString("O"));

    private static AskStateDto? MapAsk(ChatAskState? ask) => ask is null
        ? null
        : new AskStateDto(
            ask.RequestId,
            ask.Questions.Select(question => new AskQuestionDto(
                question.Text,
                question.Options.Select(option => new AskOptionDto(option.Text, option.Description)).ToList()))
                .ToList());

    private static WorkspaceDto? MapWorkspace(ChatWorkspaceState? workspace) =>
        workspace is null ? null : new WorkspaceDto(workspace.Path, workspace.Name);

    private static IReadOnlyList<WorkspaceDto> MapWorkspaces(IReadOnlyList<ChatWorkspaceState> workspaces) =>
        [.. workspaces.Select(workspace => new WorkspaceDto(workspace.Path, workspace.Name))];

    private static SessionSummaryDto MapSession(SessionRecord record) => new(
        record.Id,
        record.Title,
        record.WorkspacePath,
        record.WorkspacePath is { Length: > 0 } path ? WorkspaceName(path) : null,
        record.CreatedAt.ToString("O"),
        record.UpdatedAt.ToString("O"),
        record.MessageCount,
        record.LastUserText,
        record.ModelKey);

    private static IReadOnlyList<SessionSummaryDto> MapSessions(IReadOnlyList<SessionRecord> records) =>
        [.. records.Select(MapSession)];

    private IReadOnlyList<object> MapOps(IReadOnlyList<ChatOp> ops) => [.. ops.Select(MapOp)];

    private object MapOp(ChatOp op) => op switch
    {
        ChatOpMessageAdd add => new { op = "msg:add", msg = MapMessage(add.Msg) },
        ChatOpMessageInsert insert => new { op = "msg:insert", index = insert.Index, msg = MapMessage(insert.Msg) },
        ChatOpMessageRemove remove => new { op = "msg:remove", id = remove.Id },
        ChatOpMessageText text => new { op = "msg:text", id = text.Id, mode = text.Mode, text = text.Text },
        ChatOpMessagePatchOp patch => new { op = "msg:patch", id = patch.Id, patch = MapPatch(patch.Patch) },
        ChatOpTurn turn => new { op = "turn", generating = turn.Generating },
        ChatOpAttachments attachments => new { op = "attachments", attachments = MapAttachments(attachments.Attachments) },
        ChatOpApproval approval => new { op = "approval", mode = approval.Mode },
        ChatOpWorkspace workspace => new
        {
            op = "workspace",
            workspace = MapWorkspace(workspace.Workspace),
            workspaces = MapWorkspaces(workspace.Workspaces),
        },
        ChatOpModels models => new
        {
            op = "models",
            models = models.Models.Select(model => new ChatModelDto(model.Key, model.Provider, model.Name,
                model.ModelId)).ToList(),
            selectedModelKey = models.SelectedModelKey,
        },
        ChatOpAsk ask => new { op = "ask", ask = MapAsk(ask.Ask) },
        ChatOpUsage usage => new { op = "usage", usage = MapUsage(usage.Usage) },
        _ => new { op = "noop" },
    };

    private Dictionary<string, object?> MapPatch(ChatMessagePatch patch)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (patch.Kind is not null) result["kind"] = patch.Kind;
        if (patch.Text is not null) result["text"] = patch.Text;
        if (patch.Expanded is { } expanded) result["expanded"] = expanded;
        if (patch.Pending is { } pending) result["pending"] = pending;
        if (patch.ReasoningDone is { } reasoningDone) result["reasoningDone"] = reasoningDone;
        if (patch.Tool is { } tool) result["tool"] = new ToolDetailDto(tool.Name, tool.Args, tool.Result);
        if (patch.Usage is { } usage) result["usage"] = MapUsage(usage);
        if (patch.Attachment is { } attachment) result["attachment"] = MapAttachment(attachment);
        if (patch.Approval is { } approval)
            result["approval"] = new ApprovalDetailDto(approval.ToolName, approval.Detail, approval.Pending,
                approval.Outcome);
        return result;
    }

    // ===== 工作区 / MCP =====

    private void ResetMcpIfNeeded()
    {
        if (!HasCwdDependentMcpServer()) return;
        _ = ResetMcpConnectionsAsync();
    }

    private bool HasCwdDependentMcpServer() => _mcpConfig.Load().Any(server =>
        server is { Enabled: true, Transport: McpTransportType.Stdio }
        && string.IsNullOrWhiteSpace(server.WorkingDirectory));

    private async Task ResetMcpConnectionsAsync()
    {
        try
        {
            await _mcpTools.ResetAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "reset MCP connections after workspace switch failed");
        }
    }

    private static string WorkspaceName(string fullPath)
    {
        var trimmed = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return name.Length > 0 ? name : fullPath;
    }

    private static GlobalUsageDto ToGlobalUsage(GlobalUsageSnapshot usage) => new(
        usage.InputTokens,
        usage.OutputTokens,
        usage.CachedInputTokens,
        usage.ConversationCount,
        usage.PokeCount,
        usage.HeadPatCount,
        usage.RunSeconds);

    private static string InitialOf(string name) =>
        string.IsNullOrWhiteSpace(name) ? "舒" : name.Trim()[..1];

    // ===== 参数解析 =====

    private static string? GetString(JsonElement parameters, string name) =>
        parameters.ValueKind == JsonValueKind.Object
        && parameters.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string RequireString(JsonElement parameters, string name) =>
        GetString(parameters, name) ?? throw new BridgeRpcException("invalid_argument", $"缺少参数 {name}");

    private static bool GetBool(JsonElement parameters, string name) =>
        parameters.ValueKind == JsonValueKind.Object
        && parameters.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.True;

    private static IReadOnlyList<string>? GetStringArray(JsonElement parameters, string name)
    {
        if (parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Array)
            return null;

        return [.. value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .Where(item => item.Length > 0)];
    }

    /// <summary>
    /// 只允许打开当前会话已登记的附件，或当前会话工作区内的文件。
    /// 这是网页层唯一能触发本机 ShellExecute 的入口，必须比 settings/文件选择收得更窄。
    /// </summary>
    private void OpenFile(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            throw new BridgeRpcException("invalid_argument", $"路径无效：{ex.Message}");
        }

        if (!IsOpenablePath(full))
            throw new BridgeRpcException("forbidden", "只能打开当前会话的附件或工作区内的文件");
        if (BlockedOpenExtensions.Contains(Path.GetExtension(full)))
            throw new BridgeRpcException("forbidden", "出于安全考虑，不能直接打开可执行/脚本文件");
        if (!File.Exists(full)) throw new BridgeRpcException("not_found", "文件不存在");
        try
        {
            Process.Start(new ProcessStartInfo(full) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            throw new BridgeRpcException("io_error", $"打开文件失败：{ex.Message}");
        }
    }

    private bool IsOpenablePath(string full)
    {
        var session = _sessions.ActiveSession;
        if (session is null) return false;

        var snapshot = session.Snapshot;
        foreach (var attachment in snapshot.Attachments)
        {
            if (SamePath(full, attachment.Path)) return true;
        }

        foreach (var message in snapshot.Messages)
        {
            if (message.Attachment is { } attachment && SamePath(full, attachment.Path)) return true;
        }

        if (snapshot.Workspace is { } current && IsUnder(full, current.Path)) return true;
        foreach (var workspace in snapshot.Workspaces)
        {
            if (IsUnder(full, workspace.Path)) return true;
        }

        return false;
    }

    private static bool SamePath(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsUnder(string full, string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        try
        {
            var normalizedRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (normalizedRoot.Length == 0) return false;
            return string.Equals(full, normalizedRoot, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void OpenLink(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new BridgeRpcException("invalid_argument", "只允许 http/https 链接");

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            throw new BridgeRpcException("io_error", $"打开链接失败：{ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sessions.OpsProduced -= OnSessionOps;
        _sessions.SessionsChanged -= OnSessionsChanged;
    }
}