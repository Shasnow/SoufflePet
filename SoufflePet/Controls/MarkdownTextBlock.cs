// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SoufflePet.Utils;

namespace SoufflePet.Controls;

/// <summary>
/// 气泡正文的 Markdown 渲染控件：把 <see cref="Text"/> 解析成段落 / 标题 / 列表 / 代码块等块级元素，
/// 每块用可选中复制的 <see cref="SelectableTextBlock"/> 呈现。
/// 流式输出时只要块结构没变就只更新文字（复用已有控件），避免每个 token 都重建可视化树，
/// 也能保住用户正在做的文本选择。
/// </summary>
public sealed class MarkdownTextBlock : StackPanel
{
    /// <summary>代码统一用等宽字体；按优先级回退，缺少 Cascadia Mono 的机器会落到 Consolas。</summary>
    private static readonly FontFamily CodeFont = new("Cascadia Mono, Consolas, Microsoft YaHei Mono, Courier New");

    private static readonly IBrush CodeBackground = new SolidColorBrush(Color.Parse("#1A000000"));
    private static readonly IBrush CodeBorder = new SolidColorBrush(Color.Parse("#1FFFFFFF"));
    private static readonly IBrush InlineCodeBackground = new SolidColorBrush(Color.Parse("#1F000000"));
    private static readonly IBrush QuoteBorder = new SolidColorBrush(Color.Parse("#40E8879B"));
    private static readonly IBrush RuleBrush = new SolidColorBrush(Color.Parse("#26FFFFFF"));

    /// <summary>链接强调色：气泡只有助手消息会渲染 Markdown（深色底），用主题粉即可。</summary>
    private static readonly IBrush LinkForeground = new SolidColorBrush(Color.Parse("#F2A9B8"));

    /// <summary>列表每级缩进的宽度。</summary>
    private const double IndentWidth = 13;

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<MarkdownTextBlock, string?>(nameof(Text));

    /// <summary>要渲染的 Markdown 原文，流式输出时会被反复覆盖。</summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>已渲染的原文：内容没变时不重复解析。</summary>
    private string _renderedText = string.Empty;

    /// <summary>当前每个块的控件，与新解析结果按位置对齐复用。</summary>
    private readonly List<BlockSlot> _slots = [];

    static MarkdownTextBlock()
    {
        TextProperty.Changed.AddClassHandler<MarkdownTextBlock>((control, _) => control.Rebuild());
    }

    public MarkdownTextBlock()
    {
        Orientation = Orientation.Vertical;
        Spacing = 5;
    }

    /// <summary>隐藏期间跳过的解析在重新显示时补上。</summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && IsVisible) Rebuild();
    }

    /// <summary>重新解析并刷新子控件；块结构相同时就地更新文字。</summary>
    private void Rebuild()
    {
        var text = Text ?? string.Empty;
        if (text == _renderedText) return;

        // 不可见时不解析：控件的 Text 绑定始终有效（流式追加会持续触发），
        // 隐藏的气泡没必要为看不见的内容反复解析。这里不更新 _renderedText，
        // 等 IsVisible 变 true 时（见 OnPropertyChanged）再用最新文本补一次。
        if (!IsVisible) return;

        _renderedText = text;

        var blocks = MarkdownParser.ParseBlocks(text);
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            if (i < _slots.Count && _slots[i].Kind == block.Kind)
            {
                // 同类型块（流式输出里最常见：最后一段正文不断变长）只刷新文字
                _slots[i].Refresh(block);
                continue;
            }

            var slot = CreateSlot(block);
            if (i < _slots.Count)
            {
                Children[i] = slot.Control;
                _slots[i] = slot;
            }
            else
            {
                Children.Add(slot.Control);
                _slots.Add(slot);
            }
        }

        // 解析结果变短（例如流式中途出现又消失的代码围栏）时移除多余的块
        while (_slots.Count > blocks.Count)
        {
            Children.RemoveAt(Children.Count - 1);
            _slots.RemoveAt(_slots.Count - 1);
        }
    }

    private static BlockSlot CreateSlot(MarkdownBlock block) => block switch
    {
        MarkdownHeading heading => new HeadingSlot(heading),
        MarkdownCodeBlock code => new CodeSlot(code),
        MarkdownQuote quote => new QuoteSlot(quote),
        MarkdownListItem item => new ListItemSlot(item),
        MarkdownTable table => new TableSlot(table),
        MarkdownRule => new RuleSlot(),
        _ => new ParagraphSlot((MarkdownParagraph)block)
    };

    #region 块级控件

    /// <summary>一个块对应的控件；Kind 用于判断能否就地复用。</summary>
    private abstract class BlockSlot(MarkdownBlockKind kind)
    {
        /// <summary>上次渲染的内容签名：内容没变就不刷新，流式输出时前面已完成的块不必反复重建。</summary>
        private string? _signature;

        public MarkdownBlockKind Kind { get; } = kind;

        public abstract Control Control { get; }

        /// <summary>内容有变化时才真正刷新控件。</summary>
        public void Refresh(MarkdownBlock block)
        {
            var signature = SignatureOf(block);
            if (signature == _signature) return;
            _signature = signature;
            Update(block);
        }

        protected abstract void Update(MarkdownBlock block);
    }

    /// <summary>块的内容签名：只要能区分「文字有没有变」就够，不参与渲染。</summary>
    private static string SignatureOf(MarkdownBlock block) => block switch
    {
        MarkdownParagraph paragraph => MarkdownParser.ToPlainText(paragraph.Inlines),
        MarkdownHeading heading => $"h{heading.Level}:{MarkdownParser.ToPlainText(heading.Inlines)}",
        MarkdownCodeBlock code => $"{code.Language}\u0000{code.Code}",
        MarkdownQuote quote => MarkdownParser.ToPlainText(quote.Inlines),
        MarkdownListItem item =>
            $"{item.Marker}|{item.Indent}|{item.TaskChecked}|{MarkdownParser.ToPlainText(item.Inlines)}",
        MarkdownTable table => TableSignature(table),
        _ => block.Kind.ToString()
    };

    private static string TableSignature(MarkdownTable table)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var header in table.Headers) builder.Append(header).Append('\u0001');
        builder.Append('\u0002');
        foreach (var row in table.Rows)
        {
            foreach (var cell in row) builder.Append(cell).Append('\u0001');
            builder.Append('\u0002');
        }

        return builder.ToString();
    }

    private sealed class ParagraphSlot : BlockSlot
    {
        private readonly InlinePresenter _presenter = new();

        public ParagraphSlot(MarkdownParagraph block) : base(MarkdownBlockKind.Paragraph) => Refresh(block);

        public override Control Control => _presenter.Text;

        protected override void Update(MarkdownBlock block) =>
            _presenter.Fill(((MarkdownParagraph)block).Inlines);
    }

    private sealed class HeadingSlot : BlockSlot
    {
        private readonly InlinePresenter _presenter = new();

        public HeadingSlot(MarkdownHeading block) : base(MarkdownBlockKind.Heading)
        {
            _presenter.Text.FontWeight = FontWeight.SemiBold;
            Refresh(block);
        }

        public override Control Control => _presenter.Text;

        protected override void Update(MarkdownBlock block)
        {
            var heading = (MarkdownHeading)block;
            // 字号在样式基础上本地覆盖（本地值优先级高于样式），LineHeight 同步放大避免大字号被裁切
            var (size, lineHeight) = heading.Level switch
            {
                1 => (17d, 24d),
                2 => (16d, 23d),
                3 => (15d, 22d),
                _ => (13.5d, 20d)
            };
            _presenter.Text.FontSize = size;
            _presenter.Text.LineHeight = lineHeight;
            _presenter.Fill(heading.Inlines);
        }
    }

    private sealed class CodeSlot : BlockSlot
    {
        // 气泡只有 292px 宽，代码按长行横向滚动既不顺手、滚动条还会挡住内容，
        // 这里改为折行显示，保证每一行都能完整看到
        private readonly SelectableTextBlock _code = new()
        {
            FontFamily = CodeFont,
            FontSize = 11.5,
            LineHeight = 17,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(10, 6, 10, 9)
        };

        private readonly TextBlock _caption = new()
        {
            FontFamily = CodeFont,
            FontSize = 10,
            Opacity = 0.6,
            Margin = new Thickness(10, 6, 10, 0)
        };

        private readonly StackPanel _content = new() { Orientation = Orientation.Vertical };

        private readonly Border _border = new()
        {
            Background = CodeBackground,
            BorderBrush = CodeBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true
        };

        public CodeSlot(MarkdownCodeBlock block) : base(MarkdownBlockKind.CodeBlock)
        {
            _content.Children.Add(_caption);
            _content.Children.Add(_code);
            _border.Child = _content;
            Refresh(block);
        }

        public override Control Control => _border;

        protected override void Update(MarkdownBlock block)
        {
            var code = (MarkdownCodeBlock)block;
            _code.Text = code.Code;
            _caption.Text = code.Language ?? string.Empty;
            _caption.IsVisible = code.Language is { Length: > 0 };
        }
    }

    private sealed class QuoteSlot : BlockSlot
    {
        private readonly InlinePresenter _presenter = new();

        private readonly Border _border = new()
        {
            BorderBrush = QuoteBorder,
            BorderThickness = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(9, 1, 0, 1)
        };

        public QuoteSlot(MarkdownQuote block) : base(MarkdownBlockKind.Quote)
        {
            // 引用内容比正文弱化一档，仍继承气泡的前景色以适配左右两种气泡
            _presenter.Text.Opacity = 0.85;
            _border.Child = _presenter.Text;
            Refresh(block);
        }

        public override Control Control => _border;

        protected override void Update(MarkdownBlock block) =>
            _presenter.Fill(((MarkdownQuote)block).Inlines);
    }

    private sealed class ListItemSlot : BlockSlot
    {
        /// <summary>
        /// 真实 CheckBox 的外观资源覆盖：Fluent 默认是 32 高的行与系统蓝色强调色，这里缩到 20 高；
        /// 选中态用浅色底 + 深色勾而不是强调色底——强调色和用户气泡底色相同，勾选框会「融进」气泡里，
        /// 浅色底在深色助手气泡和粉色用户气泡里都看得清。
        /// ResourceDictionary 只能有一个宿主，所以每个勾选框各建一份。
        /// </summary>
        private static ResourceDictionary CreateTaskBoxResources() => new()
        {
            ["CheckBoxMinHeight"] = 20d,
            ["CheckBoxCheckBackgroundFillUnchecked"] = new SolidColorBrush(Color.Parse("#17FFFFFF")),
            ["CheckBoxCheckBackgroundStrokeUnchecked"] = new SolidColorBrush(Color.Parse("#99FFFFFF")),
            ["CheckBoxCheckBackgroundFillChecked"] = new SolidColorBrush(Color.Parse("#F2FFFFFF")),
            ["CheckBoxCheckGlyphForegroundChecked"] = new SolidColorBrush(Color.Parse("#1F1D24"))
        };

        private readonly InlinePresenter _presenter = new();

        private readonly TextBlock _marker = new()
        {
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 6, 0),
            Opacity = 0.75
        };

        // 只做展示：勾选状态来自消息原文，点它不会（也不应该）改动消息内容
        private readonly CheckBox _checkbox = new()
        {
            IsHitTestVisible = false,
            Focusable = false,
            Padding = new Thickness(0),
            MinHeight = 20,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 6, 0),
            Resources = CreateTaskBoxResources()
        };

        private readonly Grid _grid = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*") };

        public ListItemSlot(MarkdownListItem block) : base(MarkdownBlockKind.ListItem)
        {
            _grid.Children.Add(_marker);
            _grid.Children.Add(_checkbox);
            Grid.SetColumn(_presenter.Text, 1);
            _grid.Children.Add(_presenter.Text);
            Refresh(block);
        }

        public override Control Control => _grid;

        protected override void Update(MarkdownBlock block)
        {
            var item = (MarkdownListItem)block;

            _marker.IsVisible = item.TaskChecked is null;
            _checkbox.IsVisible = item.TaskChecked is not null;
            if (item.TaskChecked is { } isChecked) _checkbox.IsChecked = isChecked;
            else _marker.Text = item.Marker is { } marker ? $"{marker}." : "•";

            _grid.Margin = new Thickness(item.Indent * IndentWidth, 0, 0, 0);
            _presenter.Fill(item.Inlines);
        }
    }

    /// <summary>
    /// 表格：按各列内容长度分配宽度（星号比例），单元格内折行。
    /// 气泡宽度只有 292px，横向滚动条在这里既不美观也不方便，所以宁可让单元格折行。
    /// </summary>
    private sealed class TableSlot : BlockSlot
    {
        private readonly Grid _grid = new();

        public TableSlot(MarkdownTable block) : base(MarkdownBlockKind.Table) => Refresh(block);

        public override Control Control => _grid;

        protected override void Update(MarkdownBlock block)
        {
            var table = (MarkdownTable)block;
            _grid.Children.Clear();
            _grid.ColumnDefinitions.Clear();
            _grid.RowDefinitions.Clear();

            var columnCount = table.Headers.Count;
            foreach (var row in table.Rows) columnCount = Math.Max(columnCount, row.Count);
            columnCount = Math.Max(columnCount, 1);

            for (var column = 0; column < columnCount; column++)
                _grid.ColumnDefinitions.Add(new ColumnDefinition(ColumnWeight(table, column), GridUnitType.Star));

            // 表头 + 分隔线 + 每个数据行各占一行
            for (var row = 0; row < table.Rows.Count + 2; row++)
                _grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            for (var column = 0; column < columnCount; column++)
            {
                var header = column < table.Headers.Count ? table.Headers[column] : string.Empty;
                AddCell(new TextBlock
                {
                    Text = header,
                    FontWeight = FontWeight.SemiBold,
                    Margin = new Thickness(0, 0, 8, 4)
                }, row: 0, column);
            }

            // 表头与数据之间的细分隔线（跨所有列）
            var separator = new Border { Height = 1, Background = RuleBrush, Margin = new Thickness(0, 0, 0, 4) };
            _grid.Children.Add(separator);
            Grid.SetRow(separator, 1);
            Grid.SetColumnSpan(separator, columnCount);

            for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
            {
                var row = table.Rows[rowIndex];
                for (var column = 0; column < columnCount; column++)
                {
                    var text = column < row.Count ? row[column] : string.Empty;
                    AddCell(new TextBlock
                    {
                        Text = text,
                        Opacity = 0.92,
                        Margin = new Thickness(0, 0, 8, 3)
                    }, rowIndex + 2, column);
                }
            }
        }

        /// <summary>列宽权重：按该列最长内容的显示宽度取，限制在 3~22 之间避免某列被挤没或吃掉整行。</summary>
        private static double ColumnWeight(MarkdownTable table, int column)
        {
            var longest = 3;
            if (column < table.Headers.Count) longest = Math.Max(longest, DisplayWidth(table.Headers[column]));
            foreach (var row in table.Rows)
                if (column < row.Count)
                    longest = Math.Max(longest, DisplayWidth(row[column]));

            return Math.Clamp(longest, 3, 22);
        }

        /// <summary>估算显示宽度：中文等全角字符约占两个半角字符。</summary>
        private static int DisplayWidth(string text)
        {
            var width = 0;
            foreach (var ch in text) width += ch > 0x2E80 ? 2 : 1;
            return width;
        }

        private void AddCell(TextBlock cell, int row, int column)
        {
            cell.TextWrapping = TextWrapping.Wrap;
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, column);
            _grid.Children.Add(cell);
        }
    }

    private sealed class RuleSlot : BlockSlot
    {
        public RuleSlot() : base(MarkdownBlockKind.Rule) { }

        public override Control Control { get; } = new Border
        {
            Height = 1,
            Background = RuleBrush,
            Margin = new Thickness(0, 3, 0, 3),
            VerticalAlignment = VerticalAlignment.Center
        };

        protected override void Update(MarkdownBlock block)
        {
            // 分割线没有可变的文字内容
        }
    }

    #endregion

    #region 行内渲染

    /// <summary>
    /// 一段块级正文的载体：可选中复制的文本框 + 链接点击。
    /// 链接按普通文本渲染（下划线）而不是行内控件，这样长地址也能跟着正文正常折行；
    /// 点击时用文本命中测试判断是否落在链接上。
    /// </summary>
    private sealed class InlinePresenter
    {
        private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
        private static readonly Cursor TextCursor = new(StandardCursorType.Ibeam);

        /// <summary>链接在文本框纯文本里的位置：流式更新时随 Fill 重建。</summary>
        private readonly List<LinkRange> _links = [];

        public InlinePresenter()
        {
            Text = new SelectableTextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.None,
                Cursor = TextCursor
            };
            Text.PointerMoved += OnPointerMoved;
            Text.PointerReleased += OnPointerReleased;
        }

        public SelectableTextBlock Text { get; }

        public void Fill(IReadOnlyList<MarkdownInline> inlines)
        {
            _links.Clear();
            // Inlines 默认为空集合，但属性本身可空：兜底时把新建的集合挂回去，否则加进去的内容不会显示
            var collection = Text.Inlines;
            if (collection is null)
            {
                collection = new InlineCollection();
                Text.Inlines = collection;
            }

            collection.Clear();
            var offset = 0;
            Append(collection, inlines, bold: false, italic: false, strikethrough: false, ref offset);
        }

        private void Append(InlineCollection target, IReadOnlyList<MarkdownInline> inlines,
            bool bold, bool italic, bool strikethrough, ref int offset)
        {
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case MarkdownText text:
                        target.Add(CreateRun(text.Text, bold, italic, strikethrough));
                        offset += text.Text.Length;
                        break;
                    case MarkdownCodeSpan code:
                        target.Add(CreateInlineCodeRun(code.Text));
                        offset += code.Text.Length;
                        break;
                    case MarkdownLink link:
                        var label = link.Text.Length > 0 ? link.Text : link.Url;
                        // 强调色 + 下划线：深色气泡里链接要一眼能认出来（Markdown 只用于助手气泡）
                        target.Add(new Run(label)
                        {
                            Foreground = LinkForeground,
                            TextDecorations = TextDecorations.Underline
                        });
                        _links.Add(new LinkRange(offset, label.Length, link.Url));
                        offset += label.Length;
                        break;
                    case MarkdownSpan span:
                        // 嵌套强调直接叠加样式，不必再套一层 Span
                        Append(target, span.Children, bold || span.Bold, italic || span.Italic,
                            strikethrough || span.Strikethrough, ref offset);
                        break;
                }
            }
        }

        private void OnPointerMoved(object? sender, PointerEventArgs e)
        {
            var cursor = FindLink(e.GetPosition(Text)) is null ? TextCursor : HandCursor;
            if (!ReferenceEquals(Text.Cursor, cursor)) Text.Cursor = cursor;
        }

        private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            // 正在选文本（拖动过）时不触发链接，避免复制地址时误开浏览器
            if (Text.SelectionStart != Text.SelectionEnd) return;
            if (FindLink(e.GetPosition(Text)) is not { } url) return;

            e.Handled = true;
            OpenUrl(url);
        }

        private string? FindLink(Point point)
        {
            var layout = Text.TextLayout;
            if (layout is null) return null;

            var hit = layout.HitTestPoint(point);
            if (!hit.IsInside) return null;

            foreach (var link in _links)
                if (hit.TextPosition >= link.Start && hit.TextPosition < link.Start + link.Length)
                    return link.Url;

            return null;
        }

        private readonly record struct LinkRange(int Start, int Length, string Url);
    }

    private static Run CreateRun(string text, bool bold, bool italic, bool strikethrough)
    {
        var run = new Run(text);
        if (bold) run.FontWeight = FontWeight.SemiBold; // 半粗比 Bold 更贴近气泡正文的观感
        if (italic) run.FontStyle = FontStyle.Italic;
        if (strikethrough) run.TextDecorations = TextDecorations.Strikethrough;
        return run;
    }

    private static Run CreateInlineCodeRun(string text) => new(text)
    {
        FontFamily = CodeFont,
        FontSize = 12,
        Background = InlineCodeBackground
    };

    private static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // 没有默认浏览器或地址非法时忽略，不能因为点了个链接把界面弄崩
        }
    }

    #endregion
}
