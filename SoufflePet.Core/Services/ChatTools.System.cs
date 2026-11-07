// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.AI;
using SoufflePet.Models;

namespace SoufflePet.Services;

/// <summary>
/// 用户提问、本地文件读取（含多模态媒体）与 PowerShell 命令执行。
/// </summary>
public sealed partial class ChatTools
{
    /// <summary>
    /// 向用户提问：交给对话界面展示提问面板，等用户全部回答后把问答结果回传给模型。
    /// 停止对话时取消等待，按「用户没有回答」返回。
    /// </summary>
    private async Task<string> AskUser(
        [Description("问题列表，每个问题给 2-5 个预设选项")]
        AskUserQuestion[]? questions,
        CancellationToken cancellationToken)
    {
        var list = (questions ?? [])
            .Where(question => !string.IsNullOrWhiteSpace(question.Question))
            .Take(AskUserMaxQuestions)
            .Select(question => new UserQuestion(
                question.Question.Trim(),
                [.. (question.Options ?? [])
                    .Where(option => !string.IsNullOrWhiteSpace(option.Text))
                    .Select(option => new UserOption(
                        option.Text.Trim(),
                        string.IsNullOrWhiteSpace(option.Description) ? null : option.Description.Trim()))
                    .Take(AskUserMaxOptions)]))
            .ToList();

        if (list.Count == 0)
            return "提问失败：questions 不能为空，每个问题都要有 question 内容。";

        var answers = await _questions.AskAsync(list, cancellationToken).ConfigureAwait(false);
        if (answers is null) return "用户没有回答提问（已取消）。";

        var builder = new StringBuilder("用户已回答：");
        for (var i = 0; i < answers.Count; i++)
        {
            builder.Append('\n').Append(i + 1).Append(". ").Append(answers[i].Question)
                .Append("\n→ ").Append(answers[i].Answer);
        }

        return builder.ToString();
    }

    /// <summary>
    /// 读取本地文件并按类型返回合适的内容：文本走文本通道（支持按行 offset/limit 分段读取）；
    /// 图片、音频作为 DataContent 返回，由对话服务拆成多模态内容，避免二进制被当成普通文本。
    /// 其余二进制文件只回报文件信息，不把乱码塞进提示词。
    /// </summary>
    private static AIContent[] ReadFile(
        [Description("文件绝对路径")]
        string path,
        [Description("文本起始行号（从 1 开始），默认 1")]
        int offset = 1,
        [Description("最多返回的行数，默认 2000")]
        int limit = ReadFileDefaultLines)
    {
        var target = path.Trim();
        if (target.StartsWith('@')) target = target[1..].Trim().Trim('"');

        if (target.Length == 0) return [new TextContent("文件路径不能为空。")];
        if (Directory.Exists(target)) return [new TextContent($"「{target}」是目录，不能按文件读取。")];

        var info = new FileInfo(target);
        if (!info.Exists) return [new TextContent($"文件不存在：{target}")];

        var mediaType = GetReadableMediaType(info.Extension);
        if (mediaType is not null)
        {
            // 媒体文件要整份读入内存，过大直接拒绝
            if (info.Length > ReadFileMaxBytes)
                return [new TextContent($"文件「{info.Name}」大小为 {FormatSize(info.Length)}，超过 {FormatSize(ReadFileMaxBytes)} 的读取上限，未读取。")];

            var data = File.ReadAllBytes(info.FullName);
            return
            [
                new TextContent($"已读取{DescribeMedia(mediaType)}「{info.Name}」（{FormatSize(info.Length)}）。"),
                new DataContent(data, mediaType)
            ];
        }

        if (GetVideoMediaType(info.Extension) is { } videoType)
            return [new TextContent($"「{info.Name}」是视频（{FormatSize(info.Length)}，{videoType}），无法直接作为内容读取；"
                + "如需分析，可用 run_shell_command 配合 ffmpeg 抽取关键帧或音频，再读取生成的文件。")];

        offset = Math.Max(offset, 1);
        limit = Math.Clamp(limit, 1, ReadFileMaxLines);

        // 文本按行流式读取：跳过起始行之前的内容，读取 limit 行，再多读一行判断是否还有后续内容
        var builder = new StringBuilder();
        var lineNumber = 0;
        var lastLine = 0;
        var appended = 0;
        var hasMore = false;
        try
        {
            using var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);

            while (lineNumber < offset - 1)
            {
                if (reader.ReadLine() is null) break;
                lineNumber++;
            }

            while (true)
            {
                var line = reader.ReadLine();
                if (line is null) break;

                lineNumber++;
                if (lineNumber > offset + limit - 1)
                {
                    hasMore = true;
                    break;
                }

                if (appended > 0) builder.Append('\n');
                if (line.Length > ReadFileMaxLineChars)
                    builder.Append(line, 0, ReadFileMaxLineChars).Append("…（该行过长已截断）");
                else
                    builder.Append(line);
                appended++;
                lastLine = lineNumber;
            }
        }
        catch (DecoderFallbackException)
        {
            return [new TextContent($"「{info.Name}」是二进制文件（{FormatSize(info.Length)}），无法按文本读取。")];
        }

        if (builder.ToString().Contains('\0'))
            return [new TextContent($"「{info.Name}」是二进制文件（{FormatSize(info.Length)}），无法按文本读取。")];

        if (appended == 0)
            return [new TextContent($"已到文件末尾：「{info.Name}」共 {lineNumber} 行，第 {offset} 行之后没有内容。")];

        var header = $"文件 {info.FullName}（{FormatSize(info.Length)}，第 {offset}–{lastLine} 行）：";
        if (hasMore)
            builder.Append($"\n…（已截断，本次返回第 {offset}–{lastLine} 行；可传 offset={lastLine + 1} 继续读取）");

        return [new TextContent($"{header}\n{builder}")];
    }

    /// <summary>可转成多模态内容直接交给模型的媒体类型（图片、音频）；其他返回 null。</summary>
    private static string? GetReadableMediaType(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".m4a" => "audio/mp4",
        ".aac" => "audio/aac",
        ".ogg" or ".oga" => "audio/ogg",
        ".flac" => "audio/flac",
        _ => null
    };

    /// <summary>视频没有通用的多模态通道，只用于给出提示。</summary>
    private static string? GetVideoMediaType(string extension) => extension.ToLowerInvariant() switch
    {
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        ".mov" => "video/quicktime",
        ".avi" => "video/x-msvideo",
        ".mkv" => "video/x-matroska",
        _ => null
    };

    private static string DescribeMedia(string mediaType) => mediaType.Split('/')[0] switch
    {
        "image" => "图片",
        "audio" => "音频",
        _ => "文件"
    };

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024d / 1024d:0.#} MB",
        >= 1024 => $"{bytes / 1024d:0.#} KB",
        _ => $"{bytes} 字节"
    };

    private async Task<string> RunShellCommand(
        [Description(
            "要执行的 PowerShell 命令")]
        string command,
        [Description("命令超时时间（秒），默认 30，最大 1800")]
        int timeoutSeconds = DefaultShellTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        var timeoutSecondsValue = Math.Clamp(timeoutSeconds, 1, MaxShellTimeoutSeconds);

        // 命令执行按当前审批规则处理：手动审批时阻塞等待用户点击允许
        if (!await _access.RequestShellApprovalAsync(command, cancellationToken))
        {
            return cancellationToken.IsCancellationRequested
                ? "命令已取消。"
                : "用户没有允许这次命令执行，命令未运行。";
        }

        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            // 命令在会话工作区里执行：每个会话有各自的逻辑工作目录，不能依赖进程 cwd
            WorkingDirectory = WorkspaceContext.Current,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        // 强制 UTF-8，避免中文输出按控制台代码页解码成乱码
        startInfo.ArgumentList.Add("$OutputEncoding=[Console]::OutputEncoding=[Text.Encoding]::UTF8; " + command);

        using var process = new Process();
        process.StartInfo = startInfo;

        // 按行异步收集输出：不能用 ReadToEndAsync，它要等管道 EOF，
        // 而命令拉起的后台进程会继承写端句柄，导致永远等不到 EOF、看起来每次都超时。
        var stdoutLines = new ConcurrentQueue<string>();
        var stderrLines = new ConcurrentQueue<string>();
        var stdoutClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) stdoutClosed.TrySetResult();
            else stdoutLines.Enqueue(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) stderrClosed.TrySetResult();
            else stderrLines.Enqueue(e.Data);
        };

        // 用 Exited 事件等进程退出，不要用 WaitForExitAsync：开始异步读流之后，
        // WaitForExitAsync 会连同「读到管道 EOF」一起等，而命令拉起的 daemon
        // （比如 agent-browser）会继承写端句柄，EOF 永远不来，结果每次都超时。
        var exitTask = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => exitTask.TrySetResult();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSecondsValue));

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"命令启动失败：{ex.Message}";
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // 正常退出与超时只有「结束原因」不同：输出收集、收敛、拼装完全一样，
        // 所以只用一个 try 等到进程退出或被超时打断，最后按原因套不同的外壳。
        var timedOut = false;
        try
        {
            await exitTask.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                /* 已退出则忽略 */
            }

            // 只有「停止对话」才把取消继续往上抛；命令自身超时按普通结果回给模型，
            // 不能连带取消本次 LLM 请求。
            cancellationToken.ThrowIfCancellationRequested();
            timedOut = true;
        }

        // 进程已退出或已被终止：能收到 EOF 就说明输出读完，收不到也不再多等
        // （避免被继承了写端句柄的常驻进程拖住）。超时也把已收到的输出带回去：
        // CLI 常在卡住前先打印一行进度或错误，这些线索丢了就只能看到「超时」。
        await Task.WhenAny(
                Task.WhenAll(stdoutClosed.Task, stderrClosed.Task),
                Task.Delay(OutputDrainTimeout, CancellationToken.None))
            .ConfigureAwait(false);

        var stdout = TruncateOutput(JoinOutput(stdoutLines));
        var stderr = TruncateOutput(JoinOutput(stderrLines));

        var result = new StringBuilder(
            timedOut ? $"命令超过 {timeoutSecondsValue} 秒未结束，已终止。" : $"退出码 {process.ExitCode}");
        if (stdout.Length > 0) result.Append(timedOut ? "\n已产生的输出：\n" : "\n输出：\n").Append(stdout);
        if (stderr.Length > 0) result.Append(timedOut ? "\n已产生的错误：\n" : "\n错误：\n").Append(stderr);
        if (stdout.Length == 0 && stderr.Length == 0)
        {
            result.Append(timedOut
                ? "\n（没有任何输出：命令卡在启动阶段，或它拉起的常驻进程继承了输出句柄）"
                : "\n（无输出）");
        }

        return result.ToString();
    }

    private static string JoinOutput(ConcurrentQueue<string> lines) =>
        lines.IsEmpty ? string.Empty : string.Join(Environment.NewLine, lines).Trim();

    private static string TruncateOutput(string text) =>
        text.Length <= ShellOutputMaxLength ? text : text[..ShellOutputMaxLength] + "…（已截断）";
}
