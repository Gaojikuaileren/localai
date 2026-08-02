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
        var empty = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        empty.Children.Add(_hint);
        empty.Children.Add(new Border { Height = 8 });
        empty.Children.Add(import);
        stage.Children.Add(empty);
        _emptyHost = empty;

        var left = new Border { Child = stage, Margin = new Thickness(0, 0, 6, 0), AllowDrop = true };
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

        Loaded += (_, _) => { TheApp.FileTrans.Changed += Rebuild; Rebuild(); };
        Unloaded += (_, _) => TheApp.FileTrans.Changed -= Rebuild;
        _overlay.SizeChanged += (_, _) => RedrawBoxes();
    }

    readonly StackPanel _emptyHost;

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
        if (sid is null)
        {
            var sess = TheApp.Chat.NewSession(null, "translation", ProjectScope.Personal,
                $"文件翻译 · {Path.GetFileName(path)} · {DateTime.Now:M月d日 HH:mm}", fileTrans: true);
            sid = sess.SessionId;
        }
        TheApp.FileTrans.SetFile(sid, path);
    }

    string? Sid => _sessionId ?? TheApp.Chat.Sessions.LastOrDefault(s => s.FileTrans && s.DeletedAt is null)?.SessionId;

    void Rebuild()
    {
        var doc = TheApp.FileTrans.DocOf(Sid);
        if (doc is null || !File.Exists(doc.Path))
        {
            _img.Source = null;
            _emptyHost.Visibility = Visibility.Visible;
            _hint.Text = doc is null
                ? "把 PNG / JPG / PDF 拖到这里,或点下面导入。\n再用右下工具栏的「创建标注框」圈出要翻译的部分。"
                : $"原文件不在了:{doc.Path}\n(移动或删除了的话,重新导入一次。)";
            RedrawBoxes();
            return;
        }
        _emptyHost.Visibility = Visibility.Collapsed;
        if (Path.GetExtension(doc.Path).ToLowerInvariant() == ".pdf")
        {
            // ★ 如实:PDF 的渲染要 WinRT(随引擎一起接),现在不画一个假封面
            _img.Source = null;
            _emptyHost.Visibility = Visibility.Visible;
            _hint.Text = $"已导入 PDF:{Path.GetFileName(doc.Path)}\n★ PDF 预览尚未接入(需要系统渲染组件,随翻译引擎 P4 一起接)。\n标注框要在预览上画,所以 PDF 暂时只能整页翻译。";
        }
        else
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;   // 不锁文件
                bmp.UriSource = new Uri(doc.Path);
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

    void BeginBox(object s, MouseButtonEventArgs e)
    {
        if (!TheApp.FileTrans.BoxTool || Sid is null || ImageRect() is { IsEmpty: true }) return;
        _dragStart = e.GetPosition(_overlay);
        _ghost = new System.Windows.Shapes.Rectangle { StrokeThickness = 1.6, IsHitTestVisible = false,
            StrokeDashArray = new DoubleCollection { 3, 2 } };
        _ghost.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Accent");
        _overlay.Children.Add(_ghost);
        _overlay.CaptureMouse();
    }

    void DragBox(object s, MouseEventArgs e)
    {
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
        foreach (var b in doc.Boxes)
        {
            var rc = new System.Windows.Shapes.Rectangle
            {
                Width = Math.Max(1, b.W * img.Width), Height = Math.Max(1, b.H * img.Height),
                StrokeThickness = 1.6, IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(img.X + b.X * img.Width, img.Y + b.Y * img.Height, 0, 0),
            };
            rc.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Accent");
            _overlay.Children.Add(rc);
        }
    }
}
