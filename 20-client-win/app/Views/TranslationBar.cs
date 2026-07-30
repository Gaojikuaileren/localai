// P3c -- 翻译工作空间的【下半部分】:程度竖条 + 目标池 + 语言池 + 学习笔记预览。
//
// 用户裁定的排版(2026-07-30):
//   ┌──────────────────────────────────────────────┐
//   │            主会话框(上方,由 ChatView 建)      │
//   ├────┬──────────┬────────────┬─────────────────┤
//   │程度│  目标池  │   语言池   │  学习笔记(预览) │
//   │竖条│  (方)   │   (方)    │   最新几条       │
//   └────┴──────────┴────────────┴─────────────────┘
//   · 程度是"上下竖条长板块",四档从简到全;
//   · 池子是【方形板块】,语言是【漂浮的气泡】;
//   · 语言池旁边有齿轮 -> 通到设置里增删常用语言。
//
// ★ 诚实:翻译本身要 AI(P4 未接入)。这里的池子/档位/笔记都是真的,但不会产出译文。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LocalAI.Client.Services;
using LocalAI.Client.Theme;

namespace LocalAI.Client.Views;

public sealed class TranslationBar : UserControl
{
    static App TheApp => (App)Application.Current;

    /// <summary>下半部分的高度 —— 固定,免得会话区被挤得忽大忽小。</summary>
    public const double BarHeight = 168;

    readonly StackPanel _levelCol = new();
    readonly WrapPanel _targetWrap = new();
    readonly WrapPanel _poolWrap = new();
    readonly StackPanel _notesPreview = new();
    Border? _targetBox;

    public TranslationBar()
    {
        Height = BarHeight;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                    // 程度竖条
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) }); // 目标池
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) }); // 语言池
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) }); // 学习笔记

        var lv = LevelColumn();      Grid.SetColumn(lv, 0);
        var tg = TargetPanel();      Grid.SetColumn(tg, 1);
        var pl = PoolPanel();        Grid.SetColumn(pl, 2);
        var nt = NotesPanel();       Grid.SetColumn(nt, 3);
        grid.Children.Add(lv); grid.Children.Add(tg); grid.Children.Add(pl); grid.Children.Add(nt);
        Content = grid;

        Refresh();
        Loaded += (_, _) => { TheApp.Translation.Changed += Refresh; TheApp.Notes.Changed += Refresh; };
        Unloaded += (_, _) => { TheApp.Translation.Changed -= Refresh; TheApp.Notes.Changed -= Refresh; };
    }

    void Refresh() { RefreshLevel(); RefreshPools(); RefreshNotes(); }

    // ---------------------------------------------------------------- 程度:上下竖条(四档)
    FrameworkElement LevelColumn()
    {
        var card = Card(_levelCol, "程度");
        card.Width = 78;
        return card;
    }

    void RefreshLevel()
    {
        _levelCol.Children.Clear();
        var cur = TheApp.Translation.Level;
        // 由简到全【自下而上】排:越往上越详细,像一根推杆
        foreach (var (level, name, desc) in TranslationLevels.All.Reverse())
        {
            var on = cur == level;
            var t = new TextBlock { Text = name, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            t.SetResourceReference(TextBlock.ForegroundProperty, on ? "FgOnAccent" : "FgSecondary");
            t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            var cell = new Border { Child = t, Height = 26, Margin = new Thickness(0, 0, 0, 3), Cursor = Cursors.Hand, BorderThickness = new Thickness(1) };
            cell.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
            cell.SetResourceReference(Border.BackgroundProperty, on ? "Accent" : "BgSurface");
            cell.SetResourceReference(Border.BorderBrushProperty, on ? "Accent" : "Border");
            var captured = level;
            cell.MouseLeftButtonUp += (_, e) => { e.Handled = true; TheApp.Translation.SetLevel(captured); };
            if (!on)
            {
                cell.MouseEnter += (_, _) => cell.SetResourceReference(Border.BackgroundProperty, "BgHover");
                cell.MouseLeave += (_, _) => cell.SetResourceReference(Border.BackgroundProperty, "BgSurface");
            }
            _levelCol.Children.Add(cell);
        }
    }

    // ---------------------------------------------------------------- 目标池(方形,接收拖放)
    FrameworkElement TargetPanel()
    {
        _targetBox = Card(_targetWrap, $"目标池(最多 {Languages.MaxTargets})");
        _targetBox.AllowDrop = true;
        _targetBox.Margin = new Thickness(8, 0, 0, 0);
        _targetBox.DragOver += (_, e) =>
        {
            var ok = e.Data.GetDataPresent(DataFormats.StringFormat) && !TheApp.Translation.IsFull;
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            _targetBox!.SetResourceReference(Border.BorderBrushProperty, ok ? "Accent" : "RiskDanger");
            e.Handled = true;
        };
        _targetBox.DragLeave += (_, _) => _targetBox!.SetResourceReference(Border.BorderBrushProperty, "Border");
        _targetBox.Drop += (_, e) =>
        {
            _targetBox!.SetResourceReference(Border.BorderBrushProperty, "Border");
            if (e.Data.GetData(DataFormats.StringFormat) is string code) TheApp.Translation.AddTarget(code);
        };
        return _targetBox;
    }

    // ---------------------------------------------------------------- 语言池(方形 + 齿轮进设置)
    FrameworkElement PoolPanel()
    {
        var card = Card(_poolWrap, "语言池",
            gear: () => (Application.Current.MainWindow as MainWindow)?.OpenLanguagePoolSettings());
        card.Margin = new Thickness(8, 0, 0, 0);
        return card;
    }

    void RefreshPools()
    {
        var st = TheApp.Translation;

        _poolWrap.Children.Clear();
        foreach (var code in TheApp.Settings.TranslationPool)
        {
            var l = Languages.Find(code);
            if (l is null) continue;                    // 认不出的码丢掉,不显示脏数据
            if (st.Contains(code)) continue;            // 已经在目标池里的,不在语言池重复出现
            _poolWrap.Children.Add(Bubble(l, selected: false, draggable: !st.IsFull));
        }
        if (_poolWrap.Children.Count == 0)
            _poolWrap.Children.Add(Ui.Caption("语言都在目标池里了。点齿轮可增删常用语言。"));

        _targetWrap.Children.Clear();
        if (st.Targets.Count == 0)
            _targetWrap.Children.Add(Ui.Caption("把语言气泡拖进来"));
        else
            foreach (var code in st.Targets)
            {
                var l = Languages.Find(code);
                if (l is null) continue;
                var b = Bubble(l, selected: true, draggable: false);
                b.Cursor = Cursors.Hand;
                b.MouseLeftButtonUp += (_, e) => { e.Handled = true; TheApp.Translation.RemoveTarget(code); };
                _targetWrap.Children.Add(b);
            }
    }

    // 语言"气泡":圆角很大的小胶囊,像漂着的泡泡(用户要的观感)
    Border Bubble(Lang l, bool selected, bool draggable)
    {
        var t = new TextBlock { Text = l.Name, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, selected ? "FgOnAccent" : "FgPrimary");
        t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");

        var b = new Border
        {
            Child = t, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 6, 6),
            CornerRadius = new CornerRadius(14),   // ★ 气泡感:圆角走满,不跟随皮肤的方角令牌
            BorderThickness = new Thickness(1),
            Cursor = draggable ? Cursors.Hand : Cursors.Arrow,
        };
        b.SetResourceReference(Border.BackgroundProperty, selected ? "Accent" : "BgSurface");
        b.SetResourceReference(Border.BorderBrushProperty, selected ? "Accent" : "Border");
        if (draggable)
        {
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

    // ---------------------------------------------------------------- 学习笔记(预览最新几条)
    FrameworkElement NotesPanel()
    {
        var card = Card(_notesPreview, "学习笔记",
            gear: null,
            action: Chip("全部", () => (Application.Current.MainWindow as MainWindow)?.OpenSideDrawer(
                "学习笔记", new NotesBoardView(), IconName.Translation)));
        card.Margin = new Thickness(8, 0, 0, 0);
        return card;
    }

    void RefreshNotes()
    {
        _notesPreview.Children.Clear();
        var latest = TheApp.Notes.Items
            .OrderByDescending(n => n.CreatedAt ?? DateTime.MinValue)
            .Take(3).ToList();
        if (latest.Count == 0)
        {
            _notesPreview.Children.Add(Ui.Caption("翻译结果右侧点收藏,就会存到这里(按语言分类)。"));
            return;
        }
        foreach (var n in latest)
        {
            var line = new TextBlock { Text = $"[{Languages.NameOf(n.Lang)}] {n.Translation}", TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 3) };
            line.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            line.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            _notesPreview.Children.Add(line);
        }
    }

    // ---------------------------------------------------------------- 小工具
    static Border Card(UIElement body, string title, Action? gear = null, FrameworkElement? action = null)
    {
        var head = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 6) };
        var t = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
        t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        DockPanel.SetDock(t, Dock.Left);
        head.Children.Add(t);
        if (gear is not null)
        {
            var g = Icons.Make(IconName.Settings, 14, "FgMuted");
            var gb = new Border { Child = g, Padding = new Thickness(4), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            gb.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
            gb.MouseEnter += (_, _) => gb.SetResourceReference(Border.BackgroundProperty, "BgHover");
            gb.MouseLeave += (_, _) => gb.Background = Brushes.Transparent;
            gb.MouseLeftButtonUp += (_, e) => { e.Handled = true; gear(); };
            DockPanel.SetDock(gb, Dock.Right);
            head.Children.Add(gb);
        }
        if (action is not null) { DockPanel.SetDock(action, Dock.Right); head.Children.Add(action); }

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);
        dock.Children.Add(new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        }.PassThrough());

        var card = new Border { Child = dock, Padding = new Thickness(10), BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BackgroundProperty, "BgSurface");
        card.SetResourceReference(Border.BorderBrushProperty, "Border");
        card.SetResourceReference(Border.CornerRadiusProperty, "RadiusMd");
        return card;
    }

    static FrameworkElement Chip(string text, Action onClick)
    {
        var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
        t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        var b = new Border { Child = t, Padding = new Thickness(8, 2, 8, 2), Cursor = Cursors.Hand, Background = Brushes.Transparent, BorderThickness = new Thickness(1) };
        b.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        b.SetResourceReference(Border.BorderBrushProperty, "Border");
        b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "BgHover");
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick(); };
        return b;
    }
}
