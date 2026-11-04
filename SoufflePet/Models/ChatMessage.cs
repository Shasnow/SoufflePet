// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Diagnostics;
using System.IO;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoufflePet.Utils;

namespace SoufflePet.Models;

/// <summary>
/// 一条对话消息：IsUser 决定气泡的左右对齐与配色；IsTool 表示工具调用记录（不进入请求历史）；
/// IsReasoning 表示模型的思考过程（同样不进入请求历史）；
/// Text 支持流式追加时刷新；带 Approval 的消息表示等待用户确认的工具调用，气泡上显示允许/拒绝按钮。
/// DisplayAttachment 非空时，气泡里额外展示图片预览或文件卡片。
/// </summary>
public sealed partial class ChatMessage : ObservableObject
{
    /// <summary>审批结果回写时以原始文本为底，避免状态覆盖掉命令内容。</summary>
    private readonly string _baseText;

    public ChatMessage(string text, bool isUser, bool isTool = false, bool isReasoning = false,
        ToolApprovalRequest? approval = null, bool isExpanded = true, bool isPlatformNotice = false,
        ChatDisplayAttachment? displayAttachment = null, DateTimeOffset? timestamp = null)
    {
        Text = text;
        _baseText = text;
        IsUser = isUser;
        IsTool = isTool;
        IsReasoning = isReasoning;
        Approval = approval;
        IsExpanded = isExpanded;
        IsPlatformNotice = isPlatformNotice;
        DisplayAttachment = displayAttachment;
        Timestamp = timestamp ?? DateTimeOffset.Now;

        if (approval is null) return;

        // 请求可能在气泡插入前就已被取消（例如立刻按了停止）
        approval.Resolved += OnApprovalResolved;
        if (approval.Outcome == ToolApprovalOutcome.Pending) IsPending = true;
        else OnApprovalResolved();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReasoningHeader))]
    public partial string Text { get; set; }

    /// <summary>消息时间：气泡底部的用量胶囊里显示 HH:mm；恢复历史时用落盘时间。</summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>气泡底部展示的本地时间（HH:mm）。</summary>
    public string TimeText => Timestamp.ToLocalTime().ToString("HH:mm");

    /// <summary>本轮 token 用量：输入（含命中缓存的部分）。</summary>
    public long InputTokens { get; private set; }

    /// <summary>本轮 token 用量：输出。</summary>
    public long OutputTokens { get; private set; }

    /// <summary>本轮 token 用量：命中缓存的输入部分。</summary>
    public long CachedInputTokens { get; private set; }

    /// <summary>本轮使用的模型（提供方/模型），显示在用量浮窗里；为空时不显示该行。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasModelLabel))]
    public partial string ModelLabel { get; set; } = string.Empty;

    public bool HasModelLabel => ModelLabel.Length > 0;

    /// <summary>是否记录了本轮用量（助手消息结束后显示底部胶囊）。</summary>
    public bool HasUsage => InputTokens > 0 || OutputTokens > 0;

    /// <summary>气泡底部的紧凑用量文案：用量 2.5M tok。</summary>
    public string UsageCompactText => $"用量 {TokenDisplay.Compact(InputTokens + OutputTokens)} tok";

    /// <summary>用量浮窗标题右侧的总量（完整数字）：2,481,299 tok。</summary>
    public string UsageTotalText => TokenDisplay.Tokens(InputTokens + OutputTokens);

    /// <summary>缓存命中率（保留 1 位小数）。</summary>
    public string UsageCacheHitText =>
        InputTokens > 0 ? TokenDisplay.Percent((double)CachedInputTokens / InputTokens) : "—";

    /// <summary>未命中缓存的输入 tokens。</summary>
    public string UsageUncachedText => TokenDisplay.Tokens(Math.Max(0, InputTokens - CachedInputTokens));

    /// <summary>从缓存读取的输入 tokens。</summary>
    public string UsageCachedText => TokenDisplay.Tokens(CachedInputTokens);

    /// <summary>输出 tokens。</summary>
    public string UsageOutputText => TokenDisplay.Tokens(OutputTokens);

    /// <summary>本轮用量浮窗是否展开（每条消息各自独立）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsagePanel))]
    public partial bool IsUsageOpen { get; set; }

    /// <summary>
    /// 用量浮窗的内容载体：只有展开时才返回自身（收起为 null）。
    /// 浮窗子树有十几个控件，Popup 收起时同样会被实例化；用 ContentControl 承载后，
    /// 每条消息只在真正点开时才创建这棵树。
    /// </summary>
    public ChatMessage? UsagePanel => IsUsageOpen ? this : null;

    /// <summary>
    /// 气泡底部「用量胶囊 + 浮窗」整块的载体：没有用量的消息（用户发言、工具记录、思考过程）返回 null，
    /// 连胶囊一起不创建。配合虚拟化，长会话里每条气泡的控件树尽量小。
    /// </summary>
    public ChatMessage? UsageSection => HasUsage ? this : null;

    /// <summary>点击气泡底部的用量胶囊：展开 / 收起本轮用量浮窗。</summary>
    [RelayCommand]
    private void ToggleUsage() => IsUsageOpen = !IsUsageOpen;

    /// <summary>气泡被移出界面时调用：释放预览图占用的原生位图内存。</summary>
    public void ReleaseGraphics() => DisplayAttachment?.ReleaseImage();

    /// <summary>记录本轮用量（轮末或恢复历史时调用），同时刷新胶囊与浮窗里的所有文案。</summary>
    public void SetUsage(long inputTokens, long outputTokens, long cachedInputTokens, string? modelLabel = null)
    {
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        CachedInputTokens = cachedInputTokens;
        if (!string.IsNullOrWhiteSpace(modelLabel)) ModelLabel = modelLabel;

        OnPropertyChanged(nameof(HasUsage));
        OnPropertyChanged(nameof(UsageSection));
        OnPropertyChanged(nameof(UsageCompactText));
        OnPropertyChanged(nameof(UsageTotalText));
        OnPropertyChanged(nameof(UsageCacheHitText));
        OnPropertyChanged(nameof(UsageUncachedText));
        OnPropertyChanged(nameof(UsageCachedText));
        OnPropertyChanged(nameof(UsageOutputText));
    }

    public bool IsUser { get; }

    /// <summary>工具调用记录（弱化样式展示，不参与上下文）。</summary>
    public bool IsTool { get; }

    /// <summary>模型的思考过程（弱化样式展示，不参与上下文）。</summary>
    public bool IsReasoning { get; }

    /// <summary>平台推送的提醒（弱化强调样式展示，表示这轮对话由外部消息触发）。</summary>
    public bool IsPlatformNotice { get; }

    /// <summary>随消息展示的本地图片或文件；null 表示普通文本气泡。</summary>
    public ChatDisplayAttachment? DisplayAttachment { get; }

    public bool HasDisplayAttachment => DisplayAttachment is not null;

    /// <summary>折叠面板的展开状态（思考过程与工具调用共用）：思考气泡默认收起，用户可手动展开查看全文。</summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary>思考阶段是否已结束（结束时收起面板），与用户手动展开/收起无关。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReasoningHeader))]
    public partial bool IsReasoningDone { get; set; }

    /// <summary>
    /// 折叠面板的标题：思考气泡默认收起，收起时显示「思考 · 」加一行思考内容。
    /// 思考进行中取最后一行（跟着模型当前的想法滚动），思考结束后取第一行（整段思考的开头，标题不再跳动）；
    /// 没有内容时才只显示「思考」这个标签。
    /// </summary>
    public string ReasoningHeader
    {
        get
        {
            var line = IsReasoningDone ? FirstLine(Text) : LatestLine(Text);
            if (line is not null) return $"思考 · {line}";
            return IsReasoningDone ? "思考" : "思考中…";
        }
    }

    /// <summary>取文本里第一个非空行的内容；整段都是空白时返回 null。</summary>
    private static string? FirstLine(string text)
    {
        var start = 0;
        while (start < text.Length)
        {
            var end = text.IndexOf('\n', start);
            if (end < 0) end = text.Length;
            var line = text[start..end].Trim();
            if (line.Length > 0) return line;
            start = end + 1;
        }

        return null;
    }

    /// <summary>取文本里最后一个非空行的内容；整段都是空白时返回 null。</summary>
    private static string? LatestLine(string text)
    {
        var end = text.Length;
        while (end > 0)
        {
            var start = text.LastIndexOf('\n', end - 1) + 1;
            var line = text[start..end].Trim();
            if (line.Length > 0) return line;
            end = start > 0 ? start - 1 : 0;
        }

        return null;
    }

    /// <summary>工具名（仅工具记录）：折叠标题只显示它。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolHeader))]
    public partial string ToolName { get; set; } = string.Empty;

    /// <summary>工具参数原文（仅工具记录）：展开后显示。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolBody))]
    public partial string ToolArguments { get; set; } = string.Empty;

    /// <summary>工具执行结果（仅工具记录）：流式时先显示「执行中…」，展开后显示完整内容。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolBody))]
    public partial string ToolResult { get; set; } = string.Empty;

    /// <summary>工具气泡的折叠面板标题：只显示工具名，参数与结果展开后再看。</summary>
    public string ToolHeader => ToolName.Length > 0 ? $"调用工具：{ToolName}" : "调用工具";

    /// <summary>工具气泡的折叠面板内容：参数原文 + 完整结果；结果为空时给出提示。</summary>
    public string ToolBody
    {
        get
        {
            var result = ToolResult.Length > 0 ? ToolResult : "（无输出）";
            return ToolArguments.Length > 0 ? $"参数：{ToolArguments}\n结果：{result}" : $"结果：{result}";
        }
    }

    /// <summary>工具记录气泡：用折叠面板展示（待审批的气泡走普通文本，保证按钮始终可见）。</summary>
    public bool IsToolPanel => IsTool && Approval is null;

    /// <summary>普通文本气泡：既不是思考过程，也不是工具记录，也不带展示附件
    /// （附件气泡只展示卡片/图片本身，正文留空；待审批的气泡按普通消息展示）。</summary>
    public bool IsPlain => !IsReasoning && !IsToolPanel && !HasDisplayAttachment;

    /// <summary>
    /// 正文按 Markdown 渲染的气泡：只有助手正文如此。
    /// 用户发言按原样展示（`*`、`#`、`@路径` 这类字符不该被当成语法改写），
    /// 平台提醒与工具审批气泡也是普通文本（命令原文更不能被 Markdown 吃掉格式）。
    /// </summary>
    public bool IsMarkdown => IsPlain && !IsUser && !IsPlatformNotice && Approval is null;

    /// <summary>正文按纯文本展示的气泡：普通文本气泡里除助手正文以外的那些。</summary>
    public bool IsPlainText => IsPlain && !IsMarkdown;

    /// <summary>
    /// 正文 Markdown 控件的载体（即本条消息自身）：只有需要 Markdown 的气泡返回自身。
    /// ContentControl 在 Content 为 null 时不实例化模板，用户 / 思考过程 / 工具记录 / 附件气泡
    /// 就不会创建 MarkdownTextBlock —— 它们流式追加正文时也不会再触发解析。
    /// </summary>
    public ChatMessage? PlainPanel => IsMarkdown ? this : null;

    /// <summary>
    /// 思考气泡折叠面板的内容载体（即本条消息自身）：非思考消息为 null。
    /// ContentControl 在 Content 为 null 时不实例化模板，普通气泡就不会白白创建一份折叠面板。
    /// </summary>
    public ChatMessage? ReasoningPanel => IsReasoning ? this : null;

    /// <summary>工具气泡折叠面板的内容载体（即本条消息自身）：非工具记录为 null，理由同上。</summary>
    public ChatMessage? ToolPanel => IsToolPanel ? this : null;

    /// <summary>需要用户审批的工具调用；为 null 表示普通消息。</summary>
    public ToolApprovalRequest? Approval { get; }

    public bool IsApproval => Approval is not null;

    /// <summary>是否仍在等待用户点击允许/拒绝。</summary>
    [ObservableProperty]
    public partial bool IsPending { get; set; }

    [RelayCommand]
    private void Approve() => Approval?.Approve();

    [RelayCommand]
    private void Reject() => Approval?.Reject();

    private void OnApprovalResolved()
    {
        IsPending = false;

        var status = Approval?.Outcome switch
        {
            ToolApprovalOutcome.Approved => "已允许执行",
            ToolApprovalOutcome.Rejected => "已拒绝执行",
            _ => "已取消"
        };
        Text = $"{_baseText}\n→ {status}";
    }
}

/// <summary>
/// 对话气泡里展示的本地附件：图片按预览展示，其他文件展示为可点击打开的卡片。
/// 图片解码在后台完成，避免大图阻塞 UI 线程。
/// </summary>
public sealed partial class ChatDisplayAttachment : ObservableObject
{
    /// <summary>预览图解码后的最大宽度（像素）；气泡里最宽只显示 260，按 2 倍屏留够清晰度即可。</summary>
    private const int PreviewMaxWidth = 520;

    public ChatDisplayAttachment(string filePath, string fileName, bool isImage)
    {
        FilePath = filePath;
        FileName = fileName;
        IsImage = isImage;
    }

    public string FilePath { get; }

    public string FileName { get; }

    public bool IsImage { get; }

    /// <summary>图片预览；解码完成前为 null。非图片附件始终为 null。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    public partial Bitmap? Image { get; set; }

    public bool HasImage => Image is not null;

    /// <summary>非图片附件展示为文件卡片。</summary>
    public bool IsFileCard => !IsImage;

    /// <summary>按需解码图片；文件缺失或格式不支持时返回 null。</summary>
    public static Bitmap? LoadImage(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, PreviewMaxWidth);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>释放已解码的预览图：消息被移出界面后位图不再需要，尽早归还内存。</summary>
    public void ReleaseImage()
    {
        var image = Image;
        Image = null;
        image?.Dispose();
    }

    /// <summary>用系统默认程序打开文件；文件已删除等失败情况忽略，避免界面崩溃。</summary>
    [RelayCommand]
    private void Open()
    {
        try
        {
            Process.Start(new ProcessStartInfo(FilePath) { UseShellExecute = true });
        }
        catch
        {
            // 文件已被移动或没有关联程序：忽略
        }
    }
}