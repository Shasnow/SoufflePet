// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Text;

namespace SoufflePet.Utils;

/// <summary>Markdown 块级元素的种类：渲染层据此判断流式更新时能否就地复用已有控件。</summary>
public enum MarkdownBlockKind
{
    Paragraph,
    Heading,
    CodeBlock,
    Quote,
    ListItem,
    Table,
    Rule
}

/// <summary>块级元素基类。</summary>
public abstract record MarkdownBlock(MarkdownBlockKind Kind);

/// <summary>普通段落：软换行原样保留成换行（模型常靠单换行断句，按规范折成空格反而更难读）。</summary>
public sealed record MarkdownParagraph(IReadOnlyList<MarkdownInline> Inlines) : MarkdownBlock(MarkdownBlockKind.Paragraph);

/// <summary>标题（# ~ ######）：只保留层级与内容，字号由渲染层决定。</summary>
public sealed record MarkdownHeading(int Level, IReadOnlyList<MarkdownInline> Inlines) : MarkdownBlock(MarkdownBlockKind.Heading);

/// <summary>围栏代码块；Language 是 ``` 之后的首个标识，可能为空。</summary>
public sealed record MarkdownCodeBlock(string Code, string? Language) : MarkdownBlock(MarkdownBlockKind.CodeBlock);

/// <summary>引用块（&gt;）：内部内容合并成一段行内元素展示。</summary>
public sealed record MarkdownQuote(IReadOnlyList<MarkdownInline> Inlines) : MarkdownBlock(MarkdownBlockKind.Quote);

/// <summary>
/// 列表项：Marker 为有序列表的序号，null 表示无序列表；Indent 是源文本里的缩进层级（用于嵌套缩进）；
/// TaskChecked 非空表示任务列表项（- [x] / - [ ]）。
/// </summary>
public sealed record MarkdownListItem(int? Marker, int Indent, IReadOnlyList<MarkdownInline> Inlines,
    bool? TaskChecked = null) : MarkdownBlock(MarkdownBlockKind.ListItem);

/// <summary>表格：单元格只保留纯文本（宽度有限，行内样式在表格里收益不高）。</summary>
public sealed record MarkdownTable(IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows)
    : MarkdownBlock(MarkdownBlockKind.Table);

/// <summary>分割线（--- / *** / ___）。</summary>
public sealed record MarkdownRule() : MarkdownBlock(MarkdownBlockKind.Rule);

/// <summary>行内元素基类。</summary>
public abstract record MarkdownInline;

/// <summary>纯文本片段，可包含换行。</summary>
public sealed record MarkdownText(string Text) : MarkdownInline;

/// <summary>行内代码（`code`）。</summary>
public sealed record MarkdownCodeSpan(string Text) : MarkdownInline;

/// <summary>链接：只保留显示文案与目标地址（图片按链接文案展示，不加载远程资源）。</summary>
public sealed record MarkdownLink(string Text, string Url) : MarkdownInline;

/// <summary>带样式的行内片段：可叠加粗体 / 斜体 / 删除线。</summary>
public sealed record MarkdownSpan(IReadOnlyList<MarkdownInline> Children, bool Bold = false, bool Italic = false,
    bool Strikethrough = false) : MarkdownInline;

/// <summary>
/// 轻量 Markdown 解析器：只覆盖对话气泡里常见的语法（段落、标题、围栏代码块、引用、列表、表格、分割线，
/// 以及粗体 / 斜体 / 删除线 / 行内代码 / 链接）。
/// 流式输出时经常出现半截语法（未闭合的代码围栏、单个 **），解析无法闭合时一律按字面文本处理，
/// 保证输出过程中气泡内容始终连贯。
/// </summary>
public static class MarkdownParser
{
    /// <summary>行内嵌套深度上限，防止病态输入把解析拖成指数级。</summary>
    private const int MaxInlineDepth = 8;

    /// <summary>反斜杠可以转义的字符（CommonMark 的可打印 ASCII 标点）。</summary>
    private const string EscapableChars = "\\`*_{}[]()#+-.!|~<>\"";

    /// <summary>解析成块级元素序列。</summary>
    public static IReadOnlyList<MarkdownBlock> ParseBlocks(string? text)
    {
        var blocks = new List<MarkdownBlock>();
        if (string.IsNullOrEmpty(text)) return blocks;

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var index = 0;
        while (index < lines.Length)
        {
            var line = lines[index];
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                index++;
                continue;
            }

            if (TryReadFenceOpen(line, out var fence))
            {
                blocks.Add(ReadCodeBlock(lines, ref index, fence));
                continue;
            }

            if (TryReadHeading(trimmed, out var level, out var headingText))
            {
                blocks.Add(new MarkdownHeading(level, ParseInlines(headingText)));
                index++;
                continue;
            }

            if (IsHorizontalRule(trimmed))
            {
                blocks.Add(new MarkdownRule());
                index++;
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                blocks.Add(ReadQuote(lines, ref index));
                continue;
            }

            if (TryReadListMarker(line, out var marker, out var indent, out var itemText))
            {
                blocks.Add(ReadListItem(lines, ref index, marker, indent, itemText));
                continue;
            }

            if (TryReadTable(lines, ref index, out var table))
            {
                blocks.Add(table);
                continue;
            }

            blocks.Add(ReadParagraph(lines, ref index));
        }

        return blocks;
    }

    /// <summary>解析一段行内元素（表格单元格等只需要行内格式的场景直接使用）。</summary>
    public static IReadOnlyList<MarkdownInline> ParseInlines(string text) => ParseInlines(text, 0);

    /// <summary>把行内元素拍平成纯文本：表格单元格、复制场景只关心文字。</summary>
    public static string ToPlainText(IReadOnlyList<MarkdownInline> inlines)
    {
        var builder = new StringBuilder();
        AppendPlainText(builder, inlines);
        return builder.ToString();
    }

    #region 块级解析

    /// <summary>围栏的开始标记：``` 或 ~~~，至少三个字符。</summary>
    private readonly record struct Fence(char Char, int Length, string? Language);

    private static bool TryReadFenceOpen(string line, out Fence fence)
    {
        fence = default;
        var trimmed = line.TrimStart();
        if (trimmed.Length < 3) return false;

        var ch = trimmed[0];
        if (ch != '`' && ch != '~') return false;

        var count = 0;
        while (count < trimmed.Length && trimmed[count] == ch) count++;
        if (count < 3) return false;

        // 语言标识取第一个词；反引号围栏的标识里不允许再出现反引号
        var info = trimmed[count..].Trim();
        if (ch == '`' && info.Contains('`')) return false;
        var language = info.Length == 0 ? null : info.Split(' ', '\t')[0];
        fence = new Fence(ch, count, language);
        return true;
    }

    /// <summary>读取围栏代码块：闭合标记缺失时（流式输出中）把剩余内容整块当作代码。</summary>
    private static MarkdownCodeBlock ReadCodeBlock(string[] lines, ref int index, Fence fence)
    {
        var builder = new StringBuilder();
        index++; // 跳过开始围栏
        while (index < lines.Length)
        {
            var line = lines[index];
            index++;
            if (IsFenceClose(line, fence)) break;

            if (builder.Length > 0) builder.Append('\n');
            builder.Append(line);
        }

        // 闭合围栏前的最后一个空行不计入代码内容
        var code = builder.ToString().TrimEnd('\n');
        return new MarkdownCodeBlock(code, fence.Language);
    }

    private static bool IsFenceClose(string line, Fence fence)
    {
        var trimmed = line.Trim();
        if (trimmed.Length < fence.Length) return false;
        for (var i = 0; i < trimmed.Length; i++)
            if (trimmed[i] != fence.Char) return false;
        return trimmed.Length >= fence.Length;
    }

    private static bool TryReadHeading(string trimmed, out int level, out string content)
    {
        level = 0;
        content = string.Empty;
        var count = 0;
        while (count < trimmed.Length && trimmed[count] == '#') count++;
        if (count is 0 or > 6) return false;

        level = count;
        content = count == trimmed.Length ? string.Empty : trimmed[count..].Trim();
        if (content.Length > 0 && trimmed[count] != ' ' && trimmed[count] != '\t') return false;

        // 去掉 ATX 标题结尾可选的闭合 #：CommonMark 要求闭合序列前面是空白，
        // 所以 "# C#"、"# F#" 里的 # 属于标题内容，只有 "# 标题 ##" 结尾那串才算闭合标记
        var end = content.Length;
        while (end > 0 && content[end - 1] == '#') end--;
        if (end < content.Length && (end == 0 || content[end - 1] is ' ' or '\t'))
            content = content[..end].TrimEnd();

        return true;
    }

    private static bool IsHorizontalRule(string trimmed)
    {
        if (trimmed.Length < 3) return false;
        if (trimmed[0] is not ('-' or '*' or '_')) return false;

        var ch = trimmed[0];
        var count = 0;
        foreach (var c in trimmed)
        {
            if (c == ch) count++;
            else if (c != ' ' && c != '\t') return false;
        }

        return count >= 3;
    }

    private static MarkdownQuote ReadQuote(string[] lines, ref int index)
    {
        var builder = new StringBuilder();
        while (index < lines.Length)
        {
            var trimmed = lines[index].TrimStart();
            if (!trimmed.StartsWith('>')) break;

            var content = trimmed[1..];
            if (content.StartsWith(' ')) content = content[1..];
            if (builder.Length > 0) builder.Append('\n');
            builder.Append(content.Trim());
            index++;
        }

        return new MarkdownQuote(ParseInlines(builder.ToString()));
    }

    /// <summary>列表项的标记：- * + 或 1. / 1)。</summary>
    private static bool TryReadListMarker(string line, out int? marker, out int indent, out string content)
    {
        marker = null;
        indent = 0;
        content = string.Empty;

        var i = 0;
        while (i < line.Length && line[i] == ' ') i++;
        indent = i;
        if (i >= line.Length) return false;

        var ch = line[i];
        if (ch is '-' or '*' or '+')
        {
            // 单独的 - / * 也可能是分割线，交给前面的判断处理
            if (i + 1 >= line.Length) return false;
            if (line[i + 1] != ' ' && line[i + 1] != '\t') return false;
            content = line[(i + 2)..].Trim();
            return true;
        }

        if (!char.IsDigit(ch)) return false;
        var start = i;
        while (i < line.Length && char.IsDigit(line[i]) && i - start < 9) i++;
        if (i >= line.Length || (line[i] != '.' && line[i] != ')')) return false;
        if (i + 1 >= line.Length || (line[i + 1] != ' ' && line[i + 1] != '\t')) return false;

        marker = int.Parse(line[start..i]);
        content = line[(i + 2)..].Trim();
        return true;
    }

    private static MarkdownListItem ReadListItem(string[] lines, ref int index, int? marker, int indent, string content)
    {
        index++;

        // 缩进更深的后续行属于同一项（换行折行），直到空行或新的块级标记
        var builder = new StringBuilder(content);
        while (index < lines.Length)
        {
            var line = lines[index];
            if (line.Trim().Length == 0) break;
            if (IsBlockStart(lines, index)) break;

            builder.Append('\n').Append(line.Trim());
            index++;
        }

        var text = builder.ToString();

        // 任务列表：- [ ] / - [x]（也允许只有勾选框、没有正文的写法）
        bool? taskChecked = null;
        if (text.Length >= 3 && text[0] == '[' && text[2] == ']'
            && (text.Length == 3 || text[3] == ' ' || text[3] == '\t'))
        {
            var state = char.ToLowerInvariant(text[1]);
            if (state is ' ' or 'x')
            {
                taskChecked = state == 'x';
                text = text.Length > 4 ? text[4..].Trim() : string.Empty;
            }
        }

        // 嵌套层级：按两格缩进折算，最多缩进三级，避免深层列表把气泡挤没
        var level = Math.Min(indent / 2, 3);
        return new MarkdownListItem(marker, level, ParseInlines(text), taskChecked);
    }

    private static MarkdownParagraph ReadParagraph(string[] lines, ref int index)
    {
        var start = index;
        var builder = new StringBuilder();
        while (index < lines.Length)
        {
            var line = lines[index];
            if (line.Trim().Length == 0) break;
            if (builder.Length > 0 && IsBlockStart(lines, index)) break;

            if (builder.Length > 0) builder.Append('\n');
            builder.Append(line.Trim());
            index++;
        }

        // 兜底：一行都没吃掉时前进一行，避免解析卡死（正常流程里不会走到）
        if (index == start && index < lines.Length)
        {
            builder.Append(lines[index].Trim());
            index++;
        }

        return new MarkdownParagraph(ParseInlines(builder.ToString()));
    }

    /// <summary>当前行是否开启了新的块级元素（段落与列表续行据此收尾）。</summary>
    private static bool IsBlockStart(string[] lines, int index)
    {
        var line = lines[index];
        var trimmed = line.Trim();
        if (trimmed.Length == 0) return false;
        if (TryReadFenceOpen(line, out _)) return true;
        if (TryReadHeading(trimmed, out _, out _)) return true;
        if (IsHorizontalRule(trimmed)) return true;
        if (trimmed.StartsWith('>')) return true;
        if (TryReadListMarker(line, out _, out _, out _)) return true;
        return IsTableStart(lines, index);
    }

    private static bool IsTableStart(string[] lines, int index)
    {
        if (index + 1 >= lines.Length) return false;
        if (!lines[index].Contains('|')) return false;
        if (!IsTableSeparator(lines[index + 1])) return false;

        // 表头与分隔行的单元格数量必须一致，否则只是恰好带竖线的普通文本（+ 一行分割线）
        return SplitTableRow(lines[index]).Count == SplitTableRow(lines[index + 1]).Count;
    }

    private static bool IsTableSeparator(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0) return false;
        if (!trimmed.Contains('|') && !trimmed.Contains('-')) return false;

        var cells = SplitTableRow(trimmed);
        if (cells.Count == 0) return false;
        foreach (var cell in cells)
        {
            if (cell.Length == 0) return false;
            var body = cell.Trim(':');
            if (body.Length == 0) return false;
            if (body.Trim('-').Length != 0) return false;
        }

        return true;
    }

    private static bool TryReadTable(string[] lines, ref int index, out MarkdownTable table)
    {
        table = null!;
        if (!IsTableStart(lines, index)) return false;

        var headers = new List<string>();
        foreach (var cell in SplitTableRow(lines[index])) headers.Add(ToPlainText(ParseInlines(cell)));

        index += 2; // 表头 + 分隔行
        var rows = new List<IReadOnlyList<string>>();
        while (index < lines.Length)
        {
            var line = lines[index];
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || !trimmed.Contains('|')) break;
            if (TryReadFenceOpen(line, out _) || IsHorizontalRule(trimmed)) break;

            var cells = new List<string>();
            foreach (var cell in SplitTableRow(trimmed)) cells.Add(ToPlainText(ParseInlines(cell)));
            rows.Add(cells);
            index++;
        }

        table = new MarkdownTable(headers, rows);
        return true;
    }

    /// <summary>按 | 拆分表格行：支持 \| 转义，首尾竖线可有可无。</summary>
    private static List<string> SplitTableRow(string line)
    {
        var cells = new List<string>();
        var builder = new StringBuilder();
        var trimmed = line.Trim();
        var startsWithPipe = trimmed.StartsWith('|');
        var endsWithPipe = trimmed.Length > 0 && trimmed.EndsWith('|') && !trimmed.EndsWith("\\|");

        for (var i = 0; i < trimmed.Length; i++)
        {
            var ch = trimmed[i];
            if (ch == '\\' && i + 1 < trimmed.Length && trimmed[i + 1] == '|')
            {
                builder.Append('|');
                i++;
                continue;
            }

            if (ch == '|')
            {
                cells.Add(builder.ToString().Trim());
                builder.Clear();
                continue;
            }

            builder.Append(ch);
        }

        cells.Add(builder.ToString().Trim());
        if (startsWithPipe && cells.Count > 0) cells.RemoveAt(0);
        if (endsWithPipe && cells.Count > 0) cells.RemoveAt(cells.Count - 1);
        return cells;
    }

    #endregion

    #region 行内解析

    private static IReadOnlyList<MarkdownInline> ParseInlines(string text, int depth)
    {
        var result = new List<MarkdownInline>();
        if (text.Length == 0) return result;

        // 嵌套过深时不再继续解析，直接当纯文本
        if (depth > MaxInlineDepth)
        {
            result.Add(new MarkdownText(text));
            return result;
        }

        var buffer = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];

            // 反斜杠转义
            if (ch == '\\' && i + 1 < text.Length && EscapableChars.Contains(text[i + 1]))
            {
                buffer.Append(text[i + 1]);
                i += 2;
                continue;
            }

            // 行内代码
            if (ch == '`' && TryReadCodeSpan(text, ref i, out var code))
            {
                FlushBuffer(buffer, result);
                result.Add(new MarkdownCodeSpan(code));
                continue;
            }

            // 链接与图片：图片没有远程加载能力，按链接文案展示
            if ((ch == '[' || (ch == '!' && i + 1 < text.Length && text[i + 1] == '['))
                && TryReadLink(text, ref i, out var label, out var url))
            {
                FlushBuffer(buffer, result);
                result.Add(new MarkdownLink(label.Length > 0 ? label : url, url));
                continue;
            }

            // <https://...> 形式的自动链接
            if (ch == '<' && TryReadAutolink(text, ref i, out var autoUrl))
            {
                FlushBuffer(buffer, result);
                result.Add(new MarkdownLink(autoUrl, autoUrl));
                continue;
            }

            // 裸链接（模型经常直接贴地址）
            if (ch is 'h' or 'H' && IsLinkBoundary(text, i) && TryReadBareUrl(text, ref i, out var bareUrl))
            {
                FlushBuffer(buffer, result);
                result.Add(new MarkdownLink(bareUrl, bareUrl));
                continue;
            }

            // 强调、删除线
            if (ch is '*' or '_' or '~' && TryReadEmphasis(text, ref i, depth, out var span))
            {
                FlushBuffer(buffer, result);
                result.Add(span);
                continue;
            }

            buffer.Append(ch);
            i++;
        }

        FlushBuffer(buffer, result);
        return result;
    }

    private static void FlushBuffer(StringBuilder buffer, List<MarkdownInline> target)
    {
        if (buffer.Length == 0) return;
        target.Add(new MarkdownText(buffer.ToString()));
        buffer.Clear();
    }

    /// <summary>行内代码：闭合反引号数量必须与开始的一致。</summary>
    private static bool TryReadCodeSpan(string text, ref int index, out string code)
    {
        code = string.Empty;
        var start = index;
        var ticks = 0;
        while (start + ticks < text.Length && text[start + ticks] == '`') ticks++;

        var search = start + ticks;
        while (search < text.Length)
        {
            var close = text.IndexOf('`', search);
            if (close < 0) return false;

            var closeTicks = 0;
            while (close + closeTicks < text.Length && text[close + closeTicks] == '`') closeTicks++;
            if (closeTicks != ticks)
            {
                search = close + closeTicks;
                continue;
            }

            code = text[(start + ticks)..close];
            // 规范：内容两侧各去掉一个空格（内容本身不全为空格时）
            if (code.Length >= 2 && code[0] == ' ' && code[^1] == ' ' && code.Trim().Length > 0) code = code[1..^1];
            index = close + closeTicks;
            return true;
        }

        return false;
    }

    /// <summary>[文案](地址)：文案里允许出现成对方的方括号，地址里允许出现括号。</summary>
    private static bool TryReadLink(string text, ref int index, out string label, out string url)
    {
        label = string.Empty;
        url = string.Empty;

        var open = index;
        if (text[open] == '!') open++;
        if (open >= text.Length || text[open] != '[') return false;

        var depth = 0;
        var i = open;
        for (; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '\\')
            {
                i++;
                continue;
            }

            if (ch == '[') depth++;
            else if (ch == ']')
            {
                depth--;
                if (depth == 0) break;
            }
        }

        if (i >= text.Length || i + 1 >= text.Length || text[i + 1] != '(') return false;
        var closeBracket = i;

        var parenDepth = 1;
        var end = -1;
        for (var j = closeBracket + 2; j < text.Length; j++)
        {
            var ch = text[j];
            if (ch == '(') parenDepth++;
            else if (ch == ')')
            {
                parenDepth--;
                if (parenDepth == 0)
                {
                    end = j;
                    break;
                }
            }
        }

        if (end < 0) return false;

        url = text[(closeBracket + 2)..end].Trim();
        // 去掉可选的标题：url "title" / url 'title'
        var space = url.IndexOfAny([' ', '\t']);
        if (space > 0) url = url[..space];
        if (url.StartsWith('<') && url.EndsWith('>')) url = url[1..^1];
        if (url.Length == 0) return false;

        label = text[(open + 1)..closeBracket];
        index = end + 1;
        return true;
    }

    private static bool TryReadAutolink(string text, ref int index, out string url)
    {
        url = string.Empty;
        var end = text.IndexOf('>', index + 1);
        if (end < 0) return false;

        var candidate = text[(index + 1)..end].Trim();
        if (!LooksLikeUrl(candidate)) return false;

        url = candidate;
        index = end + 1;
        return true;
    }

    private static bool TryReadBareUrl(string text, ref int index, out string url)
    {
        url = string.Empty;
        if (index + 8 > text.Length) return false;

        var rest = text.AsSpan(index);
        if (!rest.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return false;

        var end = index;
        while (end < text.Length && !IsUrlTerminator(text[end])) end++;
        // 行尾常见的标点不算地址的一部分
        while (end > index && ".,;:!?\\)]}>'\"".Contains(text[end - 1])) end--;
        if (end - index < 10) return false;

        var candidate = text[index..end];
        if (!LooksLikeUrl(candidate)) return false;

        url = candidate;
        index = end;
        return true;
    }

    private static bool IsUrlTerminator(char ch) =>
        char.IsWhiteSpace(ch) || "（）[]{}<>\"'`，。！？；：、".Contains(ch);

    private static bool LooksLikeUrl(string text) =>
        text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("www.", StringComparison.OrdinalIgnoreCase);

    /// <summary>裸链接只在前一个字符不是单词字符时识别，避免把路径中间的一段当成链接。</summary>
    private static bool IsLinkBoundary(string text, int index) =>
        index == 0 || !IsWordChar(text[index - 1]);

    /// <summary>**粗体** / *斜体* / ~~删除线~~；找不到闭合标记时返回 false，交给调用方按字面文本输出。</summary>
    private static bool TryReadEmphasis(string text, ref int index, int depth, out MarkdownSpan span)
    {
        span = null!;
        var ch = text[index];
        var run = 1;
        while (index + run < text.Length && text[index + run] == ch) run++;

        if (ch == '~' && run < 2) return false;
        // snake_case、a_b 这类标识符里的下划线不算强调
        if (ch == '_' && index > 0 && IsWordChar(text[index - 1])) return false;

        var after = index + run < text.Length ? text[index + run] : ' ';
        if (char.IsWhiteSpace(after)) return false; // ** 后面紧跟空白不是合法的开始标记

        var delimiterLength = ch switch
        {
            '~' => 2,
            '_' => run >= 2 ? 2 : 1,
            _ => run >= 3 ? 3 : run == 2 ? 2 : 1
        };

        var close = FindClosingDelimiter(text, index + delimiterLength, ch, delimiterLength);
        if (close < 0) return false;

        var inner = ParseInlines(text[(index + delimiterLength)..close], depth + 1);
        span = new MarkdownSpan(inner,
            Bold: ch != '~' && delimiterLength >= 2,
            Italic: ch != '~' && (delimiterLength == 1 || delimiterLength == 3),
            Strikethrough: ch == '~');
        index = close + delimiterLength;
        return true;
    }

    private static int FindClosingDelimiter(string text, int start, char ch, int length)
    {
        var delimiter = new string(ch, length);
        var i = start;
        while (i + length <= text.Length)
        {
            var found = text.IndexOf(delimiter, i, StringComparison.Ordinal);
            if (found < 0) return -1;

            // 内容不能为空，闭合标记前不能是空白（否则 * 只是普通字符）
            if (found <= start || char.IsWhiteSpace(text[found - 1]))
            {
                i = found + 1;
                continue;
            }

            var after = found + length < text.Length ? text[found + length] : ' ';
            if (ch == '_' && IsWordChar(after))
            {
                i = found + 1;
                continue;
            }

            // 单个 * 作为闭合时，不能是 ** 或 *** 的一部分
            if (ch == '*' && length == 1 && (after == '*' || text[found - 1] == '*'))
            {
                i = found + 1;
                continue;
            }

            return found;
        }

        return -1;
    }

    private static bool IsWordChar(char ch) => char.IsLetterOrDigit(ch) || ch == '_';

    private static void AppendPlainText(StringBuilder builder, IReadOnlyList<MarkdownInline> inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case MarkdownText text:
                    builder.Append(text.Text);
                    break;
                case MarkdownCodeSpan code:
                    builder.Append(code.Text);
                    break;
                case MarkdownLink link:
                    builder.Append(link.Text);
                    break;
                case MarkdownSpan span:
                    AppendPlainText(builder, span.Children);
                    break;
            }
        }
    }

    #endregion
}
