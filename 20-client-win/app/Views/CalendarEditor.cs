// P3c -- 日程编辑器的【内容】(装进浮窗)。existing == null = 新增,否则 = 编辑那一条。
//
// 字段集按用户裁定:标题 · 开始 · 结束(默认 +1 小时) · 全天(可跨天) ·
//   iCloud 日历组(留待接入) · 地点(仅字符) · 链接 · 备注。
// 归属成员与可见范围沿用 D45 口径。AI 与手动编辑写同一个数据模型。
//
// ★ 本地优先(2026-07-30 用户裁定):没接入 Apple 也能新增/编辑/删除,当场写进本机并落盘。
//   接入 Apple 后走增量合并(不覆盖),见 CalendarData.MergeIn / LocalOnly。接入前不谎称"已同步"。

using System.Windows;
using System.Windows.Controls;
using LocalAI.Client.I18n;

namespace LocalAI.Client.Views;

public static class CalendarEditor
{
    /// <summary>新建日程的默认时长(用户裁定:结束时间默认 +1 小时)。</summary>
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromHours(1);

    public static UIElement Build(DateTime day, CalendarEvent? existing, Action onSaved, TimeSpan? presetStart = null)
    {
        // 新建日程:开始时间默认落在【当前时间之后最近的五分钟】(用户裁定),日期取所选那天。
        // 编辑已有日程则保持它自己的时间。
        // startAt 不为空 = 调用方已经指定了时刻(比如在时间轴上双击的那个半小时)。
        var start = existing?.Start ?? (day.Date + WheelPicker.Snap(presetStart ?? WheelPicker.CeilToStep(DateTime.Now.TimeOfDay)));
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

        // ★★ 非全天日程也可以【跨天】(用户 2026-07-31 让我拿方案):
        //   结束日偏移 0 = 当日,1 = 次日,2 = 第 3 天……存的时候
        //   end = 开始那天 + 偏移 + 结束时刻。
        //   为什么非得有它:转盘只给"时:分",一条 22:00 → 次日 03:00 的日程
        //   在原来的模型里【根本表达不出来】—— 而且旧逻辑还会把开始一起往前拖,
        //   把用户明明想要的跨天硬掰成同一天。
        var endOffset = existing is not null
            ? Math.Max(0, (existing.End.Date - existing.Start.Date).Days)
            : 0;
        TextBlock? endDayLabel = null;
        void SyncEndDay()
        {
            if (endDayLabel is null) return;
            endDayLabel.Text = endOffset switch { 0 => "当日", 1 => "次日", _ => $"第 {endOffset + 1} 天" };
        }

        // ★★ 开始必须早于结束 —— 这条在【编辑器里当场纠正】,并且让另一个转盘【动给你看】。
        //   此前只在保存时悄悄夹一下(结束早于开始就改成开始+1 小时):
        //   界面上你看到的是"9:00 → 8:00",存进去的却是别的东西 —— 所见非所得,
        //   而这份数据是要同步给 Apple 的,一条起止颠倒/被悄悄改过的日程在那边就是一次错。
        Action<TimeSpan>? setStartTime = null;
        Action<TimeSpan>? setEnd = null;

        var (endTimeEl, setEndTime) = WheelPicker.Time(endAt, v =>
        {
            endAt = v;
            endEdited = true;
            // ★★ 结束被拨到开始之前 = 【跨到次日】(用户裁定 2026-07-31)。
            //   人嘴里说的"22:00 到 03:00"本来就是跨天的意思。
            //   旧做法是把开始一起往前拖 —— 那等于替用户改了他刚刚没动的那一头,
            //   而且让"跨天"这件事永远表达不出来。
            if (endAt > startAt || endOffset > 0) return;
            endOffset = 1;
            SyncEndDay();
        });
        setEnd = setEndTime;

        var (startTimeEl, setStart) = WheelPicker.Time(startAt, v =>
        {
            startAt = v;
            // 改开始 -> 结束自动跟到 +1 小时(夹在当天内)。
            // ★ 即使用户已经手动改过结束,只要开始越过了它,也必须把结束推开 ——
            //   "尊重用户改过的结束"不能凌驾于"起止不许颠倒"。
            if (endOffset > 0) return;              // 已经跨天了,起止不可能颠倒
            if (endEdited && startAt < endAt) return;
            var next = startAt + DefaultDuration;
            // ★ 开始推到很晚时,不再把结束贴到 23:30 —— 直接【跨到次日】,这才是它本来的意思
            if (next < TimeSpan.FromDays(1)) endAt = next;
            else { endAt = next - TimeSpan.FromDays(1); endOffset = 1; SyncEndDay(); }
            setEnd?.Invoke(endAt);   // 内部改值不回调,不会被误判成"用户手动改过"
        });
        setStartTime = setStart;

        // 结束时刻下面挂一行「结束日」:− 当日 +
        endDayLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 46, TextAlignment = TextAlignment.Center };
        endDayLabel.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
        endDayLabel.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        SyncEndDay();

        FrameworkElement OffsetKey(string glyph, int delta, string tip)
        {
            var t = new TextBlock { Text = glyph, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            t.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            var hit = new Border
            {
                Child = t, Width = 22, Height = 20, BorderThickness = new Thickness(1),
                Background = System.Windows.Media.Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand, ToolTip = tip,
            };
            hit.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
            hit.SetResourceReference(Border.BorderBrushProperty, "Border");
            hit.MouseEnter += (_, _) => hit.SetResourceReference(Border.BackgroundProperty, "BgHover");
            hit.MouseLeave += (_, _) => hit.Background = System.Windows.Media.Brushes.Transparent;
            hit.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                var next = endOffset + delta;
                // ★ 回到"当日"时,结束时刻若还早于开始,那就不是一条合法日程 —— 不许退回去。
                if (next < 0 || (next == 0 && endAt <= startAt)) return;
                endOffset = next;
                SyncEndDay();
            };
            return hit;
        }

        var endDayRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        endDayRow.Children.Add(OffsetKey("−", -1, "结束日往前一天"));
        endDayRow.Children.Add(new Border { Width = 4 });
        endDayRow.Children.Add(endDayLabel);
        endDayRow.Children.Add(new Border { Width = 4 });
        endDayRow.Children.Add(OffsetKey("+", 1, "结束日往后一天(跨天日程就靠它)"));

        var endStack = new StackPanel();
        endStack.Children.Add(endTimeEl);
        endStack.Children.Add(endDayRow);

        var timedRow = TwoUp("开始", startTimeEl, "结束", endStack);
        // 全天同理:结束日期不能早于开始日期。日期转盘没有 Set 回调,所以这里【就地夹住】
        // 并在下面的提示里如实说明 —— 至少不会存进一条颠倒的日程。
        var allDayRow = TwoUp("开始日期", WheelPicker.Date(startDay, d =>
                              {
                                  startDay = d;
                                  if (endDay < startDay) endDay = startDay;
                              }),
                              "结束日期", WheelPicker.Date(endDay, d =>
                              {
                                  endDay = d < startDay ? startDay : d;
                              }));

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
        // ★★ 显示过【界面用词表】(家庭/团队),但【存的仍是 CalendarData.Groups 里的原词】——
        //   见保存处 `CalendarGroup: CalendarData.Groups[index]`,以及下一行按【原值】反查选中项。
        //   若把存储值也换掉:老档案里 CalendarGroup="家庭" 将匹配不上分组表 -> IndexOf 得 -1
        //   -> 回落到第 0 项 -> 用户的日程被静默改到别的分组。存储与显示必须分开(见 Services/Vocab)。
        // ★ 每一项前面点一个【该分类的颜色】—— 颜色本身也来自 Apple(见 CalendarGroups)。
        foreach (var g in CalendarData.Groups)
        {
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 8, Height = 8,
                Margin = new Thickness(0, 0, 7, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill = new System.Windows.Media.SolidColorBrush(Services.CalendarGroups.ColorOf(g)),
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(dot);
            row.Children.Add(new TextBlock { Text = Services.Vocab.Apply(g), VerticalAlignment = VerticalAlignment.Center });
            group.Items.Add(new ComboBoxItem { Content = row, Tag = g });
        }
        // ★★ 上面那段注释警告的正是这一行原来干的事:IndexOf 得 -1 -> Max(0,…) 回落到第 0 项。
        //   命中场景很常见:老档案的"家庭"、接上 Apple 之后、断开之后、在 iCloud 里改过日历名之后——
        //   用户只想改个标题点保存,分类就被永久改成另一个日历,颜色跟着变,而且没有任何提示。
        //   现在:认不出来就【临时插一项原值】并选中它,如实标明它不在当前清单里。
        var keepGroup = existing?.CalendarGroup;
        var gi = keepGroup is null ? 0 : Array.IndexOf(CalendarData.Groups, keepGroup);
        if (gi < 0)
        {
            var row2 = new StackPanel { Orientation = Orientation.Horizontal };
            var dot2 = new System.Windows.Shapes.Ellipse
            {
                Width = 8, Height = 8, Margin = new Thickness(0, 0, 7, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill = new System.Windows.Media.SolidColorBrush(Services.CalendarGroups.ColorOf(keepGroup)),
            };
            row2.Children.Add(dot2);
            row2.Children.Add(new TextBlock { Text = Services.Vocab.Apply(keepGroup!), VerticalAlignment = VerticalAlignment.Center });
            var tail = new TextBlock { Text = "（不在当前日历清单里）", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            tail.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            tail.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            row2.Children.Add(tail);
            group.Items.Add(new ComboBoxItem { Content = row2, Tag = keepGroup });
            gi = group.Items.Count - 1;
        }
        group.SelectedIndex = Math.Max(0, gi);

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

        // ★ 只给两项:"个人"与"仅本人"对日程来说是同一件事(都只在本机、不共享),
        //   两条并排出现只会让人以为有区别(用户反馈:看着像重复)。
        //   仅本人保留在枚举里给会话/D45 用 —— 那里它是有意义的。
        var scopes = new[] { Strings.Get("visibility.family"), Strings.Get("visibility.personal") };
        var scope = new ComboBox { Margin = new Thickness(0, 2, 0, 6) };
        foreach (var sc in scopes) scope.Items.Add(sc);
        scope.SelectedIndex = Math.Max(0, Array.IndexOf(scopes, existing?.Scope ?? scopes[0]));

        var status = Ui.Caption("");
        status.Margin = new Thickness(0, 8, 0, 0);
        void Warn(string msg)
        {
            status.Text = msg;
            status.SetResourceReference(TextBlock.ForegroundProperty, "RiskWarning");
        }

        // 从各控件收出一条日程(保留 existing 的 Id / 来源 / Apple UID / AI 标记,编辑不抹掉出处)。
        CalendarEvent Collect()
        {
            var isAllDay = allDay.IsChecked == true;
            DateTime s, e2;
            if (isAllDay)
            {
                s = startDay.Date;
                e2 = endDay.Date < startDay.Date ? startDay.Date : endDay.Date;   // 结束早于开始 -> 收回到当天
            }
            else
            {
                var d0 = existing?.Start.Date ?? day.Date;   // 定时日程落在所选那天
                s = d0 + startAt;
                // ★ 结束 = 开始那天 + 【结束日偏移】+ 结束时刻 —— 非全天也能跨天
                e2 = d0.AddDays(endOffset) + endAt;
                if (e2 <= s) e2 = s + DefaultDuration;        // 结束不晚于开始 -> 补 1 小时
            }
            return new CalendarEvent(
                s, e2, title.Text.Trim(),
                owners[Math.Max(0, owner.SelectedIndex)],
                scopes[Math.Max(0, scope.SelectedIndex)],
                AllDay: isAllDay,
                // ★ 按 Tag 取值而不是按 index 反查 Groups —— 列表里可能多了那一项"不在清单里"的原值。
                CalendarGroup: (group.SelectedItem as ComboBoxItem)?.Tag as string,
                Location: string.IsNullOrWhiteSpace(location.Text) ? null : location.Text,
                Url: string.IsNullOrWhiteSpace(url.Text) ? null : url.Text,
                Notes: string.IsNullOrWhiteSpace(notes.Text) ? null : notes.Text,
                CreatedByAi: existing?.CreatedByAi ?? false,
                Id: existing?.Id, Source: existing?.Source ?? "local", ExternalId: existing?.ExternalId);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        buttons.Children.Add(Ui.Primary(existing is null ? "添加" : "保存", (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(title.Text)) { Warn("请先填写标题。"); title.Focus(); return; }
            var ev = Collect();
            if (existing is null) CalendarData.Add(ev); else CalendarData.Update(ev);
            onSaved();   // 存好收起抽屉;列表由 CalendarData.Changed 自动刷新
        }));
        if (existing is not null)
        {
            var del = Ui.Danger("删除", (_, _) => { CalendarData.Remove(existing); onSaved(); });
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
            Ui.Caption("现在改动【只在本机生效并保存】。接入 Apple 家庭共享日历后按增量合并双向同步(不覆盖已有);与对方相关的日程届时只发邀请/修改建议。")
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
