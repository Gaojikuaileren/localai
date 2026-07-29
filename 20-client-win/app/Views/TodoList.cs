// P3c -- 待办 / 家务的列表行(仿 iPhone 提醒事项)。
//   左侧圆圈(点它切换完成)+ 标题(完成则删除线变淡)+ 副行(类型 · 截止 · 旗标)。
//   整行点开右侧编辑抽屉。
//
// 抽成静态、以回调解耦:HomeView 用它渲染真列表,渲染诊断(--todotest)也用同一份,
// 保证"评审看到的"与"实际跑的"是同一段布局代码。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using LocalAI.Client.Services;

namespace LocalAI.Client.Views;

public static class TodoList
{
    public static FrameworkElement Row(TodoItem t, Action onToggle, Action onOpen)
    {
        // ① 圆圈:未完成=描边空圈;完成=实心强调色 + 白勾
        var ring = new Ellipse { Width = 18, Height = 18, StrokeThickness = 1.6, VerticalAlignment = VerticalAlignment.Center };
        ring.SetResourceReference(Shape.StrokeProperty, t.Done ? "Accent" : "BorderStrong");
        if (t.Done) ring.SetResourceReference(Shape.FillProperty, "Accent");

        var check = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M4,9 L7.5,12.5 L14,5"),
            StrokeThickness = 1.8, Stretch = Stretch.None,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Visibility = t.Done ? Visibility.Visible : Visibility.Collapsed,
            IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
        };
        check.SetResourceReference(Shape.StrokeProperty, "FgOnAccent");

        var circle = new Grid { Width = 18, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), Cursor = System.Windows.Input.Cursors.Hand };
        circle.Children.Add(ring);
        circle.Children.Add(check);
        circle.MouseLeftButtonUp += (_, e) => { e.Handled = true; onToggle(); };  // 点圈只切换,不打开编辑

        // ② 标题(完成则删除线 + 变淡);中/高优先级前缀感叹号(仿提醒事项)
        var titleText = t.Priority switch
        {
            TodoPriority.High => "‼ " + t.Title,
            TodoPriority.Medium => "! " + t.Title,
            _ => t.Title,
        };
        var titleBlock = new TextBlock
        {
            Text = titleText, TextTrimming = TextTrimming.CharacterEllipsis,
            TextDecorations = t.Done ? TextDecorations.Strikethrough : null,
        };
        titleBlock.SetResourceReference(TextBlock.ForegroundProperty, t.Done ? "FgMuted" : "FgPrimary");

        // ③ 副行:类型 · 截止(逾期标红)· 旗标
        var sub = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 0) };
        void Meta(string text, string colorKey)
        {
            var tb = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
            tb.SetResourceReference(TextBlock.ForegroundProperty, colorKey);
            tb.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            sub.Children.Add(tb);
        }
        Meta(t.Kind == TodoKind.Chore ? "家务" : "待办", "FgMuted");
        if (t.Due is { } due)
            Meta("  ·  " + FormatDue(due, t.DueHasTime), t.IsOverdue ? "RiskDanger" : "FgMuted");
        if (t.Flagged)
        {
            var flag = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M0,0 L0,11 M0,0 L7,0 L5.2,2.6 L7,5.2 L0,5.2"),
                StrokeThickness = 1.2, Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center, Stretch = Stretch.None,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            };
            flag.SetResourceReference(Shape.StrokeProperty, "RiskWarning");
            flag.SetResourceReference(Shape.FillProperty, "RiskWarning");
            sub.Children.Add(flag);
        }

        var textCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        textCol.Children.Add(titleBlock);
        textCol.Children.Add(sub);

        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 2) };
        DockPanel.SetDock(circle, Dock.Left);
        row.Children.Add(circle);
        row.Children.Add(textCol);

        var host = new Border
        {
            Child = row,
            Padding = new Thickness(8, 7, 8, 7),
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = Brushes.Transparent,   // 有底色整行才接得到点击
        };
        host.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        host.MouseEnter += (_, _) => host.SetResourceReference(Border.BackgroundProperty, "BgHover");
        host.MouseLeave += (_, _) => host.Background = Brushes.Transparent;
        host.MouseLeftButtonUp += (_, _) => onOpen();   // 整行点开编辑
        return host;
    }

    public static string FormatDue(DateTime due, bool hasTime)
    {
        var day = due.Date;
        var today = DateTime.Today;
        var dayText = day == today ? "今天"
            : day == today.AddDays(1) ? "明天"
            : day == today.AddDays(-1) ? "昨天"
            : due.ToString("M月d日");
        return hasTime ? $"{dayText} {due:HH:mm}" : dayText;
    }
}
