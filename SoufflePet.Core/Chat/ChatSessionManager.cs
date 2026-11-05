// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using SoufflePet.Models;
using SoufflePet.Services;

namespace SoufflePet.Core.Chat;

/// <summary>
/// 会话工厂：把无 UI 的会话状态机与 Core 服务装配到一起。每个会话独立对话文件与窗口状态，
/// 用户档案（profile.json）在所有会话之间共享。
/// </summary>
public sealed class ChatSessionFactory(
    SouffleSettings settings,
    ILlmChatService llm,
    ChatToolAccess toolAccess,
    UserQuestionService questions,
    ChatFileService files,
    IStatisticsService statistics)
{
    internal ChatSession Create(SessionRecord record, ChatSessionStore store) => new(
        record.Id,
        store.SessionDirectory(record.Id),
        store.ProfilePath,
        record.Title,
        record.WorkspacePath,
        record.ModelKey,
        settings,
        llm,
        toolAccess,
        questions,
        files,
        statistics);
}

/// <summary>
/// 多会话管理：sessions.json 索引、新建/打开/重命名/删除/搜索，并把所有会话的事件汇聚成一个出口。
/// 打开会话不阻塞 UI（读取历史放到线程池）；活动会话在构造时同步预载，保证首个快照就有内容。
/// </summary>
public sealed class ChatSessionManager : IAsyncDisposable, IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<ChatSessionManager>();

    private readonly object _gate = new();
    private readonly ChatSessionStore _store;
    private readonly ChatSessionFactory _factory;
    private readonly SouffleSettings _settings;
    private readonly Dictionary<string, ChatSession> _open = new(StringComparer.Ordinal);
    private readonly List<SessionRecord> _records;
    private string? _activeId;
    private bool _disposed;

    public ChatSessionManager(SouffleSettings settings, ChatSessionFactory factory)
        : this(settings, factory, ChatSessionStore.CreateDefault())
    {
    }

    internal ChatSessionManager(SouffleSettings settings, ChatSessionFactory factory, ChatSessionStore store)
    {
        _settings = settings;
        _factory = factory;
        _store = store;

        var index = _store.Load();
        _records = index.Sessions.ToList();

        var preferred = !string.IsNullOrWhiteSpace(settings.UiLastSessionId)
            && _records.Any(record => string.Equals(record.Id, settings.UiLastSessionId, StringComparison.Ordinal))
                ? settings.UiLastSessionId
                : index.ActiveSessionId;

        _activeId = _records.Any(record => string.Equals(record.Id, preferred, StringComparison.Ordinal))
            ? preferred
            : _records.OrderByDescending(record => record.UpdatedAt).First().Id;

        // 首个快照必须有聊天内容：活动会话在构造期同步预载（只读一次最近窗口）
        var active = OpenCore(_activeId!);
        active?.Activate();
        PersistActive();
        Save();
    }

    public string Root => _store.Root;

    public string? ActiveSessionId
    {
        get
        {
            lock (_gate) return _activeId;
        }
    }

    public IReadOnlyList<SessionRecord> Sessions
    {
        get
        {
            lock (_gate) return [.. _records];
        }
    }

    /// <summary>当前活动会话；第一个快照与没有指定 sessionId 的命令都用它。</summary>
    public ChatSession? ActiveSession
    {
        get
        {
            lock (_gate)
            {
                if (_activeId is not null && _open.TryGetValue(_activeId, out var session)) return session;
                return _activeId is null ? null : OpenCore(_activeId);
            }
        }
    }

    public event Action<ChatOpBatch>? OpsProduced;

    public event Action<ChatSessionListPayload>? SessionsChanged;

    public Task<SessionRecord> CreateAsync(string? workspacePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? full = null;
        if (!string.IsNullOrWhiteSpace(workspacePath))
        {
            try
            {
                full = Path.GetFullPath(workspacePath);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new ChatSessionException("invalid_argument", "工作区路径无效");
            }

            if (!Directory.Exists(full)) throw new ChatSessionException("invalid_argument", "工作区目录不存在");
        }

        if (full is not null)
        {
            if (!_settings.WorkspacePaths.Any(path =>
                    string.Equals(path, full, StringComparison.OrdinalIgnoreCase)))
                _settings.WorkspacePaths = [.. _settings.WorkspacePaths, full];
            _settings.ActiveWorkspacePath = full;
            _settings.Save();
        }

        var record = _store.CreateEmptyRecord(out _);
        record = record with { WorkspacePath = full };

        lock (_gate)
        {
            _records.Add(record);
            _activeId = record.Id;
        }

        var session = OpenCore(record.Id);
        session?.Activate();
        PersistActive();
        Save();
        RaiseSessionsChanged();
        Logger.Information("session created id={Id} workspace={Workspace}", record.Id, full);
        return Task.FromResult(record);
    }

    public async Task<ChatSession> OpenAsync(string sessionId, CancellationToken cancellationToken)
    {
        ChatSession? session;
        lock (_gate)
        {
            _open.TryGetValue(sessionId, out session);
            if (session is null && !_records.Any(record => record.Id == sessionId))
                throw new ChatSessionException("not_found", "会话不存在");
        }

        if (session is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            session = await Task.Run(() => OpenCore(sessionId), cancellationToken).ConfigureAwait(false)
                      ?? throw new ChatSessionException("not_found", "会话不存在");
        }

        bool changed;
        lock (_gate)
        {
            changed = !string.Equals(_activeId, sessionId, StringComparison.Ordinal);
            _activeId = sessionId;
        }

        session.Activate();
        if (changed) RaiseSessionsChanged();
        PersistActive();
        Save();
        return session;
    }

    public Task RenameAsync(string sessionId, string title, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        title = (title ?? string.Empty).Trim();
        if (title.Length == 0) throw new ChatSessionException("invalid_argument", "标题不能为空");
        if (title.Length > 80) title = title[..80];

        SessionRecord updated;
        ChatSession? session;
        lock (_gate)
        {
            var index = _records.FindIndex(record => record.Id == sessionId);
            if (index < 0) throw new ChatSessionException("not_found", "会话不存在");
            updated = _records[index] = _records[index] with { Title = title, UpdatedAt = DateTimeOffset.Now };
            _open.TryGetValue(sessionId, out session);
        }

        session?.SetTitle(title);
        Save();
        RaiseSessionsChanged();
        return Task.CompletedTask;
    }

    public async Task<ChatSessionListPayload> DeleteAsync(string sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ChatSession? session = null;
        lock (_gate)
        {
            if (!_records.Any(record => record.Id == sessionId))
                throw new ChatSessionException("not_found", "会话不存在");
            _open.Remove(sessionId, out session);
        }

        if (session is not null)
        {
            await session.StopAsync().ConfigureAwait(false);
            await session.DisposeAsync().ConfigureAwait(false);
        }

        if (!_store.Delete(sessionId))
            throw new ChatSessionException("io_error", "删除会话目录失败");

        string? nextActive;
        lock (_gate)
        {
            _records.RemoveAll(record => record.Id == sessionId);
            if (string.Equals(_activeId, sessionId, StringComparison.Ordinal))
            {
                _activeId = _records
                    .OrderByDescending(record => record.UpdatedAt)
                    .Select(record => record.Id)
                    .FirstOrDefault();
            }

            nextActive = _activeId;
        }

        if (nextActive is null)
        {
            // 删掉最后一个会话：立即建一个空会话，保证页面始终有可打开的会话
            var replacement = await CreateAsync(null, cancellationToken).ConfigureAwait(false);
            nextActive = replacement.Id;
        }

        PersistActive();
        Save();
        RaiseSessionsChanged();

        ChatSessionListPayload payload;
        lock (_gate) payload = new ChatSessionListPayload([.. _records], _activeId);
        return payload;
    }

    public Task<IReadOnlyList<SessionRecord>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        query = (query ?? string.Empty).Trim();
        lock (_gate)
        {
            if (query.Length == 0) return Task.FromResult<IReadOnlyList<SessionRecord>>([.. _records]);
            var result = _records
                .Where(record =>
                    Contains(record.Title, query)
                    || Contains(record.LastUserText, query)
                    || Contains(record.WorkspacePath, query))
                .ToList();
            return Task.FromResult<IReadOnlyList<SessionRecord>>(result);
        }
    }

    /// <summary>
    /// 内部打开：创建会话实例并订阅它的增量/摘要事件；调用方负责持久化。
    /// 整个创建过程在管理器锁内完成，避免并发 session.open 为同一个 id 建出两个实例。
    /// </summary>
    private ChatSession? OpenCore(string sessionId)
    {
        lock (_gate)
        {
            if (_open.TryGetValue(sessionId, out var existing)) return existing;

            var record = _records.FirstOrDefault(item => item.Id == sessionId);
            if (record is null) return null;

            try
            {
                var session = _factory.Create(record, _store);
                session.OpsProduced += OnSessionOps;
                session.SummaryChanged += summary => OnSessionSummary(sessionId, summary);
                _open[sessionId] = session;
                return session;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "open session failed id={Id}", sessionId);
                return null;
            }
        }
    }

    private void OnSessionOps(ChatOpBatch batch) => OpsProduced?.Invoke(batch);

    private void OnSessionSummary(string sessionId, ChatSessionSummary summary)
    {
        lock (_gate)
        {
            var index = _records.FindIndex(record => record.Id == sessionId);
            if (index < 0) return;
            _records[index] = _records[index] with
            {
                Title = summary.Title,
                WorkspacePath = summary.WorkspacePath,
                UpdatedAt = summary.UpdatedAt,
                MessageCount = summary.MessageCount,
                LastUserText = summary.LastUserText,
                ModelKey = summary.ModelKey,
            };
        }

        Save();
        RaiseSessionsChanged();
    }

    private void RaiseSessionsChanged()
    {
        ChatSessionListPayload payload;
        lock (_gate) payload = new ChatSessionListPayload([.. _records], _activeId);
        try
        {
            SessionsChanged?.Invoke(payload);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "session list subscriber failed");
        }
    }

    private void PersistActive()
    {
        string? active;
        lock (_gate) active = _activeId;

        if (string.Equals(_settings.UiLastSessionId, active, StringComparison.Ordinal)) return;
        _settings.UiLastSessionId = active ?? string.Empty;
        _settings.Save();
    }

    private void Save()
    {
        ChatSessionIndex index;
        lock (_gate)
            index = new ChatSessionIndex { ActiveSessionId = _activeId, Sessions = [.. _records] };
        _store.Save(index);
    }

    private static bool Contains(string? value, string query) =>
        value is { Length: > 0 } && value.Contains(query, StringComparison.OrdinalIgnoreCase);

    public async ValueTask DisposeAsync()
    {
        List<ChatSession> sessions;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            sessions = [.. _open.Values];
            _open.Clear();
        }

        foreach (var session in sessions)
        {
            try
            {
                await session.StopAsync().ConfigureAwait(false);
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "dispose session failed id={Id}", session.Id);
            }
        }

        Save();
    }

    /// <summary>DI 容器在应用退出时只调用同步 Dispose；这里等待所有会话取消并落盘。</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}