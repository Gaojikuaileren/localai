// P3c -- 「正在进行的任务」抽屉内容。全局:从底部横条点开,在任何界面都可用。
// 与底部横条的分工:横条只给【一条简要 + 进度】(轮播);抽屉给【全部任务的完整列表】。

using System.Windows;
using System.Windows.Controls;
using LocalAI.Client.Services;

namespace LocalAI.Client.Views;

public sealed class TaskDrawerView : UserControl
{
    public TaskDrawerView()
    {
        var app = (App)Application.Current;
        var list = new StackPanel();

        if (app.Tasks.Tasks.Count == 0)
        {
            list.Children.Add(Ui.Body("暂无正在运行的任务。", muted: true));
        }
        else
        {
            foreach (var t in app.Tasks.Tasks)
            {
                var head = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 4) };
                var pct = new TextBlock { Text = t.PercentText, VerticalAlignment = VerticalAlignment.Center };
                pct.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
                pct.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
                DockPanel.SetDock(pct, Dock.Right);
                head.Children.Add(pct);

                var title = new TextBlock { Text = t.Title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
                title.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
                head.Children.Add(title);

                var bar = new ProgressBar { Height = 4, Minimum = 0, Maximum = 1, BorderThickness = new Thickness(0), Margin = new Thickness(0, 6, 0, 0) };
                bar.SetResourceReference(ProgressBar.ForegroundProperty, "Accent");
                bar.SetResourceReference(ProgressBar.BackgroundProperty, "BgSunken");
                if (t.Progress < 0) bar.IsIndeterminate = true; else bar.Value = t.Progress;

                list.Children.Add(Ui.Card(Ui.Stack(head, Ui.Caption(t.Detail), bar), new Thickness(0, 0, 0, 10)));
            }
        }

        Content = new ScrollViewer
        {
            Content = list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
    }
}
