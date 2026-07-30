// P3c -- 翻译工作空间的中间区(会话区里的翻译外壳)。
//
// 用户裁定(2026-07-30):
//   · 顶部:【常用语言池】拖拽到【目标语言池】(上限 3);
//   · 规则:输入某语言 -> 翻成池内【除它以外】的全部;输入语种不在池中 -> 先入池再翻(见 TranslationState);
//   · 中部:【翻译程度】滑条,四档 精简 / 带读音 / 带例句 / 详解 —— 每档对应固定的产出格式;
//   · AI 的翻译回复右侧有【收藏】按钮,存进【学习笔记】;多语言翻译按语言【拆开存】;
//   · 学习笔记按语言分类,可删可编辑。
//
// ★ 诚实:翻译本身要 AI(P4 未接入)。所以这里【不产出任何译文】——
//   语言池、档位、笔记的增删改都是真的,但"翻译结果"要等模型接上。界面如实说明,绝不假装翻了。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LocalAI.Client.Services;
using LocalAI.Client.Theme;

namespace LocalAI.Client.Views;

public sealed class TranslationView : UserControl
{
    static App TheApp => (App)Application.Current;

    readonly ContentControl _body = new();
    readonly StackPanel _poolRow = new() { Orientation = Orientation.Horizontal };
    readonly WrapPanel _targetRow = new();
    readonly TextBlock _levelLabel = new();
    Border? _dropZone;
    bool _notesOpen;

    public TranslationView()
    {
        var root = new DockPanel { LastChildFill = true };
        var head = BuildHeader();
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);
        root.Children.Add(_body);
        Content = root;

        RefreshPools();
        RefreshBody();
        Loaded += (_, _) => { TheApp.Translation.Changed += OnChanged; TheApp.Notes.Changed += OnChanged; };
        Unloaded += (_, _) => { TheApp.Translation.Changed -= OnChanged; TheApp.Notes.Changed -= OnChanged; };
    }

    void OnChanged() { RefreshPools(); RefreshBody(); }

    // ---------------------------------------------------------------- 顶部:语言池 + 程度
    FrameworkElement BuildHeader()
    {
        var st = TheApp.Translation;

        // 目标池:拖放区
        _dropZone = new Border
        {
            Child = _targetRow, MinHeight = 42, Padding = new Thickness(8, 6, 8, 2),
            BorderThickness = new Thickness(1.5), AllowDrop = true,
            Margin = new Thickness(0, 4, 0, 0),
        };
        _dropZone.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        _dropZone.SetResourceReference(Border.BorderBrushProperty, "Border");
        _dropZone.SetResourceReference(Border.BackgroundProperty, "BgSunken");
        _dropZone.DragOver += (_, e) =>
        {
            var ok = e.Data.GetDataPresent(DataFormats.StringFormat) && !st.IsFull;
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            _dropZone!.SetResourceReference(Border.BorderBrushProperty, ok ? "Accent" : "RiskDanger");
            e.Handled = true;
        };
        _dropZone.DragLeave += (_, _) => _dropZone!.SetResourceReference(Border.BorderBrushProperty, "Border");
        _dropZone.Drop += (_, e) =>
        {
            _dropZone!.SetResourceReference(Border.BorderBrushProperty, "Border");
            if (e.Data.GetData(DataFormats.StringFormat) is string code) st.AddTarget(code);
        };

        // 程度滑条
        var slider = new Slider
        {
            Minimum = 0, Maximum = TranslationLevels.All.Length - 1,
            TickFrequency = 1, IsSnapToTickEnabled = true,
            Value = (int)st.Level, Width = 260, HorizontalAlignment = HorizontalAlignment.Left,
        };
        slider.ValueChanged += (_, _) => st.SetLevel((TranslationLevel)(int)slider.Value);
        _levelLabel.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");

        var notesBtn = Chip("学习笔记", "FgSecondary", () => { _notesOpen = !_notesOpen; RefreshBody(); });

        var levelBox = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        levelBox.Children.Add(_levelLabel);
        levelBox.Children.Add(slider);

        var headTop = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(notesBtn, Dock.Right);
        headTop.Children.Add(notesBtn);
        var poolLabel = Ui.Caption("常用语言(拖到下面的目标池)");
        DockPanel.SetDock(poolLabel, Dock.Left);
        headTop.Children.Add(poolLabel);

        var box = new StackPanel();
        box.Children.Add(headTop);
        box.Children.Add(_poolRow);
        box.Children.Add(Ui.Caption($"目标语言池(最多 {Languages.MaxTargets} 个;点已选的可移除)"));
        box.Children.Add(_dropZone);
        box.Children.Add(levelBox);
        var divider = new Border { Height = 1, Margin = new Thickness(0, 10, 0, 8) };
        divider.SetResourceReference(Border.BackgroundProperty, "Border");
        box.Children.Add(divider);
        return box;
    }

    void RefreshPools()
    {
        var st = TheApp.Translation;

        _poolRow.Children.Clear();
        foreach (var l in Languages.Common)
        {
            var inPool = st.Contains(l.Code);
            var chip = LangChip(l, selected: inPool, draggable: !inPool && !st.IsFull);
            _poolRow.Children.Add(chip);
        }

        _targetRow.Children.Clear();
        if (st.Targets.Count == 0)
            _targetRow.Children.Add(Ui.Caption("把上面的语言拖进来 —— 输入其中一种,就会翻成其余几种。"));
        else
            foreach (var c in st.Targets)
            {
                var l = Languages.Find(c);
                if (l is null) continue;
                var chip = LangChip(l, selected: true, draggable: false);
                chip.Cursor = Cursors.Hand;
                chip.MouseLeftButtonUp += (_, e) => { e.Handled = true; TheApp.Translation.RemoveTarget(c); };
                _targetRow.Children.Add(chip);
            }

        _levelLabel.Text = $"翻译程度:{TranslationLevels.NameOf(st.Level)} —— {TranslationLevels.DescOf(st.Level)}";
    }

    Border LangChip(Lang l, bool selected, bool draggable)
    {
        var t = new TextBlock { Text = l.Name, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, selected ? "FgOnAccent" : "FgPrimary");
        t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        var native = new TextBlock { Text = l.Native, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0) };
        native.SetResourceReference(TextBlock.ForegroundProperty, selected ? "FgOnAccent" : "FgMuted");
        native.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(t); row.Children.Add(native);

        var b = new Border
        {
            Child = row, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 4, 6, 0),
            BorderThickness = new Thickness(1),
            Cursor = draggable ? Cursors.Hand : Cursors.Arrow,
        };
        b.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        b.SetResourceReference(Border.BackgroundProperty, selected ? "Accent" : "BgSurface");
        b.SetResourceReference(Border.BorderBrushProperty, selected ? "Accent" : "Border");
        if (draggable)
        {
            // 拖拽入池:这里用 OLE 拖放是合适的 —— 我们要的是"把一个值丢进另一个区域",
            // 不是"让元素跟着手移动"(那种场景见天气卡的手动拖拽)。
            b.PreviewMouseLeftButtonDown += (_, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed) return;
                DragDrop.DoDragDrop(b, l.Code, DragDropEffects.Copy);
            };
            b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "BgHover");
            b.MouseLeave += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "BgSurface");
        }
        return b;
    }

    // ---------------------------------------------------------------- 主体:说明 / 学习笔记
    void RefreshBody() => _body.Content = _notesOpen ? NotesBoard() : Explain();

    FrameworkElement Explain()
    {
        var st = TheApp.Translation;
        var box = new StackPanel { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 620 };

        var title = new TextBlock { Text = "翻译", FontWeight = FontWeights.SemiBold, FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center };
        title.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
        box.Children.Add(title);
        box.Children.Add(new Border { Height = 8 });

        // 当前规则如实说明(池是空的就先提示去选语言)
        var rule = st.Targets.Count == 0
            ? "先把常用语言拖进目标池(最多 3 个)。"
            : st.Targets.Count == 1
                ? $"目标池里只有【{Languages.NameOf(st.Targets[0])}】—— 再拖一个进来才知道要往哪个方向翻。"
                : $"输入其中一种语言,会翻成池内其余语言:{string.Join(" / ", st.Targets.Select(Languages.NameOf))}。" +
                  "输入池外的语言,会先把它加进池,再翻成池内其它语言。";
        var r = Ui.Caption(rule);
        r.HorizontalAlignment = HorizontalAlignment.Center;
        r.TextAlignment = TextAlignment.Center;
        box.Children.Add(r);

        box.Children.Add(new Border { Height = 12 });
        var fields = string.Join(" · ", TranslationLevels.FieldsOf(st.Level));
        var f = Ui.Caption($"当前档位【{TranslationLevels.NameOf(st.Level)}】会给出:{fields}");
        f.HorizontalAlignment = HorizontalAlignment.Center;
        box.Children.Add(f);

        box.Children.Add(new Border { Height = 14 });
        // ★ 诚实:翻译要 AI,现在没有
        var honest = Ui.Body("AI 尚未接入(P4)—— 现在还不能真的翻译。", muted: true);
        honest.HorizontalAlignment = HorizontalAlignment.Center;
        box.Children.Add(honest);
        var honest2 = Ui.Caption("语言池、档位、学习笔记都是真的、现在就能用;接上模型后,译文会按上面的档位格式产出,右侧带收藏按钮。");
        honest2.HorizontalAlignment = HorizontalAlignment.Center;
        honest2.TextAlignment = TextAlignment.Center;
        box.Children.Add(honest2);
        return box;
    }

    // ---------------------------------------------------------------- 学习笔记(按语言分类)
    FrameworkElement NotesBoard()
    {
        var notes = TheApp.Notes;
        var panel = new StackPanel();

        var back = Chip("‹ 返回翻译", "FgSecondary", () => { _notesOpen = false; RefreshBody(); });
        back.HorizontalAlignment = HorizontalAlignment.Left;
        panel.Children.Add(back);

        var langs = notes.LanguagesUsed().ToList();
        if (langs.Count == 0)
        {
            panel.Children.Add(Ui.Body("还没有学习笔记。", muted: true));
            panel.Children.Add(Ui.Caption("翻译结果右侧的【收藏】会把它存到这里,并按目标语言分类;一次翻多种语言会拆开分别存。"));
            panel.Children.Add(Ui.Caption("AI 未接入前,也可以点下面的按钮手动建一条,格式与档位一致。"));
            panel.Children.Add(NewNoteButton());
            return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }.PassThrough();
        }

        foreach (var (lang, count) in langs)
        {
            panel.Children.Add(Ui.Panel($"{Languages.NameOf(lang)}({count})",
                NotesOf(lang), IconName.Translation, new Thickness(0, 8, 0, 0), compact: true));
        }
        panel.Children.Add(NewNoteButton());
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }.PassThrough();
    }

    UIElement NotesOf(string lang)
    {
        var box = new StackPanel();
        foreach (var n in TheApp.Notes.Of(lang)) box.Children.Add(NoteRow(n));
        return box;
    }

    FrameworkElement NoteRow(StudyNote n)
    {
        var src = new TextBlock { Text = n.SourceText, TextTrimming = TextTrimming.CharacterEllipsis };
        src.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        src.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");

        var tr = new TextBlock { Text = n.Translation, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        tr.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");

        var col = new StackPanel();
        col.Children.Add(src);
        col.Children.Add(tr);
        if (!string.IsNullOrWhiteSpace(n.Reading))
        {
            var rd = Ui.Caption(n.Reading!);
            col.Children.Add(rd);
        }
        if (!string.IsNullOrWhiteSpace(n.ExampleSource))
            col.Children.Add(Ui.Caption($"例:{n.ExampleSource}" + (string.IsNullOrWhiteSpace(n.ExampleTranslation) ? "" : $" —— {n.ExampleTranslation}")));
        if (n.Words is { Count: > 0 })
            col.Children.Add(Ui.Caption("逐词:" + string.Join(" / ", n.Words.Select(w => $"{w.Word}({w.Pos}){w.Meaning}"))));
        col.Children.Add(Ui.Caption($"{TranslationLevels.NameOf(n.Level)} · {n.CreatedAt:M月d日}" + (n.CreatedByAi ? " · AI" : " · 手动")));

        var edit = Chip("编辑", "FgSecondary", () => OpenEditor(n));
        var del = Chip("删除", "RiskDanger", () =>
        {
            if (ConfirmDialog.Show("删除笔记", $"删除这条笔记?\n\n{n.Translation}", confirmText: "删除", danger: true))
                TheApp.Notes.Remove(n.Id);
        });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        actions.Children.Add(edit); actions.Children.Add(del);

        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 6, 0, 6) };
        DockPanel.SetDock(actions, Dock.Right);
        row.Children.Add(actions);
        row.Children.Add(col);
        return row;
    }

    FrameworkElement NewNoteButton()
    {
        var b = Ui.Secondary("＋ 手动新建笔记", (_, _) => OpenEditor(null));
        b.HorizontalAlignment = HorizontalAlignment.Left;
        b.Margin = new Thickness(0, 10, 0, 0);
        return b;
    }

    void OpenEditor(StudyNote? existing)
        => (Application.Current.MainWindow as MainWindow)?.OpenSideDrawer(
            existing is null ? "新建学习笔记" : "编辑学习笔记",
            NoteEditor.Build(existing, TheApp.Translation.Level, TheApp.Translation.Targets),
            IconName.Translation);

    static Border Chip(string text, string colorKey, Action onClick)
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
