// P3c -- 文件翻译场景的会话区(用户裁定 2026-08-02,D59):
//   左 = 原文件预览(导入按钮 / 直接拖入;PNG/JPG 真渲染,PDF 如实说"预览待接入"),
//       标注框画在预览上(工具栏开了"创建标注框"才能画;坐标归一化,缩放不跑偏);
//   右 = 翻译结果实时预览 + 保存 —— 引擎未接入(P4),右侧如实说明,【不伪造译文】。

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LocalAI.Client.Services;

namespace LocalAI.Client.Views;

public sealed class FileTransPanel : UserControl
{
    static App TheApp => (App)Application.Current;

    readonly string? _sessionId;
    readonly Grid _overlay = new();          // 标注框层(与图像同一显示矩形)
    readonly Image _img = new() { Stretch = Stretch.Uniform };
    readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                                       VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    Point? _dragStart;                        // 正在画的框的起点(归一化前的像素坐标)
    System.Windows.Shapes.Rectangle? _ghost;  // 正在画的框

    public FileTransPanel(string? sessionId)
    {
        _sessionId = sessionId;

        // ---- 左:原文件预览 ----
        var import = Ui.Secondary("导入文件(PNG / JPG / PDF)", (_, _) => PickFile());
        import.HorizontalAlignment = HorizontalAlignment.Center;
        _hint.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");

        var stage = new Grid();
        stage.Children.Add(_img);
        stage.Children.Add(_overlay);
        // ★ 缩放与平移(用户选定):滚轮缩放(1x-5x),画框工具关着时左键拖动平移。
        //   变换加在 stage 上,画框取的是 overlay 本地坐标 —— 命中与归一化不受缩放影响。
        stage.RenderTransformOrigin = new Point(0.5, 0.5);
        var zoomT = new ScaleTransform(1, 1);
        var panT = new TranslateTransform();
        var tg = new TransformGroup();
        tg.Children.Add(zoomT); tg.Children.Add(panT);
        stage.RenderTransform = tg;
        _zoomT = zoomT; _panT = panT;
        stage.ClipToBounds = true;
        var empty = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        empty.Children.Add(_hint);
        empty.Children.Add(new Border { Height = 8 });
        empty.Children.Add(import);
        stage.Children.Add(empty);
        _emptyHost = empty;
        // ★ 页码角标(用户裁定):左上角显示当前页;多页 PDF 的翻页随渲染(P4)接入,按下如实说
        _pageTag.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
        _pageTag.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        var pageHost = new Border { Child = _pageTag, Padding = new Thickness(6, 2, 6, 2),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(6), Cursor = Cursors.Hand };
        pageHost.SetResourceReference(Border.BackgroundProperty, "BgSurface");
        pageHost.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        pageHost.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (_isPdf) ConfirmDialog.Show("还翻不了页",
                "PDF 的渲染与翻页要等系统渲染组件随引擎(P4)一起接入 —— 页数现在也读不出来,不编一个数。",
                confirmText: "知道了", cancelText: "关闭");
        };
        _pageHost = pageHost;

        var leftGrid = new Grid();
        leftGrid.Children.Add(stage);
        leftGrid.Children.Add(pageHost);   // 角标压在缩放层外面,缩放不跟着跑
        var left = new Border { Child = leftGrid, Margin = new Thickness(0, 0, 6, 0), AllowDrop = true, ClipToBounds = true };
        // ★ 平移改【右键拖拽】(用户裁定 2026-08-02:左键专职标注/选择,两个左键抢一个手势是冲突源)
        left.MouseRightButtonDown += (_, e) =>
        { _panStart = e.GetPosition(this); _panBase = (_panT.X, _panT.Y); left.CaptureMouse(); };
        left.MouseMove += (_, e) =>
        {
            if (_panStart is { } ps && _panBase is { } pb && e.RightButton == MouseButtonState.Pressed)
            { var p2 = e.GetPosition(this); _panT.X = pb.X + (p2.X - ps.X); _panT.Y = pb.Y + (p2.Y - ps.Y); }
        };
        left.MouseRightButtonUp += (_, _) => { _panStart = null; _panBase = null; left.ReleaseMouseCapture(); };
        left.MouseWheel += (_, e) =>
        {
            var z = Math.Clamp(_zoomT.ScaleX * (e.Delta > 0 ? 1.15 : 1 / 1.15), 1, 5);
            _zoomT.ScaleX = _zoomT.ScaleY = z;
            if (z <= 1.001) { _panT.X = _panT.Y = 0; }   // 回到 1x 就归位,不留一个平移走的空画面
            e.Handled = true;
        };
        left.SetResourceReference(Border.BackgroundProperty, "BgSunken");
        left.SetResourceReference(Border.CornerRadiusProperty, "RadiusMd");
        // ★ 拖拽导入(用户裁定):拖到左半即可,不必找按钮
        left.Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) LoadFile(files[0]);
        };
        left.DragOver += (_, e) =>
        {
            var ok = e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } f && FileTransState.Supported(f[0]);
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        // 画框:只有工具开着才响应(否则单击/拖动不该有任何副作用)
        _overlay.Background = Brushes.Transparent;
        _overlay.MouseLeftButtonDown += BeginBox;
        _overlay.MouseMove += DragBox;
        _overlay.MouseLeftButtonUp += EndBox;

        // ---- 右:结果预览 + 保存 ----
        var save = Ui.Secondary("保存", (_, _) => { });
        save.IsEnabled = false;   // ★ 没有可保存的结果之前一直灰 —— 引擎未接入,伪造一个"保存成功"更糟
        save.HorizontalAlignment = HorizontalAlignment.Right;
        var saveNote = Ui.Caption("保存当前输出 —— 引擎接入(P4)后可用;现在还没有可保存的结果。");
        saveNote.TextWrapping = TextWrapping.Wrap;

        var rightBody = new TextBlock
        {
            Text = "翻译结果会在这里【按原排版】实时预览。\n★ 翻译引擎尚未接入(P4)——现在不会有输出,这里不摆假译文。\n接入后:开着「实时预览」边标边出;关着就等「开始翻译」一次出全。",
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center,
        };
        rightBody.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        var rightGrid = new DockPanel { LastChildFill = true, Margin = new Thickness(6, 0, 0, 0) };
        var saveRow = new DockPanel { LastChildFill = false, Margin = new Thickness(8, 8, 8, 0) };
        DockPanel.SetDock(save, Dock.Right);
        saveRow.Children.Add(save);
        DockPanel.SetDock(saveRow, Dock.Top);
        rightGrid.Children.Add(saveRow);
        var rightCard = new Border { Child = rightBody, Margin = new Thickness(8) };
        rightGrid.Children.Add(rightCard);
        var right = new Border { Child = rightGrid };
        right.SetResourceReference(Border.BackgroundProperty, "BgSunken");
        right.SetResourceReference(Border.CornerRadiusProperty, "RadiusMd");

        var split = new Grid();
        split.ColumnDefinitions.Add(new ColumnDefinition());
        split.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(left, 0); split.Children.Add(left);
        Grid.SetColumn(right, 1); split.Children.Add(right);
        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(saveNote, Dock.Bottom);
        saveNote.Margin = new Thickness(2, 6, 2, 0);
        root.Children.Add(saveNote);
        root.Children.Add(split);
        Content = root;

        Focusable = true;
        KeyDown += (_, e) =>
        {
            // Ctrl+Z 撤回(用户裁定)—— 与工具栏「撤回」同一动作
            if (e.Key == System.Windows.Input.Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
            { if (Sid is { } zsid) TheApp.FileTrans.UndoBox(zsid); e.Handled = true; return; }
            if (e.Key != System.Windows.Input.Key.Delete) return;
            if (Sid is { } dsid && TheApp.FileTrans.SelectedBox is { } di)
            { TheApp.FileTrans.RemoveBox(dsid, di); e.Handled = true; }
        };
        _overlay.MouseLeftButtonDown += (_, _) => Focus();   // 点了预览才吃 Del,不跟输入框抢键
        Loaded += (_, _) => { TheApp.FileTrans.Changed += Rebuild; Rebuild(); };
        Unloaded += (_, _) => TheApp.FileTrans.Changed -= Rebuild;
        _overlay.SizeChanged += (_, _) => RedrawBoxes();
    }

    readonly StackPanel _emptyHost;
    readonly TextBlock _pageTag = new() { Text = "" };
    Border _pageHost = null!;
    bool _isPdf;
    bool _usingCache;   // 源没了、在用导入时的副本(角标里如实标注)

    void PickFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "可翻译的文件|*.png;*.jpg;*.jpeg;*.pdf" };
        if (dlg.ShowDialog() == true) LoadFile(dlg.FileName);
    }

    /// <summary>导入:没会话就当场建一条【文件翻译会话】(与同传同款,标题认得出是哪个文件)。</summary>
    void LoadFile(string path)
    {
        if (!FileTransState.Supported(path))
        {
            ConfirmDialog.Show("这个格式不支持", "文件翻译目前只吃 PNG / JPG / PDF。", confirmText: "知道了", cancelText: "关闭");
            return;
        }
        var sid = _sessionId;
        var isNew = sid is null;
        if (sid is null)
        {
            var sess = TheApp.Chat.NewSession(null, "translation", ProjectScope.Personal,
                $"文件翻译 · {Path.GetFileName(path)} · {DateTime.Now:M月d日 HH:mm}", fileTrans: true);
            sid = sess.SessionId;
        }
        TheApp.FileTrans.SetFile(sid, path);
        // 新建的会话请宿主选中 —— 本面板绑的还是 null,不选中的话导入完看着像没反应
        if (isNew) TheApp.FileTrans.RequestFocus(sid);
    }

    // ★ 只认宿主递进来的会话(用户裁定 2026-08-03):原先回落到"最后一条文件翻译会话",
    //   导致进场景永远显示上次的文件,即使根本没选中那条会话 —— 进来该是空态,选了会话才加载。
    string? Sid => _sessionId;

    void Rebuild()
    {
        var doc = TheApp.FileTrans.DocOf(Sid);
        var readable = doc is null ? null : Services.FileTransState.ReadablePath(doc);
        if (doc is null || readable is null)
        {
            _img.Source = null;
            if (_pageHost is not null) _pageHost.Visibility = Visibility.Collapsed;
            _emptyHost.Visibility = Visibility.Visible;
            // ★ 源和副本都没有(老会话/副本复制失败):标注框还在数据里,但没有底图就没法画 ——
            //   如实说清,而不是画一堆浮在空气上的框
            _hint.Text = doc is null
                ? "把 PNG / JPG / PDF 拖到这里,或点下面导入。\n左键即可在预览上圈出要翻译的部分。"
                : $"原文件不在了:{doc.Path}\n导入时的副本也不可用。标注框({doc.Boxes.Count} 个)还留着 —— 重新导入同一份文件即可继续。";
            RedrawBoxes();
            return;
        }
        // 源没了、用的是副本 -> 页码角标旁如实标注
        _usingCache = !File.Exists(doc.Path);
        _emptyHost.Visibility = Visibility.Collapsed;
        _isPdf = Path.GetExtension(readable).ToLowerInvariant() == ".pdf";
        _pageHost.Visibility = Visibility.Visible;
        _pageTag.Text = (_isPdf ? "第 1 页 / ?" : "第 1 页 / 1") + (_usingCache ? " · 源文件已不在,用导入时的副本" : "");
        if (_isPdf)
        {
            // ★ 如实:PDF 的渲染要 WinRT(随引擎一起接),现在不画一个假封面
            _img.Source = null;
            _emptyHost.Visibility = Visibility.Visible;
            _hint.Text = $"已导入 PDF:{Path.GetFileName(doc.Path)}\n★ PDF 预览尚未接入(需要系统渲染组件,随翻译引擎 P4 一起接)。\n标注框要在预览上画,所以 PDF 暂时只能整页翻译。\n\n第 1 页 / —(页码导航随 PDF 渲染一起接入)";
        }
        else
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;   // 不锁文件
                bmp.UriSource = new Uri(readable);
                bmp.EndInit();
                _img.Source = bmp;
            }
            catch
            {
                _emptyHost.Visibility = Visibility.Visible;
                _hint.Text = "这张图读不出来(可能损坏)。换一份试试。";
            }
        }
        RedrawBoxes();
    }

    // ---- 标注框:显示矩形 = Uniform 缩放后的图像区域;框存归一化坐标 ----
    Rect ImageRect()
    {
        if (_img.Source is not BitmapSource b || _overlay.ActualWidth <= 0) return Rect.Empty;
        var scale = Math.Min(_overlay.ActualWidth / b.PixelWidth, _overlay.ActualHeight / b.PixelHeight);
        var w = b.PixelWidth * scale; var h = b.PixelHeight * scale;
        return new Rect((_overlay.ActualWidth - w) / 2, (_overlay.ActualHeight - h) / 2, w, h);
    }

    // 拖动模式:0=移动 1=左边 2=右边 3=上边 4=下边 5=右下角(双向)
    (int Index, int Mode, Point From, Services.MarkBox Orig)? _boxDrag;
    (int Index, Services.MarkBox Box)? _tempBox;                            // 拖动中的预览值(未提交)

    /// <summary>
    /// 按拖动量算出新框。★ 上下边只动高、左右边只动宽、右下角才双向(用户裁定 2026-08-03)——
    ///   原先"动离按下点最近的角"让拖上边时宽也跟着变,看起来就是抖。
    /// ★ 最小尺寸 12px(换算成归一化):框不会被拉没。
    /// </summary>
    Services.MarkBox Adjusted((int Index, int Mode, Point From, Services.MarkBox Orig) bd, Point now)
    {
        var img = ImageRect();
        if (img.IsEmpty) return bd.Orig;
        var dx = (now.X - bd.From.X) / img.Width;
        var dy = (now.Y - bd.From.Y) / img.Height;
        var b = bd.Orig;
        var minW = 12 / img.Width; var minH = 12 / img.Height;
        double x1 = b.X, y1 = b.Y, x2 = b.X + b.W, y2 = b.Y + b.H;
        switch (bd.Mode)
        {
            case 0: return new Services.MarkBox(Math.Clamp(b.X + dx, 0, 1 - b.W), Math.Clamp(b.Y + dy, 0, 1 - b.H), b.W, b.H);
            case 1: x1 = Math.Clamp(b.X + dx, 0, x2 - minW); break;                     // 左边:只宽
            case 2: x2 = Math.Clamp(x2 + dx, x1 + minW, 1); break;                      // 右边:只宽
            case 3: y1 = Math.Clamp(b.Y + dy, 0, y2 - minH); break;                     // 上边:只高
            case 4: y2 = Math.Clamp(y2 + dy, y1 + minH, 1); break;                      // 下边:只高
            case 5: x2 = Math.Clamp(x2 + dx, x1 + minW, 1); y2 = Math.Clamp(y2 + dy, y1 + minH, 1); break;   // 右下角:双向
        }
        return new Services.MarkBox(x1, y1, x2 - x1, y2 - y1);
    }

    /// <summary>命中在框的哪个部位:0 无 1 左 2 右 3 上 4 下 5 右下角(带 5px 边带,角优先)。</summary>
    static int EdgeHit(Rect r, Point p)
    {
        if (!Rect.Inflate(r, 5, 5).Contains(p)) return 0;
        var nearL = Math.Abs(p.X - r.X) <= 5; var nearR = Math.Abs(p.X - r.Right) <= 5;
        var nearT = Math.Abs(p.Y - r.Y) <= 5; var nearB = Math.Abs(p.Y - r.Bottom) <= 5;
        if (nearR && nearB) return 5;
        if (nearL && p.Y > r.Y - 5 && p.Y < r.Bottom + 5) return 1;
        if (nearR) return 2;
        if (nearT) return 3;
        if (nearB) return 4;
        return 0;
    }

    Point? _panStart;          // 平移起点(右键拖拽)
    (double X, double Y)? _panBase;
    ScaleTransform _zoomT = null!;
    TranslateTransform _panT = null!;

    void BeginBox(object s, MouseButtonEventArgs e)
    {
        {
            var img1 = ImageRect();
            var pt1 = e.GetPosition(_overlay);
            if (img1.IsEmpty || Sid is not { } sid1 || TheApp.FileTrans.DocOf(sid1) is not { } d1) return;
            Rect R(Services.MarkBox b) => new(img1.X + b.X * img1.Width, img1.Y + b.Y * img1.Height,
                                              b.W * img1.Width, b.H * img1.Height);
            for (int i = d1.Boxes.Count - 1; i >= 0; i--)
            {
                var r1 = R(d1.Boxes[i]);
                var tag = new Rect(r1.X, r1.Y, 16, 14);                       // 角标热区
                if (tag.Contains(pt1))                                        // 按住角标 = 移动框
                { _boxDrag = (i, 0, pt1, d1.Boxes[i]); TheApp.FileTrans.SelectBox(i); _overlay.CaptureMouse(); return; }
                var edge = EdgeHit(r1, pt1);                                  // 边/角 = 各管各的方向
                if (edge != 0)
                { _boxDrag = (i, edge, pt1, d1.Boxes[i]); TheApp.FileTrans.SelectBox(i); _overlay.CaptureMouse(); return; }
                if (r1.Contains(pt1)) { TheApp.FileTrans.SelectBox(i); return; }   // 框内 = 选中
            }
            TheApp.FileTrans.SelectBox(null);                                 // 空白 = 清选 -> 落到画框
        }
        if (Sid is null || ImageRect() is { IsEmpty: true }) return;
        _dragStart = e.GetPosition(_overlay);
        _ghost = new System.Windows.Shapes.Rectangle { StrokeThickness = 1.6, IsHitTestVisible = false,
            StrokeDashArray = new DoubleCollection { 3, 2 } };
        _ghost.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Accent");
        _overlay.Children.Add(_ghost);
        _overlay.CaptureMouse();
    }

    void DragBox(object s, MouseEventArgs e)
    {
        if (_boxDrag is null && _dragStart is null && e.LeftButton != MouseButtonState.Pressed)
        {
            // 悬停反馈(用户裁定):角标 = 移动样式,边框带 = 拉伸样式,其余 = 画框十字
            var imgH = ImageRect();
            var ptH = e.GetPosition(_overlay);
            var cur = Cursors.Cross;
            if (!imgH.IsEmpty && Sid is { } hsid && TheApp.FileTrans.DocOf(hsid) is { } hd)
                for (int i = hd.Boxes.Count - 1; i >= 0; i--)
                {
                    var b2 = hd.Boxes[i];
                    var r2 = new Rect(imgH.X + b2.X * imgH.Width, imgH.Y + b2.Y * imgH.Height,
                                      b2.W * imgH.Width, b2.H * imgH.Height);
                    if (new Rect(r2.X, r2.Y, 16, 14).Contains(ptH)) { cur = Cursors.SizeAll; break; }
                    var eh = EdgeHit(r2, ptH);
                    if (eh == 5) { cur = Cursors.SizeNWSE; break; }
                    if (eh is 1 or 2) { cur = Cursors.SizeWE; break; }
                    if (eh is 3 or 4) { cur = Cursors.SizeNS; break; }
                    if (r2.Contains(ptH)) { cur = Cursors.Hand; break; }
                }
            _overlay.Cursor = cur;
        }
        if (_boxDrag is { } bd && e.LeftButton == MouseButtonState.Pressed)
        {
            _tempBox = (bd.Index, Adjusted(bd, e.GetPosition(_overlay)));
            RedrawBoxes();
            return;
        }
        if (_dragStart is not { } p0 || _ghost is null) return;
        var p = e.GetPosition(_overlay);
        var r = new Rect(p0, p);
        _ghost.Margin = new Thickness(r.X, r.Y, 0, 0);
        _ghost.Width = r.Width; _ghost.Height = r.Height;
        _ghost.HorizontalAlignment = HorizontalAlignment.Left;
        _ghost.VerticalAlignment = VerticalAlignment.Top;
    }

    void EndBox(object s, MouseButtonEventArgs e)
    {
        _overlay.ReleaseMouseCapture();
        if (_boxDrag is { } bd)
        {
            // 提交移动/调大小(拖动过程中只画预览,这里才写状态)
            if (Sid is { } msid) TheApp.FileTrans.UpdateBox(msid, bd.Index, Adjusted(bd, e.GetPosition(_overlay)));
            _boxDrag = null; _tempBox = null;
            return;
        }
        if (_dragStart is not { } p0) return;
        var p = e.GetPosition(_overlay);
        _dragStart = null;
        if (_ghost is not null) { _overlay.Children.Remove(_ghost); _ghost = null; }
        var img = ImageRect();
        if (img.IsEmpty || Sid is not { } sid) return;
        var r = Rect.Intersect(new Rect(p0, p), img);
        if (r.IsEmpty || r.Width < 6 || r.Height < 6) return;   // 误点不算框
        TheApp.FileTrans.AddBox(sid, new Services.MarkBox(
            (r.X - img.X) / img.Width, (r.Y - img.Y) / img.Height, r.Width / img.Width, r.Height / img.Height));
    }

    void RedrawBoxes()
    {
        // 幽灵框之外全清重画
        for (int i = _overlay.Children.Count - 1; i >= 0; i--)
            if (_overlay.Children[i] != _ghost) _overlay.Children.RemoveAt(i);
        var img = ImageRect();
        var doc = TheApp.FileTrans.DocOf(Sid);
        if (img.IsEmpty || doc is null) return;
        for (int i = 0; i < doc.Boxes.Count; i++)
        {
            var b = _tempBox is { } tb && tb.Index == i ? tb.Box : doc.Boxes[i];
            var sel = TheApp.FileTrans.SelectedBox == i;
            var rc = new System.Windows.Shapes.Rectangle
            {
                Width = Math.Max(1, b.W * img.Width), Height = Math.Max(1, b.H * img.Height),
                StrokeThickness = sel ? 2.6 : 1.6, IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(img.X + b.X * img.Width, img.Y + b.Y * img.Height, 0, 0),
            };
            rc.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, sel ? "RiskDanger" : "Accent");
            _overlay.Children.Add(rc);
            // 序号角标(用户选定):框多时清单里才对得上号
            var tagT = new TextBlock { Text = (i + 1).ToString(), FontSize = 10, Margin = new Thickness(2, 0, 2, 0) };
            tagT.SetResourceReference(TextBlock.ForegroundProperty, "FgOnAccent");
            var tag = new Border
            {
                Child = tagT, IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(img.X + b.X * img.Width, img.Y + b.Y * img.Height, 0, 0),
                CornerRadius = new CornerRadius(2),
            };
            tag.SetResourceReference(Border.BackgroundProperty, sel ? "RiskDanger" : "Accent");
            _overlay.Children.Add(tag);
        }
    }
}
