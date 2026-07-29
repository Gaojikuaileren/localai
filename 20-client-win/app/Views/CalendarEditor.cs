// P3c -- 日程编辑器的【内容】(装进浮窗)。existing == null = 新增,否则 = 编辑那一条。
//
// 字段集按用户裁定:标题 · 开始 · 结束(默认 +1 小时) · 全天(可跨天) ·
//   iCloud 日历组(留待接入) · 地点(仅字符) · 链接 · 备注。
// 归属成员与可见范围沿用 D45 口径。AI 与手动编辑写同一个数据模型。
//
// ★ 数据源未接入 Apple 家庭共享日历 -> 保存/删除【如实拒绝】并说明原因,
//   不伪造日程、不伪造同步成功(设计 §4.5 / 状态矩阵 §8)。

using System.Windows;
using System.Windows.Controls;
using LocalAI.Client.I18n;

namespace LocalAI.Client.Views;

public static class CalendarEditor
{
    /// <summary>新建日程的默认时长(用户裁定:结束时间默认 +1 小时)。</summary>
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromHours(1);

    public static UIElement Build(DateTime day, CalendarEvent? existing, Action onSaved)
    {
        var start = existing?.Start ?? day.Date.AddHours(9);
        var end = existing?.End ?? start + DefaultDuration;

        var title = Field(existing?.Title ?? "");

        // ---- 时间:全天开关在「时刻」与「日期区间」两种形态间切换 ----
        var allDay = new CheckBox
        {
            Content = "全天(可跨天)",
            IsChecked = existing?.AllDay ?? false,
            Margin = new Thickness(0, 2, 0, 6),
            VerticalAlignment = VerticalAlignment.Center,
        };

        // ---- 时间/日期一律用【竖直滚轮】(用户裁定)----
        //   非全天:只显示【时:分】两列;勾选全天:只显示【年/月/日】三列。
        //   两者互斥切换 —— 不该同时出现(用户反馈)。
        var startAt = WheelPicker.Snap(start.TimeOfDay);
        var endAt = WheelPicker.Snap(end.TimeOfDay);
        var startDay = start.Date;
        var endDay = end.Date;

        var endEdited = existing is not null;   // 用户手动动过结束就不再自动跟随

        var (endTimeEl, setEndTime) = WheelPicker.Time(endAt, v => { endAt = v; endEdited = true; });
        var (startTimeEl, _) = WheelPicker.Time(startAt, v =>
        {
            startAt = v;
            if (endEdited) return;
            // 改开始 -> 结束自动跟到 +1 小时(夹在当天内)
            var next = startAt + DefaultDuration;
            endAt = next < TimeSpan.FromDays(1) ? next : WheelPicker.Snap(TimeSpan.FromHours(23.5));
            setEndTime(endAt);   // 内部改值不回调,不会被误判成"用户手动改过"
        });

        var timedRow = TwoUp("开始", startTimeEl, "结束", endTimeEl);
        var allDayRow = TwoUp("开始日期", WheelPicker.Date(startDay, d => startDay = d),
                              "结束日期", WheelPicker.Date(endDay, d => endDay = d));

        // ★ 互斥显示。上一轮我在重写时把这段连带删掉了,导致两组转盘一直同时显示。
        void SyncMode()
        {
            var on = allDay.IsChecked == true;
            timedRow.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            allDayRow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }
        allDay.Checked += (_, _) => SyncMode();
        allDay.Unchecked += (_, _) => SyncMode();
        SyncMode();

        // ---- iCloud 日历组(接入后由服务端下发真实分组)----
        var group = new ComboBox { Margin = new Thickness(0, 2, 0, 6) };
        foreach (var g in CalendarData.Groups) group.Items.Add(g);
        group.SelectedIndex = Math.Max(0, Array.IndexOf(CalendarData.Groups, existing?.CalendarGroup ?? CalendarData.Groups[0]));

        var location = Field(existing?.Location ?? "");
        var url = Field(existing?.Url ?? "");
        var notes = Field(existing?.Notes ?? "");
        notes.AcceptsReturn = true;
        notes.TextWrapping = TextWrapping.Wrap;
        notes.MinHeight = 40;   // 压缩高度:默认窗口下整页不出滚动条(用户裁定)
        notes.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;

        // ---- 归属与可见范围(D45)----
        var owners = new[] { "我", "对方", "双方" };
        var owner = new ComboBox { Margin = new Thickness(0, 2, 0, 6) };
        foreach (var o in owners) owner.Items.Add(o);
        owner.SelectedIndex = Math.Max(0, Array.IndexOf(owners, existing?.Owner ?? "我"));

        var scopes = new[] { Strings.Get("visibility.family"), Strings.Get("visibility.personal"), Strings.Get("visibility.only_me") };
        var scope = new ComboBox { Margin = new Thickness(0, 2, 0, 6) };
        foreach (var sc in scopes) scope.Items.Add(sc);
        scope.SelectedIndex = Math.Max(0, Array.IndexOf(scopes, existing?.Scope ?? scopes[0]));

        var status = Ui.Caption("");
        status.Margin = new Thickness(0, 8, 0, 0);
        void Reject(string what)
        {
            status.Text = $"日历尚未连接,暂时无法{what}。接入 Apple 家庭共享日历后,这里的修改会自动同步。";
            status.SetResourceReference(TextBlock.ForegroundProperty, "RiskWarning");
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        buttons.Children.Add(Ui.Primary(existing is null ? "添加" : "保存", (_, _) => Reject(existing is null ? "添加" : "保存")));
        if (existing is not null)
        {
            var del = Ui.Danger("删除", (_, _) => Reject("删除"));
            del.Margin = new Thickness(10, 0, 0, 0);
            buttons.Children.Add(del);
        }

        // 分组成卡片 + 带图标的小标题 —— 与主页各板块同一套视觉语言(用户反馈:抽屉风格不统一)
        var form = Ui.Stack(
            Ui.Panel("基本信息",
                Ui.Stack(Ui.Caption("标题"), title, allDay, timedRow, allDayRow),
                Theme.IconName.Calendar, new Thickness(0, 0, 0, 8), compact: true),

            Ui.Panel("归类",
                Ui.Stack(Ui.Caption("日历组"), group,
                         Ui.Caption("归属成员"), owner,
                         Ui.Caption("可见范围"), scope),
                Theme.IconName.Member, new Thickness(0, 0, 0, 8), compact: true),

            Ui.Panel("详情",
                Ui.Stack(Ui.Caption("地点"), location,
                         Ui.Caption("链接"), url,
                         Ui.Caption("备注"), notes),
                Theme.IconName.Assets, new Thickness(0, 0, 0, 8), compact: true),

            buttons,
            status,
            Ui.Caption("接入后:与对方相关的日程只能发邀请/修改建议;AI 走同一入口,遵守同样的可见范围规则。")
        );

        // 装在右侧全高抽屉里,不再需要浮窗时代的高度上限;仍留滚动以应对小窗口。
        return new ScrollViewer
        {
            Content = form,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        }.PassThrough();
    }

    static TextBox Field(string text) => new()
    {
        Text = text,
        Margin = new Thickness(0, 2, 0, 6),
        Padding = new Thickness(8, 4, 8, 4),
    };

    static Grid TwoUp(string leftLabel, UIElement left, string rightLabel, UIElement right)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var l = Ui.Stack(Ui.Caption(leftLabel), left);
        var r = Ui.Stack(Ui.Caption(rightLabel), right);
        Grid.SetColumn(l, 0); Grid.SetColumn(r, 2);
        g.Children.Add(l); g.Children.Add(r);
        return g;
    }
}
