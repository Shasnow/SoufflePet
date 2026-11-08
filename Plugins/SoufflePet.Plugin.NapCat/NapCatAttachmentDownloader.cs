// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SoufflePet.Plugin.Sdk;

namespace SoufflePet.Plugins.NapCat;

/// <summary>下载消息里的图片/文件到本地缓存目录，供模型读取。</summary>
internal sealed class NapCatAttachmentDownloader
{
    /// <summary>单个附件允许下载的最大字节数。</summary>
    private const long MaxAttachmentBytes = 20 * 1024 * 1024;

    /// <summary>单个附件的下载超时。</summary>
    private static readonly TimeSpan AttachmentDownloadTimeout = TimeSpan.FromSeconds(60);

    /// <summary>下载附件用的 HTTP 客户端（插件内共享）；IPv4 优先建连的实现收敛在 Sdk。</summary>
    private static readonly HttpClient Http = Ipv4HttpClient.Create();

    private readonly ILogger _logger;
    private readonly string _attachmentCacheDir;
    private readonly CancellationToken _shutdownToken;

    public NapCatAttachmentDownloader(
        ILogger logger,
        string attachmentCacheDir,
        CancellationToken shutdownToken)
    {
        _logger = logger;
        _attachmentCacheDir = attachmentCacheDir;
        _shutdownToken = shutdownToken;
    }

    /// <summary>把正文里的附件占位符替换成「[图片 本地路径]」；下载失败时退回「[图片]」。</summary>
    public async Task<string> HydrateAsync(string line, IReadOnlyList<NapCatPendingAttachment> attachments)
    {
        foreach (var attachment in attachments)
        {
            var path = await DownloadAsync(attachment).ConfigureAwait(false);
            var replacement = path is null
                ? attachment.Fallback
                : $"[{attachment.Label} {path}]";
            line = line.Replace(attachment.Placeholder, replacement, StringComparison.Ordinal);
        }

        return line;
    }

    /// <summary>下载单个附件到缓存目录，成功返回本地绝对路径，失败或超限返回 null。</summary>
    private async Task<string?> DownloadAsync(NapCatPendingAttachment attachment)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken);
            timeout.CancelAfter(AttachmentDownloadTimeout);

            using var response = await Http
                .GetAsync(attachment.Url, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "下载{Label}失败：{Url} 返回 {Status}",
                    attachment.Label,
                    attachment.Url,
                    (int)response.StatusCode);
                return null;
            }

            if (response.Content.Headers.ContentLength is > MaxAttachmentBytes)
            {
                _logger.LogWarning(
                    "跳过{Label}：{Url} 超过 {Limit} 字节",
                    attachment.Label,
                    attachment.Url,
                    MaxAttachmentBytes);
                return null;
            }

            var path = Path.Combine(
                _attachmentCacheDir,
                BuildFileName(attachment, response.Content.Headers.ContentType?.MediaType));
            Directory.CreateDirectory(_attachmentCacheDir);

            var tooLarge = false;
            await using (var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            await using (var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long total = 0;
                while (true)
                {
                    var read = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                    if (read == 0) break;

                    total += read;
                    if (total > MaxAttachmentBytes)
                    {
                        tooLarge = true;
                        break;
                    }

                    await target.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                }
            }

            if (tooLarge)
            {
                File.Delete(path);
                _logger.LogWarning(
                    "跳过{Label}：{Url} 超过 {Limit} 字节",
                    attachment.Label,
                    attachment.Url,
                    MaxAttachmentBytes);
                return null;
            }

            _logger.LogInformation("已保存{Label}到 {Path}", attachment.Label, path);
            return path;
        }
        catch (OperationCanceledException) when (!_shutdownToken.IsCancellationRequested)
        {
            _logger.LogWarning("下载{Label}超时：{Url}", attachment.Label, attachment.Url);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "下载{Label}失败：{Url}", attachment.Label, attachment.Url);
            return null;
        }
    }

    /// <summary>按地址哈希生成稳定的缓存文件名，尽量沿用原文件扩展名。</summary>
    private static string BuildFileName(NapCatPendingAttachment attachment, string? contentType)
    {
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(attachment.Url)))[..12];

        var extension = Path.GetExtension(attachment.FileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(extension) && Uri.TryCreate(attachment.Url, UriKind.Absolute, out var uri))
            extension = Path.GetExtension(uri.AbsolutePath);

        if (string.IsNullOrWhiteSpace(extension) || extension.Length > 10)
        {
            extension = contentType switch
            {
                "image/png" => ".png",
                "image/jpeg" => ".jpg",
                "image/gif" => ".gif",
                "image/webp" => ".webp",
                _ => ".bin",
            };
        }

        return $"{attachment.Prefix}-{hash}{extension.ToLowerInvariant()}";
    }
}
