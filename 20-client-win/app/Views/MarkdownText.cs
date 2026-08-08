// V20-⑤ -- 把 MarkdownLite 解析出来的块画成可选中、可复制的正文。
//
// ★★★ 为什么是 RichTextBox 而不是 TextBlock:
//   消息正文能**拖选 + Ctrl+C** 是它存在的唯一理由(见 Controls.xaml 里 PlainTextBox 那段说明)。
//   WPF 的 TextBlock 没有选择功能,换成它等于用"能看粗体"换掉"能复制" —— 那是净亏。
//   RichTextBox 两样都有,代价是要自己把它收拾干净(去边框/去底色/不吃滚轮)。
//
// ★★ 不吃滚轮这件事必须显式做:RichTextBox 自带一个 ScrollViewer,
//   放在会话区那个 ScrollViewer 里面就是**嵌套滚动** —— 滚轮落在气泡上时整屏不动。
//   走 PlainRichTextBox 样式(模板里两个方向都 Disabled),与 PlainTextBox 同一手法。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using LocalAI.Client.Services;

namespace LocalAI.Client.Views;

public static class MarkdownText
{
    /// <summary>代码块/内联代码的等宽字体。★ 写死一个族名而不是拄主题令牌:
    /// 主题里没有等宽令牌,而代码不等宽就读不出对齐 —— 缺了就该补,不该假装不需要。</summary>
    static readonly FontFamily Mono = new("Consolas, Cascadia Mono, Courier New, monospace");

    /// <summary>
    /// 画一段 markdown。<paramref name="user"/> = 自己发的(强调色气泡,字色跟着换)。
    /// <para>★ 用户自己发的消息**不走这里**(见 ChatView.Bubble):他打了什么就该看到什么,
    /// 把他输入的星号吃掉 = 界面在骗他"发出去的是粗体"。</para>
    /// </summary>
    public static FrameworkElement Build(string text, bool user)
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(0),
            // ★ 段间距靠 Paragraph.Margin 给,这里必须 0 —— 默认的段间距在气泡里过大
            LineHeight = double.NaN,
        };
        doc.SetResourceReference(FlowDocument.FontFamilyProperty, "FontUI");

        var blocks = MarkdownLite.Parse(text);
        for (int i = 0; i < blocks.Count; i++) doc.Blocks.Add(BlockOf(blocks[i], user, first: i == 0));

        var rtb = new RichTextBox { Document = doc };
        rtb.SetResourceReference(FrameworkElement.StyleProperty, "PlainRichTextBox");
        rtb.SetResourceReference(Control.ForegroundProperty, user ? "FgOnAccent" : "FgPrimary");
        rtb.SetResourceReference(RichTextBox.SelectionBrushProperty, user ? "FgOnAccent" : "Accent");
        return rtb;
    }

    /// <summary>这段文字里有没有值得渲染的记号。★ 没有就该走原来那条纯文本路 ——
    /// 绝大多数消息(尤其是用户自己发的)一个记号都没有,给它套个 RichTextBox 是白花钱。</summary>
    public static bool NeedsRendering(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var blocks = MarkdownLite.Parse(text);
        foreach (var b in blocks)
        {
            if (b.Kind != MdBlockKind.Paragraph) return true;
            foreach (var s in b.Spans)
                if (s.Bold || s.Italic || s.Code || s.Strike || s.Href is not null) return true;
        }
        return false;
    }

    static Block BlockOf(MdBlock b, bool user, bool first)
    {
        var topGap = first ? 0 : 6.0;
        switch (b.Kind)
        {
            case MdBlockKind.Code:
            {
                // ★ 代码块给底色 + 等宽 + 不换行截断由外层气泡的 MaxWidth 兜着。
                //   ★★ 正文**一个字符都不解析** —— 代码里的 ** 就是两个星号。
                var p = new Paragraph(new Run(b.CodeText))
                {
                    FontFamily = Mono,
                    Margin = new Thickness(0, topGap, 0, 0),
                    Padding = new Thickness(8, 6, 8, 6),
                };
                p.SetResourceReference(Block.BackgroundProperty, user ? "BgSunken" : "BgHover");
                return p;
            }
            case MdBlockKind.Rule:
            {
                // FlowDocument 里没有"分隔线"元素 —— 用一条细边框冒充,别用一行减号(那又变成记号了)
                var p = new Paragraph { Margin = new Thickness(0, topGap + 2, 0, 2), BorderThickness = new Thickness(0, 1, 0, 0) };
                p.SetResourceReference(Block.BorderBrushProperty, user ? "FgOnAccent" : "Border");
                return p;
            }
            case MdBlockKind.Heading:
            {
                var p = new Paragraph { Margin = new Thickness(0, topGap, 0, 0), FontWeight = FontWeights.SemiBold };
                // 标题在气泡里【只放大一点】:h1 也不该比气泡本身抢眼
                p.FontSize = b.Level <= 1 ? 17 : b.Level == 2 ? 15.5 : 14.5;
                Fill(p.Inlines, b.Spans, user);
                return p;
            }
            case MdBlockKind.Quote:
            {
                var p = new Paragraph { Margin = new Thickness(10, topGap, 0, 0), BorderThickness = new Thickness(2, 0, 0, 0), Padding = new Thickness(8, 0, 0, 0) };
                p.SetResourceReference(Block.BorderBrushProperty, user ? "FgOnAccent" : "Accent");
                Fill(p.Inlines, b.Spans, user);
                return p;
            }
            case MdBlockKind.Bullet:
            case MdBlockKind.Numbered:
            {
                // ★ 不用 WPF 的 List/ListItem:相邻的列表项是**独立的块**(解析器不合并),
                //   套 List 就要在这里再攒一次,而攒错的代价是序号乱掉。
                //   用"缩进 + 前缀"排版:看起来一样,而且流式时一项一项长出来不会跳。
                var marker = b.Kind == MdBlockKind.Bullet ? "· " : b.Ordinal + ". ";
                var p = new Paragraph
                {
                    Margin = new Thickness(6 + b.Level * 14, topGap == 0 ? 0 : 3, 0, 0),
                    TextIndent = -12,
                };
                p.Inlines.Add(new Run(marker));
                Fill(p.Inlines, b.Spans, user);
                return p;
            }
            default:
            {
                var p = new Paragraph { Margin = new Thickness(0, topGap, 0, 0) };
                Fill(p.Inlines, b.Spans, user);
                return p;
            }
        }
    }

    static void Fill(InlineCollection sink, IReadOnlyList<MdSpan> spans, bool user)
    {
        foreach (var s in spans)
        {
            Inline run = new Run(s.Text);
            if (s.Code)
            {
                run.FontFamily = Mono;
                // 内联代码给个轻底色 —— 只靠等宽在中文里几乎看不出来
                run.SetResourceReference(TextElement.BackgroundProperty, user ? "BgSunken" : "BgHover");
            }
            if (s.Bold) run.FontWeight = FontWeights.SemiBold;
            if (s.Italic) run.FontStyle = FontStyles.Italic;
            if (s.Strike) run.TextDecorations = TextDecorations.Strikethrough;
            if (s.Href is { Length: > 0 })
            {
                // ★★ 【不做成能点的超链接】:点一下就把浏览器打开是**外联动作**,
                //   而这段文字是**模型生成的** —— 那等于让模型决定去哪个网站。
                //   ⇒ 只标出来(下划线),地址放进 ToolTip 让人自己看清再决定。
                run.TextDecorations = TextDecorations.Underline;
                run.ToolTip = s.Href;
            }
            sink.Add(run);
        }
    }
}
