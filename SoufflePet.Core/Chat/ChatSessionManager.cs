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
        store.SessionDirectory(record.Id, record.WorkspacePath),
        store.ProfilePath,
        record.Title,
        record.WorkspacePath,
        record.ModelKey,
        settings,
        llm,
        toolAccess,
        questions,
        files,
        statistics,
        // 会话换工作区时，目录跟着换到对应工作区分组
        workspacePath => store.SessionDirectory(record.Id, workspacePath));
}

/// <summary>
/// 多会话管理：按工作区分组的会话索引、新建/打开/重命名/删除/搜索，并把所有会话的事件汇聚成一个出口。
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

    /// <summary>
    /// 草稿会话 id：已创建但还没有发出任何消息。它们留在 <see cref="_records" /> 里以便
    /// 重命名/打开，但既不写会话索引，也不出现在侧栏列表，首条消息发出后才转正。
    /// </summary>
    private readonly HashSet<string> _drafts = new(StringComparer.Ordinal);

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

        if (_records.Count == 0)
        {
            // 首次启动 / 所有会话都被删掉：先只开一个内存草稿，等第一条消息再落盘
            var draft = CreateAsync(null, CancellationToken.None).GetAwaiter().GetResult();
            _open.TryGetValue(draft.Id, out var draftSession);
            draftSession?.Activate();
            Save();
            Logger.Information("no persisted sessions; draft created id={Id}", draft.Id);
            return;
        }

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
            lock (_gate) return PersistedLocked();
        }
    }

    /// <summary>已落盘会话（草稿不出现在侧栏/搜索结果里）。</summary>
    private List<SessionRecord> PersistedLocked() =>
        [.. _records.Where(record => !_drafts.Contains(record.Id))];

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

    /// <summary>当前正在生成回复的会话 id 快照，供侧栏显示加载图标。</summary>
    public HashSet<string> GeneratingSessionIds()
    {
        lock (_gate)
        {
            return _open
                .Where(pair => pair.Value.IsGenerating)
                .Select(pair => pair.Key)
                .ToHashSet(StringComparer.Ordinal);
        }
    }

    public event Action<ChatOpBatch>? OpsProduced;

    public event Action<ChatSessionListPayload>? SessionsChanged;

    /// <summary>
    /// 新建会话：只在内存里开一个草稿，不写会话索引、不建会话目录。
    /// 草稿在第一条消息发出后由 <see cref="OnSessionSummary" /> 转正并进入侧栏列表。
    /// 已经停在空白草稿上时直接复用它；其它遗留草稿会被丢弃，避免堆出多个隐形会话。
    /// </summary>
    public async Task<SessionRecord> CreateAsync(string? workspacePath, CancellationToken cancellationToken)
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

        // 已经停在空白草稿上：直接复用它（连点「新会话」不会丢正在输入的内容，也不会堆出多个草稿）
        SessionRecord? activeDraft;
        lock (_gate)
        {
            activeDraft = _activeId is not null && _drafts.Contains(_activeId)
                ? _records.FirstOrDefault(record => record.Id == _activeId)
                : null;
        }

        if (activeDraft is not null)
        {
            var session = await OpenAsync(activeDraft.Id, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(activeDraft.WorkspacePath, full, StringComparison.OrdinalIgnoreCase))
            {
                await session.SwitchWorkspaceAsync(full ?? string.Empty).ConfigureAwait(false);
                lock (_gate)
                {
                    var index = _records.FindIndex(record => record.Id == activeDraft.Id);
                    if (index >= 0) _records[index] = _records[index] with { WorkspacePath = full };
                }

                activeDraft = activeDraft with { WorkspacePath = full };
            }

            return activeDraft;
        }

        var discarded = RemoveDraftsLocked();

        var record = _store.NewDraftRecord(full);
        lock (_gate)
        {
            _records.Add(record);
            _drafts.Add(record.Id);
            _activeId = record.Id;
        }

        var created = OpenCore(record.Id);
        created?.Activate();

        foreach (var (discardedRecord, draft) in discarded)
        {
            await draft.DisposeAsync().ConfigureAwait(false);
            _store.DeleteDraft(discardedRecord.Id, discardedRecord.WorkspacePath);
        }

        Logger.Information("draft session created id={Id} workspace={Workspace}", record.Id, full);
        return record;
    }

    /// <summary>
    /// 清掉所有还没发过消息的草稿（含当前正在看的那个），返回需要释放的会话实例与其记录。
    /// 草稿没有内容可丢，连点「新会话」不会堆出多个隐形会话。
    /// </summary>
    private List<(SessionRecord Record, ChatSession Session)> RemoveDraftsLocked()
    {
        List<(SessionRecord, ChatSession)> removed = [];
        lock (_gate)
        {
            foreach (var id in _drafts.ToList())
            {
                var record = _records.FirstOrDefault(item => item.Id == id);
                if (record is null) continue;
                if (_open.Remove(id, out var session) && session is not null) removed.Add((record, session));
                _records.RemoveAll(item => item.Id == id);
                _drafts.Remove(id);
                if (string.Equals(_activeId, id, StringComparison.Ordinal)) _activeId = null;
            }
        }

        return removed;
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
        SessionRecord? record;
        bool draft;
        lock (_gate)
        {
            record = _records.FirstOrDefault(item => item.Id == sessionId);
            if (record is null) throw new ChatSessionException("not_found", "会话不存在");
            draft = _drafts.Remove(sessionId);
            _open.Remove(sessionId, out session);
        }

        if (session is not null)
        {
            await session.StopAsync().ConfigureAwait(false);
            await session.DisposeAsync().ConfigureAwait(false);
        }

        // 草稿从没落过盘：删掉可能残留的空目录即可，不进回收站
        if (draft)
        {
            _store.DeleteDraft(record.Id, record.WorkspacePath);
        }
        else if (!_store.Delete(record))
        {
            throw new ChatSessionException("io_error", "删除会话目录失败");
        }

        string? nextActive;
        lock (_gate)
        {
            _records.RemoveAll(record => record.Id == sessionId);
            if (string.Equals(_activeId, sessionId, StringComparison.Ordinal))
            {
                _activeId = _records
                    .Where(record => !_drafts.Contains(record.Id))
                    .OrderByDescending(record => record.UpdatedAt)
                    .Select(record => record.Id)
                    .FirstOrDefault();
            }

            nextActive = _activeId;
        }

        if (nextActive is null)
        {
            // 删掉最后一个会话：内存里补一个草稿，保证页面有可打开的会话
            var replacement = await CreateAsync(null, cancellationToken).ConfigureAwait(false);
            nextActive = replacement.Id;
        }

        PersistActive();
        Save();
        RaiseSessionsChanged();

        ChatSessionListPayload payload;
        lock (_gate) payload = new ChatSessionListPayload(PersistedLocked(), _activeId);
        return payload;
    }

    /// <summary>删除工作区：组内会话全部移入回收站，并把工作区从历史列表里移除。</summary>
    public async Task<ChatSessionListPayload> DeleteWorkspaceAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? full = NormalizeWorkspacePath(path);
        if (full is null) throw new ChatSessionException("invalid_argument", "工作区目录不存在");

        List<string> ids;
        lock (_gate)
        {
            ids = _records
                .Where(record => record.WorkspacePath is { Length: > 0 } workspace
                    && string.Equals(workspace, full, StringComparison.OrdinalIgnoreCase))
                .Select(record => record.Id)
                .ToList();
        }

        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        }

        // 会话删完后仍有活动会话（可能已是新建草稿），由它负责移除工作区记录并推送新的工作区状态
        var active = ActiveSession;
        if (active is not null)
        {
            await active.DeleteWorkspaceAsync(full).ConfigureAwait(false);
            if (string.Equals(_settings.ActiveWorkspacePath, full, StringComparison.OrdinalIgnoreCase))
            {
                _settings.ActiveWorkspacePath = string.Empty;
                _settings.Save();
            }
        }
        else
        {
            lock (_gate)
            {
                _settings.WorkspacePaths = _settings.WorkspacePaths
                    .Where(item => !string.Equals(item, full, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                _settings.WorkspaceNames = _settings.WorkspaceNames
                    .Where(entry => !string.Equals(entry.Key, full, StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
                if (string.Equals(_settings.ActiveWorkspacePath, full, StringComparison.OrdinalIgnoreCase))
                    _settings.ActiveWorkspacePath = string.Empty;
            }

            _settings.Save();
        }

        Save();
        RaiseSessionsChanged();

        ChatSessionListPayload payload;
        lock (_gate) payload = new ChatSessionListPayload(PersistedLocked(), _activeId);
        return payload;
    }

    /// <summary>重命名工作区展示名（只改界面显示，不改磁盘目录）。</summary>
    public async Task<IReadOnlyList<ChatWorkspaceState>> RenameWorkspaceAsync(
        string path,
        string? name,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var active = ActiveSession ?? throw new ChatSessionException("not_found", "当前没有活动会话");
        var workspaces = await active.RenameWorkspaceAsync(path, name).ConfigureAwait(false);
        RaiseSessionsChanged();
        return workspaces;
    }

    public Task<IReadOnlyList<SessionRecord>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        query = (query ?? string.Empty).Trim();
        lock (_gate)
        {
            var source = PersistedLocked();
            if (query.Length == 0) return Task.FromResult<IReadOnlyList<SessionRecord>>(source);
            var result = source
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
        bool promoted = false;
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

            // 草稿的第一条消息已经进了会话：落盘并进入侧栏列表
            if (_drafts.Contains(sessionId) && summary.MessageCount > 0)
            {
                _drafts.Remove(sessionId);
                promoted = true;
            }
        }

        if (promoted)
        {
            // 目录由 ChatMemory 在写入对话时按需创建；这里只把记录写进索引，
            // 避免与正在进行的 AddTurn 抢同一个 conversation.jsonl。
            PersistActive();
            Logger.Information("draft session persisted id={Id} title={Title}", sessionId, summary.Title);
        }

        // 记下工作区分组：换工作区后会话目录会搬到新分组，重建索引时也要能还原分组
        _store.EnsureGroup(summary.WorkspacePath);

        Save();
        RaiseSessionsChanged();
    }

    private void RaiseSessionsChanged()
    {
        ChatSessionListPayload payload;
        lock (_gate) payload = new ChatSessionListPayload(PersistedLocked(), _activeId);
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
        lock (_gate) active = _activeId is not null && !_drafts.Contains(_activeId) ? _activeId : null;

        if (string.Equals(_settings.UiLastSessionId, active, StringComparison.Ordinal)) return;
        _settings.UiLastSessionId = active ?? string.Empty;
        _settings.Save();
    }

    private void Save()
    {
        ChatSessionIndex index;
        lock (_gate)
        {
            var active = _activeId is not null && !_drafts.Contains(_activeId) ? _activeId : null;
            index = new ChatSessionIndex { ActiveSessionId = active, Sessions = PersistedLocked() };
        }

        _store.Save(index);
    }

    private static bool Contains(string? value, string query) =>
        value is { Length: > 0 } && value.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeWorkspacePath(string? path)
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

    public async ValueTask DisposeAsync()
    {
        List<ChatSession> sessions;
        List<SessionRecord> draftRecords;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            sessions = [.. _open.Values];
            draftRecords = _records.Where(record => _drafts.Contains(record.Id)).ToList();
            _open.Clear();
            // 草稿记录不能进索引：清掉标记的同时把记录从内存表里移除
            if (_activeId is not null && _drafts.Contains(_activeId)) _activeId = null;
            _drafts.Clear();
            _records.RemoveAll(record => draftRecords.Any(draft => draft.Id == record.Id));
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

        // 草稿没有写进索引：退出时把可能残留的目录一并清掉
        foreach (var record in draftRecords) _store.DeleteDraft(record.Id, record.WorkspacePath);

        Save();
    }

    /// <summary>DI 容器在应用退出时只调用同步 Dispose；这里等待所有会话取消并落盘。</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}