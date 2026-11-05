// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace SoufflePet.Services;

/// <summary>token 指向的内容：文件路径或内存字节（二选一）。</summary>
public sealed record FileTokenEntry(
    byte[]? Bytes,
    string? FilePath,
    string ContentType,
    DateTimeOffset ExpiresAt,
    bool SingleUse);

/// <summary>
/// 本地文件服务器的 token 表：为页面登记可读路径/内存 JSON，并做过期清理。
/// 与 HTTP 无关，便于单测；同一个文件路径在有效期内复用同一个 token
/// （快照/resync 会反复登记同一张图，不能每次都发新 token）。
/// </summary>
public sealed class FileTokenStore
{
    /// <summary>默认清理间隔：注册很频繁时不必每次都全表扫描。</summary>
    public static readonly TimeSpan DefaultCleanupInterval = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, FileTokenEntry> _tokens = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _pathTokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _time;
    private readonly TimeSpan _cleanupInterval;
    private long _nextCleanupAt;

    public FileTokenStore(TimeProvider? time = null, TimeSpan? cleanupInterval = null)
    {
        _time = time ?? TimeProvider.System;
        _cleanupInterval = cleanupInterval ?? DefaultCleanupInterval;
    }

    /// <summary>当前登记的 token 数量（测试与诊断用）。</summary>
    public int Count => _tokens.Count;

    /// <summary>当前登记的路径缓存数量（测试用）。</summary>
    public int PathCount => _pathTokens.Count;

    public void Clear()
    {
        _tokens.Clear();
        _pathTokens.Clear();
        Interlocked.Exchange(ref _nextCleanupAt, 0);
    }

    /// <summary>登记本地文件；同一路径在有效期内复用 token。</summary>
    public string RegisterFile(string path, TimeSpan lifetime)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new FileNotFoundException("登记的文件不存在", full);

        if (_pathTokens.TryGetValue(full, out var cached)
            && _tokens.TryGetValue(cached, out var existing)
            && existing.ExpiresAt > Now)
        {
            return cached;
        }

        var token = NewToken();
        _tokens[token] = new FileTokenEntry(null, full, ContentTypeFor(full), Now + lifetime, SingleUse: false);
        _pathTokens[full] = token;
        CleanupIfDue();
        return token;
    }

    /// <summary>登记一次性内存 JSON（快照/超大事件）。</summary>
    public string RegisterJson(string json, TimeSpan lifetime)
    {
        var token = NewToken();
        _tokens[token] = new FileTokenEntry(
            Encoding.UTF8.GetBytes(json),
            null,
            "application/json; charset=utf-8",
            Now + lifetime,
            SingleUse: true);
        CleanupIfDue();
        return token;
    }

    /// <summary>读取 token 内容；不存在或已过期返回 null（过期项顺手移除）。</summary>
    public FileTokenEntry? Take(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        if (!_tokens.TryGetValue(token, out var entry)) return null;
        if (entry.ExpiresAt <= Now)
        {
            Remove(token);
            return null;
        }

        return entry;
    }

    /// <summary>读走后调用：一次性 token 立即失效，文件 token 保留供重渲染。</summary>
    public void Consume(string token)
    {
        if (_tokens.TryGetValue(token, out var entry) && !entry.SingleUse) return;
        Remove(token);
    }

    /// <summary>立即清理全部过期项（正常路径由 CleanupIfDue 限频触发）。</summary>
    public void Cleanup()
    {
        var now = Now;
        var dead = new List<string>();
        foreach (var pair in _tokens)
        {
            if (pair.Value.ExpiresAt <= now) dead.Add(pair.Key);
        }

        foreach (var token in dead) Remove(token);
    }

    /// <summary>按扩展名给 Content-Type：静态资源与 token 文件共用一套。</summary>
    public static string ContentTypeFor(string file) =>
        Path.GetExtension(file).ToLowerInvariant() switch
        {
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" or ".mjs" => "text/javascript; charset=utf-8",
            ".json" or ".map" => "application/json; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".ico" => "image/x-icon",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            ".ttf" => "font/ttf",
            ".wasm" => "application/wasm",
            _ => "application/octet-stream", // .moc3 等
        };

    private void Remove(string token)
    {
        if (!_tokens.TryRemove(token, out var entry)) return;
        if (entry.FilePath is { } path
            && _pathTokens.TryGetValue(path, out var current)
            && string.Equals(current, token, StringComparison.Ordinal))
        {
            _pathTokens.TryRemove(path, out _);
        }
    }

    private void CleanupIfDue()
    {
        var now = Now;
        if (now.ToUnixTimeMilliseconds() < Interlocked.Read(ref _nextCleanupAt)) return;
        Interlocked.Exchange(ref _nextCleanupAt, now.Add(_cleanupInterval).ToUnixTimeMilliseconds());
        Cleanup();
    }

    private DateTimeOffset Now => _time.GetUtcNow();

    private static string NewToken() => Guid.NewGuid().ToString("N");
}
