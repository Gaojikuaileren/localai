// P3c -- 学习笔记板块(右侧抽屉打开)。按【目标语言】分类;可编辑、可删除。
//   一次翻多语言时笔记是【拆开存】的(见 NoteCenter.AddSplit),所以这里按语言浏览不会混。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LocalAI.Client.Services;
using LocalAI.Client.Theme;

namespace LocalAI.Client.Views;

public sealed class NotesBoardView : UserControl
{
    static App TheApp => (App)Application.Current;
    readonly StackPanel _root = new();

    public NotesBoardView()
    {
        Content = new ScrollViewer
        {
            Content = _root,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        }.PassThrough();
        Build();
        Loaded += (_, _) => TheApp.Notes.Changed += Build;
        Unloaded += (_, _) => TheApp.Notes.Changed -= Build;
    }

    void Build()
    {
        _root.Children.Clear();
        var langs = TheApp.Notes.LanguagesUsed().ToList();
        if (langs.Count == 0)
        {
            _root.Children.Add(Ui.Body("还没有学习笔记。", muted: true));
            _root.Children.Add(Ui.Caption("翻译结果右侧的【收藏】会把它存到这里,并按目标语言分类;一次翻多种语言会拆开分别存。"));
            // ★ 2026-08-05 审计改写:原文「AI 未接入前」—— 模型 S11 已接入,没接的是翻译引擎。
            _root.Children.Add(Ui.Caption("翻译引擎接上之前,也可以手动新建一条 —— 格式与档位一致。"));
        }
        else
        {
            foreach (var (lang, count) in langs)
            {
                var box = new StackPanel();
                foreach (var n in TheApp.Notes.Of(lang)) box.Children.Add(Row(n));
                _root.Children.Add(Ui.Panel($"{Languages.NameOf(lang)}({count})", box,
                    IconName.Translation, new Thickness(0, 0, 0, 8), compact: true));
            }
        }
        var add = Ui.Secondary("＋ 手动新建笔记", (_, _) => OpenEditor(null));
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Margin = new Thickness(0, 6, 0, 0);
        _root.Children.Add(add);
    }

    FrameworkElement Row(StudyNote n)
    {
        var src = new TextBlock { Text = n.SourceText, TextTrimming = TextTrimming.CharacterEllipsis };
        src.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        src.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        var tr = new TextBlock { Text = n.Translation, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        tr.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");

        var col = new StackPanel();
        col.Children.Add(src);
        col.Children.Add(tr);
        if (!string.IsNullOrWhiteSpace(n.Reading)) col.Children.Add(Ui.Caption(n.Reading!));
        if (!string.IsNullOrWhiteSpace(n.ExampleSource))
            col.Children.Add(Ui.Caption($"例:{n.ExampleSource}" + (string.IsNullOrWhiteSpace(n.ExampleTranslation) ? "" : $" —— {n.ExampleTranslation}")));
        if (n.Words is { Count: > 0 })
            col.Children.Add(Ui.Caption("逐词:" + string.Join(" / ", n.Words.Select(w => $"{w.Word}({w.Pos}){w.Meaning}"))));
        col.Children.Add(Ui.Caption($"{TranslationLevels.NameOf(n.Level)} · {n.CreatedAt:M月d日}" + (n.CreatedByAi ? " · AI" : " · 手动")));

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        actions.Children.Add(Chip("编辑", "FgSecondary", () => OpenEditor(n)));
        actions.Children.Add(Chip("删除", "RiskDanger", () =>
        {
            if (ConfirmDialog.Show("删除笔记", "删除这条笔记?\n\n" + n.Translation, confirmText: "删除", danger: true))
                TheApp.Notes.Remove(n.Id);
        }));

        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 6, 0, 6) };
        DockPanel.SetDock(actions, Dock.Right);
        row.Children.Add(actions);
        row.Children.Add(col);
        return row;
    }

    void OpenEditor(StudyNote? existing)
        => (Application.Current.MainWindow as MainWindow)?.OpenSideDrawer(
            existing is null ? "新建学习笔记" : "编辑学习笔记",
            NoteEditor.Build(existing, TheApp.Translation.Level, TheApp.Translation.Targets),
            IconName.Translation);

    static FrameworkElement Chip(string text, string colorKey, Action onClick)
    {
        var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, colorKey);
        t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        var b = new Border { Child = t, Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(0, 0, 6, 4), Cursor = Cursors.Hand, Background = Brushes.Transparent, BorderThickness = new Thickness(1) };
        b.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        b.SetResourceReference(Border.BorderBrushProperty, "Border");
        b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "BgHover");
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick(); };
        return b;
    }
}
