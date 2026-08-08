// V20-⑤ -- 够用的 markdown 解析器。**纯函数,不碰 WPF** —— 渲染在 Views/MarkdownText.cs。
//
// ★★★ 为什么需要它:用户实测,回答里的 `**"Come for dinner tomorrow."**` 原样把星号画给了人看。
//   模型**一定**会输出 markdown(它就是那么被训出来的),而这一侧从来没有解析器 ——
//   于是每一次强调、每一段代码、每一条列表都变成一堆记号。
//
// ★★ 分成"解析"与"渲染"两个文件是为了让判据能为假:
//   记号认得对不对是**纯文本进、结构出**的问题,自检可以逐种记号喂进来对答案;
//   而"画出来好不好看"没法自动断言。把两者混在一个 WPF 方法里,能测的那一半也就测不了了。
//
// ★ 范围是**有意窄**的:只认模型真的会吐、且在聊天气泡里讲得通的那些记号。
//   表格、脚注、HTML 内联一律**不认**(原样显示)—— 认一半的表格比不认更难读。
//   ⇒ 不认的东西必须**原样保留**,绝不吞掉:吞掉 = 用户看不到模型说过的字。

using System.Text;

namespace LocalAI.Client.Services;

/// <summary>块级记号的种类。</summary>
public enum MdBlockKind
{
    /// <summary>普通段落。</summary>
    Paragraph,
    /// <summary>标题(<c># … ######</c>),级别见 <see cref="MdBlock.Level"/>。</summary>
    Heading,
    /// <summary>无序列表项(<c>- * +</c>)。</summary>
    Bullet,
    /// <summary>有序列表项,序号原文见 <see cref="MdBlock.Ordinal"/>。</summary>
    Numbered,
    /// <summary>围栏代码块(```)。正文在 <see cref="MdBlock.CodeText"/>,**不做内联解析**。</summary>
    Code,
    /// <summary>引用(<c>&gt; </c>)。</summary>
    Quote,
    /// <summary>分隔线(<c>---</c> / <c>***</c> / <c>___</c>)。</summary>
    Rule,
}

/// <summary>
/// 一段带样式的文字。★ 四个样式是**可叠加**的(<c>**`a`**</c> 既粗又是代码),
/// 所以用四个 bool 而不是一个枚举 —— 枚举会逼着调用方在"粗"和"代码"里选一个。
/// </summary>
public sealed record MdSpan(string Text, bool Bold = false, bool Italic = false,
                            bool Code = false, bool Strike = false, string? Href = null);

/// <summary>一个块。</summary>
public sealed record MdBlock(MdBlockKind Kind, IReadOnlyList<MdSpan> Spans,
                             int Level = 0, string Ordinal = "", string CodeText = "",
                             string CodeLang = "");

public static class MarkdownLite
{
    /// <summary>围栏代码块的围栏。★ 只认三个及以上的反引号 —— 单个是内联代码。</summary>
    const string Fence = "```";

    // ══════════════════════════════════════════════════════════════════
    //  块级
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 把一段文字切成块。★ 换行**有意义**:模型的回答靠空行分段,合并成一行会读不懂。
    /// <para>★★ 围栏代码块**优先于一切**:里面的 <c>**</c>、<c>#</c>、<c>-</c> 都是代码,
    /// 不是记号。先切围栏再分段,顺序反了会把代码里的井号解析成标题。</para>
    /// </summary>
    public static IReadOnlyList<MdBlock> Parse(string? text)
    {
        var blocks = new List<MdBlock>();
        if (string.IsNullOrEmpty(text)) return blocks;
        // ★ 只按 \n 切,行尾的 \r 单独剃掉 —— 按 "\r\n" 切会漏掉纯 \r 的那种。
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        var para = new List<string>();   // 正在攒的段落原文行
        void FlushPara()
        {
            if (para.Count == 0) return;
            blocks.Add(new MdBlock(MdBlockKind.Paragraph, ParseInline(string.Join("\n", para))));
            para.Clear();
        }

        for (int i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var t = raw.TrimStart();

            // ---- 围栏代码块 ----
            if (t.StartsWith(Fence, StringComparison.Ordinal))
            {
                FlushPara();
                var lang = t[Fence.Length..].Trim();
                var code = new List<string>();
                int j = i + 1;
                var closed = false;
                for (; j < lines.Length; j++)
                {
                    if (lines[j].TrimStart().StartsWith(Fence, StringComparison.Ordinal)) { closed = true; break; }
                    code.Add(lines[j]);
                }
                // ★★ 没收尾的围栏(流式还没吐完 / 模型忘了收)照样当代码块渲染到末尾 ——
                //   退回当普通段落会让**已经吐出来的代码**中途换一次样式,看着像出错了。
                blocks.Add(new MdBlock(MdBlockKind.Code, Array.Empty<MdSpan>(),
                                       CodeText: string.Join("\n", code), CodeLang: lang));
                i = closed ? j : lines.Length;
                continue;
            }

            // ---- 空行 = 段落边界 ----
            if (t.Length == 0) { FlushPara(); continue; }

            // ---- 分隔线:整行只有 3 个以上的 - * _ ----
            if (IsRule(t)) { FlushPara(); blocks.Add(new MdBlock(MdBlockKind.Rule, Array.Empty<MdSpan>())); continue; }

            // ---- 标题 ----
            var hashes = 0;
            while (hashes < t.Length && t[hashes] == '#') hashes++;
            if (hashes is >= 1 and <= 6 && hashes < t.Length && t[hashes] == ' ')
            {
                FlushPara();
                blocks.Add(new MdBlock(MdBlockKind.Heading, ParseInline(t[(hashes + 1)..].Trim()), Level: hashes));
                continue;
            }

            // ---- 引用 ----
            if (t.StartsWith("> ", StringComparison.Ordinal) || t == ">")
            {
                FlushPara();
                blocks.Add(new MdBlock(MdBlockKind.Quote, ParseInline(t.Length > 1 ? t[2..] : "")));
                continue;
            }

            // ---- 无序列表:`- ` / `* ` / `+ `。★ 必须带空格 ——
            //   否则 "*强调*开头的一句" 会被当成列表项,而那是很常见的一句话。
            if (t.Length > 1 && (t[0] is '-' or '*' or '+') && t[1] == ' ')
            {
                FlushPara();
                blocks.Add(new MdBlock(MdBlockKind.Bullet, ParseInline(t[2..].TrimStart()),
                                       Level: IndentOf(raw)));
                continue;
            }

            // ---- 有序列表:`1. ` / `2) ` ----
            var digits = 0;
            while (digits < t.Length && char.IsAsciiDigit(t[digits])) digits++;
            if (digits is > 0 and <= 3 && digits + 1 < t.Length
                && (t[digits] is '.' or ')') && t[digits + 1] == ' ')
            {
                FlushPara();
                blocks.Add(new MdBlock(MdBlockKind.Numbered, ParseInline(t[(digits + 2)..].TrimStart()),
                                       Level: IndentOf(raw), Ordinal: t[..digits]));
                continue;
            }

            para.Add(t);
        }
        FlushPara();
        return blocks;
    }

    static bool IsRule(string t)
    {
        if (t.Length < 3) return false;
        var c = t[0];
        if (c is not ('-' or '*' or '_')) return false;
        foreach (var ch in t) if (ch != c && ch != ' ') return false;
        return t.Count(x => x == c) >= 3;
    }

    /// <summary>缩进深度(每 2 空格算一级,Tab 算一级)。列表嵌套只用它排版,不改语义。</summary>
    static int IndentOf(string raw)
    {
        int sp = 0, tabs = 0;
        foreach (var ch in raw)
        {
            if (ch == ' ') sp++;
            else if (ch == '\t') tabs++;
            else break;
        }
        return Math.Min(4, tabs + sp / 2);
    }

    // ══════════════════════════════════════════════════════════════════
    //  内联
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 解析一行(或一段)里的内联记号。
    /// <para>★★★ 认不出来的记号**原样留在文字里**。这是本文件最要紧的一条:
    /// 一个"半吞半留"的解析器会把模型说过的字悄悄弄丢,而那比原样显示星号坏得多。</para>
    /// </summary>
    public static IReadOnlyList<MdSpan> ParseInline(string? line)
    {
        var sink = new List<MdSpan>();
        if (string.IsNullOrEmpty(line)) return sink;
        Emit(line, false, false, false, null, sink);
        return Merge(sink);
    }

    static void Emit(string s, bool bold, bool italic, bool strike, string? href, List<MdSpan> sink)
    {
        var buf = new StringBuilder();
        void Flush()
        {
            if (buf.Length == 0) return;
            sink.Add(new MdSpan(buf.ToString(), bold, italic, false, strike, href));
            buf.Clear();
        }

        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];

            // ---- 内联代码:`…`。★ 优先于所有其它记号 —— 里面的星号是代码,不是强调。
            if (c == '`')
            {
                var end = s.IndexOf('`', i + 1);
                if (end > i + 1)
                {
                    Flush();
                    sink.Add(new MdSpan(s[(i + 1)..end], bold, italic, true, strike, href));
                    i = end;
                    continue;
                }
                buf.Append(c);   // 没有配对的反引号 -> 原样
                continue;
            }

            // ---- 链接:[文字](地址)。★ 地址只留在 Href 里,不画出来(画出来一屏全是 URL)。
            if (c == '[' && href is null)
            {
                var close = s.IndexOf(']', i + 1);
                if (close > i && close + 1 < s.Length && s[close + 1] == '(')
                {
                    var end = s.IndexOf(')', close + 2);
                    if (end > close)
                    {
                        Flush();
                        var url = s[(close + 2)..end].Trim();
                        // ★ 空标签([](x))时把地址本身当文字 —— 否则整段凭空消失
                        var label = close > i + 1 ? s[(i + 1)..close] : url;
                        Emit(label, bold, italic, strike, url, sink);
                        i = end;
                        continue;
                    }
                }
                buf.Append(c);
                continue;
            }

            // ---- 删除线:~~…~~
            if (c == '~' && i + 1 < s.Length && s[i + 1] == '~' && !strike)
            {
                var end = s.IndexOf("~~", i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    Flush();
                    Emit(s[(i + 2)..end], bold, italic, true, href, sink);
                    i = end + 1;
                    continue;
                }
                buf.Append(c);
                continue;
            }

            // ---- 粗体:** 或 __(先试双、再试单,顺序反了 ** 会被当成两个 *)
            if ((c == '*' || c == '_') && i + 1 < s.Length && s[i + 1] == c && !bold)
            {
                var delim = new string(c, 2);
                var end = FindCloser(s, i + 2, delim, c == '_');
                if (end > 0)
                {
                    Flush();
                    Emit(s[(i + 2)..end], true, italic, strike, href, sink);
                    i = end + 1;
                    continue;
                }
            }

            // ---- 斜体:* 或 _
            if ((c == '*' || c == '_') && !italic && CanOpen(s, i, c))
            {
                var end = FindCloser(s, i + 1, c.ToString(), c == '_');
                if (end > 0)
                {
                    Flush();
                    Emit(s[(i + 1)..end], bold, true, strike, href, sink);
                    i = end;
                    continue;
                }
            }

            buf.Append(c);
        }
        Flush();
    }

    /// <summary>
    /// 这个位置的 <c>*</c>/<c>_</c> 能不能当**开始**记号。
    /// <para>★ 两条都是为了不误伤日常文字:
    /// ① 后面紧跟空白的不算(<c>2 * 3</c> 是乘法,不是强调);
    /// ② <c>_</c> 夹在词里的不算(<c>vram_budget_toml</c> —— 这个仓库里到处都是)。</para>
    /// </summary>
    static bool CanOpen(string s, int i, char c)
    {
        if (i + 1 >= s.Length) return false;
        if (char.IsWhiteSpace(s[i + 1])) return false;
        if (c == '_' && i > 0 && IsWord(s[i - 1])) return false;
        return true;
    }

    /// <summary>
    /// 找配对的收尾记号,返回它的起始下标;找不到返回 -1。
    /// <para>★ 收尾记号前面**不许是空白**(<c>*a *</c> 不是强调),
    /// <c>_</c> 还要求后面不是词字符(同 CanOpen 的理由)。</para>
    /// </summary>
    static int FindCloser(string s, int from, string delim, bool underscore)
    {
        for (int k = from; k + delim.Length <= s.Length; k++)
        {
            if (string.CompareOrdinal(s, k, delim, 0, delim.Length) != 0) continue;
            if (k == from) continue;                                  // 空内容(**** )不算
            if (char.IsWhiteSpace(s[k - 1])) continue;
            if (underscore && k + delim.Length < s.Length && IsWord(s[k + delim.Length])) continue;
            return k;
        }
        return -1;
    }

    static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>相邻且样式相同的段合并。★ 只为让判据好写、渲染少几个 Run,不改内容。</summary>
    static List<MdSpan> Merge(List<MdSpan> spans)
    {
        var out_ = new List<MdSpan>();
        foreach (var sp in spans)
        {
            if (sp.Text.Length == 0) continue;
            if (out_.Count > 0)
            {
                var last = out_[^1];
                if (last.Bold == sp.Bold && last.Italic == sp.Italic && last.Code == sp.Code
                    && last.Strike == sp.Strike && last.Href == sp.Href)
                {
                    out_[^1] = last with { Text = last.Text + sp.Text };
                    continue;
                }
            }
            out_.Add(sp);
        }
        return out_;
    }

    // ══════════════════════════════════════════════════════════════════
    //  纯文本落点
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 把记号去掉、内容留下 —— 给**画不了富文本的落点**用(状态行 / 提示 / 工具提示)。
    /// <para>★★ 这个项目自己的文案里就有 <c>**</c>(<c>IntentOutcome.Advice</c> 等),
    /// 而自检里那条「界面文案里不允许出现字面 **」只扫了四个视图文件。
    /// 与其在每处文案里迁就渲染能力,不如让纯文本落点**主动剃掉记号**。</para>
    /// </summary>
    public static string ToPlainText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder();
        foreach (var b in Parse(text))
        {
            if (sb.Length > 0) sb.Append('\n');
            switch (b.Kind)
            {
                case MdBlockKind.Code: sb.Append(b.CodeText); break;
                case MdBlockKind.Rule: sb.Append("———"); break;
                case MdBlockKind.Bullet: sb.Append("· ").Append(Flat(b.Spans)); break;
                case MdBlockKind.Numbered: sb.Append(b.Ordinal).Append(". ").Append(Flat(b.Spans)); break;
                default: sb.Append(Flat(b.Spans)); break;
            }
        }
        return sb.ToString();
    }

    static string Flat(IReadOnlyList<MdSpan> spans)
    {
        var sb = new StringBuilder();
        foreach (var s in spans) sb.Append(s.Text);
        return sb.ToString();
    }
}
