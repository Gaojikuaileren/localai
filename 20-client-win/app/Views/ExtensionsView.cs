// P3c -- 扩展。分【两类】(用户裁定):
//   ① 工作空间扩展:决定左边栏出现哪些工作空间 + 【拖动条目调整左栏顺序】;接入模型后也在此为每个
//      工作空间指定用哪个 AI 模型。
//   ② 主页板块扩展:决定主页显示哪些板块(内容与种类)。
//
// 拖动排序沿用天气板块那套【手动鼠标捕获】手感(被拖行跟手、其它行让位、松手滑到位再提交),
// 只是从横向改竖向。只有行首的把手能起手拖动,不与右侧的显示开关冲突。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using LocalAI.Client.I18n;
using LocalAI.Client.Theme;

namespace LocalAI.Client.Views;

public sealed class ExtensionsView : UserControl
{
    static App TheApp => (App)Application.Current;

    const double RowH = 42;   // 每行固定高 —— 让位/落位的整格步长

    readonly StackPanel _wsList = new();
    readonly Dictionary<int, FrameworkElement> _wsRows = new();
    readonly Dictionary<int, TranslateTransform> _wsShift = new();
    List<Workspaces.Def> _wsOrder = new();

    int? _dragIndex;
    int _dragTarget;
    Point _dragOrigin;

    public ExtensionsView()
    {
        BuildWorkspaceList();

        var panelList = new StackPanel();
        foreach (var p in HomePanels.All)
            panelList.Children.Add(ToggleRow(p.Icon, p.Title,
                TheApp.Settings.IsPanelVisible(p.Key),
                on => ApplyPanel(p.Key, on)));

        Content = Ui.Page(
            Ui.Title(Strings.Get("nav.extensions")),

            Ui.Card(Ui.Stack(
                Ui.Subtitle(Strings.Get("ext.ws_title")),
                Ui.Caption(Strings.Get("ext.ws_hint")),
                Ui.Caption(Strings.Get("ext.ws_reorder_hint")),
                new Border { Height = 6 },
                _wsList,
                new Border { Height = 6 },
                Ui.Caption(Strings.Get("ext.ws_model_note"))
            )),

            Ui.Card(Ui.Stack(
                Ui.Subtitle(Strings.Get("ext.panels_title")),
                Ui.Caption(Strings.Get("ext.panels_hint")),
                new Border { Height = 8 },
                panelList
            ))
        );
    }

    // ---------------------------------------------------------------- 工作空间列表(可拖动排序)
    void BuildWorkspaceList()
    {
        _wsList.Children.Clear();
        _wsRows.Clear();
        _wsShift.Clear();
        _wsOrder = Workspaces.Ordered(TheApp.Settings);
        for (int i = 0; i < _wsOrder.Count; i++)
        {
            var row = WorkspaceRow(_wsOrder[i], i);
            _wsRows[i] = row;
            _wsList.Children.Add(row);
        }
    }

    FrameworkElement WorkspaceRow(Workspaces.Def w, int index)
    {
        // 行首把手(三横线)—— 只有它起手拖动
        var grip = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M2 5 H14 M2 9 H14 M2 13 H14"),
            StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
            Width = 16, Height = 18, Stretch = Stretch.None, Opacity = 0.55,
            Cursor = Cursors.SizeAll,
        };
        grip.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "FgSecondary");
        grip.PreviewMouseLeftButtonDown += (_, e) => { e.Handled = true; BeginDrag(index, e); };

        var ic = Icons.Make(w.Icon, 17, "FgSecondary");
        ic.VerticalAlignment = VerticalAlignment.Center;
        ic.Margin = new Thickness(0, 0, 10, 0);

        var label = new TextBlock { Text = Strings.Get(w.TitleKey), VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");

        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(grip);
        left.Children.Add(ic);
        left.Children.Add(label);

        var check = new CheckBox { IsChecked = TheApp.Settings.IsWorkspaceVisible(w.Key), VerticalAlignment = VerticalAlignment.Center };
        var key = w.Key;
        check.Checked += (_, _) => ApplyWorkspaceVisible(key, true);
        check.Unchecked += (_, _) => ApplyWorkspaceVisible(key, false);

        var inner = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(check, Dock.Right);
        inner.Children.Add(check);
        inner.Children.Add(left);

        var shift = new TranslateTransform();
        var row = new Border
        {
            Child = inner,
            Height = RowH,
            Padding = new Thickness(4, 0, 4, 0),
            Background = Brushes.Transparent,
            RenderTransform = shift,
        };
        row.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        _wsShift[index] = shift;
        return row;
    }

    // ---------------------------------------------------------------- 拖动(竖向,手动捕获)
    void BeginDrag(int index, MouseButtonEventArgs e)
    {
        _dragIndex = index;
        _dragTarget = index;
        _dragOrigin = e.GetPosition(_wsList);
        if (_wsRows.TryGetValue(index, out var row))
        {
            Panel.SetZIndex(row, 10);
            row.SetResourceReference(Border.BackgroundProperty, "BgHover");
            ((Border)row).Opacity = 0.96;
        }
        _wsList.CaptureMouse();
        _wsList.MouseMove += OnDragMove;
        _wsList.MouseLeftButtonUp += OnDragEnd;
        _wsList.LostMouseCapture += OnDragLost;
    }

    void OnDragMove(object? sender, MouseEventArgs e)
    {
        if (_dragIndex is not int from) return;
        var dy = e.GetPosition(_wsList).Y - _dragOrigin.Y;

        if (_wsShift.TryGetValue(from, out var t)) { t.BeginAnimation(TranslateTransform.YProperty, null); t.Y = dy; }

        var target = Math.Clamp(from + (int)Math.Round(dy / RowH), 0, _wsOrder.Count - 1);
        if (target == _dragTarget) return;
        _dragTarget = target;
        ApplyGaps(from, target);
    }

    void ApplyGaps(int from, int target)
    {
        for (int k = 0; k < _wsOrder.Count; k++)
        {
            if (k == from) continue;                       // 被拖的那行由鼠标控制
            double to = 0;
            if (from < target && k > from && k <= target) to = -RowH;
            else if (from > target && k >= target && k < from) to = RowH;
            AnimateShift(k, to);
        }
    }

    void OnDragEnd(object? sender, MouseButtonEventArgs e) => FinishDrag(commit: true);
    void OnDragLost(object? sender, MouseEventArgs e) => FinishDrag(commit: false);

    void FinishDrag(bool commit)
    {
        if (_dragIndex is not int from) return;
        var target = _dragTarget;

        _wsList.MouseMove -= OnDragMove;
        _wsList.MouseLeftButtonUp -= OnDragEnd;
        _wsList.LostMouseCapture -= OnDragLost;
        if (_wsList.IsMouseCaptured) _wsList.ReleaseMouseCapture();
        _dragIndex = null;

        var swap = commit && target != from;
        var settleTo = swap ? (target - from) * RowH : 0;

        void Land()
        {
            if (_wsRows.TryGetValue(from, out var row))
            {
                Panel.SetZIndex(row, 0);
                ((Border)row).Opacity = 1;
                ((Border)row).Background = Brushes.Transparent;
            }
            if (swap) MoveWorkspace(from, target);   // 重建列表 + 刷新左栏,位移归零
        }

        if (_wsShift.TryGetValue(from, out var t))
        {
            var anim = new DoubleAnimation(settleTo, TimeSpan.FromMilliseconds(180))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            anim.Completed += (_, _) => Land();
            t.BeginAnimation(TranslateTransform.YProperty, null);
            anim.From = t.Y;
            t.BeginAnimation(TranslateTransform.YProperty, anim);
        }
        else Land();

        if (!swap) for (int k = 0; k < _wsOrder.Count; k++) if (k != from) AnimateShift(k, 0);
    }

    void AnimateShift(int index, double toY)
    {
        if (!_wsShift.TryGetValue(index, out var t)) return;
        var anim = new DoubleAnimation(toY, TimeSpan.FromMilliseconds(160))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        t.BeginAnimation(TranslateTransform.YProperty, anim);
    }

    void MoveWorkspace(int from, int to)
    {
        if (from == to || from < 0 || to < 0 || from >= _wsOrder.Count || to >= _wsOrder.Count) return;
        var d = _wsOrder[from];
        _wsOrder.RemoveAt(from);
        _wsOrder.Insert(to, d);
        TheApp.Settings.SetWorkspaceOrder(_wsOrder.Select(x => x.Key));   // 落盘
        BuildWorkspaceList();                                            // 重建扩展页列表
        (Application.Current.MainWindow as MainWindow)?.RefreshNavRail(); // 左栏即时跟随
    }

    // ---------------------------------------------------------------- 通用开关行(主页板块用)
    static FrameworkElement ToggleRow(IconName icon, string title, bool on, Action<bool> onChanged)
    {
        var ic = Icons.Make(icon, 17, "FgSecondary");
        ic.VerticalAlignment = VerticalAlignment.Center;
        ic.Margin = new Thickness(0, 0, 10, 0);

        var label = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");

        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(ic);
        left.Children.Add(label);

        var check = new CheckBox { IsChecked = on, VerticalAlignment = VerticalAlignment.Center };
        check.Checked += (_, _) => onChanged(true);
        check.Unchecked += (_, _) => onChanged(false);

        var row = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 5, 0, 5) };
        DockPanel.SetDock(check, Dock.Right);
        row.Children.Add(check);
        row.Children.Add(left);
        return row;
    }

    void ApplyWorkspaceVisible(string key, bool visible)
    {
        TheApp.Settings.SetWorkspaceVisible(key, visible);
        (Application.Current.MainWindow as MainWindow)?.RefreshNavRail();
    }

    static void ApplyPanel(string key, bool visible)
        => TheApp.Settings.SetPanelVisible(key, visible);
}
